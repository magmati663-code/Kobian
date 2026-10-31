using Kobian.Domain;

namespace Kobian.Scheduler.Runs;

/// <summary>Stany uruchomienia (7.1, D7.8). Terminalne: Succeeded, Warning, Failed, Cancelled, Skipped (Interrupted/Missed — pośrednie).</summary>
public enum RunState
{
    None,
    Missed,
    Queued,
    Running,
    Paused,
    Interrupted,
    Succeeded,
    Warning,
    Failed,
    Cancelled,
    Skipped,
}

public enum RunEventKind
{
    Occurrence,
    OccurrencePassed,
    MisfirePolicy,
    LocksAcquired,
    WindowClosed,
    QueueTimeout,
    PreviousStillRunning,
    WindowEnd,
    WindowOpen,
    Completed,
    CancelRequested,
    ServiceStopping,
    ProcessCrashDetected,
    Resume,
    RetryDue,
    JobDeleted,
}

public enum MisfirePolicyKind
{
    RunOnceIfMissed,
    Skip,
    RunIfMissedWithin,
}

/// <summary>Warunki z kolumny „Warunek” D7.8 — ustalane przez wywołującego (I/O poza funkcją).</summary>
public sealed record RunConditions
{
    public bool JobEnabled { get; init; } = true;
    public bool JobExists { get; init; } = true;
    public bool WindowOpen { get; init; } = true;
    public bool PowerAndNetworkOk { get; init; } = true;
    public bool PauseAtPackBoundaryOnWindowEnd { get; init; } = true;
    public bool CallerIsOperatorOrAbove { get; init; } = true;
    public MisfirePolicyKind Misfire { get; init; } = MisfirePolicyKind.RunOnceIfMissed;
    public bool IsLastMissedOccurrence { get; init; } = true;
    public TimeSpan Lateness { get; init; }
    public TimeSpan MisfireWithin { get; init; }
    public bool LastErrorTransient { get; init; }
    public int Attempt { get; init; }
    public int RetryLimit { get; init; } = 3;
    /// <summary>Dla Completed: wynik JobRunnera (Succeeded/Warning/Failed).</summary>
    public RunState CompletedStatus { get; init; } = RunState.Succeeded;
}

public enum RunActionKind
{
    InsertRun,
    InsertNewRun,
    ScheduleWithDelay,
    StartJobRunner,
    ClosePackReleaseNonJobLocks,
    ResumeFromJournal,
    PublishCompletion,
    CancelToken,
    RecordCritical,
}

public sealed record RunAction(RunActionKind Kind, string? Cause = null, TimeSpan? MinDelay = null, TimeSpan? MaxDelay = null);

public sealed record RunTransition(RunState To, string? KbCode, IReadOnlyList<RunAction> Actions)
{
    /// <summary>Czy stan docelowy jest stanem nowego uruchomienia (Queued z cause=misfire/resume/retry).</summary>
    public bool CreatesNewRun => Actions.Any(a => a.Kind == RunActionKind.InsertNewRun);
}

/// <summary>
/// Czysta funkcja przejść D7.8: brak I/O; para spoza tabeli → Failed + KB-4902 + zdarzenie Critical (NFR-10).
/// </summary>
public static class RunStateMachine
{
    public static readonly TimeSpan MisfireMinDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MisfireMaxDelay = TimeSpan.FromMinutes(10);

    public static bool IsTerminal(RunState s) => s is RunState.Succeeded or RunState.Warning or RunState.Failed or RunState.Cancelled or RunState.Skipped;

