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
using Kobian.Format.Pack;
using Kobian.Format.Repo;
using Kobian.Providers.Storage.Local;
using Xunit;

namespace Kobian.Tests.Unit.Quality;

/// <summary>
/// Repozytorium złote #3 (BL-9/BL-28, NFR-14, D3.12, D3.16): zatwierdzone repozytorium formatu v1 PO konsolidacji indeksów i prune dwufazowym —
/// pięć kopii (pełna + 4 przyrosty), dwie najstarsze zapomniane, indeksy scalone (próg 3), obiekty martwe usunięte po karencji.
/// Każda kolejna wersja kodu musi: odtworzyć każdy żywy snapshot bit w bit, przejść pełny <c>check</c> bez sierot, nie widzieć usuniętych snapshotów
/// oraz wykonać kolejny przyrost z bazą z indeksu skonsolidowanego (dedup — brak ponownego wysyłania niezmienionych danych).
/// Dane syntetyczne; klucz i sól to publiczne stałe TESTOWE (Qb104). Regeneracja (świadoma): <c>KOBIAN_GOLDEN_PRUNED_REGEN=&lt;katalog&gt;</c>
/// + <c>--method ...Golden_pruned_regenerate</c>.
/// </summary>
public sealed class GoldenPrunedRepoTests : IDisposable
{
    internal const string Name = "v1-pruned";
    // PROWIZORYCZNIE Qb104: stałe testowe (zmiana = regeneracja fixtury).
    private static readonly byte[] TestMasterKey = Enumerable.Range(0, 32).Select(i => (byte)(0x30 + i)).ToArray();
    private static readonly byte[] TestSalt = Enumerable.Range(0, 16).Select(i => (byte)(0x90 + i)).ToArray();
    private static readonly byte[] TestHost = Enumerable.Range(0, 16).Select(i => (byte)(0xB0 + i)).ToArray();
    private static readonly DateTime Mtime = new(2025, 9, 10, 11, 12, 13, DateTimeKind.Utc);
    private const int PackSize = 24 * 1024;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-golden3-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private sealed record GoldenFile(string Path, long Size, string Sha256, DateTime MtimeUtc);
    private sealed record GoldenSnapshot(string SnapIdHex, List<GoldenFile> Files);
    private sealed record GoldenManifest(string Format, string MasterKeyHex, string SaltHex, List<string> ForgottenHex, List<GoldenSnapshot> Live);

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

    private static void Step(string src, int step)
    {
        if (step == 0)
        {
            Put(src, "stale.bin", Stream("stale", 90 * 1024));
            Put(src, "zmienny.txt", Encoding.UTF8.GetBytes("wersja 0 " + string.Concat(Enumerable.Repeat("Kobian prune. ", 800))));
            Put(src, "ż/zażółć.txt", Encoding.UTF8.GetBytes("Zażółć gęślą jaźń."));
        }
        else
        {
            Put(src, "zmienny.txt", Encoding.UTF8.GetBytes($"wersja {step} " + string.Concat(Enumerable.Repeat("Kobian prune. ", 800 + step * 50))));
            Put(src, $"nowy{step}.bin", Stream("nowy" + step, 30 * 1024 + step * 1000));
            if (step == 3) File.Delete(Path.Combine(src, "ż", "zażółć.txt"));
        }
    }

    private static async Task<(string Id, List<GoldenFile> Files)> BackupAsync(IStorageSession s, RepoKeys keys, string src, string? parentHex, CancellationToken ct)
    {
        BaselineSet? baseline = null;
        if (parentHex is not null)
        {
            var p = await MetadataStore.LoadSnapshotAsync(s, keys, Convert.FromHexString(parentHex), ct);
            baseline = await BaselineSet.LoadResolvedAsync(p.Indexes, new IndexResolver(s, keys, cacheObjects: false), ct);
        }
        var r = await new BackupPipeline(keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(new FileSystemSource(src), "golden3", TestHost,
            baseline is null ? SnapshotMode.Full : SnapshotMode.Incremental, null, baseline is null ? null : Convert.FromHexString(parentHex!),
            PackTargetSize: PackSize, BaselineStream: baseline), ct);
        Assert.Equal(BackupStatus.Succeeded, r.Status);
        return (Convert.ToHexStringLower(r.SnapId!), Describe(src));
    }

