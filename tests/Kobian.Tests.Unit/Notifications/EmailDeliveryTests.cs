using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Agent.Audit;
using Kobian.Agent.Jobs;
using Kobian.Agent.Notifications;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Notifications.Email;
using Kobian.Notifications.Webhooks;
using Kobian.Providers.Storage.Local;
using Kobian.Tests.Unit.Engine;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Data.Sqlite;
using MimeKit;
using Xunit;

namespace Kobian.Tests.Unit.Notifications;

/// <summary>
/// BL-45 — wysyłka e-mail: klasyfikacja błędów (przejściowy → ponowienie, trwały → dead + audyt z kodem KB), limit czasu, kanał „email” w trwałym outboxie
/// obok webhooka (osobne wpisy, ten sam id zdarzenia, tryb minimalny per kanał, filtr „tylko problemy”), raport dzienny do obu kanałów oraz test protokołu
/// adaptera MailKit na serwerze in-proc 127.0.0.1 (jawne SMTP z flagą; STARTTLS z niezaufanym certyfikatem i brak STARTTLS — odmowa). Dane syntetyczne.
/// </summary>
public sealed class EmailDeliveryTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-mail-" + Guid.NewGuid().ToString("N"));
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
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static EmailSettings Settings(SmtpCredential? cred = null, SmtpTlsMode tls = SmtpTlsMode.StartTls, string host = "smtp.example.com", int port = 587) =>
        new(host, port, tls, "kobian@example.com", ["admin@example.com"], cred, false, true, "pl", "SRV-01");

    /// <summary>Atrapa transportu: kolejne wywołania rzucają wyjątki ze skryptu (null = sukces); zapisuje wiadomości.</summary>
    private sealed class FakeTransport(params Exception?[] script) : ISmtpTransport
    {
        private int _i;

        public List<(SmtpEndpoint Endpoint, SmtpCredential? Credential, string Raw, string? MessageId)> Calls { get; } = [];

        public bool Hang { get; init; }

        public async Task SendAsync(SmtpEndpoint endpoint, SmtpCredential? credential, MimeMessage message, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await message.WriteToAsync(ms, ct);
            Calls.Add((endpoint, credential, Encoding.UTF8.GetString(ms.ToArray()), message.MessageId));
            if (Hang) await Task.Delay(Timeout.Infinite, ct);
            var ex = script.Length == 0 ? null : script[Math.Min(_i++, script.Length - 1)];
            if (ex is not null) throw ex;
        }
    }

    private static byte[] Payload(string id = "0a1b2c3d") => EmailConfigTests.RunPayload(id, "Faktury-2026");

    [Fact, Trait("Plan", "BL-45")]
    public async Task Successful_send_uses_endpoint_and_credential_from_settings()
    {
        var cred = new SmtpCredential("u", Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)));
        var t = new FakeTransport([null]);
        var r = await new EmailSender(Settings(cred), t).DeliverAsync("0a1b2c3d", Payload(), _ct);
        Assert.Equal(DeliveryResult.Delivered, r.Result);
        var call = Assert.Single(t.Calls);
        Assert.Equal(new SmtpEndpoint("smtp.example.com", 587, SmtpTlsMode.StartTls), call.Endpoint);
        Assert.Same(cred, call.Credential);
        Assert.DoesNotContain(cred.Secret, call.Raw, StringComparison.Ordinal);
    }

    public static TheoryData<Exception, DeliveryResult, string> Errors() => new()
    {
        { new AuthenticationException("535 5.7.8 bad credentials"), DeliveryResult.Permanent, "KB-6201" },
        { new SmtpCommandException(SmtpErrorCode.SenderNotAccepted, (SmtpStatusCode)530, "5.7.0 Authentication required"), DeliveryResult.Permanent, "KB-6201" },
        { new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, (SmtpStatusCode)550, "5.7.30 Basic authentication is not supported for Client Submission"), DeliveryResult.Permanent, "KB-6201" },
        { new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "5.1.1 user unknown"), DeliveryResult.Permanent, "KB-6203" },
        { new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxBusy, "4.2.0 try later"), DeliveryResult.Retry, "KB-6203" },
        { new SmtpProtocolException("unexpected"), DeliveryResult.Retry, "KB-6203" },
        { new IOException("reset"), DeliveryResult.Retry, "KB-6203" },
        { new System.Net.Sockets.SocketException(10061), DeliveryResult.Retry, "KB-6203" },
        { new SslHandshakeException("untrusted"), DeliveryResult.Retry, "KB-6206" },
        { new NotSupportedException("The SMTP server does not support the STARTTLS extension."), DeliveryResult.Retry, "KB-6206" },
    };

    [Theory, Trait("Plan", "BL-45"), MemberData(nameof(Errors))]
    public async Task Transport_errors_are_classified_as_transient_or_permanent(Exception error, DeliveryResult expected, string code)
    {
        var r = await new EmailSender(Settings(), new FakeTransport(error)).DeliverAsync("e1", Payload(), _ct);
        Assert.Equal((expected, code), (r.Result, r.KbCode));
    }

    [Fact, Trait("Plan", "BL-45")]
    public async Task Unknown_exception_is_not_swallowed_and_unsupported_payload_is_permanent()
    {
        await Assert.ThrowsAsync<InvalidCastException>(() => new EmailSender(Settings(), new FakeTransport(new InvalidCastException())).DeliverAsync("e1", Payload(), _ct));
        var t = new FakeTransport([null]);
        var r = await new EmailSender(Settings(), t).DeliverAsync("e1", "{\"type\":\"pl.kobian.heartbeat.v1\",\"data\":{}}"u8.ToArray(), _ct);
        Assert.Equal((DeliveryResult.Permanent, "KB-6203"), (r.Result, r.KbCode));
        Assert.Empty(t.Calls);
    }

    [Fact, Trait("Plan", "BL-45"), Trait("NFR", "DoS")]
    public async Task Hanging_server_hits_the_attempt_timeout_and_is_retried_but_caller_cancellation_propagates()
    {
        var sender = new EmailSender(Settings(), new FakeTransport { Hang = true }) { Timeout = TimeSpan.FromMilliseconds(200) };
        var r = await sender.DeliverAsync("e1", Payload(), _ct);
        Assert.Equal(DeliveryResult.Retry, r.Result);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        cts.CancelAfter(100);
        var hanging = new EmailSender(Settings(), new FakeTransport { Hang = true });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging.DeliverAsync("e1", Payload(), cts.Token));
    }

    // ---------- outbox ----------

    private object? Scalar(string sql)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private Task<int> Enqueue(string channel, byte[] payload, DateTimeOffset now) =>
        _writer.ExecuteControlAsync((c, tx) =>
        {
            OutboxDispatcher.Enqueue(c, tx, channel, payload, now);
            return 0;
        }, _ct);

    [Fact, Trait("Plan", "BL-45"), Trait("Plan", "D9.10")]
    public async Task Email_channel_retries_transient_errors_and_leaves_webhook_entries_alone()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero));
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await Enqueue(OutboxDispatcher.WebhookChannel, Payload(id), clock.Now);
        await Enqueue(OutboxDispatcher.EmailChannel, Payload(id), clock.Now);
        var t = new FakeTransport(new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.MailboxBusy, "4.3.0 later"), null);
        var d = new OutboxDispatcher(Catalog, _writer, new EmailSender(Settings(), t), clock);
        Assert.Equal(0, await d.TickAsync(_ct));
        Assert.Equal("pending|1", Scalar("SELECT state || '|' || attempts FROM core_outbox WHERE channel='email';"));
        Assert.Equal(0, await d.TickAsync(_ct)); // przed terminem ponowienia
        clock.Now += OutboxDispatcher.RetrySchedule[0];
        Assert.Equal(1, await d.TickAsync(_ct));
        Assert.Equal("sent|2", Scalar("SELECT state || '|' || attempts FROM core_outbox WHERE channel='email';"));
        Assert.Equal("pending|0", Scalar("SELECT state || '|' || attempts FROM core_outbox WHERE channel='webhook';"));
        Assert.Equal(2, t.Calls.Count);
        Assert.All(t.Calls, c => Assert.Equal(id + "@example.com", c.MessageId)); // ten sam Message-ID przy ponowieniu (deduplikacja)
    }

    [Fact, Trait("Plan", "BL-45"), Trait("Plan", "D9.10")]
    public async Task Transient_failures_end_in_dead_after_the_schedule_and_permanent_ones_immediately_with_audit()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero));
        var audit = new AuditLogService(Catalog, _writer, clock);
        await Enqueue(OutboxDispatcher.EmailChannel, Payload("e1"), clock.Now);
        var d = new OutboxDispatcher(Catalog, _writer, new EmailSender(Settings(), new FakeTransport(new IOException("down"))), clock) { Audit = audit };
        await d.TickAsync(_ct);
        foreach (var wait in OutboxDispatcher.RetrySchedule)
        {
            clock.Now += wait;
            await d.TickAsync(_ct);
        }
        Assert.Equal("dead|6", Scalar("SELECT state || '|' || attempts FROM core_outbox;"));
        Assert.Equal("KB-6203", Scalar("SELECT kb_code FROM audit_log WHERE operation='notify.email';"));

        await Enqueue(OutboxDispatcher.EmailChannel, Payload("e2"), clock.Now);
        var auth = new OutboxDispatcher(Catalog, _writer, new EmailSender(Settings(), new FakeTransport(new AuthenticationException("535"))), clock) { Audit = audit };
        await auth.TickAsync(_ct);
        Assert.Equal("dead|1", Scalar("SELECT state || '|' || attempts FROM core_outbox ORDER BY id DESC LIMIT 1;"));
        Assert.Equal("KB-6201", Scalar("SELECT kb_code FROM audit_log WHERE operation='notify.email' ORDER BY seq DESC LIMIT 1;"));
        Assert.Null(audit.Verify().BrokenSeq);
    }

    private CatalogJobService SetupJob(IReadOnlyList<NotifyChannel> channels)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "repo"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.txt"), "dane");
        if (!File.Exists(Path.Combine(_dir, "k.key")))
        {
            var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            var prov = Kobian.Crypto.RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(_dir, "k.key"), prov.KeyFileJson);
            File.WriteAllText(Path.Combine(_dir, "kobian.repo"), prov.HeaderJson);
            File.WriteAllText(Path.Combine(_dir, "pw.txt"), pw);
            static string J(string s) => JsonSerializer.Serialize(s);
            var def = "{\"version\":1,\"sources\":[" + J(Path.Combine(_dir, "src")) + "],\"destination\":{\"scheme\":\"local\",\"root\":" + J(Path.Combine(_dir, "repo")) + "},\"key\":{\"file\":"
                + J(Path.Combine(_dir, "k.key")) + ",\"passwordFile\":" + J(Path.Combine(_dir, "pw.txt")) + ",\"repoHeader\":" + J(Path.Combine(_dir, "kobian.repo")) + "}}";
            using var c = CatalogBootstrap.Open(Catalog);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ('j1','Faktury-2026','files',$d,x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');";
            cmd.Parameters.AddWithValue("$d", Encoding.UTF8.GetBytes(def));
            cmd.ExecuteNonQuery();
        }
        return new CatalogJobService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, new byte[16], TimeProvider.System)
        {
            NotifyChannels = channels,
        };
    }

    [Fact, Trait("Plan", "BL-45"), Trait("Plan", "9.7"), Trait("SEC", "SEC-9.6")]
    public async Task Finished_run_is_queued_per_channel_with_its_own_minimal_mode_and_problems_only_filter()
    {
        var jobs = SetupJob([new(OutboxDispatcher.WebhookChannel, Minimal: true), new(OutboxDispatcher.EmailChannel, Minimal: false, ProblemsOnly: true)]);
        Assert.Equal(JobResult.Succeeded, (await jobs.RunAsync("Faktury-2026", _ct)).Result);
        await _writer.FlushAsync(_ct);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM core_outbox WHERE channel='email';")); // sukces nie idzie e-mailem (domyślnie tylko Warning/Failed)
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM core_outbox WHERE channel='webhook';"));

        var all = SetupJob([new(OutboxDispatcher.WebhookChannel, Minimal: true), new(OutboxDispatcher.EmailChannel, Minimal: false, ProblemsOnly: false)]);
        Assert.Equal(JobResult.Succeeded, (await all.RunAsync("Faktury-2026", _ct)).Result);
        await _writer.FlushAsync(_ct);
        var web = Encoding.UTF8.GetString((byte[])Scalar("SELECT payload FROM core_outbox WHERE channel='webhook' ORDER BY id DESC LIMIT 1;")!);
        var mail = Encoding.UTF8.GetString((byte[])Scalar("SELECT payload FROM core_outbox WHERE channel='email';")!);
        Assert.DoesNotContain("Faktury-2026", web, StringComparison.Ordinal);
        Assert.Contains("Faktury-2026", mail, StringComparison.Ordinal);
        Assert.Equal(JsonDocument.Parse(web).RootElement.GetProperty("id").GetString(), JsonDocument.Parse(mail).RootElement.GetProperty("id").GetString());
        Assert.DoesNotContain(_dir.Replace("\\", "\\\\", StringComparison.Ordinal), mail, StringComparison.Ordinal);

        var t = new FakeTransport([null]);
        var d = new OutboxDispatcher(Catalog, _writer, new EmailSender(Settings(), t), new ManualTime(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal(1, await d.TickAsync(_ct));
        Assert.Contains("Faktury-2026", Assert.Single(t.Calls).Raw, StringComparison.Ordinal);
    }

    [Fact, Trait("Plan", "BL-45"), Trait("RD", "RD-61")]
    public async Task Daily_report_is_queued_once_for_every_enabled_channel()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 17, 6, 0, 0, TimeSpan.Zero));
        var svc = new DailyReportService(Catalog, _writer, "00ff", clock, TimeZoneInfo.Utc) { Channels = [OutboxDispatcher.WebhookChannel, OutboxDispatcher.EmailChannel] };
        Assert.NotNull(await svc.TickAsync(_ct));
        Assert.Null(await svc.TickAsync(_ct)); // idempotencja w terminie
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM core_outbox;"));
        var t = new FakeTransport([null]);
        await new OutboxDispatcher(Catalog, _writer, new EmailSender(Settings(), t), clock).TickAsync(_ct);
        var m = Assert.Single(t.Calls);
        Assert.Contains("Raport dzienny", MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(m.Raw)), _ct).Subject, StringComparison.Ordinal);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM core_outbox WHERE state='pending' AND channel='webhook';"));
    }

    // ---------- protokół (MailKit ↔ serwer in-proc na 127.0.0.1) ----------

    [Fact, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.1")]
    public async Task MailKit_adapter_delivers_over_plain_smtp_only_in_insecure_mode_with_library_built_headers()
    {
        await using var server = new FakeSmtpServer(offerStartTls: false);
        var settings = Settings(tls: SmtpTlsMode.None, host: "127.0.0.1", port: server.Port);
        var payload = EmailConfigTests.RunPayload("feed01", "Faktury\r\nRCPT TO:<x@evil.example>\r\nBcc: x@evil.example");
        var r = await new EmailSender(settings, new MailKitSmtpTransport()).DeliverAsync("feed01", payload, _ct);
        Assert.Equal(DeliveryResult.Delivered, r.Result);
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("MAIL FROM:<kobian@example.com>", server.MailFrom, StringComparison.Ordinal);
        Assert.Equal("RCPT TO:<admin@example.com>", Assert.Single(server.RcptTo));
        Assert.DoesNotContain(server.Commands, c => c.Contains("evil", StringComparison.Ordinal)); // brak wstrzyknięcia komend SMTP
        Assert.Contains("EHLO " + MailKitSmtpTransport.HeloName, server.Commands);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
        var data = server.Data!;
        Assert.Contains("Message-Id: <feed01@example.com>", data, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\r\nBcc:", data, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.1")]
    public async Task Starttls_with_an_untrusted_certificate_is_refused_before_credentials_are_sent()
    {
        await using var server = new FakeSmtpServer(offerStartTls: true);
        var cred = new SmtpCredential("u", Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)));
        var r = await new EmailSender(Settings(cred, SmtpTlsMode.StartTls, "localhost", server.Port), new MailKitSmtpTransport()).DeliverAsync("e1", Payload(), _ct);
        Assert.Equal((DeliveryResult.Retry, "KB-6206"), (r.Result, r.KbCode)); // walidacja certyfikatu nie jest wyłączona
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("STARTTLS", server.Commands); // TLS podjęty; po stronie serwera TLS 1.3 kończy uzgadnianie, zanim klient odrzuci certyfikat
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase) || c.Contains(cred.Secret, StringComparison.Ordinal));
        Assert.Null(server.MailFrom);
    }

    [Fact, Trait("Plan", "BL-45"), Trait("SEC", "SEC-9.1")]
    public async Task Server_without_starttls_is_not_used_in_plain_text_when_tls_is_required()
    {
        await using var server = new FakeSmtpServer(offerStartTls: false);
        var r = await new EmailSender(Settings(null, SmtpTlsMode.StartTls, "127.0.0.1", server.Port), new MailKitSmtpTransport()).DeliverAsync("e1", Payload(), _ct);
        Assert.Equal((DeliveryResult.Retry, "KB-6206"), (r.Result, r.KbCode));
        Assert.Null(server.MailFrom); // brak cichego powrotu do jawnego tekstu
    }
}
