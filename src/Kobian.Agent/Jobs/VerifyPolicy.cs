using System.Text.Json;
using Kobian.Domain;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Polityka okresowej weryfikacji próbkowej V3 zadania (3.7, BL-26): <c>"verify":{"enabled":true,"intervalDays":7,"samplePercent":5}</c> w definicji v1.
/// Sekcja opcjonalna (zgodność wstecz): brak = <see cref="Default"/> (wartości z 3.7: co tydzień, 5% packów rotacyjnie → pokrycie 100% w 20 cyklach).
/// PROWIZORYCZNIE Qb101: polityka per zadanie w definicji (nie globalna w Agencie); interwał w dniach od ostatniego ukończonego cyklu,
/// bez okna godzinowego — alternatywa: wyrażenie cron w sched_triggers (kind 'verify'). Koszt zmiany: mały (pole definicji → wyzwalacz).
/// </summary>
public sealed record VerifyPolicy(bool Enabled, int IntervalDays, int SamplePercent)
{
    public const int MaxIntervalDays = 366;

    public static VerifyPolicy Default { get; } = new(true, 7, 5);

    /// <summary>Kubełków próbki = liczba cykli, w których rotacja obejmuje całe repozytorium (⌈100 / %⌉).</summary>
    public int CyclesForFullCoverage => (int)Math.Ceiling(100.0 / SamplePercent);

    public TimeSpan Interval => TimeSpan.FromDays(IntervalDays);

    /// <summary>Pola opcjonalne (brak = wartość domyślna); nieznane pole lub wartość spoza zakresu → KB-0103.</summary>
    public static VerifyPolicy Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw JobDefinition.Invalid("Field 'verify' must be an object.");
        foreach (var p in e.EnumerateObject())
            if (p.Name is not ("enabled" or "intervalDays" or "samplePercent")) throw JobDefinition.Invalid("Unknown field in 'verify'.");
        var enabled = Default.Enabled;
        if (e.TryGetProperty("enabled", out var en))
            enabled = en.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw JobDefinition.Invalid("verify.enabled must be a boolean.") };
        return new VerifyPolicy(enabled, Int(e, "intervalDays", Default.IntervalDays, 1, MaxIntervalDays), Int(e, "samplePercent", Default.SamplePercent, 1, 100));
    }

    public void Write(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartObject("verify");
        w.WriteBoolean("enabled", Enabled);
        w.WriteNumber("intervalDays", IntervalDays);
        w.WriteNumber("samplePercent", SamplePercent);
        w.WriteEndObject();
    }

    private static int Int(JsonElement e, string name, int fallback, int min, int max)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= min && n <= max
            ? n
            : throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, $"verify.{name} out of range.");
    }
}
