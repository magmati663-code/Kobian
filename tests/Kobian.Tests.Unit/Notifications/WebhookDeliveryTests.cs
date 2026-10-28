using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Agent.Jobs;
using Kobian.Agent.Notifications;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Notifications.Webhooks;
using Kobian.Providers.Storage.Local;
using Kobian.Tests.Unit.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Notifications;

/// <summary>
/// it. 111 — D9.10/D9.6 webhook: nagłówki i podpis weryfikowalny po stronie odbiorcy, bez przekierowań, odpowiedź ≤ 64 KiB, SSRF przy połączeniu;
/// transakcyjny outbox: zdarzenie run.finished razem z wynikiem uruchomienia, ponowienia 1/5/30 min/2 h/6 h → dead; konfiguracja z pliku sekretu. Dane syntetyczne.
/// </summary>
public sealed class WebhookDeliveryTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-wh-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);

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

    private sealed class Recorder(HttpStatusCode status, int bodyBytes = 0, string? location = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<byte[]> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var resp = new HttpResponseMessage(status) { Content = new ByteArrayContent(new byte[bodyBytes]) };
            if (location is not null) resp.Headers.Location = new Uri(location);
            return resp;
        }
    }

    [Fact, Trait("Plan", "D9.10"), Trait("SEC", "SEC-9.3")]
    public async Task Request_carries_id_timestamp_and_a_signature_the_receiver_can_verify()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero));
        var rec = new Recorder(HttpStatusCode.NoContent);
        using var s = new WebhookSender("https://hooks.example.com/kobian", Secret, clock, rec);
        var body = "{\"id\":\"e1\"}"u8.ToArray();
        Assert.Equal(DeliveryResult.Delivered, await s.SendAsync("e1", body, _ct));
        var req = Assert.Single(rec.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("e1", req.Headers.GetValues("Kobian-Webhook-Id").Single());
        var ts = req.Headers.GetValues("Kobian-Webhook-Timestamp").Single();
        Assert.Equal(clock.Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ts);
        var sig = req.Headers.GetValues("Kobian-Webhook-Signature").Single();
        Assert.True(WebhookSignature.Verify(Secret, ts, sig, rec.Bodies[0], clock.Now.ToUnixTimeSeconds()));
        Assert.False(WebhookSignature.Verify(RandomNumberGenerator.GetBytes(32), ts, sig, rec.Bodies[0], clock.Now.ToUnixTimeSeconds()));
        Assert.Equal("application/cloudevents+json", req.Content!.Headers.ContentType!.MediaType);
    }

    [Theory, Trait("Plan", "D9.10")]
    [InlineData(HttpStatusCode.OK, DeliveryResult.Delivered)]
    [InlineData(HttpStatusCode.Found, DeliveryResult.Retry)]          // przekierowanie NIE jest śledzone (AllowAutoRedirect = false)
    [InlineData(HttpStatusCode.InternalServerError, DeliveryResult.Retry)]
    [InlineData(HttpStatusCode.TooManyRequests, DeliveryResult.Retry)]
    [InlineData(HttpStatusCode.Gone, DeliveryResult.Permanent)]
    public async Task Status_codes_map_to_delivery_results(HttpStatusCode code, DeliveryResult expected)
    {
        var rec = new Recorder(code, bodyBytes: 200_000, location: code == HttpStatusCode.Found ? "https://evil.example/x" : null);
        using var s = new WebhookSender("https://hooks.example.com/kobian", Secret, handler: rec);
        Assert.Equal(expected, await s.SendAsync("e1", "{}"u8.ToArray(), _ct));
        Assert.Single(rec.Requests);
        Assert.False(WebhookSender.CreateHandler().AllowAutoRedirect);
    }

    [Fact, Trait("SEC", "SEC-9.3")]
    public void Ssrf_checks_url_and_resolved_addresses_and_secret_length()
    {
        Assert.Throws<ArgumentException>(() => new WebhookSender("http://hooks.example.com/x", Secret));
        Assert.Throws<ArgumentException>(() => new WebhookSender("https://10.0.0.5/x", Secret));
        Assert.Throws<ArgumentException>(() => new WebhookSender("https://hooks.example.com/x", new byte[16]));
        var resolved = new[] { IPAddress.Parse("10.1.2.3"), IPAddress.Parse("127.0.0.1"), IPAddress.Parse("169.254.169.254"), IPAddress.Parse("::1"), IPAddress.Parse("93.184.216.34") };
        Assert.Equal([IPAddress.Parse("93.184.216.34")], WebhookSender.PublicOnly(resolved)); // DNS rebinding: adres prywatny odrzucony przy połączeniu
        Assert.Empty(WebhookSender.PublicOnly([IPAddress.Parse("192.168.1.10"), IPAddress.Parse("fd00::1")]));
    }

    private sealed class FakeSender(params DeliveryResult[] results) : IWebhookSender
    {
        private int _i;

        public List<string> Ids { get; } = [];

        public Task<DeliveryResult> SendAsync(string eventId, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            Ids.Add(eventId);
            return Task.FromResult(results[Math.Min(_i++, results.Length - 1)]);
        }
    }

    private (string State, int Attempts, string Next) Outbox()
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT state, attempts, next_attempt_at FROM core_outbox ORDER BY id DESC LIMIT 1;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.GetString(0), r.GetInt32(1), r.GetString(2));
    }

    [Fact, Trait("Plan", "D9.10"), Trait("Plan", "D9.6")]
    public async Task Finished_run_is_queued_in_the_same_transaction_and_retried_until_dead()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "repo"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.txt"), "dane");
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = Kobian.Crypto.RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(_dir, "k.key"), prov.KeyFileJson);
        File.WriteAllText(Path.Combine(_dir, "kobian.repo"), prov.HeaderJson);
        File.WriteAllText(Path.Combine(_dir, "pw.txt"), pw);
        static string J(string s) => JsonSerializer.Serialize(s);
        var def = "{\"version\":1,\"sources\":[" + J(Path.Combine(_dir, "src")) + "],\"destination\":{\"scheme\":\"local\",\"root\":" + J(Path.Combine(_dir, "repo")) + "},\"key\":{\"file\":"
            + J(Path.Combine(_dir, "k.key")) + ",\"passwordFile\":" + J(Path.Combine(_dir, "pw.txt")) + ",\"repoHeader\":" + J(Path.Combine(_dir, "kobian.repo")) + "}}";
        using (var c = CatalogBootstrap.Open(Catalog))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ('j1','Faktury-2026','files',$d,x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');";
            cmd.Parameters.AddWithValue("$d", Encoding.UTF8.GetBytes(def));
            cmd.ExecuteNonQuery();
        }
        var jobs = new CatalogJobService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, new byte[16], TimeProvider.System) { NotifyMinimal = true };
        Assert.Equal(JobResult.Succeeded, (await jobs.RunAsync("Faktury-2026", _ct)).Result);
        await _writer.FlushAsync(_ct);
        byte[] payload;
        using (var c = CatalogBootstrap.Open(Catalog, readOnly: true))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT payload FROM core_outbox WHERE state='pending';";
            payload = (byte[])cmd.ExecuteScalar()!;
        }
        var json = Encoding.UTF8.GetString(payload);
        Assert.Contains("pl.kobian.run.finished.v1", json, StringComparison.Ordinal);
        Assert.Contains("succeeded", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Faktury-2026", json, StringComparison.Ordinal); // tryb minimalny (SEC-9.6)
        Assert.DoesNotContain(_dir.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.Ordinal); // bez ścieżek

        var clock = new ManualTime(DateTimeOffset.UtcNow.AddMinutes(1));
        var sender = new FakeSender(DeliveryResult.Retry);
        var d = new OutboxDispatcher(Catalog, _writer, sender, clock);
        Assert.Equal(0, await d.TickAsync(_ct));
        Assert.Equal(("pending", 1), (Outbox().State, Outbox().Attempts));
        Assert.Equal(0, await d.TickAsync(_ct)); // za wcześnie — następna próba za 1 min
        Assert.Single(sender.Ids);
        foreach (var wait in OutboxDispatcher.RetrySchedule)
        {
            clock.Now += wait;
            await d.TickAsync(_ct);
        }
        Assert.Equal(("dead", 6), (Outbox().State, Outbox().Attempts)); // 1 + 5 ponowień, potem dead
        Assert.All(sender.Ids, id => Assert.Equal(sender.Ids[0], id)); // ten sam Webhook-Id przy każdej próbie (deduplikacja u odbiorcy)
        Assert.Matches("^[0-9a-f]{32}$", sender.Ids[0]);
    }

    [Fact, Trait("Plan", "D9.10")]
    public void Config_requires_https_public_url_and_a_hex_secret_file()
    {
        var secrets = Path.Combine(_dir, "secrets");
        Directory.CreateDirectory(secrets);
        Dictionary<string, string?> env = new() { [WebhookConfig.UrlVariable] = null };
        string? Env(string k) => env.GetValueOrDefault(k);
        Assert.Equal((null, null), WebhookConfig.Load(Env, secrets)); // wyłączone
        env[WebhookConfig.UrlVariable] = "https://hooks.example.com/k";
        Assert.Contains(WebhookConfig.SecretFile, WebhookConfig.Load(Env, secrets).Error!, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(secrets, WebhookConfig.SecretFile), "za-krotki");
        Assert.NotNull(WebhookConfig.Load(Env, secrets).Error);
        File.WriteAllText(Path.Combine(secrets, WebhookConfig.SecretFile), Convert.ToHexStringLower(Secret) + "\n");
        var (s, e) = WebhookConfig.Load(Env, secrets);
        Assert.Null(e);
        Assert.Equal(Secret, s!.Secret);
        Assert.False(s.Minimal);
        env[WebhookConfig.UrlVariable] = "https://192.168.1.1/k";
        Assert.Contains("KB-6302", WebhookConfig.Load(Env, secrets).Error!, StringComparison.Ordinal);
    }
}
