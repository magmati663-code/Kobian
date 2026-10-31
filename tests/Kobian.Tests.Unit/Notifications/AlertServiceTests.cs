using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Agent.Hosting;
using Kobian.Agent.Notifications;
using Kobian.Catalog.Sqlite;
using Kobian.Notifications.Alerts;
using Kobian.Notifications.Alerts.Rules;
using Kobian.Notifications.Email;
using Kobian.Notifications.Events;
using Kobian.Notifications.Webhooks;
using Kobian.Tests.Unit.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Notifications;

/// <summary>
/// BL-45/BL-82 (D9.7, D9.8, R2.9.6): pętla alertów w Agencie na wirtualnym zegarze — otwarcie, deduplikacja, przypomnienie/eskalacja, rozwiązanie,
/// dokładnie jeden wpis outboxa na przejście i kanał, trwałość po restarcie, brak danych klienta w zdarzeniu. Katalog tymczasowy, dane syntetyczne.
/// </summary>
public sealed class AlertServiceTests : IAsyncLifetime
{
    private const string HostHex = "00112233445566778899aabbccddeeff";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-alert-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero));
    private CatalogWriter _writer = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");

    private static readonly NotifyChannel[] TwoChannels = [new(OutboxDispatcher.WebhookChannel, Minimal: true), new(OutboxDispatcher.EmailChannel, Minimal: false, ProblemsOnly: true)];

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private AlertService Service(IReadOnlyList<NotifyChannel>? channels = null, bool securityUpdate = false) =>
        new(Catalog, _writer, HostHex, _clock) { Channels = channels ?? TwoChannels, SecurityUpdateAvailable = securityUpdate };

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

    private List<(string Channel, string Json)> Outbox()
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT channel, payload FROM core_outbox ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read()) list.Add((r.GetString(0), Encoding.UTF8.GetString((byte[])r[1])));
        return list;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private void Job(string id, string name, byte[]? definition = null) =>
        Sql("INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ($i,$n,'files',$d,x'00',1,'UTC','skip','2026-10-01T00:00:00.000Z','2026-10-01T00:00:00.000Z');",
            ("$i", id), ("$n", name), ("$d", definition ?? "{}"u8.ToArray()));

    private int _runs;

    private void Run(string jobId, string state, DateTimeOffset finished, int attempt = 3) =>
        Sql("INSERT INTO run_runs(run_id,job_id,cause,state,attempt,started_at,finished_at) VALUES ($i,$j,'schedule',$s,$a,$f,$f);",
            ("$i", "r" + (++_runs)), ("$j", jobId), ("$s", state), ("$a", attempt), ("$f", Iso(finished)));

    private static string Type(string json)
    {
        using var d = JsonDocument.Parse(json);
        return d.RootElement.GetProperty("type").GetString()!;
    }

    [Fact, Trait("Plan", "D9.7"), Trait("AC", "AC-05.1"), Trait("BL", "BL-82")]
    public async Task Failed_job_opens_once_reminds_after_24h_and_resolves_with_exactly_one_entry_per_transition_and_channel()
    {
        Job("j1", "Księgowość");
        Run("j1", "Failed", _clock.Now.AddMinutes(-10));
        var svc = Service();

        var t1 = await svc.TickAsync(_ct);
        var opened = Assert.Single(t1, x => x.Code == "A01");
        Assert.Equal(AlertTransition.Opened, opened.Transition);
        Assert.Equal(2, opened.OutboxEntries);
        var box = Outbox();
        Assert.Equal(2, box.Count);
        Assert.Equal(new[] { OutboxDispatcher.WebhookChannel, OutboxDispatcher.EmailChannel }, box.Select(b => b.Channel));
        Assert.All(box, b => Assert.Equal(AlertEvent.OpenedType, Type(b.Json)));
        Assert.Equal("notified", Scalar("SELECT state FROM alert_alerts WHERE code='A01' AND scope='job:j1';"));

        // Deduplikacja: ten sam warunek w kolejnych taktach — bez nowych wpisów.
        for (var i = 0; i < 5; i++)
        {
            await _clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Empty(await svc.TickAsync(_ct));
        }
        Assert.Equal(2, Outbox().Count);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM alert_alerts;"));

        // Przypomnienie co 24 h (krytyczny) + eskalacja po 24 h — jedno zdarzenie na kanał, oznaczone escalated.
        _clock.Now = _clock.Now.AddHours(24);
        var t2 = Assert.Single(await svc.TickAsync(_ct));
        Assert.Equal(AlertTransition.Reminder, t2.Transition);
        Assert.True(t2.Escalated);
        box = Outbox();
        Assert.Equal(4, box.Count);
        Assert.All(box.Skip(2), b => Assert.Equal(AlertEvent.ReminderType, Type(b.Json)));
        Assert.Contains("\"escalated\":true", box[3].Json, StringComparison.Ordinal);
        Assert.Equal(1L, Scalar("SELECT reminders FROM alert_alerts;"));

        // Sukces → rozwiązanie z powiadomieniem „rozwiązano”.
        Run("j1", "Succeeded", _clock.Now);
        await _clock.Advance(TimeSpan.FromMinutes(1));
        var t3 = Assert.Single(await svc.TickAsync(_ct));
        Assert.Equal(AlertTransition.Resolved, t3.Transition);
        box = Outbox();
        Assert.Equal(6, box.Count);
        Assert.All(box.Skip(4), b => Assert.Equal(AlertEvent.ResolvedType, Type(b.Json)));
        Assert.Equal("resolved", Scalar("SELECT state FROM alert_alerts;"));
        Assert.NotNull(Scalar("SELECT resolved_at FROM alert_alerts;"));
        Assert.Empty(await svc.TickAsync(_ct));
        Assert.Empty(svc.Active());

        // Ponowna porażka = nowy alert (nowy wiersz), stary zostaje w historii.
        Run("j1", "Failed", _clock.Now);
        await _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AlertTransition.Opened, Assert.Single(await svc.TickAsync(_ct)).Transition);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM alert_alerts;"));
    }

    [Fact, Trait("Plan", "D9.8"), Trait("BL", "BL-82")]
    public async Task Failed_run_with_retries_left_waits_for_the_retry_grace_before_A01()
    {
        Job("j1", "Pliki");
        Run("j1", "Failed", _clock.Now, attempt: 0);
        var svc = Service();
        Assert.DoesNotContain(await svc.TickAsync(_ct), x => x.Code == "A01");
        await _clock.Advance(AlertFactsCollector.RetryGrace);
        Assert.Contains(await svc.TickAsync(_ct), x => x.Code == "A01" && x.Transition == AlertTransition.Opened);
    }

    [Fact, Trait("Plan", "D9.7"), Trait("BL", "BL-82")]
    public async Task Restart_of_the_agent_does_not_duplicate_notifications()
    {
        Job("j1", "ERP");
        Run("j1", "Failed", _clock.Now.AddHours(-1));
        Assert.Single(await Service().TickAsync(_ct), x => x.Code == "A01");
        Assert.Equal(2, Outbox().Count);

        // „Restart”: nowa instancja usługi i nowy pisarz na tym samym katalogu — stan z alert_alerts.
        await _writer.DisposeAsync();
        _writer = new CatalogWriter(Catalog);
        var restarted = Service();
        await _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(await restarted.TickAsync(_ct));
        Assert.Equal(2, Outbox().Count);

        // Przypomnienie liczone od wysyłki sprzed restartu (last_notified_at trwały), nie od startu.
        _clock.Now = _clock.Now.AddHours(23);
        Assert.Empty(await restarted.TickAsync(_ct));
        _clock.Now = _clock.Now.AddHours(1);
        Assert.Equal(AlertTransition.Reminder, Assert.Single(await restarted.TickAsync(_ct)).Transition);
        Assert.Equal(4, Outbox().Count);
    }

    [Fact, Trait("Plan", "D9.8"), Trait("Plan", "9.5"), Trait("BL", "BL-82")]
    public async Task No_successful_backup_beyond_RPO_raises_A03_for_scheduled_jobs_only()
    {
        Job("j1", "Dzienne");
        Job("j2", "Ręczne");
        Sql("INSERT INTO sched_triggers(trigger_id,job_id,kind,spec,enabled) VALUES ('t1','j1','cron',$s,1);", ("$s", Encoding.UTF8.GetBytes("""{"cron":"0 22 * * *"}""")));
        Sql("INSERT INTO repo_repositories(repo_id,destination_id,format_version,header_sha256,state) VALUES ('ab12','d1',1,x'00','ok');");
        var last = _clock.Now.AddHours(-37); // cykl 24 h × 1,5 = 36 h (≥ 26 h — 9.4)
        Sql("INSERT INTO snap_snapshots(snap_id,repo_id,job_id,run_id,host_id,time_utc,mode,root_tree) VALUES ('s1','ab12','j1','r','h',$t,0,x'00');", ("$t", Iso(last)));
        Sql("INSERT INTO snap_snapshots(snap_id,repo_id,job_id,run_id,host_id,time_utc,mode,root_tree) VALUES ('s2','ab12','j2','r','h',$t,0,x'00');", ("$t", Iso(last.AddDays(-30))));

        var t = await Service().TickAsync(_ct);
        var a03 = Assert.Single(t, x => x.Code == "A03");
        Assert.Equal("job:j1", a03.Scope); // zadanie ręczne (bez harmonogramu) nie ma oczekiwanego cyklu
        var json = Outbox().First(b => b.Json.Contains("\"A03\"", StringComparison.Ordinal)).Json;
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("KB-6103", data.GetProperty("kb").GetString());
        Assert.Equal(last.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), data.GetProperty("lastSuccessUtc").GetString());
        Assert.Equal("job:j1", doc.RootElement.GetProperty("subject").GetString());
    }

    [Fact, Trait("Plan", "D9.8"), Trait("SEC", "SEC-14.5"), Trait("BL", "BL-82")]
    public async Task Pending_server_identity_raises_A09_until_the_administrator_decides()
    {
        var endpoint = "sftp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) + ".klient.example:22";
        Sql("""
            INSERT INTO repo_dest_identities(destination_id,fingerprint,kind,state,endpoint,display,first_seen,last_seen)
            VALUES ('dest-0123456789abcdef','SHA256:AAAABBBBccccDDDD','ssh-hostkey','pending',$e,NULL,'2026-10-17T09:00:00.000Z','2026-10-17T09:00:00.000Z');
            """, ("$e", endpoint));
        var svc = Service();
        var a09 = Assert.Single(await svc.TickAsync(_ct), x => x.Code == "A09");
        Assert.Equal("dest:dest-0123456789abcdef", a09.Scope);
        Assert.Equal(AlertTransition.Opened, a09.Transition);
        Assert.All(Outbox(), b => Assert.DoesNotContain(endpoint, b.Json, StringComparison.Ordinal)); // adres serwera klienta nie wychodzi
        Assert.All(Outbox(), b => Assert.Contains("\"pendingIdentities\":1", b.Json, StringComparison.Ordinal));

        Sql("UPDATE repo_dest_identities SET state='pinned', approved_by_sid='S-1-5-21-1', approved_at='2026-10-17T10:01:00.000Z';");
        await _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AlertTransition.Resolved, Assert.Single(await svc.TickAsync(_ct), x => x.Code == "A09").Transition);
    }

    [Fact, Trait("Plan", "D9.8"), Trait("SEC", "SEC-17.4"), Trait("BL", "BL-82")]
    public async Task Retention_hold_A17_denials_A15_and_runtime_update_A14_are_host_alerts()
    {
        Sql("INSERT INTO core_retention_hold(id,held_at,kb_code,delta_sec,source) VALUES (1,'2026-10-17T09:00:00.000Z','KB-6117',93600,'scheduler');");
        for (var i = 1; i <= 5; i++)
        {
            var at = Iso(_clock.Now.AddMinutes(-i));
            // Łańcuch audytu nieistotny w tym teście — wyzwalacze wymagają tylko ciągłości seq.
            Sql("INSERT INTO audit_log(seq,at_utc,actor_sid,role,channel,operation,outcome,prev_hash,hash) VALUES ($s,$a,'S-1-5-21-9','Reader','api','JobService.DeleteJob','denied',zeroblob(32),zeroblob(32));",
                ("$s", i), ("$a", at));
        }
        var svc = Service(securityUpdate: true);
        var t = await svc.TickAsync(_ct);
        Assert.Contains(t, x => x.Code == "A17" && x.Scope == "host" && x.Transition == AlertTransition.Opened);
        Assert.Contains(t, x => x.Code == "A15" && x.Scope == "host" && x.Transition == AlertTransition.Opened);
        Assert.Contains(t, x => x.Code == "A14" && x.Scope == "host" && x.Transition == AlertTransition.Opened);
        Assert.Contains(Outbox(), b => b.Json.Contains("\"clockDeltaSeconds\":93600", StringComparison.Ordinal));

        Sql("DELETE FROM core_retention_hold;");
        _clock.Now = _clock.Now.AddHours(2); // odmowy poza oknem 1 h
        var t2 = await svc.TickAsync(_ct);
        Assert.Contains(t2, x => x.Code == "A17" && x.Transition == AlertTransition.Resolved);
        Assert.Contains(t2, x => x.Code == "A15" && x.Transition == AlertTransition.Resolved);
        Assert.DoesNotContain(t2, x => x.Code == "A14");
    }

    [Fact, Trait("Plan", "D9.8"), Trait("BL", "BL-82")]
    public async Task Damaged_or_suspect_repository_raises_A04()
    {
        Job("j1", "Serwer");
        Sql("INSERT INTO repo_repositories(repo_id,destination_id,format_version,header_sha256,state) VALUES ('c0ffee','d1',1,x'00','ok');");
        Sql("INSERT INTO snap_snapshots(snap_id,repo_id,job_id,run_id,host_id,time_utc,mode,root_tree,suspect) VALUES ('s1','c0ffee','j1','r','h','2026-10-16T22:00:00.000Z',0,x'00',1);");
        var svc = Service();
        Assert.Contains(await svc.TickAsync(_ct), x => x.Code == "A04" && x.Scope == "repo:c0ffee" && x.Transition == AlertTransition.Opened);
        // Nowsza, poprawna kopia zadania (następna kopia pełna — 3.7) zdejmuje warunek.
        Sql("INSERT INTO snap_snapshots(snap_id,repo_id,job_id,run_id,host_id,time_utc,mode,root_tree) VALUES ('s2','c0ffee','j1','r','h','2026-10-17T09:00:00.000Z',0,x'00');");
        Assert.Contains(await svc.TickAsync(_ct), x => x.Code == "A04" && x.Transition == AlertTransition.Resolved);
    }

    [Fact, Trait("Plan", "9.4"), Trait("BL", "BL-82")]
    public async Task Warning_run_A02_is_state_only_without_immediate_notification()
    {
        Job("j1", "Zdjęcia");
        Run("j1", "Warning", _clock.Now.AddMinutes(-3));
        var svc = Service();
        var a02 = Assert.Single(await svc.TickAsync(_ct), x => x.Code == "A02");
        Assert.Equal(0, a02.OutboxEntries);
        Assert.Empty(Outbox());
        Assert.Contains(svc.Active(), a => a.Code == "A02" && a.Severity == "warning" && a.Subject == "Zdjęcia");
    }

    [Fact, Trait("SEC", "SEC-9.6"), Trait("NFR", "NFR-11"), Trait("BL", "BL-82")]
    public async Task Alert_event_carries_no_customer_data_and_minimal_channel_has_no_job_name()
    {
        var fileName = "Umowa-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)) + ".docx";
        var folder = Path.Combine(_dir, "Klient-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        var jobName = "Kadry-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var definition = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, sources = new[] { Path.Combine(folder, fileName) }, destination = new { scheme = "local", root = folder } });
        Job("j1", jobName, definition);
        Sql("INSERT INTO snap_paths(path_id,parent_id,name) VALUES (1,NULL,$n);", ("$n", fileName));
        Run("j1", "Failed", _clock.Now.AddHours(-1));
        Sql("UPDATE run_runs SET kb_code='KB-2101';");

        await Service().TickAsync(_ct);
        var box = Outbox();
        Assert.Equal(2, box.Count);
        foreach (var (_, json) in box)
        {
            Assert.DoesNotContain(fileName, json, StringComparison.Ordinal);
            Assert.DoesNotContain(folder, json, StringComparison.Ordinal);
            Assert.DoesNotContain(_dir, json, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(jobName, box.Single(b => b.Channel == OutboxDispatcher.WebhookChannel).Json, StringComparison.Ordinal); // tryb minimalny
        Assert.Contains(jobName, box.Single(b => b.Channel == OutboxDispatcher.EmailChannel).Json, StringComparison.Ordinal); // nazwa zadania — pole dozwolone poza minimalnym
        Assert.All(box, b => Assert.True(b.Json.Length < CloudEvent.MaxBytes));
    }

    [Fact, Trait("Plan", "D9.6"), Trait("BL", "BL-82")]
    public async Task Alert_reaches_the_webhook_through_the_outbox_dispatcher()
    {
        Job("j1", "Biuro");
        Run("j1", "Failed", _clock.Now.AddHours(-1));
        await Service([new NotifyChannel(OutboxDispatcher.WebhookChannel, false)]).TickAsync(_ct);
        var sender = new Capture();
        Assert.Equal(1, await new OutboxDispatcher(Catalog, _writer, sender, _clock).TickAsync(_ct));
        var (id, body) = Assert.Single(sender.Sent);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(id, root.GetProperty("id").GetString()); // Webhook-Id = id zdarzenia (D9.10)
        Assert.Equal("1.0", root.GetProperty("specversion").GetString());
        Assert.Equal("urn:kobian:host:" + HostHex, root.GetProperty("source").GetString());
        Assert.Equal(AlertEvent.OpenedType, root.GetProperty("type").GetString());
        var data = root.GetProperty("data");
        Assert.Equal("A01", data.GetProperty("code").GetString());
        Assert.Equal("critical", data.GetProperty("severity").GetString());
        Assert.Equal("KB-6101", data.GetProperty("kb").GetString());
        Assert.Equal("Alert.A01.Title", data.GetProperty("messageKey").GetString());
        Assert.Equal("Biuro", data.GetProperty("job").GetProperty("name").GetString());
        Assert.Equal("j1", data.GetProperty("scope").GetProperty("id").GetString());
    }

    private sealed class Capture : IWebhookSender
    {
        public List<(string Id, string Body)> Sent { get; } = [];

        public Task<DeliveryResult> SendAsync(string eventId, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            Sent.Add((eventId, Encoding.UTF8.GetString(body.Span)));
            return Task.FromResult(DeliveryResult.Delivered);
        }
    }

    [Fact, Trait("Plan", "R2.6.1"), Trait("BL", "BL-82")]
    public async Task GetStatus_lists_active_alerts_security_first()
    {
        Job("j1", "Faktury");
        Run("j1", "Failed", _clock.Now.AddHours(-1));
        Sql("INSERT INTO core_retention_hold(id,held_at,kb_code,delta_sec,source) VALUES (1,'2026-10-17T09:00:00.000Z','KB-6117',-600,'scheduler');");
        await Service().TickAsync(_ct);
        var s = CatalogStatusReader.ReadStatus(Catalog, "1", _clock.Now);
        Assert.Equal(new[] { "A17", "A01" }, s.Alerts.Select(a => a.Code));
        Assert.Equal("security", s.Alerts[0].Severity);
        Assert.Equal("host", s.Alerts[0].ScopeKind);
        Assert.Equal("KB-6117", s.Alerts[0].Kb);
        Assert.Equal("Faktury", s.Alerts[1].Subject);
        Assert.Equal("job", s.Alerts[1].ScopeKind);
        Assert.Equal("notified", s.Alerts[1].State);
        Assert.Equal(_clock.Now.ToUnixTimeSeconds(), s.Alerts[1].OpenedUnixSeconds);
    }

    [Fact, Trait("Plan", "D9.8"), Trait("BL", "BL-82")]
    public void Every_rule_is_either_active_or_explicitly_listed_as_inactive()
    {
        var all = AlertRuleCatalog.LoadEmbedded().Rules.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(AlertFactsCollector.ActiveRules.Intersect(AlertFactsCollector.InactiveRules.Keys));
        Assert.True(all.SetEquals(AlertFactsCollector.ActiveRules.Union(AlertFactsCollector.InactiveRules.Keys)));
    }

    [Fact, Trait("Plan", "D9.8"), Trait("BL", "BL-82")]
    public async Task Inactive_rules_never_open_even_with_an_empty_catalog()
    {
        var t = await Service().TickAsync(_ct);
        Assert.Empty(t);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM alert_alerts;"));
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-82")]
    public async Task Foreign_row_with_unknown_scope_kind_does_not_block_the_loop()
    {
        Sql("INSERT INTO alert_alerts(alert_id,code,scope,severity,state,opened_at) VALUES ($i,'A01','foo:bar','critical','notified','2026-10-17T09:00:00.000Z');",
            ("$i", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))));
        Job("j1", "Nowe");
        Run("j1", "Failed", _clock.Now.AddHours(-1));
        Assert.Contains(await Service().TickAsync(_ct), x => x.Code == "A01" && x.Scope == "job:j1");
        Assert.DoesNotContain(Service().Active(), a => a.Subject == "bar");
    }

    [Fact, Trait("Plan", "R2.9.6"), Trait("BL", "BL-82")]
    public void Alert_table_rejects_rows_outside_the_format()
    {
        const string ok = "INSERT INTO alert_alerts(alert_id,code,scope,severity,state,opened_at) VALUES ($i,$c,$s,'critical','notified','2026-10-17T10:00:00.000Z');";
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Sql(ok, ("$i", id), ("$c", "A01"), ("$s", "job:j1"));
        Assert.Throws<SqliteException>(() => Sql(ok, ("$i", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))), ("$c", "A01"), ("$s", "job:j1"))); // jeden otwarty na klucz
        Assert.Throws<SqliteException>(() => Sql(ok, ("$i", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))), ("$c", "A02"), ("$s", @"job:C:\Users\x")));
        Assert.Throws<SqliteException>(() => Sql(ok, ("$i", "nothex"), ("$c", "A02"), ("$s", "host")));
        Assert.Throws<SqliteException>(() => Sql("UPDATE alert_alerts SET state='resolved';")); // resolved wymaga resolved_at
        Sql("UPDATE alert_alerts SET state='resolved', resolved_at='2026-10-17T11:00:00.000Z';");
        Sql(ok, ("$i", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))), ("$c", "A01"), ("$s", "job:j1")); // po rozwiązaniu — nowy alert dozwolony
    }
}

