using System.Text;
using System.Text.Json;
using Kobian.Agent.Audit;
using Kobian.Agent.Notifications;
using Kobian.Catalog.Sqlite;
using Kobian.Notifications.Webhooks;
using Kobian.Tests.Unit.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Notifications;

/// <summary>it. 112 — D9.11 raport dzienny 07:00 z kotwicą audytu (RD-61) przez outbox; KB-6301 w audycie, gdy powiadomienie przechodzi w dead. Dane syntetyczne.</summary>
public sealed class DailyReportTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-dr-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

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

    private void Sql(string sql)
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Theory, Trait("Plan", "D9.11")]
    [InlineData("2026-10-17T04:59:00Z", "2026-10-16T05:00:00Z")] // 06:59 CEST → termin wczorajszy
    [InlineData("2026-10-17T05:00:00Z", "2026-10-17T05:00:00Z")] // 07:00 CEST
    [InlineData("2026-12-01T06:30:00Z", "2026-12-01T06:00:00Z")] // CET (zima)
    public void Slot_is_seven_local_time(string now, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DailyReportSchedule.LastSlotUtc(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), Warsaw));

    [Fact, Trait("Plan", "D9.11"), Trait("RD", "RD-61")]
    public async Task Report_summarizes_the_day_carries_the_audit_anchor_and_is_queued_once_per_slot()
    {
        Sql("""
            INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES
              ('j1','Faktury-2026','files',x'7B7D',x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z'),
              ('j2','Kadry-Tajne','files',x'7B7D',x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');
            INSERT INTO run_runs(run_id,job_id,cause,state,finished_at) VALUES
              ('r1','j1','schedule','Succeeded','2026-10-16T20:00:00.000Z'),
              ('r2','j1','schedule','Succeeded','2026-10-17T01:00:00.000Z'),
              ('r3','j2','schedule','Failed','2026-10-17T02:00:00.000Z'),
              ('r4','j2','schedule','Warning','2026-10-17T03:00:00.000Z'),
              ('r0','j1','schedule','Failed','2026-10-15T20:00:00.000Z');
            INSERT INTO core_outbox(channel,payload,attempts,next_attempt_at,state) VALUES ('webhook',x'7B7D',6,'2026-10-17T00:00:00.000Z','dead');
            """);
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 17, 4, 50, 0, TimeSpan.Zero)); // 06:50 CEST
        var audit = new AuditLogService(Catalog, _writer, clock);
        await audit.AppendAsync(new AuditRecord("S-1-5-21-1", "Administrator", "cli", "job.run", "j1", null, "allowed", null), _ct);
        clock.Now = new DateTimeOffset(2026, 10, 17, 4, 55, 0, TimeSpan.Zero);
        var anchor = await audit.CreateAnchorAsync(_ct);
        Assert.NotNull(anchor);

        clock.Now = new DateTimeOffset(2026, 10, 17, 5, 1, 0, TimeSpan.Zero); // 07:01 CEST
        var svc = new DailyReportService(Catalog, _writer, "00112233445566778899aabbccddeeff", clock, Warsaw);
        var d = await svc.TickAsync(_ct);
        Assert.NotNull(d);
        Assert.Equal(2, d.Results["succeeded"]);
        Assert.Equal(1, d.Results["failed"]);
        Assert.Equal(1, d.Results["warning"]);
        Assert.Equal(["j2"], d.FailedJobIds); // r0 spoza okna
        Assert.Equal(1, d.DeadNotifications);
        Assert.Null(await svc.TickAsync(_ct)); // ten sam termin — bez duplikatu

        var payload = (byte[])Scalar("SELECT payload FROM core_outbox WHERE state='pending';")!;
        var json = Encoding.UTF8.GetString(payload);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(DailyReportService.EventType, doc.RootElement.GetProperty("type").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(anchor.Text, data.GetProperty("auditAnchor").GetProperty("text").GetString());
        Assert.NotNull(AuditAnchor.TryParse(data.GetProperty("auditAnchor").GetProperty("text").GetString())); // wejście dla `audit verify --anchor`
        Assert.Equal("2026-10-16T05:00:00Z", data.GetProperty("fromUtc").GetString());
        Assert.DoesNotContain("Kadry-Tajne", json, StringComparison.Ordinal); // bez nazw zadań (SEC-9.6)
        Assert.DoesNotContain("Faktury", json, StringComparison.Ordinal);
        Assert.True(audit.Verify(AuditAnchor.TryParse(anchor.Text)).BrokenSeq is null);

        clock.Now = clock.Now.AddDays(1);
        Assert.NotNull(await svc.TickAsync(_ct)); // następny dzień = następny raport
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM core_outbox WHERE state='pending';"));
        Assert.Equal(0, svc.Preview(clock.Now.AddDays(-1), clock.Now).Results["succeeded"]);
    }

    private sealed class Gone : IWebhookSender
    {
        public Task<DeliveryResult> SendAsync(string eventId, ReadOnlyMemory<byte> body, CancellationToken ct) => Task.FromResult(DeliveryResult.Permanent);
    }

    [Fact, Trait("Plan", "D9.10")]
    public async Task Dead_notification_is_recorded_in_the_audit_chain_with_KB_6301()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero));
        await _writer.ExecuteControlAsync((c, tx) =>
        {
            OutboxDispatcher.Enqueue(c, tx, "{\"id\":\"e9\"}"u8.ToArray(), clock.Now);
            return 0;
        }, _ct);
        var audit = new AuditLogService(Catalog, _writer, clock);
        var d = new OutboxDispatcher(Catalog, _writer, new Gone(), clock) { Audit = audit };
        Assert.Equal(0, await d.TickAsync(_ct));
        Assert.Equal("dead", Scalar("SELECT state FROM core_outbox;"));
        Assert.Equal("KB-6301", Scalar("SELECT kb_code FROM audit_log WHERE operation='notify.webhook';"));
        Assert.Equal("failed", Scalar("SELECT outcome FROM audit_log WHERE operation='notify.webhook';"));
        Assert.Null(audit.Verify().BrokenSeq);
    }
}
