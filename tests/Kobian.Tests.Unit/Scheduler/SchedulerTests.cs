using Kobian.Domain;
using Kobian.Scheduler.Locks;
using Kobian.Scheduler.Runs;
using Kobian.Scheduler.Timing;
using Xunit;

namespace Kobian.Tests.Unit.Scheduler;

public sealed class RunStateMachineTests
{
    private static readonly RunConditions C = new();

    [Theory]
    [InlineData(RunState.None, RunEventKind.Occurrence, RunState.Queued)]
    [InlineData(RunState.None, RunEventKind.OccurrencePassed, RunState.Missed)]
    [InlineData(RunState.Queued, RunEventKind.LocksAcquired, RunState.Running)]
    [InlineData(RunState.Queued, RunEventKind.WindowClosed, RunState.Skipped)]
    [InlineData(RunState.Queued, RunEventKind.QueueTimeout, RunState.Skipped)]
    [InlineData(RunState.Queued, RunEventKind.PreviousStillRunning, RunState.Skipped)]
    [InlineData(RunState.Running, RunEventKind.WindowEnd, RunState.Paused)]
    [InlineData(RunState.Paused, RunEventKind.WindowOpen, RunState.Running)]
    [InlineData(RunState.Running, RunEventKind.Completed, RunState.Succeeded)]
    [InlineData(RunState.Running, RunEventKind.CancelRequested, RunState.Cancelled)]
    [InlineData(RunState.Running, RunEventKind.ServiceStopping, RunState.Interrupted)]
    [InlineData(RunState.Paused, RunEventKind.ProcessCrashDetected, RunState.Interrupted)]
    [InlineData(RunState.Running, RunEventKind.JobDeleted, RunState.Cancelled)]
    public void Table_rows_D7_8(RunState from, RunEventKind e, RunState to) =>
        Assert.Equal(to, RunStateMachine.Apply(from, e, C).To);

    [Theory, Trait("NFR", "NFR-10")]
    [InlineData(RunState.Succeeded, RunEventKind.LocksAcquired)]
    [InlineData(RunState.Skipped, RunEventKind.Completed)]
    [InlineData(RunState.Queued, RunEventKind.WindowOpen)]
    public void Pairs_outside_table_fail_with_critical(RunState from, RunEventKind e)
    {
        var t = RunStateMachine.Apply(from, e, C);
        Assert.Equal(RunState.Failed, t.To);
        Assert.Equal(KbCode.IllegalTransition, t.KbCode);
        Assert.Contains(t.Actions, a => a.Kind == RunActionKind.RecordCritical);
    }

    [Fact]
    public void Locks_acquired_outside_window_keeps_queued()
    {
        Assert.Equal(RunState.Queued, RunStateMachine.Apply(RunState.Queued, RunEventKind.LocksAcquired, C with { WindowOpen = false }).To);
    }

    [Fact]
    public void Retry_only_for_transient_errors_below_limit()
    {
        Assert.True(RunStateMachine.Apply(RunState.Failed, RunEventKind.RetryDue, C with { LastErrorTransient = true, Attempt = 1 }).CreatesNewRun);
        Assert.False(RunStateMachine.Apply(RunState.Failed, RunEventKind.RetryDue, C with { LastErrorTransient = true, Attempt = 3 }).CreatesNewRun);
        Assert.False(RunStateMachine.Apply(RunState.Failed, RunEventKind.RetryDue, C with { LastErrorTransient = false }).CreatesNewRun);
    }

    [Theory]
    [InlineData(MisfirePolicyKind.RunOnceIfMissed, 1000, 0, true)]
    [InlineData(MisfirePolicyKind.Skip, 1, 0, false)]
    [InlineData(MisfirePolicyKind.RunIfMissedWithin, 30, 60, true)]
    [InlineData(MisfirePolicyKind.RunIfMissedWithin, 90, 60, false)]
    public void Misfire_policies(MisfirePolicyKind p, int latenessMin, int withinMin, bool runs)
    {
        var t = RunStateMachine.Apply(RunState.Missed, RunEventKind.MisfirePolicy,
            C with { Misfire = p, Lateness = TimeSpan.FromMinutes(latenessMin), MisfireWithin = TimeSpan.FromMinutes(withinMin) });
        Assert.Equal(runs, t.CreatesNewRun);
    }

