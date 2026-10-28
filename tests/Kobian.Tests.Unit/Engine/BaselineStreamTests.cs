using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Format.Metadata;
using Kobian.Engine.Repo;
using Kobian.Providers.Storage.Local;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>BL-83 (RD-64; it. 113): baza przyrostu ładowana strumieniowo daje ten sam wynik dedup co lista BaselineIndex; zestaw jednorazowy. Dane syntetyczne.</summary>
public sealed class BaselineStreamTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-bs-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly RepoKeys _keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
    private readonly byte[] _host = RandomNumberGenerator.GetBytes(16);

    public BaselineStreamTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Repo);
    }

    private string Src => Path.Combine(_tmp, "src");

    private string Repo => Path.Combine(_tmp, "repo");

    public void Dispose()
    {
        _keys.Dispose();
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private async Task<IStorageSession> Open() =>
        await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", Repo), CredentialRole.Backup, _ct);

    [Fact, Trait("BL", "BL-83"), Trait("D", "D3.15")]
    public async Task Streamed_baseline_dedups_like_materialized_baseline_and_references_the_same_indexes()
    {
        for (var i = 0; i < 12; i++) File.WriteAllBytes(Path.Combine(Src, $"f{i}.bin"), RandomNumberGenerator.GetBytes(40_000 + i));
        await using var s = await Open();
        var p = new BackupPipeline(_keys, new VerifiedUploader(s));
        var full = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host, PackTargetSize: 100_000), _ct);
        // druga kopia pełna z innymi plikami = drugi indeks w bazie (wiele indeksów ładowanych po kolei)
        var snap = await MetadataStore.LoadSnapshotAsync(s, _keys, full.SnapId!, _ct);
        Assert.True(snap.Indexes.Count >= 1);
        File.WriteAllBytes(Path.Combine(Src, "f3.bin"), RandomNumberGenerator.GetBytes(41_000)); // 1 zmieniony plik
        File.WriteAllBytes(Path.Combine(Src, "nowy.bin"), RandomNumberGenerator.GetBytes(10_000));

        var loads = 0;
        var stream = await BaselineSet.LoadAsync([.. snap.Indexes.Select(i => i.IndexId)], async (id, t) =>
        {
            loads++;
            return await MetadataStore.LoadIndexAsync(s, _keys, id, t);
        }, _ct);
        Assert.Equal(snap.Indexes.Count, loads);
        Assert.Equal(13, stream.Blocks); // 12 bloków plików + 1 drzewo
        var inc = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host, SnapshotMode.Incremental, null, full.SnapId, BaselineStream: stream), _ct);
        Assert.Equal(BackupStatus.Succeeded, inc.Status);
        var incSnap = await MetadataStore.LoadSnapshotAsync(s, _keys, inc.SnapId!, _ct);
        Assert.Equal(snap.Indexes.Count + 1, incSnap.Indexes.Count); // bazowe + nowy
        Assert.Equal(snap.Indexes.Select(i => Convert.ToHexString(i.IndexId)), incSnap.Indexes.Take(snap.Indexes.Count).Select(i => Convert.ToHexString(i.IndexId)));
        Assert.Equal(snap.Indexes.Select(i => Convert.ToHexString(i.Sha256)), incSnap.Indexes.Take(snap.Indexes.Count).Select(i => Convert.ToHexString(i.Sha256)));

        // ten sam przyrost przez listę BaselineIndex: identyczne liczby nowych bajtów
        var baseline = new List<BaselineIndex>();
        foreach (var (id, _) in snap.Indexes)
        {
            var (idx, sha) = await MetadataStore.LoadIndexAsync(s, _keys, id, _ct);
            baseline.Add(new BaselineIndex(id, sha, idx));
        }
        var inc2 = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host, SnapshotMode.Incremental, baseline, full.SnapId), _ct);
        Assert.Equal(inc.Stats.BytesNew, inc2.Stats.BytesNew);
        Assert.True(inc.Stats.BytesNew > 0 && inc.Stats.BytesNew < 60_000); // tylko zmieniony + nowy plik (po kompresji/szyfrowaniu)

        // zestaw jednorazowy — tabela zamrożona i rozszerzona o bloki nowe
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.RunAsync(
            new BackupRequest(new FileSystemSource(Src), "job-1", _host, SnapshotMode.Incremental, null, full.SnapId, BaselineStream: stream), _ct));
        var both = await BaselineSet.FromIndexes(baseline, _ct);
        await Assert.ThrowsAsync<ArgumentException>(() => p.RunAsync(
            new BackupRequest(new FileSystemSource(Src), "job-1", _host, SnapshotMode.Incremental, baseline, full.SnapId, BaselineStream: both), _ct));
    }

    [Fact, Trait("BL", "BL-83")]
    public void Reserve_grows_by_half_not_double()
    {
        var t = new DedupTable();
        t.Reserve(1000);
        for (var i = 0; i < 1000; i++) t.AddConfirmed(RandomNumberGenerator.GetBytes(32));
        t.Reserve(10);
        Assert.Equal(1000, t.Count);
        t.Reference(RandomNumberGenerator.GetBytes(32));
        Assert.Equal(1000, t.Shape.BaseBlocks);
        t.Reserve(5); // po zamrożeniu bez skutku
    }
}
