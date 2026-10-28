using System.Text.Json;

namespace Kobian.Notifications.Alerts.Rules;

/// <summary>Skompilowany warunek: czysta funkcja (fakty, czas) → czy alert aktywny.</summary>
public delegate bool AlertCondition(AlertFacts facts, DateTimeOffset now);

/// <summary>
/// Funkcje warunków D9.8 zaimplementowane w kodzie; JSON wybiera funkcję i parametry (brak języka wyrażeń, SEC-19.3).
/// Nieznana funkcja, nieznany lub błędny argument = FormatException przy ładowaniu katalogu reguł.
/// </summary>
public static class AlertConditions
{
    public static readonly IReadOnlySet<string> KnownFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "verificationFailed", "restoreTestFailed", "snapshotMissing", "retentionOrScheduleChanged", "hostKeyMismatch",
        "protectionDegraded", "ransomwareAnomaly", "heartbeatMissing", "securityUpdateAvailable",
    };

    private static readonly Dictionary<string, Func<JsonElement, AlertCondition>> Registry = new(StringComparer.Ordinal)
    {
        ["lastRunStatus"] = LastRunStatus,
        ["noSuccessSince"] = NoSuccessSince,
        ["flag"] = Flag,
        ["freeSpace"] = FreeSpace,
        ["expiresWithin"] = ExpiresWithin,
        ["unsupportedOrUnpatched"] = UnsupportedOrUnpatched,
        ["clockJump"] = ClockJump,
        ["repeatedDenials"] = RepeatedDenials,
    };

    public static IReadOnlyCollection<string> Names => Registry.Keys;

    public static AlertCondition Compile(string fn, JsonElement args)
    {
        if (!Registry.TryGetValue(fn, out var factory)) throw new FormatException($"Nieznana funkcja warunku: {fn}");
        if (args.ValueKind != JsonValueKind.Object) throw new FormatException($"{fn}: args musi być obiektem");
        return factory(args);
    }

    private static void Allow(string fn, JsonElement a, params string[] keys)
    {
        var count = 0;
        foreach (var p in a.EnumerateObject())
        {
            if (Array.IndexOf(keys, p.Name) < 0) throw new FormatException($"{fn}: nieznany argument {p.Name}");
            count++;
        }
        if (count != keys.Length) throw new FormatException($"{fn}: wymagane argumenty: {string.Join(", ", keys)}");
    }

    private static double Num(string fn, JsonElement a, string key, double min, double max)
    {
        var e = a.GetProperty(key);
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var v) || double.IsNaN(v) || v < min || v > max)
            throw new FormatException($"{fn}.{key}: liczba w zakresie {min}–{max}");
        return v;
    }

    private static bool Bool(string fn, JsonElement a, string key)
    {
        var e = a.GetProperty(key);
        return e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : throw new FormatException($"{fn}.{key}: wartość logiczna");
    }

    private static AlertCondition LastRunStatus(JsonElement a)
    {
        Allow("lastRunStatus", a, "status", "requireRetriesExhausted");
        var s = a.GetProperty("status");
        if (s.ValueKind != JsonValueKind.String || !Enum.TryParse<RunOutcome>(s.GetString(), false, out var status) || status == RunOutcome.Success)
            throw new FormatException("lastRunStatus.status: Failed lub Warning");
        var exhausted = Bool("lastRunStatus", a, "requireRetriesExhausted");
        return (f, _) => f.LastOutcome == status && (!exhausted || f.RetriesExhausted);
    }

    private static AlertCondition NoSuccessSince(JsonElement a)
    {
        Allow("noSuccessSince", a, "factor", "minHours", "useRpoIfSet");
        var factor = Num("noSuccessSince", a, "factor", 1, 100);
        var minHours = Num("noSuccessSince", a, "minHours", 0, 24 * 365);
        var useRpo = Bool("noSuccessSince", a, "useRpoIfSet");
        return (f, now) =>
        {
            var reference = f.LastSuccessAt ?? f.Since;
            if (reference is null) return false;
            TimeSpan limit;
            if (useRpo && f.Rpo is { } rpo && rpo > TimeSpan.Zero) limit = rpo;
            else if (f.ScheduleInterval is { } iv && iv > TimeSpan.Zero)
            {
                limit = iv * factor;
                // „min. 26 h dla dziennych” (9.4): dolny próg dotyczy zadań rzadszych niż co 24 h.
                if (iv >= TimeSpan.FromHours(24) && limit < TimeSpan.FromHours(minHours)) limit = TimeSpan.FromHours(minHours);
            }
            else limit = TimeSpan.FromHours(minHours);
            return now - reference.Value > limit;
        };
    }

    private static AlertCondition Flag(JsonElement a)
    {
        Allow("flag", a, "name");
        var n = a.GetProperty("name");
        var name = n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (name is null || !KnownFlags.Contains(name)) throw new FormatException("flag.name: nieznana flaga");
        return (f, _) => f.Flags.Contains(name);
    }

    private static AlertCondition FreeSpace(JsonElement a)
    {
        Allow("freeSpace", a, "minFreePercent", "forecastDays");
        var minFree = Num("freeSpace", a, "minFreePercent", 0, 100) / 100.0;
        var days = Num("freeSpace", a, "forecastDays", 0, 3650);
        return (f, _) => f.FreeSpaceRatio < minFree || f.DaysToFull < days;
    }

    private static AlertCondition ExpiresWithin(JsonElement a)
    {
        Allow("expiresWithin", a, "days");
        var days = (int)Num("expiresWithin", a, "days", 0, 3650);
        return (f, _) => f.DaysToExpiry <= days;
    }

    private static AlertCondition UnsupportedOrUnpatched(JsonElement a)
    {
        Allow("unsupportedOrUnpatched", a, "maxPatchAgeDays");
        var max = (int)Num("unsupportedOrUnpatched", a, "maxPatchAgeDays", 1, 3650);
        return (f, _) => f.VendorSupported == false || f.DaysSincePatch > max;
    }

    private static AlertCondition ClockJump(JsonElement a)
    {
        Allow("clockJump", a, "forwardHours", "backwardMinutes");
        var fwd = TimeSpan.FromHours(Num("clockJump", a, "forwardHours", 0, 24 * 365));
        var back = TimeSpan.FromMinutes(Num("clockJump", a, "backwardMinutes", 0, 24 * 60));
        return (f, _) => f.ClockDelta > fwd || f.ClockDelta < -back;
    }

    private static AlertCondition RepeatedDenials(JsonElement a)
    {
        Allow("repeatedDenials", a, "threshold");
        var t = (int)Num("repeatedDenials", a, "threshold", 1, 10_000);
        return (f, _) => f.DenialsInWindow >= t;
    }
}
