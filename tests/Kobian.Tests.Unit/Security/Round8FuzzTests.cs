using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Agent.Audit;
using Kobian.Agent.Jobs;
using Kobian.Agent.Notifications;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;
using Kobian.Format.Pack;
using Kobian.Format.Repo;
using Kobian.Notifications.Webhooks;
using Kobian.Providers.Storage.Throttling;
using Kobian.Tests.Unit.TestDoubles;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Security;

/// <summary>
/// Przegląd rundy 8 (it. 120, rola 11, SEC-3.3): losowe wejścia elementów dodanych w rundzie 8 — konfiguracja webhooka (zmienna środowiskowa + plik sekretu,
/// D9.10/SEC-9.3), polityka adresów webhooka (SSRF), raport dzienny z wierszy katalogu (D9.11), wyuczony limit AIMD z katalogu (D4.16 pkt 2), odbudowa indeksu
/// z trailerów packów-sierot (D8.10) przy wrogiej zawartości celu. Kryterium: tylko oczekiwany typ/kod wyjątku i zachowane niezmienniki. Ziarna stałe.
/// </summary>
public sealed class Round8FuzzTests : IDisposable
{
    private const string Alphabet = "ab./\\:@#?&%[]{}0123456789fFxX \t\r\n\0\u0001-_~*,;\"'<>|é😀\ud800";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-r8fuzz-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));

    public Round8FuzzTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static string RandomText(Random rnd, int max)
    {
        var n = rnd.Next(max + 1);
        var sb = new StringBuilder(n);
        for (var i = 0; i < n; i++)
            sb.Append(rnd.Next(12) == 0 ? (char)rnd.Next(0, 0x10000) : Alphabet[rnd.Next(Alphabet.Length)]);
        return sb.ToString();
    }

    private static string Mutate(Random rnd, string s)
    {
        var sb = new StringBuilder(s);
        for (var k = rnd.Next(1, 4); k > 0; k--)
        {
            switch (rnd.Next(4))
            {
                case 0 when sb.Length > 0: sb.Remove(rnd.Next(sb.Length), 1); break;
                case 1: sb.Insert(rnd.Next(sb.Length + 1), Alphabet[rnd.Next(Alphabet.Length)]); break;
                case 2 when sb.Length > 0: sb[rnd.Next(sb.Length)] = (char)rnd.Next(0x20, 0x7F); break;
                default: sb.Insert(rnd.Next(sb.Length + 1), RandomText(rnd, 6)); break;
            }
        }
        return sb.ToString();
    }

    // ---------- Webhook: KOBIAN_WEBHOOK_URL + webhook.secret ----------

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "SEC-9.3"), Trait("Plan", "D9.10")]
    public void Webhook_config_accepts_only_https_public_url_and_32_byte_hex_secret()
    {
        var rnd = new Random(1201);
        string[] urls = ["https://hooks.example.com/kobian", "https://hooks.example.com:443/x?a=1", "http://hooks.example.com/", "https://10.0.0.1/", "https://[::1]/",
            "https://user:pw@hooks.example.com/", "https://hooks.example.com:8443/", "https://localhost/", "https://printer.local/", "https://8.8.8.8/x", " https://a.example.org/ "];
        var secretsDir = Path.Combine(_dir, "secrets");
        Directory.CreateDirectory(secretsDir);
        var secretPath = Path.Combine(secretsDir, WebhookConfig.SecretFile);
        for (var i = 0; i < 3000; i++)
        {
            var url = rnd.Next(4) switch { 0 => null, 1 => RandomText(rnd, 40), _ => Mutate(rnd, urls[rnd.Next(urls.Length)]) };
            if (rnd.Next(5) == 0) url = urls[rnd.Next(urls.Length)];
            var hex = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(rnd.Next(0, 80)));
            var secret = rnd.Next(5) switch { 0 => RandomText(rnd, 100), 1 => Mutate(rnd, hex), 2 => "\n " + hex.ToUpperInvariant() + " \r\n", _ => hex };
            if (rnd.Next(15) == 0) File.Delete(secretPath);
            else if (rnd.Next(20) == 0) File.WriteAllText(secretPath, new string('a', 5000));
            else if (rnd.Next(20) == 0) File.WriteAllBytes(secretPath, [0xC3, 0x28, 0xFF, .. Encoding.ASCII.GetBytes(hex)]); // niepoprawne UTF-8
            else File.WriteAllBytes(secretPath, Encoding.UTF8.GetBytes(secret)); // samotne surogaty → U+FFFD
            var env = new Dictionary<string, string?> { [WebhookConfig.UrlVariable] = url, [WebhookConfig.MinimalVariable] = rnd.Next(3) == 0 ? "1" : RandomText(rnd, 2) };

            var (settings, error) = WebhookConfig.Load(k => env.GetValueOrDefault(k), secretsDir);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Null(settings);
                Assert.Null(error); // wyłączone — nie błąd
                continue;
            }
            if (settings is null)
            {
                Assert.False(string.IsNullOrEmpty(error));
                // NFR-11: komunikat nie niesie treści sekretu ani adresu (adres może mieć token w ścieżce)
                if (secret.Trim().Length >= 8) Assert.DoesNotContain(secret.Trim(), error, StringComparison.Ordinal);
                continue;
            }
            Assert.Null(error);
            Assert.True(WebhookUrlPolicy.IsAllowed(settings.Url));
            var u = new Uri(settings.Url);
            Assert.Equal(Uri.UriSchemeHttps, u.Scheme);
            Assert.True(u.IsDefaultPort);
            Assert.Equal("", u.UserInfo);
            Assert.True(settings.Secret.Length >= 32);
        }
    }

    [Theory, Trait("SEC", "SEC-3.3"), Trait("SEC", "SEC-9.3"), Trait("Plan", "D9.10")]
    [InlineData("https://a\uFFFF.example.com/")]
    [InlineData("https://a\uFDD0b.example.com/")]
    [InlineData("https://\u0378.example.com/")]
    public void Webhook_url_with_invalid_idn_host_is_refused_not_thrown(string url)
    {
        // Regresja z it. 120 (Niskie): Uri.IdnHost rzuca UriFormatException dla hosta niepoprawnego wg IDNA — WebhookConfig.Load (start usługi)
        // i konstruktor WebhookSender kończyły się wyjątkiem zamiast odmowy KB-6302 / kodu wyjścia 64.
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var u));
        Assert.Throws<UriFormatException>(() => u.IdnHost); // warunek wstępny: to wejście wywołuje wyjątek IDNA w .NET
        Assert.False(WebhookUrlPolicy.IsAllowed(url));
        var (settings, error) = WebhookConfig.Load(k => k == WebhookConfig.UrlVariable ? url : null, _dir);
        Assert.Null(settings);
        Assert.Contains("KB-6302", error!, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new WebhookSender(url, new byte[32]));
    }

    /// <summary>Wzorcowa (niezależna) klasyfikacja: adres wewnętrzny = prywatny/lokalny IPv4 albo IPv6 lokalny lub z osadzonym wewnętrznym IPv4.</summary>
    private static bool ReferenceInternal(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        var v6 = ip.GetAddressBytes();
        if (ip.IsIPv4MappedToIPv6) return ReferenceInternal(ip.MapToIPv4());
        if (v6.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0) return true; // ::/96 (nieokreślony, loopback, IPv4-compatible — przestarzałe)
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b) // NAT64: 64:ff9b::/96 (osadzony IPv4) i 64:ff9b:1::/48 (lokalny, RFC 8215)
            return (v6[4] == 0 && v6[5] == 1) || ReferenceInternal(new IPAddress(v6.AsSpan(12, 4)));
        if (v6[0] == 0x20 && v6[1] == 0x02) return ReferenceInternal(new IPAddress(v6.AsSpan(2, 4))); // 6to4
        return (v6[0] & 0xfe) == 0xfc || (v6[0] == 0xfe && (v6[1] & 0xc0) is 0x80 or 0xc0) || v6[0] == 0xff;
    }

    private static IPAddress RandomAddress(Random rnd)
    {
        var v4 = new byte[4];
        rnd.NextBytes(v4);
        byte[][] privateV4 = [[10, 1, 2, 3], [127, 0, 0, 1], [192, 168, 1, 1], [172, 16, 0, 1], [169, 254, 169, 254], [100, 64, 0, 1], [0, 0, 0, 0]];
        var embedded = rnd.Next(2) == 0 ? privateV4[rnd.Next(privateV4.Length)] : v4;
        var v6 = new byte[16];
        switch (rnd.Next(7))
        {
            case 0: return new IPAddress(embedded);
            case 1: return new IPAddress(embedded).MapToIPv6();
            case 2: v6[0] = 0x00; v6[1] = 0x64; v6[2] = 0xff; v6[3] = 0x9b; embedded.CopyTo(v6, 12); return new IPAddress(v6); // NAT64 64:ff9b::/96
            case 3: v6[0] = 0x20; v6[1] = 0x02; embedded.CopyTo(v6, 2); rnd.NextBytes(v6.AsSpan(6)); return new IPAddress(v6); // 6to4 2002::/16
            case 4: embedded.CopyTo(v6, 12); return new IPAddress(v6); // ::a.b.c.d
            case 5: v6[0] = (byte)(rnd.Next(2) == 0 ? 0xfd : 0xfe); v6[1] = (byte)rnd.Next(256); rnd.NextBytes(v6.AsSpan(2)); return new IPAddress(v6);
            default: rnd.NextBytes(v6); return new IPAddress(v6);
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "SEC-9.3"), Trait("Plan", "D9.10")]
    public void Webhook_address_policy_never_admits_internal_targets()
    {
        var rnd = new Random(1202);
        for (var i = 0; i < 20_000; i++)
        {
            var ip = RandomAddress(rnd);
            var pub = WebhookUrlPolicy.IsPublic(ip);
            if (ReferenceInternal(ip)) Assert.False(pub, ip.ToString());
            Assert.Equal(pub, WebhookSender.PublicOnly([ip]).Count == 1);
            var host = ip.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString();
            if (WebhookUrlPolicy.IsAllowed("https://" + host + "/hook")) Assert.False(ReferenceInternal(ip), ip.ToString());
        }
        // Zapisy IPv4 nietypowe (dziesiętne, szesnastkowe, ósemkowe, skrócone) — Uri normalizuje do IPv4 i polityka je odrzuca.
        foreach (var u in new[] { "https://2130706433/", "https://0x7f000001/", "https://017700000001/", "https://127.1/", "https://0x0a.0.0.1/", "https://10.1/",
                     "https://[::ffff:10.0.0.1]/", "https://[64:ff9b::a00:1]/", "https://[2002:a00:1::]/", "https://[::127.0.0.1]/", "https://[fe80::1%25eth0]/" })
            Assert.False(WebhookUrlPolicy.IsAllowed(u), u);
        // Losowe teksty: bez wyjątku.
        for (var i = 0; i < 20_000; i++) WebhookUrlPolicy.IsAllowed(rnd.Next(2) == 0 ? "https://" + RandomText(rnd, 30) : RandomText(rnd, 40));
    }

    // ---------- Raport dzienny: DailyReport.Build i wiersze katalogu ----------

    [Fact, Trait("SEC", "SEC-3.3"), Trait("Plan", "D9.11"), Trait("SEC", "SEC-9.6")]
    public void Daily_report_build_emits_bounded_valid_json_or_refuses_bad_job_ids()
    {
        var rnd = new Random(1203);
        for (var i = 0; i < 5000; i++)
        {
            var ids = Enumerable.Range(0, rnd.Next(0, DailyReportService.MaxFailedJobs + 1))
                .Select(_ => rnd.Next(3) == 0 ? RandomText(rnd, 70) : "job-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))).ToList();
            var results = new Dictionary<string, long> { ["succeeded"] = rnd.NextInt64(long.MinValue, long.MaxValue), ["failed"] = rnd.Next() };
            var anchor = rnd.Next(3) == 0 ? null : new AuditAnchor(rnd.NextInt64(), RandomNumberGenerator.GetBytes(32), RandomText(rnd, 30));
            var from = new DateTimeOffset(rnd.NextInt64(DateTimeOffset.MinValue.UtcTicks, DateTimeOffset.MaxValue.UtcTicks), TimeSpan.Zero);
            var d = new DailyReportData(from, from, results, ids, rnd.NextInt64(), anchor);
            byte[] payload;
            try
            {
                payload = DailyReportService.Build("e-" + i.ToString(CultureInfo.InvariantCulture), "00112233445566778899aabbccddeeff", DateTimeOffset.UnixEpoch, d);
            }
            catch (ArgumentException)
            {
                Assert.Contains(ids, id => id.Length is 0 or > 64 || !id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'));
                continue;
            }
            Assert.InRange(payload.Length, 1, Kobian.Notifications.Events.CloudEvent.MaxBytes);
            using var doc = JsonDocument.Parse(payload);
            Assert.Equal(DailyReportService.EventType, doc.RootElement.GetProperty("type").GetString());
            Assert.Equal(string.Join(',', ids), doc.RootElement.GetProperty("data").GetProperty("failedJobIds").GetString());
        }
    }

    private static void Sql(string catalog, string sql, params (string Name, object Value)[] args)
    {
        using var c = CatalogBootstrap.Open(catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("Plan", "D9.11")]
    public async Task Daily_report_tick_survives_hostile_catalog_rows()
    {
        var catalog = Path.Combine(_dir, "report.db");
        CatalogBootstrap.Bootstrap(catalog, TimeProvider.System);
        var rnd = new Random(1204);
        var clock = new FixedTime(new DateTimeOffset(2026, 10, 17, 7, 30, 0, TimeSpan.Zero));
        await using var writer = new CatalogWriter(catalog);
        var svc = new DailyReportService(catalog, writer, "00112233445566778899aabbccddeeff", clock, TimeZoneInfo.Utc);
        string[] states = ["Succeeded", "Failed", "Warning", "Cancelled", "Skipped", "Interrupted", "Queued", "failed", "", "Failed\0"];
        for (var i = 0; i < 120; i++)
        {
            var job = rnd.Next(3) switch { 0 => RandomText(rnd, 80), 1 => "job-" + i.ToString(CultureInfo.InvariantCulture), _ => "zadanie/" + RandomText(rnd, 8) };
            if (job.Length == 0) job = "x";
            Sql(catalog, "INSERT OR IGNORE INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ($j,$n,'files',x'7B7D',x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');",
                ("$j", job), ("$n", "n" + i.ToString(CultureInfo.InvariantCulture)));
            clock.Now = clock.Now.AddDays(1); // każdy przebieg — nowy termin raportu; okno = poprzednia doba do 07:00
            var finished = rnd.Next(4) switch { 0 => RandomText(rnd, 25), 3 => "9999-12-31T23:59:59.999Z", _ => OutboxDispatcher.Iso(clock.Now.AddHours(-3)) };
            Sql(catalog, "INSERT INTO run_runs(run_id,job_id,cause,state,finished_at) VALUES ($r,$j,'schedule',$s,$f);",
                ("$r", "r" + i.ToString(CultureInfo.InvariantCulture)), ("$j", job), ("$s", rnd.Next(2) == 0 ? "Failed" : states[rnd.Next(states.Length)]), ("$f", finished));
            if (i % 10 == 0)
                Sql(catalog, "INSERT INTO core_outbox(channel,payload,attempts,next_attempt_at,state) VALUES ('webhook',$p,$a,$t,'dead');",
                    ("$p", RandomNumberGenerator.GetBytes(rnd.Next(1, 64))), ("$a", rnd.Next()), ("$t", RandomText(rnd, 25)));

            var data = await svc.TickAsync(_ct);
            Assert.NotNull(data);
            Assert.All(data.FailedJobIds, id => Assert.Matches("^[A-Za-z0-9-]{1,64}$", id));
            Assert.InRange(data.FailedJobIds.Count, 0, DailyReportService.MaxFailedJobs);
        }
        using var c = CatalogBootstrap.Open(catalog, readOnly: true);
        using var q = c.CreateCommand();
        q.CommandText = "SELECT payload FROM core_outbox WHERE state='pending';";
        using var r = q.ExecuteReader();
        var n = 0;
        while (r.Read())
        {
            using var doc = JsonDocument.Parse((byte[])r[0]);
            Assert.Equal(DailyReportService.EventType, doc.RootElement.GetProperty("type").GetString());
            n++;
        }
        Assert.Equal(120, n);
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("Plan", "D9.11")]
    public void Daily_report_build_with_hostile_anchor_text_is_valid_json()
    {
        // at_utc kotwicy pochodzi z katalogu — także nieparzyste surogaty i znaki sterujące nie mogą przerwać raportu.
        var rnd = new Random(1205);
        for (var i = 0; i < 2000; i++)
        {
            var anchor = new AuditAnchor(1, new byte[32], RandomText(rnd, 40) + (rnd.Next(2) == 0 ? "\ud800" : ""));
            var d = new DailyReportData(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new Dictionary<string, long>(), [], 0, anchor);
            var payload = DailyReportService.Build("e", "00112233445566778899aabbccddeeff", DateTimeOffset.UnixEpoch, d);
            using var doc = JsonDocument.Parse(payload);
        }
    }

    // ---------- AIMD: wyuczony limit z katalogu ----------

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private long _ts;

        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => Interlocked.Read(ref _ts);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan d)
        {
            Interlocked.Add(ref _ts, d.Ticks);
            Now = Now.Add(d);
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("Plan", "D4.16"), Trait("RD", "RD-73")]
    public void Aimd_stays_within_one_and_recommended_for_any_learned_entry_and_signal_sequence()
    {
        var rnd = new Random(1206);
        int[] extremes = [int.MinValue, -1, 0, 1, 2, 63, 64, 65, int.MaxValue];
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset[] dates = [DateTimeOffset.MinValue, DateTimeOffset.MaxValue, now, now.AddDays(-7), now.AddDays(-6.99), now.AddSeconds(1), now.AddYears(-200)];
        for (var i = 0; i < 20_000; i++)
        {
            var t = new ManualTime(now);
            var rec = rnd.Next(1, AimdParallelism.MaxParallelism + 1);
            int? learned = rnd.Next(4) == 0 ? null : rnd.Next(2) == 0 ? extremes[rnd.Next(extremes.Length)] : rnd.Next(-5, 70);
            DateTimeOffset? at = rnd.Next(4) == 0 ? null : dates[rnd.Next(dates.Length)];
            var a = new AimdParallelism(rec, t, learned, at);
            Assert.InRange(a.Current, 1, rec);
            if (at is { } d && (d > now || now - d >= AimdParallelism.LearnedTtl)) Assert.Equal(rec, a.Current); // przyszłość / przeterminowany = ignorowany
            for (var k = rnd.Next(60); k > 0; k--)
            {
                switch (rnd.Next(4))
                {
                    case 0: a.OnLimitSignal(); break;
                    case 1: t.Advance(TimeSpan.FromSeconds(rnd.Next(0, 700))); break;
                    default: a.OnSuccess(); break;
                }
                Assert.InRange(a.Current, 1, rec);
                if (a.Learned is { } l) Assert.InRange(l.Limit, 1, rec - 1);
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("Plan", "D4.16"), Trait("RD", "RD-73")]
    public async Task Learned_parallelism_row_with_any_date_text_never_fails_the_backup()
    {
        // Regresja z it. 120 (Niskie): nieparsowalny learned_at w repo_dest_parallelism dawał FormatException z CatalogParallelismStore.Create
        // — przed otwarciem sesji wysyłki, czyli KAŻDA kopia na ten cel kończyła się błędem (optymalizacja blokowała kopie).
        var catalog = Path.Combine(_dir, "aimd.db");
        CatalogBootstrap.Bootstrap(catalog, TimeProvider.System);
        var rnd = new Random(1207);
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        string[] seeds = ["2026-10-17T11:00:00.000Z", "2026-10-17T13:00:00.000Z", "2026-10-01T00:00:00.000Z", "0001-01-01T00:00:00Z", "9999-12-31T23:59:59.999Z",
            "2026-10-17 11:00:00", "2026-10-17T11:00:00+14:00", "17.10.2026", "", " ", "NaN", "2026-02-30T00:00:00Z"];
        await using var writer = new CatalogWriter(catalog);
        var store = new CatalogParallelismStore(catalog, writer, new ManualTime(now));
        for (var i = 0; i < 400; i++)
        {
            var text = rnd.Next(2) == 0 ? seeds[rnd.Next(seeds.Length)] : Mutate(rnd, seeds[rnd.Next(seeds.Length)]);
            var p = rnd.Next(1, 65);
            Sql(catalog, "INSERT INTO repo_dest_parallelism(destination_id, parallelism, learned_at) VALUES ('d1',$p,$t) ON CONFLICT(destination_id) DO UPDATE SET parallelism=excluded.parallelism, learned_at=excluded.learned_at;",
                ("$p", p), ("$t", text));
            var rec = rnd.Next(1, 65);
            var a = store.Create("d1", rec);
            Assert.InRange(a.Current, 1, rec);
        }
    }

    // ---------- check --repair: odbudowa indeksu z trailerów packów-sierot ----------

    private static byte[] BuildPack(RepoKeys keys, byte[] packId, int blobs)
    {
        using var cipher = new AesGcmCipher(keys.DerivePackKey(packId));
        using var ms = new MemoryStream();
        var w = new PackWriter(ms, cipher, keys.RepoId, packId, keys.CommitTag(packId));
        for (var i = 0; i < blobs; i++)
        {
            var d = RandomNumberGenerator.GetBytes(40 + i);
            w.AddBlob(keys.ChunkId(d), BlobType.Data, CompressionAlgorithm.None, d.Length, d);
        }
        w.Finish();
        return ms.ToArray();
    }

    private static byte[] MutateBytes(Random rnd, byte[] good)
    {
        var b = new List<byte>(good);
        for (var k = rnd.Next(1, 4); k > 0; k--)
        {
            switch (rnd.Next(5))
            {
                case 0 when b.Count > 1: b.RemoveRange(rnd.Next(b.Count), 1); break;
                case 1: b.Insert(rnd.Next(b.Count + 1), (byte)rnd.Next(256)); break;
                case 2 when b.Count > 0: b[rnd.Next(b.Count)] ^= (byte)(1 << rnd.Next(8)); break;
                case 3 when b.Count > 8:
                    var cut = rnd.Next(b.Count / 2, b.Count);
                    b.RemoveRange(cut, b.Count - cut);
                    break;
                default:
                    if (b.Count > 8)
                    {
                        var pos = rnd.Next(b.Count - 4);
                        var v = BitConverter.GetBytes(rnd.Next(3) switch { 0 => 0, 1 => -1, _ => int.MaxValue });
                        for (var j = 0; j < 4; j++) b[pos + j] = v[j];
                    }
                    break;
            }
        }
        return [.. b];
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("Plan", "D8.10"), Trait("BL", "BL-81")]
    public async Task Index_recovery_trusts_only_authentic_packs_under_their_own_name()
    {
        var rnd = new Random(1208);
        using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
        using var foreign = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), keys.RepoId);
        for (var round = 0; round < 25; round++)
        {
            var store = new MemoryStorage();
            var genuine = new List<(byte[] Id, byte[] Bytes)>();
            for (var g = rnd.Next(0, 3); g > 0; g--)
            {
                var id = RandomNumberGenerator.GetBytes(16);
                var bytes = BuildPack(keys, id, rnd.Next(1, 6));
                genuine.Add((id, bytes));
                store.Objects[RepoPaths.Pack(id)] = bytes;
            }
            var template = genuine.Count > 0 ? genuine[0].Bytes : BuildPack(keys, RandomNumberGenerator.GetBytes(16), 3);
            for (var h = 0; h < 30; h++)
            {
                var id = RandomNumberGenerator.GetBytes(16);
                store.Objects[RepoPaths.Pack(id)] = rnd.Next(6) switch
                {
                    0 => template, // autentyczny pack pod cudzą nazwą (klucz/commitTag z nazwy)
                    1 => BuildPack(foreign, id, 2), // pack innego klucza
                    2 => RandomNumberGenerator.GetBytes(rnd.Next(0, 300)),
                    _ => MutateBytes(rnd, template),
                };
            }
            store.Objects["data/zz/nie-pack.pack"] = [1, 2, 3];
            await using var s = store.Open();
            var r = await new RepoIndexRecovery(s, keys, new byte[16]).RecoverAsync(write: true, _ct);
            Assert.Equal(genuine.Count, r.RecoveredPacks);
            Assert.Equal(r.OrphanPacks, r.RecoveredPacks + r.UnreadablePacks);
            if (genuine.Count == 0)
            {
                Assert.Null(r.IndexId);
                continue;
            }
            var (idx, _) = await MetadataStore.LoadIndexAsync(s, keys, r.IndexId!, _ct);
            Assert.Equal(genuine.Select(g => Convert.ToHexStringLower(g.Id)).Order(StringComparer.Ordinal), idx.Packs.Select(p => Convert.ToHexStringLower(p.PackId)));
            foreach (var p in idx.Packs)
                Assert.Equal(SHA256.HashData(genuine.Single(g => g.Id.AsSpan().SequenceEqual(p.PackId)).Bytes), p.Sha256);
        }
    }

    /// <summary>Cel kłamiący o rozmiarze obiektu (lista/stat) albo ucinający odczyt — wrogi lub uszkodzony serwer.</summary>
    private sealed class LyingSession(IStorageSession inner, string path, long reportedLength) : IStorageSession
    {
        public CredentialRole Role => inner.Role;

        public ProviderCapabilities Capabilities => inner.Capabilities;

        public ServerIdentity? PresentedIdentity => inner.PresentedIdentity;

        public async ValueTask<StatResult?> StatAsync(string p, CancellationToken ct) =>
            p == path ? new StatResult(p, reportedLength, null, false) : await inner.StatAsync(p, ct);

        public async IAsyncEnumerable<StatResult> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var o in inner.ListAsync(prefix, ct)) yield return o.Path == path ? o with { Length = reportedLength } : o;
        }

        public ValueTask<Stream> OpenReadAsync(string p, long offset, long? length, CancellationToken ct) => inner.OpenReadAsync(p, offset, null, ct);

        public ValueTask<IObjectWriter> CreateAsync(string p, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct) => inner.CreateAsync(p, expectedLength, mode, resumeFrom, ct);

        public ValueTask<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);

        public ValueTask DeleteAsync(string p, CancellationToken ct) => inner.DeleteAsync(p, ct);

        public ValueTask<byte[]?> ServerHashAsync(string p, HashAlgorithmName algorithm, CancellationToken ct) => inner.ServerHashAsync(p, algorithm, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Theory, Trait("SEC", "SEC-3.3"), Trait("Plan", "D8.10"), Trait("BL", "BL-81")]
    [InlineData(2L * 1024 * 1024 * 1024)] // = stary limit: new byte[2 GiB] → OverflowException poza kontraktem (regresja it. 120)
    [InlineData(int.MaxValue - 1L)]
    [InlineData(5_000_000L)] // dłuższy niż faktyczny obiekt — ucięty odczyt (EndOfStream)
    public async Task Index_recovery_counts_lying_object_sizes_as_unreadable(long reported)
    {
        using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
        var store = new MemoryStorage();
        var good = RandomNumberGenerator.GetBytes(16);
        store.Objects[RepoPaths.Pack(good)] = BuildPack(keys, good, 2);
        var bad = RandomNumberGenerator.GetBytes(16);
        store.Objects[RepoPaths.Pack(bad)] = BuildPack(keys, bad, 2);
        await using var s = new LyingSession(store.Open(), RepoPaths.Pack(bad), reported);
        var r = await new RepoIndexRecovery(s, keys, new byte[16]).RecoverAsync(write: false, _ct);
        Assert.Equal(2, r.OrphanPacks);
        Assert.Equal(1, r.RecoveredPacks);
        Assert.Equal(1, r.UnreadablePacks);
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("Plan", "D8.10")]
    public void Recovery_pack_limit_covers_the_largest_pack_a_job_can_write()
    {
        // Pack zamykany po przekroczeniu celu: ≤ cel + jedna ramka bloku + trailer + stopka.
        Assert.True(RepoIndexRecovery.MaxPackSize >= JobDefinition.MaxPackTarget + PackLayout.MaxFrameLength + PackLayout.MaxTrailerLength + PackLayout.HeaderSize + PackLayout.FooterSize);
        Assert.True(RepoIndexRecovery.MaxPackSize <= (long)Array.MaxLength);
    }
}