/// <summary>BL-82: render alert.* w kanale e-mail (PL/EN, tryb minimalny, temat ≤ 120) — z koperty jak z outboxa.</summary>
public sealed class AlertEmailTests
{
    private static EmailSettings Settings(string lang = "pl", bool minimal = false) =>
        new("smtp.example.com", 587, SmtpTlsMode.StartTls, "kobian@example.com", ["admin@example.com"], null, minimal, true, lang, "SRV-01");

    private static byte[] Event(AlertTransition t, bool escalated = false, string code = "A03", string scope = "job:7a2e01", string? jobName = "ERP-noc", bool minimal = false) =>
        AlertEvent.Build("e0e1e2e3", "00112233445566778899aabbccddeeff",
            new AlertNotice("0123456789abcdef0123456789abcdef", code, AlertSeverity.Critical, "availability", code == "A03" ? "KB-6103" : null, scope, t,
                new DateTimeOffset(2026, 10, 16, 21, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 17, 21, 0, 0, TimeSpan.Zero), escalated ? 1 : 0, escalated, jobName,
                new DateTimeOffset(2026, 10, 15, 20, 4, 0, TimeSpan.Zero)), minimal);

    private static string Text(MimeKit.MimeMessage m) => m.TextBody ?? "";

    [Fact, Trait("Plan", "9.7"), Trait("BL", "BL-82")]
    public void Opened_alert_renders_in_polish_with_rule_title_action_and_kb()
    {
        using var m = EmailMessageBuilder.Build(Settings(), Event(AlertTransition.Opened));
        Assert.StartsWith("[Kobian] Alert A03: Brak udanej kopii", m.Subject, StringComparison.Ordinal);
        Assert.Contains("ERP-noc", m.Subject, StringComparison.Ordinal);
        Assert.True(m.Subject!.Length <= EmailMessageBuilder.MaxSubjectLength);
        var text = Text(m);
        Assert.Contains("Krytyczny", text, StringComparison.Ordinal);
        Assert.Contains("KB-6103", text, StringComparison.Ordinal);
        Assert.Contains("2026-10-15 20:04:00", text, StringComparison.Ordinal);
        Assert.Contains("uruchom kopię ręcznie", text, StringComparison.Ordinal);
        Assert.Equal("pl.kobian.alert.opened.v1", m.Headers["X-Kobian-Event-Type"]);
        Assert.Contains("<table", m.HtmlBody, StringComparison.Ordinal);
    }

