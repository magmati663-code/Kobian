using System.Security.Cryptography;
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
/// Repozytoria złote (BL-9, KOSZT-16, NFR-14 zgodność wstecz): zatwierdzone do repo, WYGENEROWANE RAZ repozytorium formatu v1
/// (<c>tests/golden/v1-basic</c>) musi być odczytane i odtworzone bit w bit przez każdą kolejną wersję kodu.
/// Zmiana formatu bez migracji/ADR = czerwony test. Dane wyłącznie syntetyczne; klucz główny i sól to stałe TESTOWE (nie sekrety).
/// Regeneracja (świadoma, tylko przy zmianie wersji formatu): <c>KOBIAN_GOLDEN_REGEN=&lt;katalog&gt; dotnet test --filter Golden_regenerate</c>.
/// </summary>
public sealed class GoldenRepoTests : IDisposable
{
    internal const string Name = "v1-basic";
    // PROWIZORYCZNIE Qb104: klucz i sól złotego repo to publiczne stałe testowe (zmiana = regeneracja fixtury).
    private static readonly byte[] TestMasterKey = Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();
    private static readonly byte[] TestSalt = Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();
    private static readonly byte[] TestHost = Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray();
    private static readonly DateTime Mtime1 = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private static readonly DateTime Mtime2 = new(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-golden-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private sealed record GoldenFile(string Path, long Size, string Sha256, DateTime MtimeUtc);
    private sealed record GoldenSnapshot(string SnapIdHex, List<GoldenFile> Files);
    private sealed record GoldenManifest(string Format, string MasterKeyHex, string SaltHex, List<GoldenSnapshot> Snapshots);

    /// <summary>Deterministyczny strumień pseudolosowy (SHA-256 w trybie licznika) — te same bajty na każdej platformie.</summary>
    private static byte[] Stream(string label, int length)
    {
        var buf = new byte[length];
        for (int off = 0, ctr = 0; off < length; ctr++)
        {
            var block = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{label}/{ctr}"));
            var n = Math.Min(block.Length, length - off);
            Array.Copy(block, 0, buf, off, n);
            off += n;
        }
        return buf;
    }

    private static void Put(string root, string rel, byte[] data, DateTime mtime)
    {
        var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        File.SetLastWriteTimeUtc(p, mtime);
    }

    private static List<GoldenFile> Describe(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => new GoldenFile(Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(f).Length,
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), File.GetLastWriteTimeUtc(f)))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Golden_regenerate()
    {
        var outDir = Environment.GetEnvironmentVariable("KOBIAN_GOLDEN_REGEN");
        if (string.IsNullOrEmpty(outDir)) return; // normalnie no-op: fixtura jest zatwierdzona
        var dest = Path.Combine(outDir, Name);
        if (Directory.Exists(dest)) throw new IOException("Katalog docelowy istnieje — nie nadpisuję złotego repo: " + dest);
        var src = Path.Combine(_tmp, "src");
        Put(src, "a.txt", System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Kobian złote repozytorium v1. ", 700))), Mtime1);
        Put(src, "b/rand.bin", Stream("rand", 300 * 1024), Mtime1);
        Put(src, "b/dup.bin", Stream("rand", 300 * 1024), Mtime1);
        Put(src, "pusty", [], Mtime1);
        Put(src, "ż/zażółć gęślą.txt", System.Text.Encoding.UTF8.GetBytes("Zażółć gęślą jaźń."), Mtime1);

        Directory.CreateDirectory(Path.Combine(dest, "repo"));
        using var keys = RepoKeys.Derive(TestMasterKey, TestSalt);
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", Path.Combine(dest, "repo")), CredentialRole.Backup, _ct);
        var pipeline = new BackupPipeline(keys, new VerifiedUploader(s));
        var full = await pipeline.RunAsync(new BackupRequest(new FileSystemSource(src), "golden", TestHost, PackTargetSize: 128 * 1024), _ct);
        Assert.Equal(BackupStatus.Succeeded, full.Status);
        var snaps = new List<GoldenSnapshot> { new(Convert.ToHexStringLower(full.SnapId!), Describe(src)) };
        var snap1 = await MetadataStore.LoadSnapshotAsync(s, keys, full.SnapId!, _ct);

        Put(src, "a.txt", System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Kobian złote repozytorium v1, wersja 2. ", 650))), Mtime2);
        Put(src, "c.txt", System.Text.Encoding.UTF8.GetBytes("nowy plik"), Mtime2);
        File.Delete(Path.Combine(src, "pusty"));
        var baseline = new List<BaselineIndex>();
        foreach (var (id, _) in snap1.Indexes)
        {
            var (idx, sha) = await MetadataStore.LoadIndexAsync(s, keys, id, _ct);
            baseline.Add(new BaselineIndex(id, sha, idx));
        }
        var inc = await pipeline.RunAsync(new BackupRequest(new FileSystemSource(src), "golden", TestHost, SnapshotMode.Incremental, baseline, full.SnapId, PackTargetSize: 128 * 1024), _ct);
        Assert.Equal(BackupStatus.Succeeded, inc.Status);
        snaps.Add(new GoldenSnapshot(Convert.ToHexStringLower(inc.SnapId!), Describe(src)));

        var manifest = new GoldenManifest("kobian-repo-v1", Convert.ToHexStringLower(TestMasterKey), Convert.ToHexStringLower(TestSalt), snaps);
        File.WriteAllText(Path.Combine(dest, "golden.json"), JsonSerializer.Serialize(manifest, Indented));
    }

    private static string GoldenDir() => Path.Combine(AppContext.BaseDirectory, "golden", Name);

    [Fact, Trait("NFR", "NFR-14"), Trait("D", "D10.9")]
    public async Task Golden_v1_repository_restores_every_snapshot_bit_identical()
    {
        var manifest = JsonSerializer.Deserialize<GoldenManifest>(File.ReadAllText(Path.Combine(GoldenDir(), "golden.json")))!;
        Assert.Equal("kobian-repo-v1", manifest.Format);
        Assert.Equal(2, manifest.Snapshots.Count);
        // kopia robocza: odczyt nie może modyfikować zatwierdzonej fixtury
        var repo = Path.Combine(_tmp, "repo");
        foreach (var f in Directory.GetFiles(Path.Combine(GoldenDir(), "repo"), "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(repo, Path.GetRelativePath(Path.Combine(GoldenDir(), "repo"), f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }

        using var keys = RepoKeys.Derive(Convert.FromHexString(manifest.MasterKeyHex), Convert.FromHexString(manifest.SaltHex));
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", repo), CredentialRole.Backup, _ct);
        for (var i = 0; i < manifest.Snapshots.Count; i++)
        {
            var g = manifest.Snapshots[i];
            var snap = await MetadataStore.LoadSnapshotAsync(s, keys, Convert.FromHexString(g.SnapIdHex), _ct);
            using var reader = new RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap, _ct);
            var target = Path.Combine(_tmp, "restore" + i);
            Directory.CreateDirectory(target);
            var r = await new RestoreExecutor(reader).RestoreAsync(snap, target, null, _ct);
            Assert.Equal(RestoreStatus.Succeeded, r.Status);

            var actual = Directory.GetFiles(target, "*", SearchOption.AllDirectories)
                .Select(f => new GoldenFile(Path.GetRelativePath(target, f).Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(f).Length,
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), File.GetLastWriteTimeUtc(f)))
                .OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            Assert.Equal(g.Files, actual);
        }
    }

    [Fact, Trait("NFR", "NFR-14"), Trait("SEC", "SEC-3")]
    public async Task Golden_v1_repository_rejects_wrong_key()
    {
        var manifest = JsonSerializer.Deserialize<GoldenManifest>(File.ReadAllText(Path.Combine(GoldenDir(), "golden.json")))!;
        var wrong = Convert.FromHexString(manifest.MasterKeyHex);
        wrong[0] ^= 0xFF;
        using var keys = RepoKeys.Derive(wrong, Convert.FromHexString(manifest.SaltHex));
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", Path.Combine(GoldenDir(), "repo")), CredentialRole.Backup, _ct);
        await Assert.ThrowsAnyAsync<Exception>(async () => await MetadataStore.LoadSnapshotAsync(s, keys, Convert.FromHexString(manifest.Snapshots[0].SnapIdHex), _ct));
    }
}
