using System.Security.Cryptography;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>
/// BL-26 (KOSZT-14): <c>check</c> poziomu V2 (strukturalny) i V3 (próbka rotacyjna) na repozytorium z prawdziwego potoku (MemoryStorage, dane syntetyczne).
/// AC-09.1: brak jednego packa → Failed (KB-1501) i lista dotkniętych snapshotów; kompletne repo → Succeeded z poziomem V2.
/// </summary>
public sealed class RepoCheckerTests : IDisposable
{
    private readonly string _src = Path.Combine(Path.GetTempPath(), "kobian-chk-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly RepoKeys _keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
    private readonly MemoryStorage _store = new();

    public RepoCheckerTests()
    {
        var rnd = new Random(55);
        Directory.CreateDirectory(Path.Combine(_src, "d1", "d2"));
        for (var i = 0; i < 10; i++)
        {
            var b = new byte[rnd.Next(50_000, 400_000)];
            rnd.NextBytes(b);
            File.WriteAllBytes(Path.Combine(_src, i % 2 == 0 ? "d1" : Path.Combine("d1", "d2"), $"f{i}.bin"), b);
        }
    }

    public void Dispose()
    {
        _keys.Dispose();
        if (Directory.Exists(_src)) Directory.Delete(_src, recursive: true);
    }

    /// <summary>Pełny backup, zmiana jednego pliku, przyrost — dwa snapshoty współdzielące packi.</summary>
    private async Task<(byte[] Full, byte[] Inc)> TwoSnapshots()
    {
        var host = RandomNumberGenerator.GetBytes(16);
        var s = _store.Open();
        var pipeline = new BackupPipeline(_keys, new VerifiedUploader(s));
        var full = await pipeline.RunAsync(new BackupRequest(new FileSystemSource(_src), "job", host, PackTargetSize: 512 * 1024), _ct);
        Assert.Equal(BackupStatus.Succeeded, full.Status);
        var snap1 = await MetadataStore.LoadSnapshotAsync(s, _keys, full.SnapId!, _ct);
        File.WriteAllBytes(Path.Combine(_src, "nowy.bin"), RandomNumberGenerator.GetBytes(70_000));
        var baseline = new List<BaselineIndex>();
        foreach (var (id, _) in snap1.Indexes)
        {
            var (idx, sha) = await MetadataStore.LoadIndexAsync(s, _keys, id, _ct);
            baseline.Add(new BaselineIndex(id, sha, idx));
        }
        var inc = await pipeline.RunAsync(new BackupRequest(new FileSystemSource(_src), "job", host, SnapshotMode.Incremental, baseline, full.SnapId, PackTargetSize: 512 * 1024), _ct);
        Assert.Equal(BackupStatus.Succeeded, inc.Status);
        return (full.SnapId!, inc.SnapId!);
    }

    private Task<CheckReport> Check(int percent = 0, long rotation = 0, int maxIssues = 1000) =>
        new RepoChecker(_store.Open(), _keys).CheckAsync(new CheckOptions(percent, rotation, maxIssues), _ct);

    private List<string> PackPaths() => _store.Objects.Keys.Where(k => k.StartsWith("data/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

    [Fact, Trait("AC", "AC-09.1"), Trait("V", "V2"), Trait("V", "V3")]
    public async Task Complete_repository_passes_V2_and_full_V3_and_check_is_read_only()
    {
        await TwoSnapshots();
        var before = _store.Objects.ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value)));

        var v2 = await Check();
        Assert.Equal(CheckStatus.Succeeded, v2.Status);
        Assert.Equal(2, v2.Level);
        Assert.Equal(2, v2.Snapshots);
        Assert.Equal(PackPaths().Count, v2.Packs);
        Assert.True(v2.Packs >= 3, $"packs={v2.Packs}");
        Assert.Empty(v2.Issues);
        Assert.Empty(v2.DamagedSnapshots);
        Assert.Equal(0, v2.PacksRead);

        var v3 = await Check(100);
        Assert.Equal(CheckStatus.Succeeded, v3.Status);
        Assert.Equal(3, v3.Level);
        Assert.Equal(v3.Packs, v3.PacksRead);
        Assert.True(v3.BlocksVerified > 10);

        // tylko odczyt: zero nowych, zmienionych i usuniętych obiektów
        Assert.Equal(before, _store.Objects.ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value))));
    }

    [Fact, Trait("AC", "AC-09.1"), Trait("V", "V2")]
    public async Task Missing_pack_fails_V2_with_KB_1501_and_names_affected_snapshots()
    {
        var (full, inc) = await TwoSnapshots();
        // pack z pierwszego (pełnego) przebiegu jest używany przez oba snapshoty (przyrost reużywa niezmienione bloki)
        var victim = PackPaths()[0];
        var packHex = victim[^37..^5];
        Assert.True(_store.Objects.TryRemove(victim, out _));

        var r = await Check();
        Assert.Equal(CheckStatus.Failed, r.Status);
        Assert.Contains(r.Issues, i => i.Code == KbCode.CheckPackMissing && i.Target == "pack:" + packHex);
        Assert.NotEmpty(r.DamagedSnapshots);
        Assert.All(r.DamagedSnapshots, d => Assert.Contains(d, new[] { Convert.ToHexStringLower(full), Convert.ToHexStringLower(inc) }));
        // raport nie zawiera nazw plików źródłowych (NFR-11)
        Assert.DoesNotContain(r.Issues, i => i.Target.Contains(".bin", StringComparison.Ordinal));
    }

    [Fact, Trait("AC", "AC-09.1"), Trait("V", "V2")]
    public async Task Truncated_pack_fails_with_KB_1502()
    {
        await TwoSnapshots();
        var victim = PackPaths()[^1];
        _store.Objects[victim] = _store.Objects[victim][..^1];
        var r = await Check();
        Assert.Equal(CheckStatus.Failed, r.Status);
        Assert.Contains(r.Issues, i => i.Code == KbCode.CheckPackSizeMismatch && i.Target == "pack:" + victim[^37..^5]);
    }

    [Fact, Trait("AC", "AC-09.1"), Trait("V", "V2")]
    public async Task Single_snapshot_V2_passes_without_listing_and_is_read_only()
    {
        var (full, inc) = await TwoSnapshots();
        var before = _store.Objects.ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value)));
        var r = await new RepoChecker(_store.Open(), _keys).CheckSnapshotAsync(inc, _ct);
        Assert.Equal(CheckStatus.Succeeded, r.Status);
        Assert.Equal(2, r.Level);
        Assert.Equal(1, r.Snapshots);
        Assert.True(r.Packs >= 1);
        Assert.Empty(r.Issues);
        Assert.Equal(before, _store.Objects.ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value))));
        // sieroty i snapshoty obok nie są przedmiotem V2 pojedynczego snapshotu
        _store.Objects["data/ab/" + new string('a', 30) + "ab.pack"] = [1, 2, 3];
        Assert.Equal(CheckStatus.Succeeded, (await new RepoChecker(_store.Open(), _keys).CheckSnapshotAsync(full, _ct)).Status);
    }

    [Fact, Trait("AC", "AC-09.1"), Trait("V", "V2")]
    public async Task Single_snapshot_V2_reports_missing_and_truncated_packs_and_missing_snapshot()
    {
        var (full, inc) = await TwoSnapshots();
        // pack używany przez przyrost (packi z samymi starymi drzewami pełnej kopii nie są jego częścią) — wyszukanie deterministyczne
        string? victim = null;
        byte[]? victimBytes = null;
        CheckReport? r = null;
        foreach (var path in PackPaths())
        {
            Assert.True(_store.Objects.TryRemove(path, out var bytes));
            r = await new RepoChecker(_store.Open(), _keys).CheckSnapshotAsync(inc, _ct);
            if (r.Status == CheckStatus.Failed) { (victim, victimBytes) = (path, bytes); break; }
            _store.Objects[path] = bytes;
        }
        Assert.NotNull(victim);
        Assert.Contains(r!.Issues, i => i.Code == KbCode.CheckPackMissing && i.Target == "pack:" + victim[^37..^5] && i.SnapshotId == Convert.ToHexStringLower(inc));
        Assert.Equal([Convert.ToHexStringLower(inc)], r.DamagedSnapshots);

        // obcięcie każdego packa (brakujący wrócił — inaczej drzewo w nim zatrzymuje przejście i reszta nie jest sprawdzana): używane → KB-1502
        _store.Objects[victim] = victimBytes!;
        foreach (var path in PackPaths()) _store.Objects[path] = _store.Objects[path][..^1];
        var r2 = await new RepoChecker(_store.Open(), _keys).CheckSnapshotAsync(inc, _ct);
        Assert.Contains(r2.Issues, i => i.Code == KbCode.CheckPackSizeMismatch);
        Assert.All(r2.Issues.Where(i => i.Code == KbCode.CheckPackSizeMismatch), i => Assert.Contains(PackPaths(), p => "pack:" + p[^37..^5] == i.Target));

        Assert.True(_store.Objects.TryRemove("snapshots/" + Convert.ToHexStringLower(full) + ".snap", out _));
        var r3 = await new RepoChecker(_store.Open(), _keys).CheckSnapshotAsync(full, _ct);
        Assert.Equal(CheckStatus.Failed, r3.Status);
        Assert.Equal("snapshot:" + Convert.ToHexStringLower(full), Assert.Single(r3.Issues).Target);
    }

    [Fact, Trait("V", "V3")]
    public async Task Flipped_byte_in_a_pack_is_found_by_V3_and_the_pack_is_not_counted_as_read()
    {
        await TwoSnapshots();
        var victim = PackPaths().OrderByDescending(p => _store.Objects[p].Length).First();
        var bytes = (byte[])_store.Objects[victim].Clone();
        bytes[bytes.Length / 2] ^= 0x40;
        _store.Objects[victim] = bytes;

        var r = await Check(100);
        Assert.Equal(CheckStatus.Failed, r.Status);
        Assert.Contains(r.Issues, i => i.Target == "pack:" + victim[^37..^5] && i.Code is KbCode.ReferenceIntegrityFailed or KbCode.AuthenticationFailed);
        Assert.Equal(r.Packs - 1, r.PacksRead);
        Assert.NotEmpty(r.DamagedSnapshots);
    }

    [Fact, Trait("V", "V3")]
    public async Task Rotating_sample_covers_every_pack_exactly_once_over_the_cycle()
    {
        await TwoSnapshots();
        var packs = PackPaths().Count;
        var total = 0;
        for (var rot = 0; rot < 3; rot++)
        {
            var r = await Check(34, rot); // ⌈100/34⌉ = 3 kubełki
            Assert.Equal(CheckStatus.Succeeded, r.Status);
            Assert.InRange(r.PacksRead, 0, (packs / 3) + 1);
            total += r.PacksRead;
        }
        Assert.Equal(packs, total);
        // rotacja ujemna i duża mapuje się na ten sam cykl
        Assert.Equal((await Check(34, 0)).PacksRead, (await Check(34, 300)).PacksRead);
        Assert.Equal((await Check(34, 2)).PacksRead, (await Check(34, -1)).PacksRead);
    }

    [Fact, Trait("D", "D3.16")]
    public async Task Objects_of_a_removed_snapshot_are_orphans_reported_as_warning_only()
    {
        var (_, inc) = await TwoSnapshots();
        Assert.True(_store.Objects.TryRemove("snapshots/" + Convert.ToHexStringLower(inc) + ".snap", out _));
        var r = await Check();
        Assert.Equal(CheckStatus.Warning, r.Status);
        Assert.Equal(1, r.Snapshots);
        Assert.NotEmpty(r.Issues);
        Assert.All(r.Issues, i => Assert.Equal(KbCode.CheckOrphanObject, i.Code));
        Assert.Contains(r.Issues, i => i.Target.StartsWith("index:", StringComparison.Ordinal));
        Assert.Empty(r.DamagedSnapshots);
    }

    [Fact, Trait("V", "V2")]
    public async Task Corrupted_snapshot_object_is_reported_and_the_other_snapshot_still_checked()
    {
        var (full, inc) = await TwoSnapshots();
        var path = "snapshots/" + Convert.ToHexStringLower(inc) + ".snap";
        var b = (byte[])_store.Objects[path].Clone();
        b[^3] ^= 1;
        _store.Objects[path] = b;
        var r = await Check();
        Assert.Equal(CheckStatus.Failed, r.Status);
        Assert.Equal(1, r.Snapshots);
        Assert.Contains(r.Issues, i => i.Target == "snapshot:" + Convert.ToHexStringLower(inc) && i.SnapshotId == Convert.ToHexStringLower(inc));
        Assert.DoesNotContain(Convert.ToHexStringLower(full), r.DamagedSnapshots);
    }

    [Fact]
    public async Task Issue_list_is_capped_and_flagged_as_truncated()
    {
        await TwoSnapshots();
        foreach (var p in PackPaths()) _store.Objects.TryRemove(p, out _);
        var r = await Check(maxIssues: 1);
        Assert.Equal(CheckStatus.Failed, r.Status);
        Assert.Single(r.Issues);
        Assert.True(r.IssuesTruncated);
        Assert.Equal(2, r.DamagedSnapshots.Count);
    }

    [Fact]
    public async Task Empty_repository_is_succeeded_and_options_are_validated()
    {
        var r = await Check();
        Assert.Equal(CheckStatus.Succeeded, r.Status);
        Assert.Equal(0, r.Snapshots);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Check(101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Check(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Check(maxIssues: 0));
    }

    [Theory]
    [InlineData("data/ab/ab0123456789abcdef0123456789abcd.pack", "ab0123456789abcdef0123456789abcd")]
    [InlineData("data/ac/ab0123456789abcdef0123456789abcd.pack", null)]
    [InlineData("data/ab/AB0123456789abcdef0123456789abcd.pack", null)]
    [InlineData("data/ab/ab0123456789abcdef0123456789abcd.pack.tmp", null)]
    [InlineData("data/ab0123456789abcdef0123456789abcd.pack", null)]
    [InlineData("index/ab/ab0123456789abcdef0123456789abcd.pack", null)]
    [InlineData("data/ab/../ab0123456789abcdef0123456789ab.pack", null)]
    public void Pack_paths_are_parsed_strictly(string path, string? expected) =>
        Assert.Equal(expected, RepoChecker.TryParsePackPath(path));
}
