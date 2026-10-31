using Kobian.Application.Jobs;
using Kobian.Domain;

namespace Kobian.Scheduler.Runs;

/// <summary>Jedno uruchomienie (wiersz run_runs): przyczyna, numer próby, stan końcowy, kod KB, snapshot.</summary>
public sealed record RunRecord(int Attempt, string Cause, RunState State, string? Code, string? SnapshotId);

/// <summary>
/// Wykonawca uruchomienia (BL-42, D7.8): prowadzi uruchomienie przez <see cref="RunStateMachine"/> — Queued → Running → wynik JobRunnera
/// → ewentualne ponowienia (RetryDue, tylko błąd przejściowy, limit prób). Każde ponowienie = nowe uruchomienie (cause=retry).
/// Wyjątek JobRunnera = Failed z KB-0001 (nie przejściowy); anulowanie = Cancelled. Stan nigdy nie jest „poprawiany” na Succeeded.
/// Blokady, okna i katalog — wywołujący (Agent); tu tylko logika przejść, testowalna bez I/O.
/// </summary>
public sealed class RunExecutor(IJobRunner runner, Func<int, CancellationToken, Task>? retryDelay = null)
{
    private readonly IJobRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<IReadOnlyList<RunRecord>> ExecuteAsync(string jobId, RunConditions conditions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        var records = new List<RunRecord>();
        var cause = "schedule";
        for (var attempt = 0; ; attempt++)
        {
            var state = RunState.None;
            state = Step(state, RunEventKind.Occurrence, conditions);
            if (state != RunState.Queued) { records.Add(new RunRecord(attempt, cause, RunState.Skipped, null, null)); return records; }
            state = Step(state, RunEventKind.LocksAcquired, conditions);
            if (state != RunState.Running) { records.Add(new RunRecord(attempt, cause, state, null, null)); return records; }

            JobOutcome outcome;
            try
            {
                outcome = await _runner.RunAsync(jobId, attempt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                state = Step(state, RunEventKind.CancelRequested, conditions with { CallerIsOperatorOrAbove = true });
                records.Add(new RunRecord(attempt, cause, state, null, null));
                return records;
            }
#pragma warning disable CA1031 // granica wykonawcy: każdy nieoczekiwany wyjątek = Failed (Internal), nie awaria schedulera
            catch (Exception)
#pragma warning restore CA1031
            {
                outcome = new JobOutcome(JobResult.Failed, KbCode.Internal, false, null);
            }

            var completed = outcome.Result switch
            {
                JobResult.Succeeded => RunState.Succeeded,
                JobResult.Warning => RunState.Warning,
                _ => RunState.Failed,
            };
            state = Step(state, RunEventKind.Completed, conditions with { CompletedStatus = completed });
            records.Add(new RunRecord(attempt, cause, state, outcome.Code, outcome.SnapshotId));
            if (state != RunState.Failed) return records;

            var retry = RunStateMachine.Apply(state, RunEventKind.RetryDue, conditions with { LastErrorTransient = outcome.Transient, Attempt = attempt });
            if (!retry.CreatesNewRun) return records;
            cause = "retry";
            if (retryDelay is not null) await retryDelay(attempt + 1, ct).ConfigureAwait(false);
        }
    }

    private static RunState Step(RunState from, RunEventKind e, RunConditions c)
    {
        var t = RunStateMachine.Apply(from, e, c);
        return t.To;
    }
}
