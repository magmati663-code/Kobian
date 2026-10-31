using Grpc.Core;
using Kobian.Agent.Hosting;
using Kobian.Api.Contracts;
using Kobian.Api.V1;
using Kobian.Domain;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// it. 101 — SEC-6.3 / AC-07.2 / R2.8.1 AuthorizeTargets: Operator odtwarza (i planuje) tylko do folderu, do którego KLIENT ma prawo zapisu
/// (adapter AccessCheck — W-43); odmowa KB-5001 zanim backend dotknie kopii; Administrator bez kontroli; błąd adaptera = odmowa.
/// </summary>
public sealed class RestoreTargetAuthorizationTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly List<WebApplication> _hosts = [];
    private readonly List<Grpc.Net.Client.GrpcChannel> _channels = [];
    private static readonly string Abs = OperatingSystem.IsWindows() ? @"C:\Odtw" : "/srv/odtw";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _channels) c.Dispose();
        foreach (var h in _hosts) await h.DisposeAsync();
    }

    private sealed class Backend : IAgentBackend
    {
        public int Restores { get; private set; }

        public int Plans { get; private set; }

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

    private sealed class Authorizer(RestoreTargetAccess result, bool throws = false) : IRestoreTargetAuthorizer
    {
        public List<string> Targets { get; } = [];

        public RestoreTargetAccess Check(ServerCallContext context, string target)
        {
            Targets.Add(target);
            return throws ? throw new UnauthorizedAccessException("brak READ_CONTROL") : result;
        }
    }

    private async Task<Api.V1.Agent.AgentClient> Start(AgentRole role, IRestoreTargetAuthorizer authorizer, Backend backend)
    {
        var pipe = "kobian-test-" + Guid.NewGuid().ToString("N");
        var app = AgentHost.Create(new AgentHostOptions(pipe) { RestoreTargets = authorizer }, backend, new FixedRoleResolver(role));
        _hosts.Add(app);
        await app.StartAsync(_ct);
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var sock = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                await sock.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(ApiConventions.UnixSocketPath(pipe)), ct);
                return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);
            },
        };
        var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost", new Grpc.Net.Client.GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
        _channels.Add(channel);
        return new Api.V1.Agent.AgentClient(channel);
    }

    private static Metadata Headers => new() { { ApiConventions.ClientRevisionHeader, ApiConventions.CurrentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) } };

    [Theory, Trait("SEC", "SEC-6.3"), Trait("AC", "AC-07.2")]
    [InlineData(AgentRole.Operator, RestoreTargetAccess.Denied, false, KbCode.RestoreTargetAccessDenied)]
    [InlineData(AgentRole.Operator, RestoreTargetAccess.Allowed, false, null)]
    [InlineData(AgentRole.Operator, RestoreTargetAccess.Allowed, true, KbCode.RestoreTargetAccessDenied)] // błąd adaptera = odmowa
    [InlineData(AgentRole.Administrator, RestoreTargetAccess.Denied, false, null)] // Administrator bez AccessCheck
    public void Gate_decides_by_role_and_client_access(AgentRole role, RestoreTargetAccess access, bool throws, string? expected)
    {
        var calls = 0;
        var code = RestoreTargetGate.Deny(role, Abs, _ =>
        {
            calls++;
            return throws ? throw new IOException("x") : access;
        });
        Assert.Equal(expected, code);
        Assert.Equal(role == AgentRole.Administrator ? 0 : 1, calls);
        Assert.Equal(KbCode.RestoreTargetAccessDenied, RestoreTargetGate.Deny(AgentRole.Operator, "wzgledny", _ => RestoreTargetAccess.Allowed));
        Assert.Equal(KbCode.RestoreTargetAccessDenied, RestoreTargetGate.Deny(AgentRole.Operator, "", _ => RestoreTargetAccess.Allowed));
    }

    [Fact, Trait("SEC", "SEC-6.3"), Trait("AC", "AC-07.2"), Trait("Category", "Integration")]
    public async Task Operator_without_client_write_access_is_refused_before_the_backend_reads_anything()
    {
        if (OperatingSystem.IsWindows()) return; // pipe testowy w Linuksie = gniazdo Unix; Windows — W-43
        var backend = new Backend();
        var auth = new Authorizer(RestoreTargetAccess.Denied);
        var client = await Start(AgentRole.Operator, auth, backend);
        var restore = await client.RestoreAsync(new RestoreRequest { Snapshot = "latest", Target = Abs }, Headers, cancellationToken: _ct);
        Assert.Equal((OverallState.Failed, KbCode.RestoreTargetAccessDenied), (restore.Result, restore.Code));
        var exact = new RestoreRequest { Snapshot = "latest", Target = Abs };
        exact.ExactPaths.Add("a.txt");
        Assert.Equal(KbCode.RestoreTargetAccessDenied, (await client.RestoreAsync(exact, Headers, cancellationToken: _ct)).Code);
        var plan = await client.PlanRestoreAsync(new RestoreRequest { Snapshot = "latest", Target = Abs }, Headers, cancellationToken: _ct);
        Assert.Equal(KbCode.RestoreTargetAccessDenied, plan.Code);
        Assert.Equal((0, 0), (backend.Restores, backend.Plans));
        Assert.Equal([Abs, Abs, Abs], auth.Targets);
    }

    [Fact, Trait("SEC", "SEC-6.3"), Trait("Category", "Integration")]
    public async Task Allowed_operator_and_administrator_reach_the_backend()
    {
        if (OperatingSystem.IsWindows()) return;
        var backend = new Backend();
        var op = await Start(AgentRole.Operator, new Authorizer(RestoreTargetAccess.Allowed), backend);
        Assert.Equal(OverallState.Ok, (await op.RestoreAsync(new RestoreRequest { Snapshot = "latest", Target = Abs }, Headers, cancellationToken: _ct)).Result);
        Assert.Equal(1, (await op.PlanRestoreAsync(new RestoreRequest { Snapshot = "latest", Target = Abs }, Headers, cancellationToken: _ct)).Files);
        var deniedAuth = new Authorizer(RestoreTargetAccess.Denied);
        var admin = await Start(AgentRole.Administrator, deniedAuth, backend);
        Assert.Equal(OverallState.Ok, (await admin.RestoreAsync(new RestoreRequest { Snapshot = "latest", Target = Abs }, Headers, cancellationToken: _ct)).Result);
        Assert.Empty(deniedAuth.Targets);
        Assert.Equal((2, 1), (backend.Restores, backend.Plans));
    }
}