    [Fact]
    public async Task Golden_pruned_regenerate()
    {
        var outDir = Environment.GetEnvironmentVariable("KOBIAN_GOLDEN_PRUNED_REGEN");
        if (string.IsNullOrEmpty(outDir)) return; // normalnie no-op
        var dest = Path.Combine(outDir, Name);
        if (Directory.Exists(dest)) throw new IOException("Katalog docelowy istnieje — nie nadpisuję złotego repo: " + dest);
        var repoDir = Path.Combine(dest, "repo");
        Directory.CreateDirectory(repoDir);
        var src = Path.Combine(_tmp, "src");
        using var keys = RepoKeys.Derive(TestMasterKey, TestSalt);
        var factory = new LocalStorageProviderFactory();
        var cfg = new DestinationConfig("d", "local", repoDir);

        var all = new List<(string Id, List<GoldenFile> Files)>();
        await using (var backup = await factory.OpenAsync(cfg, CredentialRole.Backup, _ct))
        {
            string? parent = null;
            for (var step = 0; step < 5; step++)
            {
                Step(src, step);
                var s = await BackupAsync(backup, keys, src, parent, _ct);
                all.Add(s);
                parent = s.Id;
            }
        }

        var forgotten = new HashSet<string>([all[0].Id, all[1].Id], StringComparer.Ordinal);
        await using (var maint = await factory.OpenAsync(cfg, CredentialRole.Maintenance, _ct))
        {
            await using (var lease = await RepoLocks.AcquireExclusiveAsync(maint, keys, RepoLockOwner.For(TestHost), TimeProvider.System, null, (_, _) => Task.CompletedTask, _ct))
            {
                var c = await IndexConsolidator.RunAsync(maint, keys, lease, forgotten, TestHost, new IndexConsolidationOptions(MaxActive: 3, MaxSmall: 10_000), _ct);
                Assert.Null(c.Code);
                Assert.True(c.Needed);
                await lease.ReleaseAsync(_ct);
            }

            var now = DateTimeOffset.UtcNow;
            var plan = PrunePlanner.Plan(await PrunePlanner.CollectAsync(maint, keys, forgotten, _ct), new Dictionary<string, DateTimeOffset>(), now, PrunePlanner.DefaultGrace);
            Assert.Null(plan.BlockedCode);
            var marks = plan.Dead.ToDictionary(o => o.Key, _ => now - TimeSpan.FromHours(25), StringComparer.Ordinal);
            await using var lease2 = await RepoLocks.AcquireExclusiveAsync(maint, keys, RepoLockOwner.For(TestHost), TimeProvider.System, null, (_, _) => Task.CompletedTask, _ct);
            var d = await PruneExecutor.DeleteAsync(maint, keys, lease2, forgotten, marks, plan.Dead, TimeProvider.System, PrunePlanner.DefaultGrace, _ct);
            await lease2.ReleaseAsync(_ct);
            Assert.Null(d.StopCode);
            Assert.NotEmpty(d.Deleted);
        }

        var locks = Path.Combine(repoDir, "locks");
        if (Directory.Exists(locks) && Directory.GetFiles(locks, "*", SearchOption.AllDirectories).Length == 0) Directory.Delete(locks, recursive: true);
        var manifest = new GoldenManifest("kobian-repo-v1", Convert.ToHexStringLower(TestMasterKey), Convert.ToHexStringLower(TestSalt),
            [all[0].Id, all[1].Id], all.Skip(2).Select(a => new GoldenSnapshot(a.Id, a.Files)).ToList());
        File.WriteAllText(Path.Combine(dest, "golden.json"), JsonSerializer.Serialize(manifest, Indented));
    }

    private static string GoldenDir() => Path.Combine(AppContext.BaseDirectory, "golden", Name);

    private static GoldenManifest LoadManifest() => JsonSerializer.Deserialize<GoldenManifest>(File.ReadAllText(Path.Combine(GoldenDir(), "golden.json")))!;

