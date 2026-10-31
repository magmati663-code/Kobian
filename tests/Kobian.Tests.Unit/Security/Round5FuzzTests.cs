using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Jobs;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql.Tls;
using Xunit;

namespace Kobian.Tests.Unit.Security;

/// <summary>
/// Przegląd rundy 5 (it. 75, rola 11, SEC-3.3): losowe mutacje wejść parserów dodanych w rundzie 5 (BL-40 cz. 2–5) —
/// sekcja „database” definicji zadania (MsSqlSourceDefinition), adres serwera SQL (SqlEndpoint), odpowiedź PRELOGIN (opcja ENCRYPTION),
/// odpowiedź SQL Browser (SSRP SVR_RESP) i ramkowanie TLS w pakietach PRELOGIN (TdsPreloginStream). Dane z sieci pochodzą
/// od niezatwierdzonego jeszcze serwera — kryterium: tylko oczekiwany typ wyjątku, brak zawieszeń, brak ścieżek w komunikatach. Ziarna stałe.
/// </summary>
public sealed class Round5FuzzTests
{
    private const int Rounds = 20_000;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static string Abs(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kb-fuzz5", name));

    private static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static byte[] ValidDatabaseDefinition() => Encoding.UTF8.GetBytes(
        "{\"version\":1,\"database\":{\"engine\":\"mssql\",\"server\":\"srv01\\\\SQLEXPRESS\",\"name\":\"Firma_1\",\"auth\":\"sql\",\"secretRef\":\"file:erp\"," +
        "\"strict\":false,\"staging\":" + Q(Abs("staging")) + ",\"sqlStaging\":\"C:\\\\ProgramData\\\\Kobian\\\\staging\",\"lowImpact\":true,\"conflictWaitMinutes\":5}," +
        "\"destination\":{\"scheme\":\"local\",\"root\":" + Q(Abs("repo")) + "},\"key\":{\"file\":" + Q(Abs("k.key")) + ",\"passwordFile\":" + Q(Abs("pw")) +
        ",\"repoHeader\":null},\"packTargetSize\":1048576}");

    private static byte[] Mutate(Random rnd, byte[] good, string structural)
    {
        var b = (byte[])good.Clone();
        if (b.Length == 0) return b;
        switch (rnd.Next(5))
        {
            case 0:
                for (var k = rnd.Next(1, 4); k > 0; k--) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
                return b;
            case 1:
                return b[..rnd.Next(b.Length)];
            case 2:
                for (var k = rnd.Next(1, 3); k > 0; k--) b[rnd.Next(b.Length)] = (byte)structural[rnd.Next(structural.Length)];
                return b;
            case 3:
            {
                var i = rnd.Next(b.Length);
                var n = rnd.Next(1, Math.Min(16, b.Length - i) + 1);
                return [.. b[..i], .. b[(i + n)..]];
            }
            default:
            {
                var i = rnd.Next(b.Length);
                var n = rnd.Next(1, Math.Min(32, b.Length - i) + 1);
                return [.. b[..(i + n)], .. b[i..]];
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "NFR-11"), Trait("BL", "BL-40")]
    public void Database_job_definition_only_throws_JobDefinitionInvalid_without_paths()
    {
        var good = ValidDatabaseDefinition();
        Assert.NotNull(JobDefinition.Parse("database", good).MsSql); // punkt wyjścia poprawny
        var rnd = new Random(7501);
        var accepted = 0;
        for (var i = 0; i < Rounds; i++)
        {
            var b = Mutate(rnd, good, "{}[]\",:0-e\\tf");
            try
            {
                var def = JobDefinition.Parse("database", b);
                accepted++;
                // przyjęta definicja zachowuje niezmienniki sekcji „database”
                var db = Assert.IsType<MsSqlSourceDefinition>(def.MsSql);
                Assert.InRange(db.ConflictWait, TimeSpan.Zero, TimeSpan.FromMinutes(MsSqlSourceDefinition.MaxConflictWaitMinutes));
                Assert.Null(db.Connection.UserName); // login nigdy nie zostaje w definicji
                Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), db.ToString(), StringComparison.Ordinal);
            }
            catch (KobianException ex)
            {
                Assert.Equal(KbCode.JobDefinitionInvalid, ex.Code);
                Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), ex.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("srv01", ex.Message, StringComparison.Ordinal); // NFR-11: bez nazwy serwera
            }
        }
        Assert.True(accepted < Rounds, "fuzzer nie zmienił wejścia");
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "D8.9"), Trait("BL", "BL-40")]
    public void Sql_endpoint_parser_only_throws_JobDefinitionInvalid()
    {
        const string alphabet = "abcXYZ019.-_\\,:[]() \t/@;=\"'\0é";
        var rnd = new Random(7502);
        for (var i = 0; i < Rounds; i++)
        {
            var len = rnd.Next(0, 40);
            var chars = new char[len];
            for (var k = 0; k < len; k++) chars[k] = alphabet[rnd.Next(alphabet.Length)];
            var s = new string(chars);
            try
            {
                var ep = SqlEndpoint.Parse(s);
                Assert.False(string.IsNullOrEmpty(ep.Host));
                Assert.True(ep.Port is null or (>= 1 and <= 65535));
                Assert.True(ep.Port is null || ep.Instance is null);
                foreach (var c in ep.Host + ep.Instance)
                    Assert.True(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']', $"znak spoza listy: {(int)c}");
                Assert.DoesNotContain('\\', ep.Host);
            }
            catch (KobianException ex)
            {
                Assert.Equal(KbCode.JobDefinitionInvalid, ex.Code);
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-40")]
    public void Prelogin_encryption_parser_only_throws_InvalidData()
    {
        byte[] good = [0x00, 0, 11, 0, 6, 0x01, 0, 17, 0, 1, 0xFF, 16, 0, 0x0F, 0xA0, 0, 0, 0x03];
        Assert.Equal(0x03, TdsCertificateFetcher.ParseEncryption(good));
        var rnd = new Random(7503);
        for (var i = 0; i < Rounds; i++)
        {
            var b = rnd.Next(4) == 0 ? RandomNumberGenerator.GetBytes(rnd.Next(0, 64)) : Mutate(rnd, good, "\x00\x01\xFF\x11\x7F");
            try
            {
                _ = TdsCertificateFetcher.ParseEncryption(b);
            }
            catch (InvalidDataException)
            {
                // oczekiwane
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-40")]
    public void Ssrp_reply_parser_never_throws_and_returns_only_valid_ports()
    {
        static byte[] R(string body)
        {
            var b = Encoding.ASCII.GetBytes(body);
            return [0x05, (byte)b.Length, (byte)(b.Length >> 8), .. b];
        }
        var good = R("ServerName;SRV;InstanceName;SQLEXPRESS;IsClustered;No;Version;16.0.1000.6;tcp;49172;;");
        Assert.Equal(49172, SsrpClient.ParseTcpPort(good, "SQLEXPRESS"));
        var rnd = new Random(7504);
        for (var i = 0; i < Rounds; i++)
        {
            var b = rnd.Next(5) == 0 ? RandomNumberGenerator.GetBytes(rnd.Next(0, 1100)) : Mutate(rnd, good, ";0123456789\x05\xFF");
            var port = SsrpClient.ParseTcpPort(b, "SQLEXPRESS");
            Assert.True(port is null or (>= 1 and <= 65535));
        }
    }

    private static byte[] Framed(byte[] payload, int chunk)
    {
        var ms = new MemoryStream();
        for (var o = 0; o < payload.Length; o += chunk)
        {
            var n = Math.Min(chunk, payload.Length - o);
            ms.Write(TdsPreloginStream.Header(TdsPreloginStream.PreloginType, n, 1));
            ms.Write(payload, o, n);
        }
        return ms.ToArray();
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-40")]
    public async Task Prelogin_framing_mutations_only_throw_protocol_errors()
    {
        var payload = RandomNumberGenerator.GetBytes(300);
        var good = Framed(payload, 97);
        var ok = new byte[payload.Length];
        await new TdsPreloginStream(new MemoryStream(good)).ReadExactlyAsync(ok, _ct);
        Assert.Equal(payload, ok);
        var rnd = new Random(7505);
        for (var i = 0; i < Rounds / 4; i++)
        {
            var b = Mutate(rnd, good, "\x12\x04\x00\x01\x7F\xFF");
            var s = new TdsPreloginStream(new MemoryStream(b));
            var buf = new byte[64];
            try
            {
                var total = 0;
                int n;
                while ((n = await s.ReadAsync(buf, _ct)) > 0) total += n;
                Assert.True(total <= b.Length);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                // oczekiwane: protokół niezgodny albo obcięty pakiet
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-40")]
    public async Task Empty_packet_floods_are_rejected_instead_of_spinning()
    {
        var ms = new MemoryStream();
        for (var i = 0; i < 10_000; i++) ms.Write(TdsPreloginStream.Header(TdsPreloginStream.PreloginType, 0, 1));
        ms.Write(TdsPreloginStream.Header(TdsPreloginStream.PreloginType, 1, 1));
        ms.WriteByte(0x42);
        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new TdsPreloginStream(ms).ReadExactlyAsync(new byte[1], _ct));

        // kilka pustych pakietów (≤ limit) nadal jest dopuszczalne
        var few = new MemoryStream();
        for (var i = 0; i < TdsPreloginStream.MaxEmptyPackets; i++) few.Write(TdsPreloginStream.Header(TdsPreloginStream.PreloginType, 0, 1));
        few.Write(TdsPreloginStream.Header(TdsPreloginStream.PreloginType, 1, 1));
        few.WriteByte(0x42);
        few.Position = 0;
        var one = new byte[1];
        await new TdsPreloginStream(few).ReadExactlyAsync(one, _ct);
        Assert.Equal(0x42, one[0]);

        // odpowiedź PRELOGIN: pakiety bez EOM (status 0) — limit liczby pakietów, nie tylko sumy ładunku
        var resp = new MemoryStream();
        for (var i = 0; i < 1000; i++) resp.Write([0x04, 0x00, 0x00, 0x08, 0, 0, 1, 0]);
        resp.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => TdsCertificateFetcher.ReadPreloginEncryptionAsync(resp, _ct));
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("BL", "BL-40")]
    public async Task Prelogin_response_mutations_only_throw_protocol_errors()
    {
        byte[] body = [0x00, 0, 11, 0, 6, 0x01, 0, 17, 0, 1, 0xFF, 16, 0, 0x0F, 0xA0, 0, 0, 0x01];
        byte[] good = [.. TdsPreloginStream.Header(0x04, body.Length, 1), .. body];
        Assert.Equal(0x01, await TdsCertificateFetcher.ReadPreloginEncryptionAsync(new MemoryStream(good), _ct));
        var rnd = new Random(7506);
        for (var i = 0; i < Rounds / 4; i++)
        {
            var b = Mutate(rnd, good, "\x04\x00\x01\xFF\x10");
            try
            {
                _ = await TdsCertificateFetcher.ReadPreloginEncryptionAsync(new MemoryStream(b), _ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                // oczekiwane
            }
        }
    }
}
