using Kobian.Agent.Hosting;
using Kobian.Api.Contracts;
using Kobian.Api.V1;
using AgentCli = Kobian.Cli.Commands;
using Kobian.Domain;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>RD-81 cz. 2 (it. 138): klient gRPC wysyła odtworzenie w tle (detach=true) i zwraca run_id; zwykłe Restore nadal czeka na wynik. Dane syntetyczne.</summary>
public sealed class DetachedRestoreClientTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly List<WebApplication> _hosts = [];
    private static readonly string Abs = OperatingSystem.IsWindows() ? @"C:\Odtw" : "/srv/odtw";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
    }

    private sealed class Backend : IAgentBackend
    {
        public int Restores { get; private set; }

        public int Plans { get; private set; }

        public bool? LastDetach { get; private set; }

        public string? LastSnapshot { get; private set; }

        public Task<RunOutcome> RestoreDetachedAsync(string snapshot, string? pathFilter, IReadOnlyList<string> exactPaths, string target, CancellationToken ct)
        {
            LastDetach = true;
            LastSnapshot = snapshot;
            return Task.FromResult(new RunOutcome { Result = OverallState.Ok, RunId = "0a1b2c3d" });
        }

        public Task<GetStatusResponse> GetStatusAsync(CancellationToken ct) => Task.FromResult(new GetStatusResponse());

        public Task<ListSnapshotsResponse> ListSnapshotsAsync(string? job, CancellationToken ct) => Task.FromResult(new ListSnapshotsResponse());

        public Task<RunOutcome> RunJobAsync(string job, bool wait, CancellationToken ct) => Task.FromResult(new RunOutcome());

        public Task<InitJobReply> InitJobAsync(InitJobArgs args, CancellationToken ct) => Task.FromResult(new InitJobReply());

        public Task<ListSchedulesResponse> ListSchedulesAsync(string? job, CancellationToken ct) => Task.FromResult(new ListSchedulesResponse());

        public Task<SetScheduleReply> SetScheduleAsync(SetScheduleArgs args, CancellationToken ct) => Task.FromResult(new SetScheduleReply());

        public Task<CheckReply> CheckAsync(CheckArgs args, CancellationToken ct) => Task.FromResult(new CheckReply());

        public Task<ListRunsResponse> ListRunsAsync(string? job, int? limit, CancellationToken ct) => Task.FromResult(new ListRunsResponse());

        public Task<ProbeDestinationReply> ProbeDestinationAsync(ProbeDestinationArgs args, CancellationToken ct) => Task.FromResult(new ProbeDestinationReply());

        public Task<UpsertJobReply> UpsertJobAsync(UpsertJobArgs args, CancellationToken ct) => Task.FromResult(new UpsertJobReply());

        public Task<InitRepositoryReply> InitRepositoryAsync(InitRepositoryArgs args, CancellationToken ct) => Task.FromResult(new InitRepositoryReply());

        public Task<ApplyRetentionReply> ApplyRetentionAsync(ApplyRetentionArgs args, CancellationToken ct) => Task.FromResult(new ApplyRetentionReply());

        public Task<PreviewRetentionReply> PreviewRetentionAsync(PreviewRetentionArgs args, CancellationToken ct) => Task.FromResult(new PreviewRetentionReply());

        public Task<RunOutcome> RestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct)
        {
            Restores++;
            LastDetach = false;
            return Task.FromResult(new RunOutcome { Result = OverallState.Ok });
        }

        public Task<RunOutcome> RestoreExactAsync(string snapshot, IReadOnlyList<string> exactPaths, string target, CancellationToken ct)
        {
            Restores++;
            return Task.FromResult(new RunOutcome { Result = OverallState.Ok });
        }

        public Task<RestorePlanReply> PlanRestoreAsync(RestoreRequest request, CancellationToken ct)
        {
            Plans++;
            return Task.FromResult(new RestorePlanReply { Files = 1 });
        }
    }

    private sealed class AllowAll : IRestoreTargetAuthorizer
    {
        public RestoreTargetAccess Check(Grpc.Core.ServerCallContext context, string target) => RestoreTargetAccess.Allowed;
    }

    private async Task<string> StartAgent(AgentRole role, Backend backend)
    {
        var pipe = "kobian-test-" + Guid.NewGuid().ToString("N");
        var app = AgentHost.Create(new AgentHostOptions(pipe, ApiConventions.CurrentRevision) { RestoreTargets = new AllowAll() }, backend, new FixedRoleResolver(role));
        _hosts.Add(app);
        await app.StartAsync(_ct);
        return pipe;
    }

    [Fact, Trait("BL", "BL-64"), Trait("Plan", "R2.8.1"), Trait("Category", "Integration")]
    public async Task Client_detached_restore_returns_run_id_and_sets_detach()
    {
        var backend = new Backend();
        var pipe = await StartAgent(AgentRole.Operator, backend);
        using var client = new AgentCli.GrpcAgentClient(pipe, TimeSpan.FromSeconds(2));
        var r = await client.RestoreDetachedAsync("latest", null, Abs, _ct);
        Assert.Equal(AgentCli.OverallState.Ok, r.Result);
        Assert.Equal("0a1b2c3d", r.RunId);
        Assert.True(backend.LastDetach);
        Assert.Equal("latest", backend.LastSnapshot);
        Assert.Equal(0, backend.Restores);
    }

    [Fact, Trait("BL", "BL-64"), Trait("Plan", "R2.8.1"), Trait("Category", "Integration")]
    public async Task Client_blocking_restore_does_not_set_detach_and_has_no_run_id()
    {
        var backend = new Backend();
        var pipe = await StartAgent(AgentRole.Operator, backend);
        using var client = new AgentCli.GrpcAgentClient(pipe, TimeSpan.FromSeconds(2));
        var r = await client.RestoreAsync("latest", null, Abs, _ct);
        Assert.Null(r.RunId);
        Assert.False(backend.LastDetach);
        Assert.Equal(1, backend.Restores);
    }

    [Fact, Trait("BL", "BL-64")]
    public async Task Clients_without_support_report_agent_unavailable()
    {
#pragma warning disable CA1859 // typ interfejsu celowo: metoda jest domyślną implementacją interfejsu, niedostępną przez typ klasy
        AgentCli.IAgentClient client = new AgentCli.UnavailableAgentClient();
#pragma warning restore CA1859
        await Assert.ThrowsAsync<AgentCli.AgentUnavailableException>(() => client.RestoreDetachedAsync("latest", null, Abs, _ct));
    }
}
