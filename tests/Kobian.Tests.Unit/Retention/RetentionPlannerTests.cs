using System.Diagnostics;
using Kobian.Application.Jobs;
using Kobian.Engine.Retention;
using Xunit;

namespace Kobian.Tests.Unit.Retention;

/// <summary>BL-25 / D3.12: retencja GFS jako czysta funkcja — AC-10.1 (V-10), przypadki brzegowe kubełków i strefy, testy właściwości, NFR-25.</summary>
public sealed class RetentionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static List<RetentionSnapshot> Daily(int n, DateTimeOffset newest) =>
        [.. Enumerable.Range(0, n).Select(i => new RetentionSnapshot($"s{i:D3}", newest.AddDays(-i)))];

    [Fact, Trait("AC", "AC-10.1"), Trait("Plan", "V-10")]
    public void AC_10_1_sixty_dailies_keep_last_7_daily_14_lock_14_days_keeps_exactly_the_14_newest()
    {
        var s = Daily(60, Now.AddHours(-8)); // codziennie o 01:00 UTC
        var plan = RetentionPlanner.Plan(s, new RetentionPolicy(KeepLast: 7, KeepDaily: 14, RetentionLock: TimeSpan.FromDays(14)), Now, TimeZoneInfo.Utc);
        Assert.Equal(Enumerable.Range(0, 14).Select(i => $"s{i:D3}").ToHashSet(), plan.Keep.Keys.ToHashSet());
        Assert.Equal(46, plan.Forget.Count);
        Assert.Equal(KeepReason.Last | KeepReason.Daily | KeepReason.Locked | KeepReason.Newest, plan.Keep["s000"]);
        Assert.Equal(KeepReason.Daily | KeepReason.Locked, plan.Keep["s013"]);
    }

    [Fact, Trait("Plan", "D3.12")]
    public void Gfs_keeps_newest_in_each_of_n_periods_that_have_a_copy_with_iso_weeks()
    {
        // Luka (wyłączony laptop): okresy bez kopii nie są liczone (semantyka restic).
        var s = new List<RetentionSnapshot>
        {
            new("mon", new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero)), // pon., tydzień 42
            new("sun", new DateTimeOffset(2026, 10, 11, 23, 0, 0, TimeSpan.Zero)), // niedz., tydzień 41 (ISO: tydzień kończy się w niedzielę)
            new("sat", new DateTimeOffset(2026, 10, 10, 10, 0, 0, TimeSpan.Zero)), // tydzień 41 — starszy w tym samym tygodniu
            new("sep", new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero)),
            new("aug", new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero)),
            new("old", new DateTimeOffset(2025, 12, 31, 10, 0, 0, TimeSpan.Zero)),
        };
        var weekly = RetentionPlanner.Plan(s, new RetentionPolicy(KeepWeekly: 3), Now, TimeZoneInfo.Utc);
        Assert.Equal(new HashSet<string> { "mon", "sun", "sep" }, weekly.Keep.Keys.ToHashSet()); // 3 tygodnie z kopią: 42, 41, 36
        var monthly = RetentionPlanner.Plan(s, new RetentionPolicy(KeepMonthly: 3, KeepYearly: 2), Now, TimeZoneInfo.Utc);
        Assert.Equal(KeepReason.Monthly | KeepReason.Yearly | KeepReason.Newest, monthly.Keep["mon"]);
        Assert.Equal(KeepReason.Monthly, monthly.Keep["sep"]);
        Assert.Equal(KeepReason.Monthly, monthly.Keep["aug"]);
        Assert.Equal(KeepReason.Yearly, monthly.Keep["old"]);
        Assert.Equal(["sat", "sun"], monthly.Forget.Order(StringComparer.Ordinal));
    }

    [Fact, Trait("Plan", "D7.2")]
    public void Day_buckets_use_the_job_time_zone()
    {
        // 21:30 i 22:30 UTC 12.10 to jeden dzień w UTC, ale w Warszawie (CEST, +2) 23:30 dnia 12. i 00:30 dnia 13. — dwa dni.
        var s = new List<RetentionSnapshot>
        {
            new("after-midnight", new DateTimeOffset(2026, 10, 12, 22, 30, 0, TimeSpan.Zero)),
            new("before-midnight", new DateTimeOffset(2026, 10, 12, 21, 30, 0, TimeSpan.Zero)),
        };
        Assert.Equal(["after-midnight"], RetentionPlanner.Plan(s, new RetentionPolicy(KeepDaily: 5), Now, TimeZoneInfo.Utc).Keep.Keys);
        Assert.Equal(2, RetentionPlanner.Plan(s, new RetentionPolicy(KeepDaily: 5), Now, Warsaw).Keep.Count);
    }

    [Fact, Trait("Plan", "D3.12")]
    public void Always_keeps_newest_suspect_and_base_of_kept_differential_and_everything_when_paused_or_no_rule()
    {
        var s = new List<RetentionSnapshot>
        {
            new("diff", Now.AddDays(-1), Differential: true, BaseFull: "full"),
            new("bad", Now.AddDays(-2), Suspect: true),
            new("mid", Now.AddDays(-3)),
            new("full", Now.AddDays(-40)),
        };
        var plan = RetentionPlanner.Plan(s, new RetentionPolicy(KeepLast: 1), Now, TimeZoneInfo.Utc);
        Assert.Equal(KeepReason.Last | KeepReason.Newest, plan.Keep["diff"]);
        Assert.Equal(KeepReason.BaseOfDifferential, plan.Keep["full"]);
        Assert.Equal(KeepReason.Suspect, plan.Keep["bad"]); // podejrzany zostaje do decyzji człowieka (D9.3)
        Assert.Equal(["mid"], plan.Forget);

        Assert.Empty(RetentionPlanner.Plan(s, new RetentionPolicy(), Now, TimeZoneInfo.Utc).Forget); // brak reguł = nic nie zapominamy
        Assert.Empty(RetentionPlanner.Plan(s, new RetentionPolicy(KeepLast: 1), Now, TimeZoneInfo.Utc, paused: true).Forget);
        Assert.Empty(RetentionPlanner.Plan([], new RetentionPolicy(KeepLast: 1), Now, TimeZoneInfo.Utc).Keep);
    }

    [Fact, Trait("Plan", "SEC-17.4")]
    public void Clock_set_back_does_not_strip_lock_from_copies_newer_than_the_clock()
    {
        var s = Daily(10, Now);
        var plan = RetentionPlanner.Plan(s, new RetentionPolicy(KeepLast: 1, RetentionLock: TimeSpan.FromDays(3)), Now.AddDays(-30), TimeZoneInfo.Utc);
        Assert.Equal(["s000", "s001", "s002"], plan.Keep.Keys.Order(StringComparer.Ordinal)); // wiek liczony od najnowszej kopii, nie od cofniętego zegara
    }

    [Fact, Trait("AC", "AC-10.1"), Trait("Plan", "D3.12")]
    public void Properties_hold_on_random_histories()
    {
        var rng = new Random(20261014);
        for (var round = 0; round < 400; round++)
        {
            var n = rng.Next(1, 120);
            var s = Enumerable.Range(0, n).Select(i => new RetentionSnapshot($"x{i:D4}", Now.AddMinutes(-rng.Next(0, 60 * 24 * 800)),
                Suspect: rng.Next(50) == 0)).ToList();
            var p = new RetentionPolicy(rng.Next(0, 5), rng.Next(0, 15), rng.Next(0, 6), rng.Next(0, 13), rng.Next(0, 3),
                TimeSpan.FromDays(rng.Next(0, 3)), TimeSpan.FromDays(rng.Next(0, 4)));
            var plan = RetentionPlanner.Plan(s, p, Now, Warsaw);
            var newest = s.OrderByDescending(x => x.TimeUtc).ThenByDescending(x => x.Id, StringComparer.Ordinal).First();
            Assert.Contains(newest.Id, plan.Keep.Keys); // nigdy nie usuwa ostatniego udanego
            Assert.All(s.Where(x => Now - x.TimeUtc < p.RetentionLock), x => Assert.Contains(x.Id, plan.Keep.Keys)); // ani młodszego niż lock
            Assert.All(s.Where(x => x.Suspect), x => Assert.Contains(x.Id, plan.Keep.Keys));
            Assert.Equal(s.Count, plan.Keep.Count + plan.Forget.Count);
            Assert.Empty(plan.Forget.Intersect(plan.Keep.Keys));
            var again = RetentionPlanner.Plan([.. s.Where(x => plan.Keep.ContainsKey(x.Id))], p, Now, Warsaw);
            Assert.Empty(again.Forget); // idempotentność: druga retencja nic nie dokłada
        }
    }

    [Fact, Trait("NFR", "NFR-25"), Trait("Category", "Perf")]
    public void Keep_for_5000_snapshots_is_fast()
    {
        var s = Enumerable.Range(0, 5000).Select(i => new RetentionSnapshot($"p{i:D5}", Now.AddHours(-3 * i))).ToList();
        var p = new RetentionPolicy(10, 30, 12, 24, 5, TimeSpan.FromDays(2), TimeSpan.FromDays(7));
        RetentionPlanner.Plan(s, p, Now, Warsaw); // rozgrzewka JIT
        var sw = Stopwatch.StartNew();
        var plan = RetentionPlanner.Plan(s, p, Now, Warsaw);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Keep(5000) = {sw.Elapsed.TotalMilliseconds} ms"); // NFR-25 (środowisko CI, nie sprzęt referencyjny)
        Assert.True(plan.Keep.Count is > 30 and < 200);
    }
}
