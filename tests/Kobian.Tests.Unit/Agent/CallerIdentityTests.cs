using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Kobian.Agent.Audit;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.Contracts;
using Kobian.Api.V1;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-17 (D2.9, SP-02 część platformowo-neutralna): mapa token → rola, fail-closed resolwera i interceptor z tożsamością z tokenu
/// (KB-7001, aktor = SID w audycie). Adapter RunAsClient (NamedPipeIdentityReader) — Windows, W-17/W-37.
/// </summary>
public sealed class CallerIdentityTests : IAsyncLifetime
{
    private const string Ops = "S-1-5-21-1000-2000-3000-1101";
    private const string Admins = "S-1-5-21-1000-2000-3000-1102";
    private const string Alice = "S-1-5-21-1000-2000-3000-1201";
    private const string Users = "S-1-5-32-545";
    private static readonly AgentRoleMap Map = new(Admins, Ops);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-caller-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "kobian-caller-" + Guid.NewGuid().ToString("N")[..12];
    private CatalogWriter _writer = null!;
    private WebApplication? _app;
    private string Catalog => Path.Combine(_dir, "catalog.db");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static CallerIdentity Id(string sid, CallerTokenLevel level = CallerTokenLevel.Identification, params string[] groups) =>
        new(sid, new HashSet<string>(groups, StringComparer.Ordinal), level);

    public static TheoryData<string, CallerIdentity?, AgentRole> Cases() => new()
    {
        // 4 typy kont z kryterium SP-02 + przypadki brzegowe
        { "admin podniesiony", Id(Alice, CallerTokenLevel.Identification, Users, AgentPipeAcl.AdministratorsSid), AgentRole.Administrator },
        { "admin bez podniesienia (deny-only pominięte) w Operators", Id(Alice, CallerTokenLevel.Identification, Users, Ops), AgentRole.Operator },
        { "admin bez podniesienia poza grupami Kobian", Id(Alice, CallerTokenLevel.Identification, Users), AgentRole.Reader },
        { "operator", Id(Alice, CallerTokenLevel.Identification, Users, Ops), AgentRole.Operator },
        { "viewer", Id(Alice, CallerTokenLevel.Identification, Users), AgentRole.Reader },
        { "Kobian Admins", Id(Alice, CallerTokenLevel.Identification, Users, Admins, Ops), AgentRole.Administrator },
        { "SYSTEM", Id(AgentPipeAcl.SystemSid), AgentRole.Administrator },
        { "impersonation też wystarcza", Id(Alice, CallerTokenLevel.Impersonation, Ops), AgentRole.Operator },
        { "logowanie sieciowe nawet z adminem", Id(Alice, CallerTokenLevel.Identification, AgentPipeAcl.NetworkSid, AgentPipeAcl.AdministratorsSid), AgentRole.None },
        { "anonimowy", Id(Alice, CallerTokenLevel.Anonymous, AgentPipeAcl.AdministratorsSid), AgentRole.None },
        { "token procesu zamiast klienta", Id(Alice, CallerTokenLevel.None, AgentPipeAcl.AdministratorsSid), AgentRole.None },
        { "pusty SID", Id(string.Empty, CallerTokenLevel.Identification, AgentPipeAcl.AdministratorsSid), AgentRole.None },
        { "nazwa zamiast SID", Id("BUILTIN\\Administrators", CallerTokenLevel.Identification, AgentPipeAcl.AdministratorsSid), AgentRole.None },
        { "brak tożsamości", null, AgentRole.None },
    };

    [Theory, MemberData(nameof(Cases)), Trait("SEC", "D2.9"), Trait("SP", "SP-02"), Trait("BL", "BL-17")]
    public void Token_maps_to_the_highest_role_fail_closed(string label, CallerIdentity? caller, AgentRole expected)
    {
        Assert.False(string.IsNullOrEmpty(label));
        Assert.Equal(expected, Map.Map(caller));
    }

