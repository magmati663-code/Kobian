using Kobian.Domain;
using Kobian.Scheduler.Runs;
using Kobian.Scheduler.Timing;
using Xunit;

namespace Kobian.Tests.Unit.Scheduler;

/// <summary>
/// [RD-5] Symulacja deterministyczna pętli D7.9 na wirtualnym zegarze: losowe wyłączenia, uśpienia i skoki zegara.
/// Niezmienniki: (I1) brak duplikatów wystąpień (KB-4901 = 0); (I2) każde wystąpienie z okresu pracy ma dokładnie jeden run;
/// (I3) po każdym starcie co najwyżej 1 uruchomienie misfire na wyzwalacz; (I4) skok w tył nie powtarza wystąpień.
/// </summary>
public sealed class SchedulerSimulationTests
{
    private sealed class VirtualTime(DateTimeOffset start) : TimeProvider
    {
        private long _mono;

        public DateTimeOffset Wall { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Wall;

        public override long GetTimestamp() => _mono;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>Upływ czasu rzeczywistego (oba zegary).</summary>
        public void Advance(TimeSpan d)
        {
            Wall += d;
            _mono += d.Ticks;
        }
    }

    private sealed class MemoryStore : ISchedulerStore
    {
        public readonly List<ScheduledRun> Runs = [];
        private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);
        private readonly HashSet<(string, DateTimeOffset)> _occ = [];

        public DateTimeOffset? LastHandled(string triggerId) => _last.TryGetValue(triggerId, out var v) ? v : null;

        public void SetLastHandled(string triggerId, DateTimeOffset utc) => _last[triggerId] = utc;

        public bool TryInsertRun(ScheduledRun run)
        {
            if (run.ScheduledForUtc is { } t && run.Cause is "schedule" && !_occ.Add((run.TriggerId, t))) return false;
            Runs.Add(run);
            return true;
        }
    }

    private static readonly DateTimeOffset Start = new(2026, 3, 20, 12, 0, 0, TimeSpan.Zero); // obejmuje zmianę czasu 29.03

    private static List<TimeTrigger> Triggers() =>
    [
        new("t-daily", "j1", CronSchedule.Parse("30 2 * * *", "Europe/Warsaw"), MisfirePolicyKind.RunOnceIfMissed, TimeSpan.Zero),
        new("t-hourly", "j2", CronSchedule.Parse("15 * * * 1-5", "Europe/Warsaw"), MisfirePolicyKind.Skip, TimeSpan.Zero),
        new("t-within", "j3", CronSchedule.Parse("0 22 * * *", "Europe/Warsaw"), MisfirePolicyKind.RunIfMissedWithin, TimeSpan.FromHours(6)),
    ];

    [Theory, Trait("RD", "RD-5"), Trait("E", "E1"), Trait("E", "E3"), Trait("E", "E5")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Random_power_and_clock_events_keep_invariants(int seed)
    {
        var rnd = new Random(seed);
        var time = new VirtualTime(Start);
        var store = new MemoryStore();
        var triggers = Triggers();
        var core = new SchedulerCore(triggers, store, time, (min, _) => min);
        core.OnStart();
        var poweredOn = new List<(DateTimeOffset From, DateTimeOffset To)>();
        var onSince = time.Wall;
        var forwardJumpWindows = new List<(DateTimeOffset From, DateTimeOffset To)>();
        var misfirePerStart = new List<int>();
        var end = Start.AddDays(20);

        while (time.Wall < end)
        {
            var roll = rnd.Next(1000);
            if (roll < 3)
            {
                // wyłączenie / uśpienie na 1 h – 3 dni, potem start (OnStart)
                poweredOn.Add((onSince, time.Wall));
                var before = store.Runs.Count(r => r.Cause == "misfire");
                time.Advance(TimeSpan.FromMinutes(rnd.Next(60, 3 * 24 * 60)));
                core = new SchedulerCore(triggers, store, time, (min, _) => min);
                core.OnStart();
                onSince = time.Wall;
                // misfire zaplanowane po starcie — zliczamy po 15 min działania
                for (var i = 0; i < 15; i++)
                {
                    time.Advance(TimeSpan.FromMinutes(1));
                    core.Tick();
                }
                misfirePerStart.Add(store.Runs.Count(r => r.Cause == "misfire") - before);
            }
            else if (roll < 5)
            {
                // skok zegara ściennego: w przód do 2 dni lub w tył do 1 dnia (monotoniczny bez zmian)
                var jump = rnd.Next(2) == 0 ? TimeSpan.FromMinutes(rnd.Next(5, 2 * 24 * 60)) : -TimeSpan.FromMinutes(rnd.Next(5, 24 * 60));
                poweredOn.Add((onSince, time.Wall));
                if (jump > TimeSpan.Zero) forwardJumpWindows.Add((time.Wall, time.Wall + jump));
                time.Wall += jump;
                onSince = time.Wall;
                core.Tick();
            }
            else
            {
                time.Advance(TimeSpan.FromMinutes(1));
                core.Tick();
            }
        }
        poweredOn.Add((onSince, time.Wall));

        // I1
        Assert.DoesNotContain(core.Diagnostics, d => d.KbCode == KbCode.OccurrenceDuplicate);
        // I2: każde wystąpienie z okresu pracy (z wyłączeniem okien przeskoczonych skokiem w przód i ostatniej minuty) ma run
        foreach (var t in triggers)
        {
            var scheduled = store.Runs.Where(r => r.TriggerId == t.TriggerId && r.ScheduledForUtc is not null).Select(r => r.ScheduledForUtc!.Value).ToHashSet();
            foreach (var (from, to) in poweredOn.Where(p => p.To - p.From > TimeSpan.FromMinutes(2)))
            {
                foreach (var occ in t.Schedule.Between(from.AddMinutes(1), to.AddMinutes(-1)))
                {
                    if (forwardJumpWindows.Any(w => occ > w.From && occ <= w.To)) continue;
                    if (store.Runs.Any(r => r.TriggerId == t.TriggerId && r.ScheduledForUtc > occ && r.CreatedAtUtc < occ)) continue; // po skoku w tył
                    Assert.Contains(occ, scheduled);
                }
            }
        }
        // I3
        Assert.All(misfirePerStart, n => Assert.InRange(n, 0, triggers.Count(t => t.Misfire != MisfirePolicyKind.Skip)));
    }

    [Fact, Trait("E", "E5")]
    public void Backward_clock_jump_does_not_repeat_occurrence()
    {
        var time = new VirtualTime(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        var store = new MemoryStore();
        var core = new SchedulerCore([new("t", "j", CronSchedule.Parse("0 * * * *", "UTC"), MisfirePolicyKind.Skip, TimeSpan.Zero)], store, time, (a, _) => a);
        core.OnStart();
        for (var i = 0; i < 180; i++) { time.Advance(TimeSpan.FromMinutes(1)); core.Tick(); }
        Assert.Equal(3, store.Runs.Count);
        time.Wall -= TimeSpan.FromHours(2); // ręczne cofnięcie zegara
        for (var i = 0; i < 180; i++) { time.Advance(TimeSpan.FromMinutes(1)); core.Tick(); }
        Assert.Contains(core.Diagnostics, d => d.KbCode == KbCode.ClockJump);
        Assert.Equal(store.Runs.Count, store.Runs.Select(r => r.ScheduledForUtc).Distinct().Count());
        Assert.Equal(4, store.Runs.Count); // 01:00, 02:00, 03:00 i po powrocie do 04:00 dopiero następne nowe
    }
}