    [Fact, Trait("Plan", "9.7"), Trait("BL", "BL-82")]
    public void Escalation_and_resolution_render_in_english()
    {
        using var esc = EmailMessageBuilder.Build(Settings("en"), Event(AlertTransition.Reminder, escalated: true));
        Assert.StartsWith("[Kobian] Escalation A03: No successful backup", esc.Subject, StringComparison.Ordinal);
        using var res = EmailMessageBuilder.Build(Settings("en"), Event(AlertTransition.Resolved));
        Assert.StartsWith("[Kobian] Resolved A03", res.Subject, StringComparison.Ordinal);
        Assert.Contains("no action is needed", Text(res), StringComparison.Ordinal);
        Assert.DoesNotContain("run the backup manually", Text(res), StringComparison.Ordinal);
    }

    [Fact, Trait("SEC", "SEC-9.6"), Trait("BL", "BL-82")]
    public void Minimal_mode_mail_has_no_job_name_even_if_payload_carries_it()
    {
        using var m = EmailMessageBuilder.Build(Settings(minimal: true), Event(AlertTransition.Opened));
        Assert.DoesNotContain("ERP-noc", m.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("ERP-noc", Text(m), StringComparison.Ordinal);
        Assert.Contains("zadanie 7a2e01", m.Subject, StringComparison.Ordinal);
    }

    [Fact, Trait("Plan", "9.7"), Trait("BL", "BL-82")]
    public void Every_rule_has_title_and_action_in_both_languages_and_destination_scope_renders()
    {
        foreach (var lang in new[] { "pl", "en" })
            for (var i = 1; i <= 17; i++)
            {
                var code = $"A{i:00}";
                using var m = EmailMessageBuilder.Build(Settings(lang), Event(AlertTransition.Opened, code: code, scope: "dest:dest-0123456789abcdef", jobName: null));
                Assert.Contains(code + ":", m.Subject, StringComparison.Ordinal);
                Assert.DoesNotContain("A??", m.Subject, StringComparison.Ordinal);
                Assert.Contains("dest-0123456789abcdef", Text(m), StringComparison.Ordinal);
            }
    }

    [Fact, Trait("SEC", "SEC-9.1"), Trait("BL", "BL-82")]
    public void Hostile_job_name_is_sanitized_in_subject()
    {
        using var m = EmailMessageBuilder.Build(Settings(), Event(AlertTransition.Opened, jobName: "x\r\nBcc: evil@example.com\u202E"));
        Assert.DoesNotContain("\r", m.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", m.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", m.Subject, StringComparison.Ordinal);
        Assert.Single(m.To);
        Assert.Empty(m.Bcc);
    }

    [Fact, Trait("Plan", "D9.6"), Trait("BL", "BL-82")]
    public void Alert_event_rejects_values_outside_the_closed_format()
    {
        var at = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        AlertNotice N(string scope = "host", string code = "A01", string? kb = null, IReadOnlyDictionary<string, long>? det = null) =>
            new("0123456789abcdef0123456789abcdef", code, AlertSeverity.Critical, "availability", kb, scope, AlertTransition.Opened, at, at, 0, false, Details: det);
        Assert.Throws<ArgumentException>(() => AlertEvent.Build("e", "h", N(scope: @"job:C:\x"), false));
        Assert.Throws<ArgumentException>(() => AlertEvent.Build("e", "h", N(scope: "path:/etc"), false));
        Assert.Throws<ArgumentException>(() => AlertEvent.Build("e", "h", N(code: "X1"), false));
        Assert.Throws<ArgumentException>(() => AlertEvent.Build("e", "h", N(kb: "KB-61x1"), false));
        Assert.Throws<ArgumentException>(() => AlertEvent.Build("e", "h", N(det: new Dictionary<string, long> { ["fileName"] = 1 }), false));
        var ok = Encoding.UTF8.GetString(AlertEvent.Build("e", "h", N(), true));
        Assert.Contains("\"kind\":\"host\"", ok, StringComparison.Ordinal);
    }
}