    [Fact, Trait("BL", "BL-17")]
    public void Missing_kobian_groups_leave_only_builtin_admins_and_viewers()
    {
        var none = new AgentRoleMap(null, null);
        Assert.Equal(AgentRole.Reader, none.Map(Id(Alice, CallerTokenLevel.Identification, Ops, Admins)));
        Assert.Equal(AgentRole.Administrator, none.Map(Id(Alice, CallerTokenLevel.Identification, AgentPipeAcl.AdministratorsSid)));
        Assert.Throws<ArgumentException>(() => new AgentRoleMap("Kobian Admins", null));
        Assert.Throws<ArgumentException>(() => new AgentRoleMap(null, "S-1-x"));
    }

    private sealed class FakeReader(Func<CallerIdentity?> read) : IPipeClientIdentityReader
    {
        public int Calls { get; private set; }

        public CallerIdentity? Read(ServerCallContext context)
        {
            Calls++;
            return read();
        }
    }

    [Fact, Trait("SEC", "D2.9"), Trait("BL", "BL-17")]
    public void Resolver_never_falls_back_to_a_default_role()
    {
        Assert.Same(CallerInfo.Unauthenticated, new TokenCallerResolver(new FakeReader(() => throw new UnauthorizedAccessException("x")), Map).ResolveCaller(null!));
        Assert.Same(CallerInfo.Unauthenticated, new TokenCallerResolver(new FakeReader(() => null), Map).ResolveCaller(null!));
        Assert.Same(CallerInfo.Unauthenticated, new TokenCallerResolver(new FakeReader(() => Id(Alice, CallerTokenLevel.Anonymous)), Map).ResolveCaller(null!));
        var ok = new TokenCallerResolver(new FakeReader(() => Id(Alice, CallerTokenLevel.Identification, Ops)), Map);
        Assert.Equal(new CallerInfo(AgentRole.Operator, Alice), ok.ResolveCaller(null!));
        Assert.Equal(AgentRole.Operator, ok.Resolve(null!));
        Assert.False(CallerInfo.Unauthenticated.Authenticated);
        // tryb konsoli: stała rola, aktor „local-pipe”
        Assert.Equal(new CallerInfo(AgentRole.Reader, AgentCallAudit.LocalActor), ((ICallerRoleResolver)new FixedRoleResolver(AgentRole.Reader)).ResolveCaller(null!));
    }

    [Fact, Trait("SEC", "D2.9"), Trait("SEC", "D2.15"), Trait("BL", "BL-17")]
    public void Pipe_acl_admits_kobian_admins_read_write_only()
    {
        const string service = "S-1-5-80-1-2-3-4-5";
        var plan = AgentPipeAcl.Plan(service, Ops, Admins);
        var admins = Assert.Single(plan, a => a.Sid == Admins);
        Assert.Equal((PipeAceKind.Allow, PipeAceRights.ReadWrite), (admins.Kind, admins.Rights));
        Assert.Equal(PipeAceKind.Deny, plan[0].Kind); // Deny NETWORK nadal pierwszy
        Assert.All(plan.Where(a => a.Sid != service), a => Assert.False(a.Rights.HasFlag(PipeAceRights.CreateNewInstance)));
        Assert.Single(AgentPipeAcl.Plan(service, Ops, Ops), a => a.Sid == Ops); // ta sama grupa — jeden wpis
        Assert.Throws<ArgumentException>(() => AgentPipeAcl.Plan(service, Ops, AgentPipeAcl.NetworkSid));
        Assert.Throws<ArgumentException>(() => AgentPipeAcl.Plan(service, Ops, "Kobian Admins"));
        Assert.Equal(AgentPipeAcl.Plan(service, Ops).Count + 1, plan.Count);
    }

    private (AgentClientHolder Client, FakeReader Reader, AuditLogService Audit) Start(Func<CallerIdentity?> identity)
    {
        var audit = new AuditLogService(Catalog, _writer, TimeProvider.System);
        var backend = new CatalogAgentBackend(Catalog, "9.9.9", schedules: new ScheduleService(Catalog, _writer)) { Audit = audit };
        var reader = new FakeReader(identity);
        _app = AgentHost.Create(new AgentHostOptions(_pipe), backend, new TokenCallerResolver(reader, Map), audit);
        return (new AgentClientHolder(_pipe), reader, audit);
    }

