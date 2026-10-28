using Grpc.Core;
using Kobian.UI.Dashboard;
using Kobian.UI.Services;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>
/// Awaria usługi w komendzie ekranu nie może zamykać aplikacji: AsyncRelayCommand przekazuje nieobsłużony wyjątek do kontekstu synchronizacji (proces UI kończy się).
/// Komunikat ma też nazywać prawdziwą przyczynę (odmowa roli ≠ błąd serwera ≠ brak połączenia).
/// </summary>
public sealed class ServiceFailureTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class RunSource(Func<Task<string?>> start) : IDashboardSource
    {
        public Task<DashboardData> LoadAsync(CancellationToken ct) => Task.FromResult(FakeDashboardSource.Mixed());

        public Task RunJobAsync(string jobId, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> StartJobAsync(string jobId, CancellationToken ct) => start();
    }

    private async Task<DashboardViewModel> Loaded(IDashboardSource src)
    {
        var vm = new DashboardViewModel(src, new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc);
        await vm.RefreshAsync(_ct);
        return vm;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Run_when_the_service_dropped_shows_offline_banner_instead_of_throwing()
    {
        var vm = await Loaded(new RunSource(() => throw new IOException("down")));
        var tile = vm.Jobs.First(j => j.RunsJob);
        await vm.ActCommand.ExecuteAsync(tile); // wcześniej: IOException z komendy → zamknięcie aplikacji
        Assert.True(vm.AlertMessageIsError);
        Assert.Contains("KB-9001", vm.AlertMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_refused_by_the_agent_shows_its_kb_code_instead_of_being_silently_dropped()
    {
        var vm = await Loaded(new RunSource(() => Task.FromResult<string?>("KB-0103")));
        await vm.RunJobCommand.ExecuteAsync(vm.Jobs.First(j => j.RunsJob));
        Assert.True(vm.AlertMessageIsError);
        Assert.Contains("KB-0103", vm.AlertMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied, "KB-7011")]
    [InlineData(StatusCode.Unauthenticated, "KB-7001")]
    [InlineData(StatusCode.Unimplemented, "KB-7012")]
    [InlineData(StatusCode.Internal, "KB-0001")]
    [InlineData(StatusCode.Unknown, "KB-0001")]
    [InlineData(StatusCode.Unavailable, "KB-9001")]
    public void Rpc_failures_map_to_the_real_cause_not_always_to_a_role_denial(StatusCode status, string code) =>
        Assert.Equal(code, UiFailure.CodeOf(new InvalidOperationException("x", new RpcException(new Status(status, "x")))));

    [Fact]
    public void Outdated_client_is_reported_as_such()
    {
        var trailers = new Metadata { { "kb-code", "KB-7006" } };
        Assert.Equal("KB-7006", UiFailure.CodeOf(new InvalidOperationException("x", new RpcException(new Status(StatusCode.FailedPrecondition, "x"), trailers))));
    }

    [Fact]
    public void Plain_transport_errors_are_offline_and_unknown_invalid_operations_are_internal()
    {
        Assert.Equal("KB-9001", UiFailure.CodeOf(new IOException()));
        Assert.Equal("KB-9001", UiFailure.CodeOf(new TimeoutException()));
        Assert.Equal("KB-0001", UiFailure.CodeOf(new InvalidOperationException("bug")));
        Assert.True(UiFailure.IsServiceFailure(new IOException()));
        Assert.False(UiFailure.IsServiceFailure(new ArgumentException()));
    }

    [Fact]
    public void Paused_schedule_makes_the_job_disabled_on_the_dashboard_like_in_the_jobs_screen()
    {
        var data = FakeDashboardSource.AllGood();
        var resp = new Kobian.Api.V1.ListSchedulesResponse();
        resp.Schedules.Add(new Kobian.Api.V1.ScheduleInfo { Job = data.Jobs[0].JobId, Cron = "0 22 * * *", Enabled = false });
        resp.Schedules.Add(new Kobian.Api.V1.ScheduleInfo { Job = data.Jobs[1].JobId, Cron = "0 * * * *", Enabled = true });

        var mapped = GrpcDashboardSource.ApplyPausedSchedules(data, resp);

        Assert.False(mapped.Jobs[0].Enabled);
        Assert.True(mapped.Jobs[1].Enabled);
        Assert.Same(data, GrpcDashboardSource.ApplyPausedSchedules(data, new Kobian.Api.V1.ListSchedulesResponse())); // nic do zmiany
    }
}
