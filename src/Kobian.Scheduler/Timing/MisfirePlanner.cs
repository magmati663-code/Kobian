using Kobian.Scheduler.Runs;

namespace Kobian.Scheduler.Timing;

/// <summary>Plan po starcie usługi lub wybudzeniu (D7.9 OnStart): wpisy Missed + co najwyżej jedno uruchomienie wg polityki.</summary>
public sealed record MisfirePlan(IReadOnlyList<DateTimeOffset> Missed, DateTimeOffset? RunAt, DateTimeOffset? NextFire);

public static class MisfirePlanner
{
    /// <summary>
    /// <paramref name="lastHandled"/> = MAX(scheduled_for_utc) z run_runs dla wyzwalacza. Jitter wstrzykiwany (deterministyczne testy).
    /// AC-03.1: 3 dni wyłączenia, RunOnceIfMissed → 3 × Missed, 1 uruchomienie 5–10 min po starcie.
    /// </summary>
    public static MisfirePlan Plan(CronSchedule schedule, DateTimeOffset lastHandled, DateTimeOffset nowUtc, MisfirePolicyKind policy, TimeSpan within, Func<TimeSpan, TimeSpan, TimeSpan> jitter)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(jitter);
        var missed = schedule.Between(lastHandled, nowUtc);
        DateTimeOffset? runAt = null;
        if (missed.Count > 0)
        {
            var t = RunStateMachine.Apply(RunState.Missed, RunEventKind.MisfirePolicy, new RunConditions
            {
                Misfire = policy,
                IsLastMissedOccurrence = true,
                Lateness = nowUtc - missed[^1],
                MisfireWithin = within,
            });
            if (t.CreatesNewRun)
            {
                var d = t.Actions.Single(a => a.Kind == RunActionKind.ScheduleWithDelay);
                runAt = nowUtc + jitter(d.MinDelay!.Value, d.MaxDelay!.Value);
            }
        }
        return new MisfirePlan(missed, runAt, schedule.NextAfter(nowUtc));
    }

    /// <summary>Jitter produkcyjny: równomiernie w [min, max] (CSPRNG nie jest wymagany — brak znaczenia dla bezpieczeństwa).</summary>
    public static TimeSpan UniformJitter(TimeSpan min, TimeSpan max) =>
        min + TimeSpan.FromSeconds(Random.Shared.NextDouble() * (max - min).TotalSeconds);
}
