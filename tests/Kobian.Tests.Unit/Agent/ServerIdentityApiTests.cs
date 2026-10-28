using Kobian.Agent.Audit;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.V1;
using Kobian.Catalog.Sqlite;
using Kobian.Cli.Commands;
using Kobian.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;
using E = Kobian.Agent.Jobs.ServerIdentityEvent;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-35 (SCR-09 backend, D4.11, SEC-4.1, SEC-14.5): RPC ListServerIdentities / ApproveServerIdentity / SetServerIdentityPolicy przez prawdziwy host gRPC
/// (gniazdo lokalne) i CLI <c>kobian destinations identity …</c>: role (Reader/Operator odrzuceni dla decyzji, Administrator OK), audyt w łańcuchu
/// (odmowa, porażka z KB-2141, sukces z odciskiem i decyzją, SID decydującego), walidacja danych.
/// </summary>
public sealed class ServerIdentityApiTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-identapi-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _hosts = [];
    private CatalogWriter _writer = null!;
    private ServerIdentityStore _store = null!;
    private AuditLogService _audit = null!;
    private const string AdminSid = "S-1-5-21-1000-2000-3000-500";
    private const string Dest = "dest-00112233aabbccdd";

    private string Catalog => Path.Combine(_dir, "catalog.db");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        _store = new ServerIdentityStore(Catalog, _writer, TimeProvider.System);
        _audit = new AuditLogService(Catalog, _writer, TimeProvider.System);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private sealed class SidResolver(AgentRole role, string sid) : ICallerRoleResolver
    {
        public AgentRole Resolve(Grpc.Core.ServerCallContext context) => role;

        public CallerInfo ResolveCaller(Grpc.Core.ServerCallContext context) => new(role, sid);
    }

    private async Task<string> StartAgent(AgentRole role, string sid)
    {
        var pipe = "kobian-test-" + Guid.NewGuid().ToString("N");
        var backend = new CatalogAgentBackend(Catalog, "9.9.9") { Audit = _audit, Identities = _store };
        var app = AgentHost.Create(new AgentHostOptions(pipe), backend, new SidResolver(role, sid), _audit);
        _hosts.Add(app);
        await app.StartAsync(_ct);
        return pipe;
    }

    private async Task<(int Code, string Out, string Err)> Cli(string pipe, params string[] args)
    {
        using var client = new GrpcAgentClient(pipe);
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(args, client, o, e, _ct);
        return (code, o.ToString(), e.ToString());
    }

    private List<(string Op, string Outcome, string? Kb, string? Target, string? Params, string Actor)> AuditRows()
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT operation, outcome, kb_code, target, params, actor_sid FROM audit_log ORDER BY seq;";
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string?, string?, string?, string)>();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : System.Text.Encoding.UTF8.GetString((byte[])r[4]), r.GetString(5)));
        return list;
    }

    /// <summary>Parametr wpisu audytu (JSON z domyślnym koderem — „+” jako \u002B, więc porównanie po parsowaniu).</summary>
    private static string? Param(string? json, string key)
    {
        if (json is null) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
    }

    private async Task<string> Pending()
    {
        var fp = ServerIdentityMachineTests.Fp();
        await _store.ApplyAsync(Dest, new E.Observed(ServerIdentityMachine.SshHostKey, fp, "sftp://backup.example.test:22", "ssh-ed25519"), _ct);
        return fp;
    }

    [Fact, Trait("D", "D4.11"), Trait("SEC", "SEC-4.1"), Trait("RD", "RD-42"), Trait("Category", "Integration")]
    public async Task Reader_and_operator_cannot_decide_and_every_denial_is_audited()
    {
        var fp = await Pending();
        var reader = await StartAgent(AgentRole.Reader, "S-1-5-21-1-2-3-1001");
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(reader, "destinations", "identity", "list")).Code); // lista: Operator
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(reader, "destinations", "identity", "approve", Dest, fp)).Code);
        var op = await StartAgent(AgentRole.Operator, "S-1-5-21-1-2-3-1002");
        var list = await Cli(op, "destinations", "identity", "list", "--json");
        Assert.Equal(CliApp.ExitOk, list.Code);
        using (var doc = System.Text.Json.JsonDocument.Parse(list.Out))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("identities").EnumerateArray());
            Assert.Equal((fp, "pending", Dest), (item.GetProperty("fingerprint").GetString(), item.GetProperty("state").GetString(), item.GetProperty("destinationId").GetString()));
        }
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(op, "destinations", "identity", "approve", Dest, fp, "--replace")).Code);
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(op, "destinations", "identity", "reject", Dest, fp)).Code);
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(op, "destinations", "identity", "require-pin", Dest, "on")).Code);
        await _writer.FlushAsync(_ct);

        Assert.Equal(IdentityState.Pending, Assert.Single(_store.Load(Dest)).State); // nic się nie zmieniło
        Assert.False(_store.RequiresPin(Dest));
        var rows = AuditRows();
        // Odmowy (Reader ×2, Operator ×3); lista Operatora (odczyt) nie jest audytowana.
        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Equal(("denied", (string?)KbCode.ApiAccessDenied), (r.Outcome, r.Kb)));
        Assert.Equal(["Agent/ListServerIdentities", "Agent/ApproveServerIdentity", "Agent/ApproveServerIdentity", "Agent/ApproveServerIdentity", "Agent/SetServerIdentityPolicy"],
            rows.Select(r => r.Op));
        Assert.Contains(rows, r => r.Target == Dest && Param(r.Params, "fingerprint") == fp && r.Actor == "S-1-5-21-1-2-3-1002");
        Assert.Null(_audit.Verify().BrokenSeq);
    }

    [Fact, Trait("D", "D4.11"), Trait("SEC", "SEC-4.1"), Trait("RD", "RD-42"), Trait("Category", "Integration")]
    public async Task Administrator_approves_only_the_exact_pending_fingerprint_and_the_decision_is_audited_with_the_sid()
    {
        var fp = await Pending();
        var admin = await StartAgent(AgentRole.Administrator, AdminSid);

        var wrong = await Cli(admin, "destinations", "identity", "approve", Dest, ServerIdentityMachineTests.Fp(), "--json");
        Assert.Equal(CliApp.ExitFailed, wrong.Code);
        Assert.Contains(KbCode.ServerIdentityNotPending, wrong.Out, StringComparison.Ordinal);
        var malformed = await Cli(admin, "destinations", "identity", "approve", Dest, "SHA256:a,SHA256:b");
        Assert.Equal(CliApp.ExitFailed, malformed.Code);
        Assert.Contains(KbCode.JobDefinitionInvalid, malformed.Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitUsage, (await Cli(admin, "destinations", "identity", "approve", Dest)).Code); // brak odcisku — składnia
        Assert.Equal(CliApp.ExitUsage, (await Cli(admin, "destinations", "identity", "require-pin", Dest, "maybe")).Code);

        var ok = await Cli(admin, "destinations", "identity", "approve", Dest, fp, "--json");
        Assert.Equal(CliApp.ExitOk, ok.Code);
        Assert.Contains("\"state\":\"pinned\"", ok.Out, StringComparison.Ordinal);
        var row = Assert.Single(_store.Load(Dest));
        Assert.Equal((IdentityState.Pinned, AdminSid), (row.State, row.DecidedBy));
        Assert.Equal(CliApp.ExitFailed, (await Cli(admin, "destinations", "identity", "approve", Dest, fp)).Code); // już nie oczekuje

        Assert.Equal(CliApp.ExitOk, (await Cli(admin, "destinations", "identity", "require-pin", Dest, "on")).Code);
        Assert.True(_store.RequiresPin(Dest));
        var text = await Cli(admin, "destinations", "identity", "list", "--destination", Dest);
        Assert.Contains("pinned\t" + fp, text.Out, StringComparison.Ordinal);
        Assert.Contains(Dest + "\trequire-pin", text.Out, StringComparison.Ordinal);

        var other = await Pending();
        Assert.Equal(CliApp.ExitOk, (await Cli(admin, "destinations", "identity", "reject", Dest, other)).Code);
        Assert.Equal(IdentityState.Rejected, _store.Load(Dest).Single(r => r.Fingerprint == other).State);
        await _writer.FlushAsync(_ct);

        var rows = AuditRows();
        Assert.Equal(["failed", "failed", "allowed", "failed", "allowed", "allowed"], rows.Select(r => r.Outcome));
        Assert.Equal(KbCode.ServerIdentityNotPending, rows[0].Kb);
        Assert.Equal(KbCode.JobDefinitionInvalid, rows[1].Kb);
        Assert.All(rows, r => Assert.Equal((Dest, AdminSid), (r.Target, r.Actor)));
        Assert.Equal((fp, "Add"), (Param(rows[2].Params, "fingerprint"), Param(rows[2].Params, "decision")));
        Assert.Equal("true", Param(rows[4].Params, "require_pin"));
        Assert.Equal((other, "Reject"), (Param(rows[5].Params, "fingerprint"), Param(rows[5].Params, "decision")));
        Assert.Null(_audit.Verify().BrokenSeq);
    }

    [Fact, Trait("D", "D4.11"), Trait("SEC", "D2.9")]
    public async Task Policy_map_and_backend_validation()
    {
        Assert.Equal(new MethodPolicy.Entry(AgentRole.Administrator, true), MethodPolicy.Find("/kobian.v1.Agent/ApproveServerIdentity"));
        Assert.Equal(new MethodPolicy.Entry(AgentRole.Administrator, true), MethodPolicy.Find("/kobian.v1.Agent/SetServerIdentityPolicy"));
        Assert.Equal(new MethodPolicy.Entry(AgentRole.Operator, false), MethodPolicy.Find("/kobian.v1.Agent/ListServerIdentities"));

        var b = new CatalogAgentBackend(Catalog, "t") { Identities = _store };
        var fp = await Pending();
        foreach (var bad in new[]
        {
            new ApproveServerIdentityArgs { DestinationId = Dest, Fingerprint = fp }, // decyzja nieokreślona
            new ApproveServerIdentityArgs { DestinationId = Dest, Fingerprint = fp, Decision = (ServerIdentityDecision)42 },
            new ApproveServerIdentityArgs { DestinationId = "dest-../../x", Fingerprint = fp, Decision = ServerIdentityDecision.Add },
            new ApproveServerIdentityArgs { DestinationId = Dest, Fingerprint = "", Decision = ServerIdentityDecision.Add },
        })
        {
            var r = await b.ApproveServerIdentityAsync(bad, AdminSid, _ct);
            Assert.Equal((Kobian.Api.V1.OverallState.Failed, KbCode.JobDefinitionInvalid), (r.Result, r.Code));
        }
        Assert.Equal(KbCode.JobDefinitionInvalid, (await b.ListServerIdentitiesAsync(new ListServerIdentitiesArgs { DestinationId = "x" }, _ct)).Code);
        Assert.Equal(KbCode.JobDefinitionInvalid, (await b.SetServerIdentityPolicyAsync(new SetServerIdentityPolicyArgs { DestinationId = Dest }, "", _ct)).Code);
        // Bez katalogu tożsamości — uczciwie KB-7012.
        var none = new CatalogAgentBackend(Catalog, "t");
        Assert.Equal(KbCode.OperationNotAvailable, (await none.ApproveServerIdentityAsync(new ApproveServerIdentityArgs(), AdminSid, _ct)).Code);
        Assert.Equal(KbCode.OperationNotAvailable, (await none.ListServerIdentitiesAsync(new ListServerIdentitiesArgs(), _ct)).Code);
        Assert.Equal(IdentityState.Pending, Assert.Single(_store.Load(Dest)).State);

        var (t, p) = AgentCallAudit.Describe(new ApproveServerIdentityArgs { DestinationId = Dest, Fingerprint = fp, Decision = ServerIdentityDecision.Replace });
        Assert.Equal(Dest, t);
        Assert.Equal((fp, "Replace"), (p!["fingerprint"], p["decision"]));
    }

    [Fact, Trait("D", "D4.11")]
    public async Task Expired_pending_is_listed_as_expired_and_cannot_be_approved()
    {
        var past = new ServerIdentityStoreTests.ManualTime(DateTimeOffset.UtcNow - TimeSpan.FromHours(30));
        var oldStore = new ServerIdentityStore(Catalog, _writer, past);
        var fp = ServerIdentityMachineTests.Fp();
        await oldStore.ApplyAsync(Dest, new E.Observed(ServerIdentityMachine.TlsSpki, fp, "ftps://nas:21", null), _ct);
        var b = new CatalogAgentBackend(Catalog, "t") { Identities = _store };
        var list = await b.ListServerIdentitiesAsync(new ListServerIdentitiesArgs(), _ct);
        Assert.Equal(Kobian.Api.V1.ServerIdentityState.Expired, Assert.Single(list.Identities).State);
        var r = await b.ApproveServerIdentityAsync(new ApproveServerIdentityArgs { DestinationId = Dest, Fingerprint = fp, Decision = ServerIdentityDecision.Add }, AdminSid, _ct);
        Assert.Equal(KbCode.ServerIdentityApprovalExpired, r.Code);
    }
}