    [Fact, Trait("NFR", "NFR-10")]
    public void Random_sequences_from_running_always_reach_a_defined_state()
    {
        var events = Enum.GetValues<RunEventKind>();
        var rnd = new Random(1234);
        for (var i = 0; i < 5000; i++)
        {
            var s = RunState.Queued;
            for (var k = 0; k < 20 && !RunStateMachine.IsTerminal(s); k++)
            {
                var t = RunStateMachine.Apply(s, events[rnd.Next(events.Length)], C with { CompletedStatus = (RunState)rnd.Next((int)RunState.Succeeded, (int)RunState.Failed + 1) });
                Assert.True(Enum.IsDefined(t.To));
                if (t.KbCode == KbCode.IllegalTransition) Assert.Equal(RunState.Failed, t.To);
                s = t.To;
            }
        }
    }
}

public sealed class CronScheduleTests
{
    private static DateTimeOffset Utc(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact, Trait("E", "E1")]
    public void Spring_forward_gap_runs_once_at_first_valid_instant()
    {
        var c = CronSchedule.Parse("30 2 * * *", "Europe/Warsaw");
        // 29.03.2026: 02:00 → 03:00 CEST. 02:30 nie istnieje → 03:00 lokalnie = 01:00 UTC.
        var next = c.NextAfter(Utc("2026-03-28T23:00:00Z"));
        Assert.Equal(Utc("2026-03-29T01:00:00Z"), next);
        Assert.Single(c.Between(Utc("2026-03-28T22:00:00Z"), Utc("2026-03-29T12:00:00Z")));
    }

    [Fact, Trait("E", "E2")]
    public void Fall_back_overlap_runs_once()
    {
        var c = CronSchedule.Parse("30 2 * * *", "Europe/Warsaw");
        // 25.10.2026: 03:00 CEST → 02:00 CET; 02:30 występuje dwukrotnie → jedno uruchomienie (pierwsze, 00:30 UTC).
        var occ = c.Between(Utc("2026-10-24T22:00:00Z"), Utc("2026-10-25T12:00:00Z"));
        Assert.Equal([Utc("2026-10-25T00:30:00Z")], occ);
    }

    [Fact, Trait("AC", "AC-03.1"), Trait("E", "E3")]
    public void Three_days_off_gives_three_missed_and_one_run_5_to_10_min_after_start()
    {
        var c = CronSchedule.Parse("0 22 * * *", "Europe/Warsaw");
        var lastHandled = Utc("2026-10-05T20:00:00Z"); // 22:00 CEST 5.10
        var now = Utc("2026-10-09T08:00:00Z");
        var plan = MisfirePlanner.Plan(c, lastHandled, now, MisfirePolicyKind.RunOnceIfMissed, TimeSpan.Zero, (min, max) => min + (max - min) / 2);
        Assert.Equal(3, plan.Missed.Count);
        Assert.NotNull(plan.RunAt);
        Assert.InRange(plan.RunAt!.Value - now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        Assert.Equal(Utc("2026-10-09T20:00:00Z"), plan.NextFire);
    }

    [Fact]
    public void Skip_policy_produces_no_run()
    {
        var c = CronSchedule.Parse("0 22 * * *", "Europe/Warsaw");
        var plan = MisfirePlanner.Plan(c, Utc("2026-10-05T20:00:00Z"), Utc("2026-10-09T08:00:00Z"), MisfirePolicyKind.Skip, TimeSpan.Zero, (a, _) => a);
        Assert.Null(plan.RunAt);
    }

    [Fact, Trait("AC", "AC-03.3")]
    public void Preview_returns_five_occurrences_for_business_hours_expression()
    {
        var c = CronSchedule.Parse("*/30 7-20 * * 1-5", "Europe/Warsaw");
        var p = c.Preview(Utc("2026-10-09T18:10:00Z")); // piątek 20:10 CEST
        Assert.Equal(5, p.Count);
        Assert.Equal(Utc("2026-10-09T18:30:00Z"), p[0]); // 20:30
        Assert.Equal(Utc("2026-10-12T05:00:00Z"), p[1]); // poniedziałek 07:00
    }

    [Theory, Trait("AC", "AC-03.3")]
    [InlineData("61 * * * *", "Europe/Warsaw")]
    [InlineData("* * *", "Europe/Warsaw")]
    [InlineData("0 22 * * *", "Mars/Olympus")]
    public void Invalid_expression_or_zone_is_rejected(string expr, string tz) =>
        Assert.Equal(KbCode.ScheduleInvalid, Assert.Throws<KobianException>(() => CronSchedule.Parse(expr, tz)).Code);

    [Fact]
    public void Minimum_interval_rule()
    {
        Assert.False(CronSchedule.Parse("* * * * *", "UTC").RespectsMinimumInterval(Utc("2026-10-12T00:00:00Z")));
        Assert.True(CronSchedule.Parse("*/5 * * * *", "UTC").RespectsMinimumInterval(Utc("2026-10-12T00:00:00Z")));
    }
}

public sealed class LockManagerTests
{
    [Fact, Trait("E", "E6")]
    public void Two_accounts_to_same_smb_server_are_serialized()
    {
        var m = new LockManager(4);
        Assert.True(m.TryAcquireAll("r1", [new("smb:nas", LockMode.Shared, "biuro\\a")]));
        Assert.False(m.TryAcquireAll("r2", [new("smb:nas", LockMode.Shared, "biuro\\b")]));
        Assert.True(m.TryAcquireAll("r3", [new("smb:nas", LockMode.Shared, "BIURO\\A")]));
        m.Release("r1");
        m.Release("r3");
        Assert.True(m.TryAcquireAll("r2", [new("smb:nas", LockMode.Shared, "biuro\\b")]));
    }

