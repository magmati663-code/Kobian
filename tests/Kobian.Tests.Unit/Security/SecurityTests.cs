using System.Security.Cryptography;
using System.Text.Json;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Domain.Serialization;
using Kobian.Format.Pack;
using Kobian.Providers.Sources.Erp;
using Kobian.Tests.Support;
using Xunit;

namespace Kobian.Tests.Unit.Security;

/// <summary>NFR-11: brak sekretów w repozytorium kodu i dokumentacji (BL-7).</summary>
public sealed class SecretScanTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kobian.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact, Trait("NFR", "NFR-11")]
    public void Repository_sources_and_docs_contain_no_secrets()
    {
        var root = Root();
        var files = new[] { "src", "docs", "tools", "tests" }
            .Where(d => Directory.Exists(Path.Combine(root, d)))
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetExtension(f) is ".cs" or ".json" or ".md" or ".yaml" or ".py" or ".sh" or ".ps1" or ".csproj" or ".props" or ".config");
        // Wyjątki: definicje wzorców skanera i przykłady w testach reguł.
        var findings = SecretScanner.Scan(files, allow: (f, _, _) => f.EndsWith("SecretScanner.cs", StringComparison.Ordinal) || f.EndsWith("SecurityTests.cs", StringComparison.Ordinal));
        Assert.Empty(findings.Select(x => $"{Path.GetRelativePath(root, x.File)}:{x.Line} {x.Rule}"));
    }

    [Theory, Trait("NFR", "NFR-11")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", "private-key")]
    [InlineData("password = \"Sekret123!\"", "password-assignment")]
    [InlineData("Server=x;User Id=sa;Password=Sekret123;", "connection-string-password")]
    [InlineData("key AKIAABCDEFGHIJKLMNOP end", "aws-access-key")]
    public void Scanner_detects_patterns(string line, string rule)
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, line);
            Assert.Contains(SecretScanner.Scan([f]), x => x.Rule == rule);
        }
        finally
        {
            File.Delete(f);
        }
    }

    [Fact, Trait("NFR", "NFR-11")]
    public void Scanner_finds_known_random_test_secret_without_revealing_it()
    {
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "log line ... " + secret + " ...");
            var r = SecretScanner.Scan([f], [secret]);
            var hit = Assert.Single(r);
            Assert.DoesNotContain(secret, hit.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(f);
        }
    }
}

