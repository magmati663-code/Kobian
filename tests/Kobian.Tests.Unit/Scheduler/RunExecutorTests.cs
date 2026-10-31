using Kobian.Application.Jobs;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Format.Metadata;
using Kobian.Scheduler.Runs;
using Xunit;

namespace Kobian.Tests.Unit.Scheduler;

/// <summary>BL-42 / D7.8 + D3.14: wynik potoku → stan uruchomienia, ponowienia tylko dla błędów przejściowych.</summary>
public sealed class RunExecutorTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class ScriptedRunner(params Func<JobOutcome>[] script) : IJobRunner
    {
        public int Calls { get; private set; }

        public Task<JobOutcome> RunAsync(string jobId, int attempt, CancellationToken ct) => Task.FromResult(script[Math.Min(Calls++, script.Length - 1)]());
    }

    private static readonly SnapshotStats Zero = new(0, 0, 0, 0, 0);

    [Fact, Trait("D", "D3.14"), Trait("AC", "AC-08.4")]
    public async Task Repo_write_failure_is_retried_until_success_each_attempt_a_new_run()
    {
        var failed = new BackupResult(BackupStatus.Failed, KbCode.RepoWriteFailed, null, Zero, 0, []).ToJobOutcome();
        var ok = new BackupResult(BackupStatus.Succeeded, null, new byte[16], Zero, 0, []).ToJobOutcome();
        Assert.True(failed.Transient);
        var runner = new ScriptedRunner(() => failed, () => failed, () => ok);
        var delays = new List<int>();
        var recs = await new RunExecutor(runner, (n, _) => { delays.Add(n); return Task.CompletedTask; }).ExecuteAsync("job", new RunConditions(), _ct);
        Assert.Equal([RunState.Failed, RunState.Failed, RunState.Succeeded], recs.Select(r => r.State));
        Assert.Equal(["schedule", "retry", "retry"], recs.Select(r => r.Cause));
        Assert.Equal(KbCode.RepoWriteFailed, recs[0].Code);
        Assert.Equal([1, 2], delays);
    }

    [Fact, Trait("D", "D7.8")]
    public async Task Retry_limit_stops_and_last_state_stays_failed()
    {
        var failed = new JobOutcome(JobResult.Failed, KbCode.RepoWriteFailed, true, null);
        var runner = new ScriptedRunner(() => failed);
        var recs = await new RunExecutor(runner).ExecuteAsync("job", new RunConditions { RetryLimit = 2 }, _ct);
        Assert.Equal(3, recs.Count); // próba 0 + 2 ponowienia
        Assert.All(recs, r => Assert.Equal(RunState.Failed, r.State));
    }

    [Theory, Trait("D", "D3.14")]
    [InlineData("KB-1403")]
    [InlineData("KB-1405")]
    public async Task Integrity_and_source_failures_are_not_retried(string code)
    {
        var outcome = new BackupResult(BackupStatus.Failed, code, null, Zero, 0, []).ToJobOutcome();
        Assert.False(outcome.Transient);
        var runner = new ScriptedRunner(() => outcome);
        var recs = await new RunExecutor(runner).ExecuteAsync("job", new RunConditions(), _ct);
        Assert.Single(recs);
        Assert.Equal(1, runner.Calls);
    }

    [Fact, Trait("D", "D3.14"), Trait("AC", "AC-11.1")]
    public async Task Warning_stays_warning_and_is_not_retried()
    {
        var w = new BackupResult(BackupStatus.Warning, KbCode.SourceUnreadable, new byte[16], Zero, 2, ["a", "b"]).ToJobOutcome();
        var recs = await new RunExecutor(new ScriptedRunner(() => w)).ExecuteAsync("job", new RunConditions(), _ct);
        var r = Assert.Single(recs);
        Assert.Equal(RunState.Warning, r.State);
        Assert.Equal(KbCode.SourceUnreadable, r.Code);
    }

    [Fact, Trait("D", "D7.8")]
    public async Task Runner_exception_is_failed_internal_and_disabled_job_is_skipped()
    {
        var recs = await new RunExecutor(new ScriptedRunner(() => throw new InvalidOperationException("boom"))).ExecuteAsync("job", new RunConditions(), _ct);
        Assert.Equal(RunState.Failed, Assert.Single(recs).State);
        Assert.Equal(KbCode.Internal, recs[0].Code);
        var skipped = await new RunExecutor(new ScriptedRunner(() => throw new InvalidOperationException())).ExecuteAsync("job", new RunConditions { JobEnabled = false }, _ct);
        Assert.Equal(RunState.Skipped, Assert.Single(skipped).State);
    }

    [Fact, Trait("D", "D7.8")]
    public async Task Cancellation_gives_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var runner = new CancellingRunner(cts);
        var recs = await new RunExecutor(runner).ExecuteAsync("job", new RunConditions(), cts.Token);
        Assert.Equal(RunState.Cancelled, Assert.Single(recs).State);
    }

    private sealed class CancellingRunner(CancellationTokenSource cts) : IJobRunner
    {
        public async Task<JobOutcome> RunAsync(string jobId, int attempt, CancellationToken ct)
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new JobOutcome(JobResult.Succeeded, null, false, null);
        }
    }
}
