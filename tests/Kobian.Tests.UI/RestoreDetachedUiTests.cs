using Kobian.UI.History;
using Kobian.UI.Progress;
using Kobian.UI.Restore;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>RD-81 cz. 3 (SCR-07): „Odtwórz” startuje odtwarzanie w tle i śledzi je przez WatchRun; wynik końcowy zamieniany na wynik ekranu.</summary>
public sealed class RestoreDetachedUiTests
{
    private static readonly SnapshotEntry Snap = FakeHistorySource.Sample()[0];
    private static readonly string Abs = OperatingSystem.IsWindows() ? @"D:\Odtworzone" : "/srv/odtworzone";

    private sealed class DetachedService(DetachedRestoreStart? start) : IRestoreService
    {
        public int DetachedCalls { get; private set; }

        public int BlockingCalls { get; private set; }

        public Task<DetachedRestoreStart?> StartDetachedAsync(string snapshotId, string? exactPath, string target, CancellationToken ct)
        {
            DetachedCalls++;
            return Task.FromResult(start);
        }

        public Task<RestoreRunResult> RestoreAsync(string snapshotId, string? exactPath, string target, CancellationToken ct)
        {
            BlockingCalls++;
            return Task.FromResult(new RestoreRunResult(RestoreRunState.Succeeded, null));
        }
    }

    private sealed class Source(params RunProgressUpdate[] items) : IRunProgressSource
    {
        public bool Hang { get; init; }

        public async IAsyncEnumerable<RunProgressUpdate> WatchAsync(string runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var u in items) { await Task.Yield(); yield return u; }
            if (Hang) await Task.Delay(Timeout.Infinite, ct);
        }
    }

    private static RunProgressUpdate Final(RunOutcomeKind r, string? code = null) =>
        new("r1", "Pliki", "restore", r == RunOutcomeKind.Failed ? "Failed" : "Succeeded", r, code, 3, null, 1024, true);

    private static RestoreViewModel Vm(IRestoreService svc, IRunProgressSource? src) =>
        new(svc, new RestoreSelection(Snap, "Faktury/2026", true), TimeZoneInfo.Utc, progressSource: src) { Target = Abs };

    [Theory, Trait("RD", "RD-81")]
    [InlineData(RunOutcomeKind.Ok, null, RestoreRunState.Succeeded)]
    [InlineData(RunOutcomeKind.Warning, "KB-5105", RestoreRunState.Warning)]
    [InlineData(RunOutcomeKind.Failed, "KB-5201", RestoreRunState.Failed)]
    public async Task Detached_restore_is_watched_and_final_state_becomes_the_result(RunOutcomeKind kind, string? code, RestoreRunState expected)
    {
        var svc = new DetachedService(new("r1", null));
        var vm = Vm(svc, new Source(Final(kind, code)));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsDone);
        Assert.Equal(expected, vm.Result!.State);
        Assert.Equal(code, vm.Result.Code);
        Assert.Equal(1, svc.DetachedCalls);
        Assert.Equal(0, svc.BlockingCalls); // nic nie jest odtwarzane drugi raz
        Assert.False(vm.HasRunProgress);
    }

    [Fact, Trait("RD", "RD-81")]
    public async Task Start_failure_is_shown_without_watching()
    {
        var svc = new DetachedService(new(null, new RestoreRunResult(RestoreRunState.Failed, "KB-5001")));
        var vm = Vm(svc, new Source());
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsFailure);
        Assert.Equal("KB-5001", vm.Result!.Code);
        Assert.Equal(0, svc.BlockingCalls);
    }

    [Fact, Trait("RD", "RD-81")]
    public async Task Service_without_detached_mode_falls_back_to_the_blocking_restore()
    {
        var svc = new DetachedService(null);
        var vm = Vm(svc, new Source());
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsSuccess);
        Assert.Equal(1, svc.DetachedCalls);
        Assert.Equal(1, svc.BlockingCalls);
    }

    [Fact, Trait("RD", "RD-81")]
    public async Task Without_a_progress_source_the_blocking_restore_is_used()
    {
        var svc = new DetachedService(new("r1", null));
        var vm = Vm(svc, null);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsSuccess);
        Assert.Equal(0, svc.DetachedCalls);
        Assert.Equal(1, svc.BlockingCalls);
    }

    [Fact, Trait("RD", "RD-81")]
    public async Task Stopping_the_watch_returns_to_the_form_and_does_not_report_a_result()
    {
        var svc = new DetachedService(new("r1", null));
        var vm = Vm(svc, new Source(new RunProgressUpdate("r1", "Pliki", "restore", "Running", RunOutcomeKind.Unknown, null, 1, null, 10, false)) { Hang = true });
        var start = vm.StartCommand.ExecuteAsync(null);
        while (vm.RunProgress is not { Stage: ProgressStage.Running }) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(vm.IsRunning);
        Assert.True(vm.HasRunProgress);
        vm.RunProgress!.StopWatchingCommand.Execute(null);
        await start;
        Assert.True(vm.IsForm);
        Assert.Null(vm.Result);
        Assert.False(vm.HasRunProgress);
    }
}
