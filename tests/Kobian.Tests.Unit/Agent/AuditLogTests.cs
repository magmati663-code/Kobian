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

namespace Kobian.Tests.Unit.Agent;

/// <summary>RD-42 (D9.9, SEC-19.1): łańcuch audytu w katalogu — zapis przez pisarza, wykrycie ręcznej zmiany, audyt API (odmowy + metody modyfikujące) przez pipe.</summary>
public sealed class AuditLogTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-audit-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _hosts = [];
    private CatalogWriter _writer = null!;
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
        foreach (var h in _hosts) await h.DisposeAsync();
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static AuditRecord R(string op, string outcome = "allowed", string? kb = null, Dictionary<string, string>? p = null) =>
        new(AgentCallAudit.LocalActor, "Operator", "api", op, "cel", p, outcome, kb);

    private List<(long Seq, string Op, string Outcome, string? Kb, string? Target, string? Params)> Rows()
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq, operation, outcome, kb_code, target, params FROM audit_log ORDER BY seq;";
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, string, string?, string?, string?)>();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : System.Text.Encoding.UTF8.GetString((byte[])r[5])));
        return list;
    }

    [Fact, Trait("SEC", "SEC-19.1"), Trait("RD", "RD-42")]
    public async Task Concurrent_appends_form_one_contiguous_chain_that_verifies()
    {
        var audit = new AuditLogService(Catalog, _writer, TimeProvider.System);
        Assert.Equal(new AuditVerification(0, null, null), audit.Verify());
        await Task.WhenAll(Enumerable.Range(0, 25).Select(i => audit.AppendAsync(R("Agent/Op" + i, p: new() { ["k"] = new string('x', 1000) }), _ct)));
        var rows = Rows();
        Assert.Equal(Enumerable.Range(1, 25).Select(i => (long)i), rows.Select(r => r.Seq));
        Assert.All(rows, r => Assert.Equal(AuditLogService.MaxParamLength + 8, r.Params!.Length)); // {"k":"…256…"}
        var v = audit.Verify();
        Assert.Equal(25, v.Entries);
        Assert.Null(v.BrokenSeq);
        Assert.Equal(32, v.LastHash!.Length);
    }

    [Fact, Trait("SEC", "SEC-19.1"), Trait("RD", "RD-42")]
    public async Task Manual_edit_outside_the_application_is_detected_at_the_first_changed_seq()
    {
        var audit = new AuditLogService(Catalog, _writer, TimeProvider.System);
        for (var i = 0; i < 6; i++) await audit.AppendAsync(R("Agent/Op" + i), _ct);
        await _writer.FlushAsync(_ct);
        using (var c = new SqliteConnection($"Data Source={Catalog}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            // Aplikacja nie może zmienić wpisu (wyzwalacz) …
            cmd.CommandText = "UPDATE audit_log SET outcome='denied' WHERE seq=4;";
            Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
            // … ale ktoś z dostępem do pliku może zdjąć wyzwalacz — wtedy wykrywa to łańcuch.
            cmd.CommandText = "DROP TRIGGER audit_no_update; UPDATE audit_log SET outcome='denied' WHERE seq=4;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal(4, audit.Verify().BrokenSeq);
        var backend = new CatalogAgentBackend(Catalog, "t") { Audit = audit };
        var reply = await backend.VerifyAuditAsync(_ct);
        // it. 99 (RD-51): zdjęty wyzwalacz = zmieniony schemat — KB-6502 ma pierwszeństwo, pierwszy zły seq nadal podany.
        Assert.Equal((Kobian.Api.V1.OverallState.Failed, KbCode.AuditSchemaChanged, 4L), (reply.Result, reply.Code, reply.BrokenSeq));
    }

    [Fact, Trait("RD", "RD-42"), Trait("SEC", "D2.9"), Trait("Category", "Integration")]
    public async Task Api_denials_and_modifying_calls_are_audited_read_only_calls_are_not()
    {
        var pipe = "kobian-test-" + Guid.NewGuid().ToString("N");
        var audit = new AuditLogService(Catalog, _writer, TimeProvider.System);
        var backend = new CatalogAgentBackend(Catalog, "9.9.9", schedules: new ScheduleService(Catalog, _writer)) { Audit = audit };
        var app = AgentHost.Create(new AgentHostOptions(pipe), backend, new FixedRoleResolver(AgentRole.Operator), audit);
        _hosts.Add(app);
        await app.StartAsync(_ct);
        using var client = new GrpcAgentClient(pipe);

        await client.GetStatusAsync(_ct); // odczyt — bez wpisu
        var set = await client.SetScheduleAsync("brak-zadania", "0 21 * * *", _ct); // modyfikujące, nieudane (KB-0104)
        Assert.Equal(Kobian.Cli.Commands.OverallState.Failed, set.Result);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReleaseRetentionHoldAsync(_ct)); // Administrator — odmowa dla Operatora
        var verify = await client.VerifyAuditAsync(_ct);
        await _writer.FlushAsync(_ct);

        var rows = Rows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("Agent/SetSchedule", "failed", set.Code, "brak-zadania"), (rows[0].Op, rows[0].Outcome, rows[0].Kb, rows[0].Target));
        Assert.Contains("\"cron\":\"0 21 * * *\"", rows[0].Params, StringComparison.Ordinal);
        Assert.Equal(("Agent/ReleaseRetentionHold", "denied", (string?)KbCode.ApiAccessDenied), (rows[1].Op, rows[1].Outcome, rows[1].Kb));
        Assert.Equal(Kobian.Cli.Commands.OverallState.Ok, verify.Result);
        Assert.Null(audit.Verify().BrokenSeq);
        Assert.Equal(64, verify.LastHash.Length);
    }

    [Fact, Trait("RD", "RD-42")]
    public void Trust_changes_carry_target_and_fingerprint_but_no_password()
    {
        var (t, p) = AgentCallAudit.Describe(new ApproveSqlCertificateArgs { Server = "sql01", Sha256 = new string('a', 64), FileName = "sql01.cer" });
        Assert.Equal("sql01.cer", t);
        Assert.Equal(new string('a', 64), p!["sha256"]);
        var secretValue = "t" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        var init = new InitRepositoryArgs { JobName = "Zdj", Password = secretValue, Destination = new DestinationSpec { Scheme = "sftp", Root = "/b", HostKey = "SHA256:abc" } };
        var (t2, p2) = AgentCallAudit.Describe(init);
        Assert.Equal("Zdj", t2);
        Assert.Equal("SHA256:abc", p2!["host_key"]);
        Assert.DoesNotContain(secretValue, string.Join(";", p2.Values), StringComparison.Ordinal);
        Assert.Equal("Agent/Prune", AgentCallAudit.Operation("/kobian.v1.Agent/Prune"));
    }
}
