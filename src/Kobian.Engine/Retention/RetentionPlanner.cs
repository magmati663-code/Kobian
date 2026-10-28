using System.Globalization;
using Kobian.Application.Jobs;

namespace Kobian.Engine.Retention;

/// <summary>Snapshot widziany przez retencję: tylko udane kopie z katalogu (bez zapomnianych). <c>BaseFull</c> — kopia pełna, od której zależy różnicowa.</summary>
public sealed record RetentionSnapshot(string Id, DateTimeOffset TimeUtc, bool Differential = false, string? BaseFull = null, bool Suspect = false);

/// <summary>Powód zachowania (podgląd w UI i CLI, D3.12 dry-run). Snapshot może mieć kilka powodów.</summary>
[Flags]
public enum KeepReason
{
    None = 0,
    Last = 1,
    Daily = 2,
    Weekly = 4,
    Monthly = 8,
    Yearly = 16,
    Within = 32,
    Locked = 64,
    Newest = 128,
    BaseOfDifferential = 256,
    Suspect = 512,
    Paused = 1024,
}

/// <summary>Wynik <c>Keep</c>: powody dla zachowanych; reszta (<see cref="Forget"/>) do logicznego zapomnienia.</summary>
public sealed record RetentionPlan(IReadOnlyDictionary<string, KeepReason> Keep, IReadOnlyList<string> Forget);

/// <summary>
/// Retencja GFS jako czysta funkcja <c>Keep(S, policy, now)</c> (D3.12, AC-10.1, V-10): „najnowszy snapshot w każdym z N ostatnich okresów, w których istnieje kopia”
/// (semantyka restic <c>forget</c>), okresy w strefie zadania (D7.2), tydzień ISO 8601 (od poniedziałku). Zawsze zachowuje: najnowszy, młodsze niż retentionLock,
/// bazy kopii różnicowych, podejrzane (D9.3 — do decyzji człowieka), a przy wstrzymanej retencji — wszystko. Polityka bez reguł = zachowaj wszystko (fail-safe).
/// </summary>
public static class RetentionPlanner
{
    public static RetentionPlan Plan(IReadOnlyList<RetentionSnapshot> snapshots, RetentionPolicy policy, DateTimeOffset nowUtc, TimeZoneInfo zone, bool paused = false)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(zone);
        var s = snapshots.OrderByDescending(x => x.TimeUtc).ThenByDescending(x => x.Id, StringComparer.Ordinal).ToList();
        var keep = new Dictionary<string, KeepReason>(StringComparer.Ordinal);
        void Add(RetentionSnapshot x, KeepReason r) => keep[x.Id] = keep.GetValueOrDefault(x.Id) | r;
        if (s.Count == 0) return new RetentionPlan(keep, []);

        // SEC-17.4: wiek liczony względem maksimum z zegara i najnowszej kopii (cofnięty zegar nie odbiera ochrony lock-u kopiom „z przyszłości”).
        // Skok zegara w przód wykrywa wywołujący i przekazuje paused=true (D9.3) — funkcja pozostaje czysta.
        var now = nowUtc > s[0].TimeUtc ? nowUtc : s[0].TimeUtc;
        if (paused || !policy.HasAnyRule)
        {
            foreach (var x in s) Add(x, KeepReason.Paused);
            return new RetentionPlan(keep, []);
        }

        // Reprezentantami okresów i „ostatnich N” są tylko kopie niepodejrzane: podejrzana (V2 nie przeszło) nowsza w tym samym dniu/tygodniu/miesiącu wypierała dobrą,
        // która nie dostawała żadnego powodu zachowania i była zapominana — z okresu zostawała wyłącznie kopia uznana za uszkodzoną. Podejrzane są zachowywane osobno (niżej).
        var good = s.Where(x => !x.Suspect).ToList();
        foreach (var x in good.Take(Math.Max(0, policy.KeepLast))) Add(x, KeepReason.Last);
        Bucket(good, policy.KeepDaily, zone, KeepReason.Daily, t => t.Year * 1000 + t.DayOfYear, Add);
        Bucket(good, policy.KeepWeekly, zone, KeepReason.Weekly, t => ISOWeek.GetYear(t) * 100 + ISOWeek.GetWeekOfYear(t), Add);
        Bucket(good, policy.KeepMonthly, zone, KeepReason.Monthly, t => t.Year * 100 + t.Month, Add);
        Bucket(good, policy.KeepYearly, zone, KeepReason.Yearly, t => t.Year, Add);
        foreach (var x in s)
        {
            var age = now - x.TimeUtc;
            if (policy.KeepWithin > TimeSpan.Zero && age <= policy.KeepWithin) Add(x, KeepReason.Within);
            if (policy.RetentionLock > TimeSpan.Zero && age < policy.RetentionLock) Add(x, KeepReason.Locked);
            if (x.Suspect) Add(x, KeepReason.Suspect);
        }
        Add(s[0], KeepReason.Newest);
        var byId = s.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var x in s.Where(x => keep.ContainsKey(x.Id) && x.Differential && x.BaseFull is not null).ToList())
            if (byId.TryGetValue(x.BaseFull!, out var b)) Add(b, KeepReason.BaseOfDifferential);
        return new RetentionPlan(keep, [.. s.Where(x => !keep.ContainsKey(x.Id)).Select(x => x.Id)]);
    }

    private static void Bucket(List<RetentionSnapshot> s, int n, TimeZoneInfo zone, KeepReason reason, Func<DateTime, int> key, Action<RetentionSnapshot, KeepReason> add)
    {
        if (n <= 0) return;
        var seen = new HashSet<int>();
        foreach (var x in s)
        {
            var b = key(TimeZoneInfo.ConvertTime(x.TimeUtc, zone).DateTime);
            if (seen.Contains(b)) continue;
            if (seen.Count >= n) break;
            seen.Add(b);
            add(x, reason);
        }
    }
}
