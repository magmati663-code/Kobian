using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Engine.Restore;
using Kobian.Format.Metadata;
using Kobian.Providers.Storage.Local;
using Xunit;

namespace Kobian.Tests.Unit.Quality;

/// <summary>
/// Repozytorium złote #2 (BL-9/BL-28, NFR-14, D3.4/D3.11): zatwierdzone repozytorium formatu v1 Z NAGŁÓWKIEM <c>kobian.repo</c>,
/// plikiem klucza <c>keys/&lt;id&gt;.key</c> opakowanym hasłem i plikiem hasła — odczyt przez <see cref="KeyFileLoader"/> (ta sama ścieżka co
/// <c>kobian-restore</c>) i odtworzenie bit w bit. Zmiana opakowania klucza/nagłówka bez migracji = czerwony test.
/// Dane syntetyczne; hasło i MK to publiczne stałe TESTOWE (nie sekrety, Qb104 — plik hasła w fixturze to wartość testowa).
/// Regeneracja (świadoma): <c>KOBIAN_GOLDEN_KEYED_REGEN=&lt;katalog&gt; dotnet test --filter Golden_keyed_regenerate</c>.
/// </summary>
public sealed class GoldenKeyedRepoTests : IDisposable
{
    internal const string Name = "v1-keyed";
    // PROWIZORYCZNIE Qb104: stałe testowe (zmiana = regeneracja fixtury).
    private const string TestPassword = "kobian-golden-fixture-pw-NOT-A-SECRET";
    private static readonly byte[] TestMasterKey = Enumerable.Range(0, 32).Select(i => (byte)(0xC0 + i)).ToArray();
    private static readonly byte[] TestRepoId = Enumerable.Range(0, 16).Select(i => (byte)(0x50 + i)).ToArray();
    private static readonly byte[] TestKeyId = Enumerable.Range(0, 16).Select(i => (byte)(0x60 + i)).ToArray();
    private static readonly byte[] TestHost = Enumerable.Range(0, 16).Select(i => (byte)(0x70 + i)).ToArray();
    private static readonly DateTimeOffset Created = new(2025, 8, 9, 10, 11, 12, TimeSpan.Zero);
    private static readonly DateTime Mtime = new(2025, 8, 9, 10, 11, 12, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-golden2-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private sealed record GoldenFile(string Path, long Size, string Sha256, DateTime MtimeUtc);
    private sealed record GoldenManifest(string Format, string KeyFile, string SnapIdHex, List<GoldenFile> Files);

    private static byte[] Stream(string label, int length)
    {
        var buf = new byte[length];
        for (int off = 0, ctr = 0; off < length; ctr++)
        {
            var block = SHA256.HashData(Encoding.UTF8.GetBytes($"{label}/{ctr}"));
            var n = Math.Min(block.Length, length - off);
            Array.Copy(block, 0, buf, off, n);
            off += n;
        }
        return buf;
    }

    private static void Put(string root, string rel, byte[] data)
    {
        var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        File.SetLastWriteTimeUtc(p, Mtime);
    }

    private static List<GoldenFile> Describe(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => new GoldenFile(Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(f).Length,
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), File.GetLastWriteTimeUtc(f)))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Golden_keyed_regenerate()
    {
        var outDir = Environment.GetEnvironmentVariable("KOBIAN_GOLDEN_KEYED_REGEN");
        if (string.IsNullOrEmpty(outDir)) return; // normalnie no-op
        var dest = Path.Combine(outDir, Name);
        if (Directory.Exists(dest)) throw new IOException("Katalog docelowy istnieje — nie nadpisuję złotego repo: " + dest);
        var src = Path.Combine(_tmp, "src");
        Put(src, "docs/umowa.txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Kobian złote repozytorium z kluczem. ", 600))));
        Put(src, "data/blob.bin", Stream("blob", 200 * 1024));
        Put(src, "pusty", []);
        Put(src, "ż/zażółć.txt", Encoding.UTF8.GetBytes("Zażółć gęślą jaźń."));

        var repoDir = Path.Combine(dest, "repo");
        Directory.CreateDirectory(Path.Combine(repoDir, "keys"));
        var keyIdHex = Convert.ToHexStringLower(TestKeyId);
        using var keys = RepoKeys.Derive(TestMasterKey, TestRepoId);
        File.WriteAllText(Path.Combine(repoDir, "kobian.repo"), RepoHeader.Create(keys, Created, [TestKeyId]), new UTF8Encoding(false));
        var keyRel = "keys/" + keyIdHex + ".key";
        File.WriteAllText(Path.Combine(repoDir, "keys", keyIdHex + ".key"), KeyFiles.WrapWithPassword(TestMasterKey, TestPassword, TestKeyId, Created), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dest, "password.txt"), TestPassword + "\n", new UTF8Encoding(false));

        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", repoDir), CredentialRole.Backup, _ct);
        var res = await new BackupPipeline(keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(new FileSystemSource(src), "golden2", TestHost, PackTargetSize: 128 * 1024), _ct);
        Assert.Equal(BackupStatus.Succeeded, res.Status);
        var manifest = new GoldenManifest("kobian-repo-v1", keyRel, Convert.ToHexStringLower(res.SnapId!), Describe(src));
        File.WriteAllText(Path.Combine(dest, "golden.json"), JsonSerializer.Serialize(manifest, Indented));
    }

    private static string GoldenDir() => Path.Combine(AppContext.BaseDirectory, "golden", Name);

    /// <summary>Kopia roboczą (odczyt nie może zmienić fixtury); zwraca katalog z <c>repo/</c> i <c>password.txt</c>.</summary>
    private string CopyFixture()
    {
        var src = GoldenDir();
        var dst = Path.Combine(_tmp, "fx");
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return dst;
    }

    private static GoldenManifest LoadManifest() => JsonSerializer.Deserialize<GoldenManifest>(File.ReadAllText(Path.Combine(GoldenDir(), "golden.json")))!;

    [Fact, Trait("NFR", "NFR-14"), Trait("D", "D3.11")]
    public async Task Golden_keyed_repository_unlocks_with_password_file_and_restores_bit_identical()
    {
        var m = LoadManifest();
        var fx = CopyFixture();
        var repo = Path.Combine(fx, "repo");
        using var keys = KeyFileLoader.Load(Path.Combine(repo, m.KeyFile.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(fx, "password.txt"), Path.Combine(repo, "kobian.repo"));
        Assert.Equal(TestRepoId, keys.RepoId.ToArray());

        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", repo), CredentialRole.Backup, _ct);
        var snap = await MetadataStore.LoadSnapshotAsync(s, keys, Convert.FromHexString(m.SnapIdHex), _ct);
        using var reader = new RepoReader(s, keys);
        await reader.LoadIndexesAsync(snap, _ct);
        var target = Path.Combine(_tmp, "restore");
        Directory.CreateDirectory(target);
        var r = await new RestoreExecutor(reader).RestoreAsync(snap, target, null, _ct);
        Assert.Equal(RestoreStatus.Succeeded, r.Status);
        Assert.Equal(m.Files, Describe(target));
    }

    [Fact, Trait("NFR", "NFR-14"), Trait("SEC", "SEC-3")]
    public void Golden_keyed_repository_rejects_wrong_password_and_forged_header()
    {
        var m = LoadManifest();
        var fx = CopyFixture();
        var repo = Path.Combine(fx, "repo");
        var keyPath = Path.Combine(repo, m.KeyFile.Replace('/', Path.DirectorySeparatorChar));
        var header = Path.Combine(repo, "kobian.repo");

        var badPw = Path.Combine(_tmp, "bad.pw");
        File.WriteAllText(badPw, TestPassword + "x\n", new UTF8Encoding(false));
        var ex = Assert.Throws<KobianException>(() => KeyFileLoader.Load(keyPath, badPw, header));
        Assert.Equal(KbCode.KeyUnwrapFailed, ex.Code);

        // podrobiony nagłówek (zmiana createdAt) → MAC się nie zgadza → KB-1104, bez wyroczni
        var text = File.ReadAllText(header);
        var forged = Path.Combine(_tmp, "forged.repo");
        File.WriteAllText(forged, text.Replace("2025-08-09", "2025-08-10", StringComparison.Ordinal), new UTF8Encoding(false));
        Assert.NotEqual(text, File.ReadAllText(forged));
        var ex2 = Assert.Throws<KobianException>(() => KeyFileLoader.Load(keyPath, Path.Combine(fx, "password.txt"), forged));
        Assert.Equal(KbCode.RepoHeaderInvalid, ex2.Code);
    }
}