    [Fact]
    public void All_or_nothing_does_not_hold_partial_locks()
    {
        var m = new LockManager(4);
        Assert.True(m.TryAcquireAll("r1", [new("db:sql:erp", LockMode.Exclusive)]));
        Assert.False(m.TryAcquireAll("r2", [new("repo:x", LockMode.Exclusive), new("db:sql:erp", LockMode.Exclusive)]));
        Assert.True(m.TryAcquireAll("r3", [new("repo:x", LockMode.Exclusive)])); // r2 niczego nie zatrzymał
    }

    [Fact]
    public void Global_semaphore_limits_concurrency_and_is_restored()
    {
        var m = new LockManager(1);
        Assert.True(m.TryAcquireAll("r1", [new("job:a", LockMode.Exclusive)]));
        Assert.False(m.TryAcquireAll("r2", [new("job:b", LockMode.Exclusive)]));
        m.Release("r1");
        Assert.Equal(1, m.GlobalFree);
    }

    [Fact, Trait("E", "E10")]
    public void Pause_keeps_job_lock_and_resume_reacquires_rest()
    {
        var m = new LockManager(2);
        Assert.True(m.TryAcquireAll("r1", [new("job:a", LockMode.Exclusive), new("repo:x", LockMode.Shared)]));
        m.Release("r1", keepJobLock: true);
        Assert.Equal(2, m.GlobalFree);
        Assert.False(m.TryAcquireAll("r2", [new("job:a", LockMode.Exclusive)])); // drugi run tego zadania czeka
        Assert.True(m.TryAcquireAll("r3", [new("repo:x", LockMode.Exclusive)]));
        Assert.False(m.TryAcquireAll("r1", [new("job:a", LockMode.Exclusive), new("repo:x", LockMode.Shared)]));
        m.Release("r3");
        Assert.True(m.TryAcquireAll("r1", [new("job:a", LockMode.Exclusive), new("repo:x", LockMode.Shared)]));
        m.Release("r1");
        Assert.Equal(2, m.GlobalFree);
        Assert.True(m.TryAcquireAll("r2", [new("job:a", LockMode.Exclusive)]));
    }

    [Fact]
    public void Concurrent_random_acquire_release_never_violates_exclusivity()
    {
        var m = new LockManager(3);
        var keys = new[] { "repo:a", "repo:b", "db:x", "job:1", "job:2" };
        var owners = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var violations = 0;
        Parallel.For(0, 4000, i =>
        {
            var rnd = new Random(i);
            var k = keys[rnd.Next(keys.Length)];
            var run = "r" + i;
            if (m.TryAcquireAll(run, [new(k, LockMode.Exclusive)]))
            {
                if (!owners.TryAdd(k, run)) Interlocked.Increment(ref violations);
                Thread.SpinWait(50);
                owners.TryRemove(k, out _);
                m.Release(run);
            }
        });
        Assert.Equal(0, violations);
        Assert.Equal(3, m.GlobalFree);
    }

    [Fact]
    public void Aging_raises_priority_every_30_minutes() =>
        Assert.Equal(1, LockManager.EffectivePriority(0, TimeSpan.FromMinutes(31)));
}