    public static RunTransition Apply(RunState from, RunEventKind e, RunConditions c)
    {
        ArgumentNullException.ThrowIfNull(c);
        RunTransition T(RunState to, string? kb = null, params RunAction[] a) => new(to, kb, a);

        switch (from, e)
        {
            case (RunState.None, RunEventKind.Occurrence) when c.JobEnabled:
                return T(RunState.Queued, null, new RunAction(RunActionKind.InsertRun, "schedule"));
            case (RunState.None, RunEventKind.OccurrencePassed):
                return T(RunState.Missed, null, new RunAction(RunActionKind.InsertRun, "schedule"));
            case (RunState.Missed, RunEventKind.MisfirePolicy):
                var run = c.Misfire switch
                {
                    MisfirePolicyKind.RunOnceIfMissed => c.IsLastMissedOccurrence,
                    MisfirePolicyKind.RunIfMissedWithin => c.IsLastMissedOccurrence && c.Lateness <= c.MisfireWithin,
                    _ => false,
                };
                // Missed pozostaje w historii; nowe uruchomienie (cause=misfire) z opóźnieniem 5 min + jitter 0–5 min.
                return run
                    ? T(RunState.Missed, null, new RunAction(RunActionKind.InsertNewRun, "misfire"), new RunAction(RunActionKind.ScheduleWithDelay, null, MisfireMinDelay, MisfireMaxDelay))
                    : T(RunState.Skipped);
            case (RunState.Queued, RunEventKind.LocksAcquired) when c.WindowOpen && c.PowerAndNetworkOk:
                return T(RunState.Running, null, new RunAction(RunActionKind.StartJobRunner));
            case (RunState.Queued, RunEventKind.WindowClosed):
                return T(RunState.Skipped, KbCode.OutsideWindow);
            case (RunState.Queued, RunEventKind.QueueTimeout):
                return T(RunState.Skipped, KbCode.QueueTimeout);
            case (RunState.Queued, RunEventKind.PreviousStillRunning):
                return T(RunState.Skipped, KbCode.PreviousRunActive);
            case (RunState.Running, RunEventKind.WindowEnd) when c.PauseAtPackBoundaryOnWindowEnd:
                return T(RunState.Paused, null, new RunAction(RunActionKind.ClosePackReleaseNonJobLocks));
            case (RunState.Paused, RunEventKind.WindowOpen):
                return T(RunState.Running, null, new RunAction(RunActionKind.ResumeFromJournal));
            case (RunState.Running, RunEventKind.Completed) when c.CompletedStatus is RunState.Succeeded or RunState.Warning or RunState.Failed:
                return T(c.CompletedStatus, null, new RunAction(RunActionKind.PublishCompletion));
            case (RunState.Running, RunEventKind.CancelRequested) when c.CallerIsOperatorOrAbove:
                return T(RunState.Cancelled, null, new RunAction(RunActionKind.CancelToken));
            case (RunState.Running or RunState.Paused, RunEventKind.ServiceStopping or RunEventKind.ProcessCrashDetected):
                return T(RunState.Interrupted);
            case (RunState.Interrupted, RunEventKind.Resume) when c.JobExists && c.JobEnabled:
                return T(RunState.Interrupted, null, new RunAction(RunActionKind.InsertNewRun, "resume"), new RunAction(RunActionKind.ResumeFromJournal));
            case (RunState.Interrupted, RunEventKind.Resume):
                return T(RunState.Cancelled);
            case (RunState.Failed, RunEventKind.RetryDue) when c.LastErrorTransient && c.Attempt < c.RetryLimit:
                return T(RunState.Failed, null, new RunAction(RunActionKind.InsertNewRun, "retry"));
            case (RunState.Queued or RunState.Running or RunState.Paused, RunEventKind.JobDeleted):
                return T(RunState.Cancelled, null, new RunAction(RunActionKind.CancelToken));
            default:
                // Warunek niespełniony dla dozwolonej pary → brak zmiany; para spoza tabeli → błąd krytyczny.
                return IsListedPair(from, e) ? T(from) : T(RunState.Failed, KbCode.IllegalTransition, new RunAction(RunActionKind.RecordCritical));
        }
    }

    private static bool IsListedPair(RunState s, RunEventKind e) => (s, e) switch
    {
        (RunState.None, RunEventKind.Occurrence) => true,
        (RunState.Queued, RunEventKind.LocksAcquired) => true,
        (RunState.Running, RunEventKind.WindowEnd) => true,
        (RunState.Running, RunEventKind.Completed) => true,
        (RunState.Running, RunEventKind.CancelRequested) => true,
        (RunState.Failed, RunEventKind.RetryDue) => true,
        _ => false,
    };
}
