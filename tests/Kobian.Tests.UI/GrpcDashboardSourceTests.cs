using Kobian.Api.V1;
using Kobian.UI.Dashboard;
using Kobian.UI.History;
using Kobian.UI.Jobs;
using Kobian.UI.Services;
using Xunit;
using UiRole = Kobian.UI.Dashboard.CallerRole;

namespace Kobian.Tests.UI;

/// <summary>Adapter SCR-01 → Agent (BL-14): mapowanie kontraktu i zachowanie bez usługi (R2.6.4).</summary>
public sealed class GrpcDashboardSourceTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Maps_status_and_latest_snapshot_per_job()
    {
        var status = new GetStatusResponse { State = OverallState.Failed, AgentVersion = "1.0" };
        status.Jobs.Add(new JobStatus { Name = "Dokumenty", LastResult = OverallState.Ok, LastRunUnixSeconds = Now.AddHours(-2).ToUnixTimeSeconds() });
        status.Jobs.Add(new JobStatus { Name = "ERP", LastResult = OverallState.Failed, LastRunUnixSeconds = Now.AddHours(-1).ToUnixTimeSeconds(), LastCode = "KB-2104" });
        status.Jobs.Add(new JobStatus { Name = "Nowe" });
        var snaps = new ListSnapshotsResponse();
        snaps.Snapshots.Add(new SnapshotInfo { Id = "a", Job = "Dokumenty", TimeUnixSeconds = Now.AddDays(-1).ToUnixTimeSeconds() });
        snaps.Snapshots.Add(new SnapshotInfo { Id = "b", Job = "Dokumenty", TimeUnixSeconds = Now.AddHours(-2).ToUnixTimeSeconds() });
        snaps.Snapshots.Add(new SnapshotInfo { Id = "c", Job = "ERP", TimeUnixSeconds = Now.AddDays(-4).ToUnixTimeSeconds() });

        var d = GrpcDashboardSource.Map(status, snaps, Now);
        var doc = d.Jobs.Single(j => j.Name == "Dokumenty");
        Assert.Equal(LastRunResult.Succeeded, doc.LastResult);
        Assert.Equal(Now.AddHours(-2), doc.LastSuccessUtc);
        Assert.Equal(2, doc.LastSuccessVerifyLevel);
        Assert.Equal(new JobProtection(ProtectionState.Protected, ProtectionReason.Ok), ProtectionEvaluator.Evaluate(doc, Now));

        var erp = d.Jobs.Single(j => j.Name == "ERP");
        Assert.Equal("KB-2104", erp.LastCode);
        Assert.Equal(ProtectionState.AtRisk, ProtectionEvaluator.Evaluate(erp, Now).State);

        var fresh = d.Jobs.Single(j => j.Name == "Nowe");
        Assert.Equal(LastRunResult.None, fresh.LastResult);
        Assert.Equal(new JobProtection(ProtectionState.AtRisk, ProtectionReason.NeverSucceeded), ProtectionEvaluator.Evaluate(fresh, Now));
        Assert.Empty(d.Destinations);
    }

    [Fact, Trait("BL", "BL-60")]
    public void Maps_contract_fields_without_fallbacks_when_agent_sends_them()
    {
        var status = new GetStatusResponse { State = OverallState.Failed, AgentVersion = "1.1", CallerRole = Api.V1.CallerRole.Reader };
        status.Jobs.Add(new JobStatus
        {
            Name = "Zalegle", LastResult = OverallState.Ok, LastRunUnixSeconds = Now.AddDays(-3).ToUnixTimeSeconds(),
            LastSuccessUnixSeconds = Now.AddDays(-3).ToUnixTimeSeconds(), LastSuccessVerifyLevel = 2,
            NextRunUnixSeconds = Now.AddHours(13).ToUnixTimeSeconds(), IntervalSeconds = 86_400, DestinationScheme = "local",
        });
        status.Jobs.Add(new JobStatus
        {
            Name = "Niezweryfikowane", LastResult = OverallState.Ok, LastRunUnixSeconds = Now.AddHours(-1).ToUnixTimeSeconds(),
            LastSuccessUnixSeconds = Now.AddHours(-1).ToUnixTimeSeconds(), LastSuccessVerifyLevel = 1,
        });
        status.Jobs.Add(new JobStatus { Name = "Uszkodzone", LastResult = OverallState.Ok, LastRunUnixSeconds = Now.AddHours(-1).ToUnixTimeSeconds(), LastSuccessUnixSeconds = Now.AddDays(-1).ToUnixTimeSeconds(), LastSuccessVerifyLevel = 2, RepositorySuspect = true });
        status.Jobs.Add(new JobStatus { Name = "Wylaczone", Disabled = true });
        var snaps = new ListSnapshotsResponse();
        snaps.Snapshots.Add(new SnapshotInfo { Id = "x", Job = "Zalegle", TimeUnixSeconds = Now.AddMinutes(-5).ToUnixTimeSeconds() }); // pole z GetStatus ma pierwszeństwo

        var d = GrpcDashboardSource.Map(status, snaps, Now);
        Assert.Equal(UiRole.Viewer, d.Role);
        Assert.False(d.RetentionHeld);
        status.RetentionHold = new RetentionHoldInfo { Code = "KB-6117", ClockDeltaSeconds = 90_000 };
        Assert.True(GrpcDashboardSource.Map(status, snaps, Now).RetentionHeld); // SEC-17.4
        var late = d.Jobs.Single(j => j.Name == "Zalegle");
        Assert.Equal(Now.AddDays(-3), late.LastSuccessUtc);
        Assert.Equal(Now.AddHours(13), late.NextRunUtc);
        Assert.Equal(TimeSpan.FromDays(1), late.Interval);
        Assert.Equal("local", late.DestinationName);
        Assert.Equal(new JobProtection(ProtectionState.AtRisk, ProtectionReason.Overdue), ProtectionEvaluator.Evaluate(late, Now));
        Assert.Equal(new JobProtection(ProtectionState.Warning, ProtectionReason.NotVerified), ProtectionEvaluator.Evaluate(d.Jobs.Single(j => j.Name == "Niezweryfikowane"), Now));
        Assert.Equal(new JobProtection(ProtectionState.AtRisk, ProtectionReason.RepositoryDamaged), ProtectionEvaluator.Evaluate(d.Jobs.Single(j => j.Name == "Uszkodzone"), Now));
        var off = d.Jobs.Single(j => j.Name == "Wylaczone");
        Assert.False(off.Enabled);
        Assert.Null(off.DestinationName);
        Assert.Equal(new JobProtection(ProtectionState.Paused, ProtectionReason.Disabled), ProtectionEvaluator.Evaluate(off, Now));
    }

    [Theory, Trait("BL", "BL-60"), Trait("Plan", "D2.9")]
    [InlineData(Api.V1.CallerRole.Unspecified, UiRole.Operator)] // starszy Agent — PROWIZORYCZNIE Qb18
    [InlineData(Api.V1.CallerRole.Reader, UiRole.Viewer)]
    [InlineData(Api.V1.CallerRole.Operator, UiRole.Operator)]
    [InlineData(Api.V1.CallerRole.Administrator, UiRole.Admin)]
    public void Maps_caller_role(Api.V1.CallerRole wire, UiRole expected) => Assert.Equal(expected, GrpcDashboardSource.MapRole(wire));

    [Fact, Trait("BL", "BL-60")]
    public void Maps_runs_with_best_available_time_and_skips_timeless()
    {
        var r = new ListRunsResponse();
        r.Runs.Add(new RunInfo { RunId = "a", Job = "J", Kind = "backup", State = "Failed", StartedUnixSeconds = 100, FinishedUnixSeconds = 200, Code = "KB-2104" });
        r.Runs.Add(new RunInfo { RunId = "b", Job = "J", Kind = "check", State = "Running", StartedUnixSeconds = 300 });
        r.Runs.Add(new RunInfo { RunId = "c", Job = "J", Kind = "backup", State = "Queued", QueuedUnixSeconds = 400, SnapshotId = "s" });
        r.Runs.Add(new RunInfo { RunId = "d", Job = "J", Kind = "backup", State = "Queued" });
        Assert.Equal(
            [new RunEntry("a", "J", "backup", "Failed", DateTimeOffset.FromUnixTimeSeconds(200), "KB-2104", null),
             new RunEntry("b", "J", "check", "Running", DateTimeOffset.FromUnixTimeSeconds(300), null, null),
             new RunEntry("c", "J", "backup", "Queued", DateTimeOffset.FromUnixTimeSeconds(400), null, "s")],
            GrpcDashboardSource.MapRuns(r));
    }

    [Fact, Trait("Plan", "R2.6.4")]
    public async Task Without_agent_load_fails_as_unreachable_and_dashboard_goes_offline()
    {
        using var src = new GrpcDashboardSource("kobian-test-" + Guid.NewGuid().ToString("N")[..12]);
        await Assert.ThrowsAsync<IOException>(() => src.LoadAsync(_ct));
        var vm = new DashboardViewModel(src);
        await vm.RefreshAsync(_ct);
        Assert.True(vm.IsOffline);
        Assert.Equal("KB-9001", vm.OfflineCode);
    }
    [Fact]
    public void Maps_schedules_and_snapshots_for_jobs_and_history()
    {
        var sched = new ListSchedulesResponse();
        sched.Schedules.Add(new ScheduleInfo { Job = "Dokumenty", TriggerId = "cron-1", Cron = "0 22 * * *", TimeZone = "Europe/Warsaw", Enabled = false });
        Assert.Equal([new JobSchedule("Dokumenty", "0 22 * * *", false)], GrpcDashboardSource.MapSchedules(sched));

        var snaps = new ListSnapshotsResponse();
        snaps.Snapshots.Add(new SnapshotInfo { Id = "abc", Job = "Dokumenty", TimeUnixSeconds = Now.ToUnixTimeSeconds(), Mode = "full", Files = 7, BytesRaw = 4096 });
        Assert.Equal([new SnapshotEntry("abc", "Dokumenty", Now, "full", 7, 4096)], GrpcDashboardSource.MapSnapshots(snaps));
    }

    [Fact, Trait("Plan", "R2.6.4")]
    public async Task Without_agent_jobs_and_history_go_offline()
    {
        using var src = new GrpcDashboardSource("kobian-test-" + Guid.NewGuid().ToString("N")[..12]);
        var jobs = new JobsViewModel(src);
        await jobs.RefreshAsync(_ct);
        Assert.True(jobs.IsOffline);
        var history = new HistoryViewModel(src);
        await history.RefreshAsync(_ct);
        Assert.True(history.IsOffline);
        await Assert.ThrowsAsync<IOException>(() => src.StartJobAsync("x", _ct));
        await Assert.ThrowsAsync<IOException>(() => src.SetScheduleAsync("x", null, _ct));
    }

    [Theory, Trait("Backlog", "BL-76")]
    [InlineData("Queued", ActiveRun.Queued)]
    [InlineData("Running", ActiveRun.Running)]
    [InlineData("", ActiveRun.None)]
    [InlineData("Paused?", ActiveRun.None)]
    public void Maps_active_state(string wire, ActiveRun expected)
    {
        var status = new GetStatusResponse();
        status.Jobs.Add(new JobStatus { Name = "A", ActiveState = wire });
        Assert.Equal(expected, Assert.Single(GrpcDashboardSource.Map(status, new ListSnapshotsResponse(), Now).Jobs).Activity);
    }
}