/// <summary>
/// Fuzzing mutacyjny bez zewnętrznych bibliotek (SEC-3.3, 10.1): parsery danych niezaufanych mogą zgłaszać wyłącznie
/// wyjątki domenowe — nigdy IndexOutOfRange/NullReference/OutOfMemory/Overflow. Ziarno stałe → powtarzalność.
/// </summary>
public sealed class ParserFuzzTests
{
    private static byte[] Mutate(Random rnd, byte[] input)
    {
        var b = input.ToList();
        var ops = rnd.Next(1, 6);
        for (var k = 0; k < ops; k++)
        {
            switch (rnd.Next(5))
            {
                case 0 when b.Count > 0: b[rnd.Next(b.Count)] ^= (byte)(1 << rnd.Next(8)); break;
                case 1 when b.Count > 0: b[rnd.Next(b.Count)] = (byte)rnd.Next(256); break;
                case 2 when b.Count > 1:
                    var at = rnd.Next(b.Count);
                    b.RemoveRange(at, Math.Min(b.Count - at, rnd.Next(1, 64)));
                    break;
                case 3: b.InsertRange(rnd.Next(b.Count + 1), Enumerable.Range(0, rnd.Next(1, 32)).Select(_ => (byte)rnd.Next(256))); break;
                default:
                    if (b.Count > 8)
                    {
                        var pos = rnd.Next(b.Count - 4);
                        var v = rnd.Next(4) switch { 0 => 0, 1 => -1, 2 => int.MaxValue, _ => rnd.Next() };
                        var bytes = BitConverter.GetBytes(v);
                        for (var j = 0; j < 4; j++) b[pos + j] = bytes[j];
                    }
                    break;
            }
        }
        return [.. b];
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void PackReader_only_throws_domain_errors_on_mutated_packs()
    {
        var repoId = new byte[16]; var packId = new byte[16]; var mk = new byte[32];
        RandomNumberGenerator.Fill(repoId); RandomNumberGenerator.Fill(packId); RandomNumberGenerator.Fill(mk);
        using var keys = RepoKeys.Derive(mk, repoId);
        using var cipher = new AesGcmCipher(keys.DerivePackKey(packId));
        var commit = keys.CommitTag(packId);
        using var ms = new MemoryStream();
        var w = new PackWriter(ms, cipher, repoId, packId, commit);
        for (var i = 0; i < 4; i++)
        {
            var d = RandomNumberGenerator.GetBytes(50 + i);
            w.AddBlob(keys.ChunkId(d), i == 3 ? BlobType.Tree : BlobType.Data, CompressionAlgorithm.None, d.Length, d);
        }
        w.Finish();
        var good = ms.ToArray();
        var rnd = new Random(20261012);
        var accepted = 0;
        for (var iter = 0; iter < 5000; iter++)
        {
            var bad = Mutate(rnd, good);
            try
            {
                var r = PackReader.Open(new MemoryStream(bad), cipher, repoId, packId, commit);
                for (var i = 0; i < r.Entries.Count; i++) r.ReadBlob(i);
                accepted++;
            }
            catch (KobianException)
            {
            }
            catch (EndOfStreamException)
            {
                // dopuszczalne: odczyt poza końcem strumienia przy uciętym pliku — sprawdzane niżej, że nie występuje
                Assert.Fail("EndOfStream should be prevented by layout checks");
            }
        }
        // AEAD: żadna mutacja nie może dać poprawnie odczytanego packa (poza mutacjami przywracającymi oryginał).
        Assert.True(accepted <= 5, $"accepted {accepted}");
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Trailer_decoder_only_throws_domain_errors()
    {
        var w = new System.Formats.Cbor.CborWriter(System.Formats.Cbor.CborConformanceMode.Canonical);
        w.WriteStartArray(2);
        for (var i = 0; i < 2; i++)
        {
            w.WriteStartArray(5); w.WriteByteString(new byte[32]); w.WriteUInt64(64); w.WriteUInt64(100); w.WriteUInt64(1); w.WriteUInt64(80); w.WriteEndArray();
        }
        w.WriteEndArray();
        var good = w.Encode();
        var rnd = new Random(7);
        for (var iter = 0; iter < 20000; iter++)
        {
            try
            {
                PackReader.DecodeTrailer(Mutate(rnd, good));
            }
            catch (KobianException)
            {
            }
        }
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Jcs_and_profile_parsers_only_throw_expected_exceptions()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "corpus", "small-mixed.json"));
        var good = System.Text.Encoding.UTF8.GetBytes(json);
        var rnd = new Random(99);
        for (var iter = 0; iter < 5000; iter++)
        {
            var bad = Mutate(rnd, good);
            try
            {
                Jcs.Canonicalize(System.Text.Encoding.UTF8.GetString(bad));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
            {
            }
            try
            {
                ErpProfileCatalog.Parse(new MemoryStream(bad));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
            {
            }
        }
    }

    [Fact]
    public void RecoveryKey_decoder_never_throws()
    {
        var rnd = new Random(3);
        var buf = new byte[32];
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567-  01890ąż\0‮";
        for (var iter = 0; iter < 20000; iter++)
        {
            var len = rnd.Next(0, 90);
            var s = new string(Enumerable.Range(0, len).Select(_ => chars[rnd.Next(chars.Length)]).ToArray());
            RecoveryKey.TryDecode(s, buf);
        }
    }
}
