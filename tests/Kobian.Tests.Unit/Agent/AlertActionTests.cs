using Kobian.Agent.Audit;
using Kobian.Agent.Hosting;
using Kobian.Agent.Notifications;
using Kobian.Api.V1;
using Kobian.Catalog.Sqlite;
using Kobian.Cli.Commands;
using Kobian.Domain;
using Kobian.Notifications.Alerts;
using Kobian.Notifications.Events;
using Kobian.Tests.Unit.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-45 (D9.7, R2.9.6): ręczne potwierdzanie i wyciszanie alertów — <see cref="AlertService"/> (maszyna stanów w jednej transakcji z taktem),
/// RPC AckAlert / SilenceAlert przez prawdziwy host gRPC, role (Operator potwierdza, tylko Administrator wycisza), audyt, CLI <c>kobian alerts …</c>.
/// Wirtualny zegar, katalog tymczasowy, dane syntetyczne.
/// </summary>
public sealed class AlertActionTests : IAsyncLifetime
{
    private const string HostHex = "00112233445566778899aabbccddeeff";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-alertact-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero));
    private readonly List<WebApplication> _hosts = [];
    private CatalogWriter _writer = null!;
    private AuditLogService _audit = null!;
    private AlertService _svc = null!;
    private int _runs;

    private string Catalog => Path.Combine(_dir, "catalog.db");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        _audit = new AuditLogService(Catalog, _writer, TimeProvider.System);
        _svc = new AlertService(Catalog, _writer, HostHex, _clock) { Channels = [new(OutboxDispatcher.WebhookChannel, Minimal: true)] };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private void Sql(string sql, params (string Name, object? Value)[] p)
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Zadanie z nieudanym uruchomieniem → po jednym takcie otwarty (powiadomiony) alert A01; zwraca jego alert_id.</summary>
    private async Task<string> OpenA01(string jobId = "j1")
    {
        Sql("INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ($i,$n,'files',$d,x'00',1,'UTC','skip','2026-10-01T00:00:00.000Z','2026-10-01T00:00:00.000Z');",
            ("$i", jobId), ("$n", "Zadanie " + jobId), ("$d", "{}"u8.ToArray()));
        FailRun(jobId);
        Assert.Contains(await _svc.TickAsync(_ct), x => x.Code == "A01" && x.Transition == AlertTransition.Opened);
        return (string)Scalar($"SELECT alert_id FROM alert_alerts WHERE scope='job:{jobId}';")!;
    }

    private void FailRun(string jobId) =>
        Sql("INSERT INTO run_runs(run_id,job_id,cause,state,attempt,started_at,finished_at) VALUES ($i,$j,'schedule','Failed',3,$f,$f);",
            ("$i", "r" + (++_runs)), ("$j", jobId), ("$f", Iso(_clock.Now.AddMinutes(-10))));

    private long OutboxCount() => (long)Scalar("SELECT COUNT(*) FROM core_outbox;")!;

    [Fact, Trait("Plan", "D9.7"), Trait("AC", "AC-05.1"), Trait("BL", "BL-45")]
    public async Task Acknowledge_moves_notified_to_acknowledged_once_and_stops_reminders()
    {
        var id = await OpenA01();
        Assert.Equal(1, OutboxCount());

        var ack = await _svc.AcknowledgeAsync(id, _ct);
        Assert.Equal(("acknowledged", (string?)null), (ack.State, ack.Code));
        Assert.Equal("acknowledged", Scalar("SELECT state FROM alert_alerts;"));
        Assert.NotNull(Scalar("SELECT acknowledged_at FROM alert_alerts;"));

        // Drugie potwierdzenie i nieznany identyfikator = KB-6120, bez zmiany stanu.
        Assert.Equal(KbCode.AlertNotActionable, (await _svc.AcknowledgeAsync(id, _ct)).Code);
        Assert.Equal(KbCode.AlertNotActionable, (await _svc.AcknowledgeAsync(new string('a', 32), _ct)).Code);
        Assert.Equal(KbCode.AlertNotActionable, (await _svc.AcknowledgeAsync("x'; DROP TABLE alert_alerts;--", _ct)).Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM alert_alerts;"));

        // Po potwierdzeniu: brak przypomnień co 24 h (D9.7); eskalacja po 24 h od otwarcia nadal działa (jednorazowo),
        // ponowne powiadomienie dopiero po 72 h od potwierdzenia, jeśli warunek trwa.
        _clock.Now = _clock.Now.AddHours(25);
        var esc = Assert.Single(await _svc.TickAsync(_ct), x => x.Code == "A01");
        Assert.True(esc.Escalated);
        Assert.Equal("acknowledged", Scalar("SELECT state FROM alert_alerts WHERE code='A01';"));
        _clock.Now = _clock.Now.AddHours(25);
        Assert.DoesNotContain(await _svc.TickAsync(_ct), x => x.Code == "A01");
        _clock.Now = _clock.Now.AddHours(23);
        var again = Assert.Single(await _svc.TickAsync(_ct), x => x.Code == "A01");
        Assert.Equal(AlertTransition.Reminder, again.Transition);
        Assert.Equal("notified", Scalar("SELECT state FROM alert_alerts WHERE code='A01';"));
    }

    [Fact, Trait("Plan", "D9.7"), Trait("BL", "BL-45")]
    public async Task Acknowledged_alert_is_still_resolved_with_a_notification_when_the_condition_clears()
    {
        var id = await OpenA01();
        Assert.Null((await _svc.AcknowledgeAsync(id, _ct)).Code);
        Sql("INSERT INTO run_runs(run_id,job_id,cause,state,attempt,started_at,finished_at) VALUES ('ok1','j1','schedule','Succeeded',1,$f,$f);", ("$f", Iso(_clock.Now.AddMinutes(1))));
        await _clock.Advance(TimeSpan.FromMinutes(2));
        var res = Assert.Single(await _svc.TickAsync(_ct));
        Assert.Equal(AlertTransition.Resolved, res.Transition);
        Assert.Equal("resolved", Scalar("SELECT state FROM alert_alerts;"));
        // Rozwiązanego alertu nie da się już potwierdzić ani wyciszyć.
        Assert.Equal(KbCode.AlertNotActionable, (await _svc.AcknowledgeAsync(id, _ct)).Code);
        Assert.Equal(KbCode.AlertNotActionable, (await _svc.SilenceAsync(id, TimeSpan.FromHours(1), _ct)).Code);
    }

    [Fact, Trait("Plan", "D9.7"), Trait("BL", "BL-45")]
    public async Task Silence_suppresses_notifications_until_it_expires_then_reminds_once()
    {
        var id = await OpenA01();
        var s = await _svc.SilenceAsync(id, TimeSpan.FromMinutes(60), _ct);
        Assert.Equal(("silenced", (string?)null), (s.State, s.Code));
        Assert.Equal(Iso(_clock.Now.AddMinutes(60)), (string)Scalar("SELECT silenced_until FROM alert_alerts;")!);

        // Wyciszony nie można wyciszyć ani potwierdzić ponownie (tylko z „notified”).
        Assert.Equal(KbCode.AlertNotActionable, (await _svc.SilenceAsync(id, TimeSpan.FromMinutes(5), _ct)).Code);
        Assert.Equal(KbCode.AlertNotActionable, (await _svc.AcknowledgeAsync(id, _ct)).Code);

        await _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Empty(await _svc.TickAsync(_ct));
        Assert.Equal(1, OutboxCount());
        await _clock.Advance(TimeSpan.FromMinutes(31));
        var back = Assert.Single(await _svc.TickAsync(_ct));
        Assert.Equal(AlertTransition.Reminder, back.Transition);
        Assert.Equal("notified", Scalar("SELECT state FROM alert_alerts;"));
        Assert.IsType<DBNull>(Scalar("SELECT silenced_until FROM alert_alerts;"));
        Assert.Equal(2, OutboxCount());
    }

    [Fact, Trait("Plan", "D9.7"), Trait("BL", "BL-45")]
    public async Task Silence_is_capped_at_seven_days_and_a_non_positive_duration_is_rejected()
    {
        var id = await OpenA01();
        Assert.Equal(KbCode.AlertNotActionable, (await _svc.SilenceAsync(id, TimeSpan.Zero, _ct)).Code);
        Assert.Equal("notified", Scalar("SELECT state FROM alert_alerts;"));
        Assert.Null((await _svc.SilenceAsync(id, TimeSpan.FromDays(30), _ct)).Code);
        Assert.Equal(Iso(_clock.Now.AddDays(7)), (string)Scalar("SELECT silenced_until FROM alert_alerts;")!);
    }

    [Fact, Trait("Plan", "D9.7"), Trait("BL", "BL-45")]
    public async Task Manual_change_survives_a_restart_of_the_service()
    {
        var id = await OpenA01();
        Assert.Null((await _svc.AcknowledgeAsync(id, _ct)).Code);
        var restarted = new AlertService(Catalog, _writer, HostHex, _clock) { Channels = [new(OutboxDispatcher.WebhookChannel, Minimal: true)] };
        _clock.Now = _clock.Now.AddHours(10);
        Assert.Empty(await restarted.TickAsync(_ct));
        Assert.Equal("acknowledged", Scalar("SELECT state FROM alert_alerts;"));
        Assert.Equal(1, OutboxCount());
    }

    [Fact, Trait("Plan", "D9.7"), Trait("BL", "BL-45")]
    public async Task Status_exposes_the_alert_id_used_by_the_actions_and_nothing_customer_specific()
    {
        var id = await OpenA01();
        var info = Assert.Single(_svc.Active());
        Assert.Equal(id, info.AlertId);
        Assert.Matches("^[0-9a-f]{32}$", info.AlertId);
        Assert.True(AlertService.IsAlertId(id));
        Assert.False(AlertService.IsAlertId("ABCDEF0123456789ABCDEF0123456789")); // wielkie litery — nie jest to format Agenta
        Assert.False(AlertService.IsAlertId(null));
        Assert.False(AlertService.IsAlertId(id + "0"));
    }

    [Fact, Trait("D", "D2.9"), Trait("BL", "BL-45")]
    public async Task Backend_validates_arguments_and_reports_unavailable_without_the_alert_service()
    {
        var id = await OpenA01();
        var b = new CatalogAgentBackend(Catalog, "t") { Alerts = _svc };
        Assert.Equal(KbCode.JobDefinitionInvalid, (await b.AckAlertAsync(new AckAlertArgs { AlertId = "nie-hex" }, _ct)).Code);
        foreach (var bad in new[] { 0, -5, 10081 })
            Assert.Equal(KbCode.JobDefinitionInvalid, (await b.SilenceAlertAsync(new SilenceAlertArgs { AlertId = id, DurationMinutes = bad }, _ct)).Code);
        Assert.Equal("notified", Scalar("SELECT state FROM alert_alerts;"));

        var unknown = await b.AckAlertAsync(new AckAlertArgs { AlertId = new string('0', 32) }, _ct);
        Assert.Equal((Kobian.Api.V1.OverallState.Failed, KbCode.AlertNotActionable), (unknown.Result, unknown.Code));
        var ok = await b.SilenceAlertAsync(new SilenceAlertArgs { AlertId = id, DurationMinutes = 10080 }, _ct);
        Assert.Equal((Kobian.Api.V1.OverallState.Ok, "silenced"), (ok.Result, ok.State));

        var none = new CatalogAgentBackend(Catalog, "t");
        Assert.Equal(KbCode.OperationNotAvailable, (await none.AckAlertAsync(new AckAlertArgs { AlertId = id }, _ct)).Code);
        Assert.Equal(KbCode.OperationNotAvailable, (await none.SilenceAlertAsync(new SilenceAlertArgs { AlertId = id, DurationMinutes = 5 }, _ct)).Code);

        Assert.Equal(new MethodPolicy.Entry(AgentRole.Operator, true), MethodPolicy.Find("/kobian.v1.Agent/AckAlert"));
        Assert.Equal(new MethodPolicy.Entry(AgentRole.Administrator, true), MethodPolicy.Find("/kobian.v1.Agent/SilenceAlert"));
        var (t1, p1) = AgentCallAudit.Describe(new AckAlertArgs { AlertId = id });
        Assert.Equal(id, t1);
        Assert.Null(p1);
        var (t2, p2) = AgentCallAudit.Describe(new SilenceAlertArgs { AlertId = id, DurationMinutes = 90 });
        Assert.Equal(id, t2);
        Assert.Equal("90", p2!["minutes"]);
    }

    private sealed class SidResolver(AgentRole role, string sid) : ICallerRoleResolver
    {
        public AgentRole Resolve(Grpc.Core.ServerCallContext context) => role;

        public CallerInfo ResolveCaller(Grpc.Core.ServerCallContext context) => new(role, sid);
    }

    private async Task<string> StartAgent(AgentRole role, string sid)
    {
        var pipe = "kobian-test-" + Guid.NewGuid().ToString("N");
        var backend = new CatalogAgentBackend(Catalog, "9.9.9") { Audit = _audit, Alerts = _svc };
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

    [Fact, Trait("D", "D9.7"), Trait("SEC", "D2.9"), Trait("Category", "Integration")]
    public async Task Cli_lists_alerts_operator_acknowledges_only_administrator_silences_and_every_call_is_audited()
    {
        var id = await OpenA01();
        var reader = await StartAgent(AgentRole.Reader, "S-1-5-21-1-2-3-1001");
        var op = await StartAgent(AgentRole.Operator, "S-1-5-21-1-2-3-1002");
        var admin = await StartAgent(AgentRole.Administrator, "S-1-5-21-1-2-3-500");

        // Lista pochodzi z GetStatus (Reader) — zawiera identyfikator; kod wyjścia 1 = są alerty.
        var list = await Cli(reader, "alerts", "list", "--json");
        Assert.Equal(CliApp.ExitWarning, list.Code);
        using (var doc = System.Text.Json.JsonDocument.Parse(list.Out))
        {
            var a = Assert.Single(doc.RootElement.GetProperty("alerts").EnumerateArray());
            Assert.Equal((id, "A01", "notified", "job"), (a.GetProperty("id").GetString(), a.GetProperty("code").GetString(), a.GetProperty("state").GetString(), a.GetProperty("scopeKind").GetString()));
        }
        Assert.Contains(id + "\tcritical\tnotified\tA01\tjob:", (await Cli(reader, "alerts")).Out, StringComparison.Ordinal);

        // Reader nie potwierdza; Operator potwierdza, ale nie wycisza; Administrator wycisza.
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(reader, "alerts", "ack", id)).Code);
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(op, "alerts", "silence", id, "30")).Code);
        Assert.Equal("notified", Scalar("SELECT state FROM alert_alerts;"));

        var ack = await Cli(op, "alerts", "ack", id, "--json");
        Assert.Equal(CliApp.ExitOk, ack.Code);
        Assert.Contains("\"state\":\"acknowledged\"", ack.Out, StringComparison.Ordinal);
        var again = await Cli(op, "alerts", "ack", id);
        Assert.Equal(CliApp.ExitFailed, again.Code);
        Assert.Contains(KbCode.AlertNotActionable, again.Out, StringComparison.Ordinal);

        // Składnia: czas poza 1…10080 min i brak liczby to błąd użycia (nic nie jest wysyłane do Agenta).
        Assert.Equal(CliApp.ExitUsage, (await Cli(admin, "alerts", "silence", id, "0")).Code);
        Assert.Equal(CliApp.ExitUsage, (await Cli(admin, "alerts", "silence", id, "10081")).Code);
        Assert.Equal(CliApp.ExitUsage, (await Cli(admin, "alerts", "silence", id)).Code);

        // Wyciszanie dopiero z „notified”: po potwierdzeniu — KB-6120; na świeżym alercie — OK.
        Assert.Equal(CliApp.ExitFailed, (await Cli(admin, "alerts", "silence", id, "30")).Code);
        var id2 = await OpenA01("j2");
        var sil = await Cli(admin, "alerts", "silence", id2, "30", "--json");
        Assert.Equal(CliApp.ExitOk, sil.Code);
        Assert.Contains("\"state\":\"silenced\"", sil.Out, StringComparison.Ordinal);
        await _writer.FlushAsync(_ct);

        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT operation, outcome, kb_code, target, actor_sid FROM audit_log ORDER BY seq;";
        using var r = cmd.ExecuteReader();
        var rows = new List<(string Op, string Outcome, string? Kb, string? Target, string Actor)>();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4)));
        Assert.Equal(["Agent/AckAlert", "Agent/SilenceAlert", "Agent/AckAlert", "Agent/AckAlert", "Agent/SilenceAlert", "Agent/SilenceAlert"], rows.Select(x => x.Op));
        Assert.Equal(["denied", "denied", "allowed", "failed", "failed", "allowed"], rows.Select(x => x.Outcome));
        Assert.Equal(KbCode.AlertNotActionable, rows[3].Kb);
        Assert.Equal(id2, rows[5].Target);
        Assert.Equal("S-1-5-21-1-2-3-500", rows[5].Actor);
        Assert.Null(_audit.Verify().BrokenSeq);
    }
}