    private string CopyRepo()
    {
        var repo = Path.Combine(_tmp, "repo");
        var from = Path.Combine(GoldenDir(), "repo");
        Directory.CreateDirectory(repo);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(repo, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
        return repo;
    }

    private static RepoKeys Keys(GoldenManifest m) => RepoKeys.Derive(Convert.FromHexString(m.MasterKeyHex), Convert.FromHexString(m.SaltHex));

    private async Task<List<GoldenFile>> RestoreAsync(IStorageSession s, RepoKeys keys, string snapIdHex, string target)
    {
        var snap = await MetadataStore.LoadSnapshotAsync(s, keys, Convert.FromHexString(snapIdHex), _ct);
        using var reader = new RepoReader(s, keys);
        await reader.LoadIndexesAsync(snap, _ct);
        Directory.CreateDirectory(target);
        var r = await new RestoreExecutor(reader).RestoreAsync(snap, target, null, _ct);
        Assert.Equal(RestoreStatus.Succeeded, r.Status);
        return Describe(target);
    }

    [Fact, Trait("NFR", "NFR-14"), Trait("D", "D3.16")]
    public async Task Golden_pruned_repository_restores_every_live_snapshot_bit_identical_and_forgotten_ones_are_gone()
    {
        var m = LoadManifest();
        Assert.Equal(3, m.Live.Count);
        Assert.Equal(2, m.ForgottenHex.Count);
        var repo = CopyRepo();
        using var keys = Keys(m);
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", repo), CredentialRole.Backup, _ct);
        for (var i = 0; i < m.Live.Count; i++)
            Assert.Equal(m.Live[i].Files, await RestoreAsync(s, keys, m.Live[i].SnapIdHex, Path.Combine(_tmp, "restore" + i)));

        foreach (var f in m.ForgottenHex)
            Assert.False(File.Exists(Path.Combine(repo, "snapshots", f + ".snap")), "zapomniany snapshot powinien być usunięty przez prune");
        Assert.Equal(m.Live.Count, Directory.GetFiles(Path.Combine(repo, "snapshots"), "*.snap").Length);
        Assert.True(Directory.GetFiles(Path.Combine(repo, "index"), "*.idx").Length <= 2, "po konsolidacji i prune zostaje ≤ 2 indeksów aktywnych");
    }

    [Fact, Trait("NFR", "NFR-14"), Trait("D", "D3.12")]
    public async Task Golden_pruned_repository_passes_full_check_without_orphans()
    {
        var m = LoadManifest();
        var repo = CopyRepo();
        using var keys = Keys(m);
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", repo), CredentialRole.Backup, _ct);
        var check = await new RepoChecker(s, keys).CheckAsync(new CheckOptions(100), _ct);
        Assert.True(check.Status == CheckStatus.Succeeded, string.Join(", ", check.Issues.Select(i => i.Code + " " + i.Target)));
    }

    [Fact, Trait("NFR", "NFR-14"), Trait("D", "D3.16")]
    public async Task Golden_pruned_repository_accepts_incremental_backup_on_consolidated_index_with_dedup()
    {
        var m = LoadManifest();
        var repo = CopyRepo();
        using var keys = Keys(m);
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", repo), CredentialRole.Backup, _ct);
        var last = m.Live[^1];
        var src = Path.Combine(_tmp, "src");
        Assert.Equal(last.Files, await RestoreAsync(s, keys, last.SnapIdHex, src)); // stan źródła = ostatni żywy snapshot
        var packsBefore = Directory.GetFiles(Path.Combine(repo, "data"), "*.pack", SearchOption.AllDirectories).Length;

        Put(src, "dodatek.bin", Stream("dodatek", 20 * 1024));
        var next = await BackupAsync(s, keys, src, last.SnapIdHex, _ct);
        Assert.Equal(next.Files, await RestoreAsync(s, keys, next.Id, Path.Combine(_tmp, "restore-next")));
        var packsAfter = Directory.GetFiles(Path.Combine(repo, "data"), "*.pack", SearchOption.AllDirectories).Length;
        Assert.True(packsAfter - packsBefore <= 1, $"przyrost zdeduplikowany: nowe packi {packsAfter - packsBefore} (oczekiwane ≤ 1)");
    }
}