    private sealed class AgentClientHolder(string pipe) : IDisposable
    {
        private readonly GrpcChannel _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await s.ConnectAsync(new UnixDomainSocketEndPoint(ApiConventions.UnixSocketPath(pipe)), ct);
                    return new NetworkStream(s, ownsSocket: true);
                },
            },
            DisposeHttpClient = true,
        });

        public Kobian.Api.V1.Agent.AgentClient Api => new(_channel);

        public static Metadata Revision => new() { { ApiConventions.ClientRevisionHeader, ApiConventions.CurrentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) } };

        public void Dispose() => _channel.Dispose();
    }

    private List<(string Actor, string Role, string Op, string Outcome, string? Kb)> AuditRows()
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT actor_sid, role, operation, outcome, kb_code FROM audit_log ORDER BY seq;";
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string, string, string?)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    [Fact, Trait("SEC", "D2.9"), Trait("BL", "BL-17"), Trait("Category", "Integration")]
    public async Task Unauthenticated_caller_gets_KB7001_even_for_read_only_methods_and_is_audited()
    {
        if (OperatingSystem.IsWindows()) return; // gniazdo Unix; Windows — W-17
        var (holder, _, _) = Start(() => null);
        using var client = holder;
        await _app!.StartAsync(_ct);
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await client.Api.GetStatusAsync(new GetStatusRequest(), cancellationToken: _ct));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Equal(KbCode.CallerUnauthenticated, ex.Trailers.GetValue(ApiConventions.KbCodeTrailer));
        await _writer.FlushAsync(_ct);
        var row = Assert.Single(AuditRows());
        Assert.Equal(("unauthenticated", "None", "Agent/GetStatus", "denied", (string?)KbCode.CallerUnauthenticated), row);
    }

    [Fact, Trait("SEC", "D2.9"), Trait("SEC", "D9.9"), Trait("BL", "BL-17"), Trait("Category", "Integration")]
    public async Task Role_comes_from_the_token_and_the_audit_actor_is_the_callers_sid()
    {
        if (OperatingSystem.IsWindows()) return;
        var current = Id(Alice, CallerTokenLevel.Identification, Users); // viewer
        var (holder, reader, audit) = Start(() => current);
        using var client = holder;
        await _app!.StartAsync(_ct);

        await client.Api.GetStatusAsync(new GetStatusRequest(), cancellationToken: _ct); // Reader może czytać
        var denied = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.Api.SetScheduleAsync(new SetScheduleArgs { Job = "brak", Cron = "0 21 * * *" }, AgentClientHolder.Revision, cancellationToken: _ct));
        Assert.Equal((StatusCode.PermissionDenied, KbCode.ApiAccessDenied), (denied.StatusCode, denied.Trailers.GetValue(ApiConventions.KbCodeTrailer)));

        current = Id(Alice, CallerTokenLevel.Identification, Users, Ops); // ten sam użytkownik dodany do Kobian Operators
        var set = await client.Api.SetScheduleAsync(new SetScheduleArgs { Job = "brak", Cron = "0 21 * * *" }, AgentClientHolder.Revision, cancellationToken: _ct);
        Assert.Equal(KbCode.JobNotFound, set.Code); // dotarło do backendu (zadanie nie istnieje)
        await Assert.ThrowsAsync<RpcException>(async () =>
            await client.Api.ReleaseRetentionHoldAsync(new ReleaseRetentionHoldArgs(), AgentClientHolder.Revision, cancellationToken: _ct)); // Administrator

        await _writer.FlushAsync(_ct);
        var rows = AuditRows();
        Assert.Equal(
        [
            (Alice, "Reader", "Agent/SetSchedule", "denied", KbCode.ApiAccessDenied),
            (Alice, "Operator", "Agent/SetSchedule", "failed", KbCode.JobNotFound),
            (Alice, "Operator", "Agent/ReleaseRetentionHold", "denied", KbCode.ApiAccessDenied),
        ], rows);
        Assert.DoesNotContain(rows, r => r.Actor == AgentCallAudit.LocalActor);
        Assert.Null(audit.Verify().BrokenSeq);
        Assert.True(reader.Calls >= 4); // interceptor pyta o tożsamość przy każdym wywołaniu (cache per połączenie — adapter Windows)
    }
}
