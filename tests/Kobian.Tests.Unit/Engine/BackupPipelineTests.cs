using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;
using Kobian.Format.Repo;
using Kobian.Providers.Storage.Local;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>BL-22 / D3.14 na dostawcy local; dane syntetyczne w katalogach tymczasowych.</summary>
public sealed class BackupPipelineTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-bp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly RepoKeys _keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
    private readonly byte[] _host = RandomNumberGenerator.GetBytes(16);

    public BackupPipelineTests()
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

    private void MakeSource()
    {
        Directory.CreateDirectory(Path.Combine(Src, "a", "b"));
        File.WriteAllBytes(Path.Combine(Src, "big.bin"), RandomNumberGenerator.GetBytes((4 * 1024 * 1024) + 123)); // 2 bloki
        var same = RandomNumberGenerator.GetBytes(50_000);
        File.WriteAllBytes(Path.Combine(Src, "a", "x.dat"), same);
        File.WriteAllBytes(Path.Combine(Src, "a", "b", "y.dat"), same); // dedup
        File.WriteAllText(Path.Combine(Src, "a", "b", "żółw.txt"), "zawartość");
        File.WriteAllBytes(Path.Combine(Src, "empty"), []);
    }

    private async Task<IStorageSession> Open() =>
        await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", Repo), CredentialRole.Backup, _ct);

    private int SnapshotCount() => Directory.Exists(Path.Combine(Repo, "snapshots"))
        ? Directory.GetFiles(Path.Combine(Repo, "snapshots"), "*.snap").Length : 0;

    [Fact, Trait("BL", "BL-22"), Trait("D", "D3.14")]
    public async Task Full_backup_writes_packs_index_and_snapshot_last_with_dedup()
    {
        MakeSource();
        await using var s = await Open();
        var p = new BackupPipeline(_keys, new VerifiedUploader(s));
        var r = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host, PackTargetSize: 1024 * 1024), _ct);

        Assert.Equal(BackupStatus.Succeeded, r.Status);
        Assert.Equal(5, r.Stats.Files);
        Assert.Equal(2, r.Stats.Dirs);
        Assert.Equal(1, SnapshotCount());
        var snap = await MetadataStore.LoadSnapshotAsync(s, _keys, r.SnapId!, _ct);
        Assert.Equal("job-1", snap.JobId);
        Assert.Single(snap.Indexes);
        var (idx, sha) = await MetadataStore.LoadIndexAsync(s, _keys, snap.Indexes[0].IndexId, _ct);
        Assert.Equal(snap.Indexes[0].Sha256, sha);
        var chunks = idx.Packs.SelectMany(pk => pk.Entries).Select(e => Convert.ToHexString(e.ChunkId)).ToList();
        Assert.Equal(chunks.Count, chunks.Distinct().Count()); // każdy blok zapisany raz (dedup x.dat = y.dat)
        Assert.True(idx.Packs.Count >= 2); // zamknięcie packa po progu
        Assert.False(Directory.EnumerateFiles(Repo, "*.tmp-*", SearchOption.AllDirectories).Any());
    }

    /// <summary>
    /// Regresja wydajności (smoke 50 tys. małych plików: backup 44 s → 6 s po naprawie): każdy plik alokował własny bufor 4 MiB (sterta dużych obiektów, zerowanie),
    /// więc alokacje rosły z liczbą plików, nie z ich rozmiarem. 400 małych plików ma alokować rzędy dziesiątek MB, nie ~1,6 GB.
    /// </summary>
    [Fact, Trait("BL", "BL-22"), Trait("Category", "Perf-regression")]
    public async Task Many_small_files_do_not_allocate_a_read_buffer_per_file()
    {
        for (var d = 0; d < 4; d++)
        {
            Directory.CreateDirectory(Path.Combine(Src, "d" + d));
            for (var i = 0; i < 100; i++) File.WriteAllBytes(Path.Combine(Src, "d" + d, "f" + i + ".dat"), RandomNumberGenerator.GetBytes(100 + i));
        }
        await using var s = await Open();
        var p = new BackupPipeline(_keys, new VerifiedUploader(s));
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var r = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-small", _host, PackTargetSize: 1024 * 1024), _ct);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(BackupStatus.Succeeded, r.Status);
        Assert.Equal(400, r.Stats.Files);
        Assert.True(allocated < 400L * 1024 * 1024, $"Allocated {allocated / (1024 * 1024)} MiB for 400 small files (regression: per-file 4 MiB buffer).");
    }

    /// <summary>
    /// Format dopuszcza 65 536 bloków w packu; zapis pilnował tylko rozmiaru, więc źródło z bardzo wieloma drobnymi plikami przy dużym packTargetSize kończyło się KB-1308 (Failed,
    /// deterministycznie przy każdym uruchomieniu). Pack zamykany jest też po liczbie ramek.
    /// </summary>
    [Fact, Trait("BL", "BL-22"), Trait("Category", "Perf-regression")]
    public async Task Pack_is_closed_at_the_frame_limit_even_when_the_size_target_is_far_away()
    {
        for (var d = 0; d < 66; d++)
        {
            var dir = Path.Combine(Src, "d" + d);
            Directory.CreateDirectory(dir);
            for (var i = 0; i < 1000; i++) File.WriteAllText(Path.Combine(dir, "f" + i), "plik " + d + "/" + i); // unikalna treść = osobny blok
        }
        await using var s = await Open();
        var p = new BackupPipeline(_keys, new VerifiedUploader(s));
        var r = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-many", _host, PackTargetSize: 512L * 1024 * 1024), _ct);

        Assert.Equal(BackupStatus.Succeeded, r.Status);
        Assert.Equal(66_000, r.Stats.Files);
        Assert.True(r.Stats.PacksNew >= 2, "ponad 65 536 bloków nie mieści się w jednym packu");
    }

    /// <summary>Nazwa, której format drzewa nie przyjmie (tu: znak „\\” w nazwie na Linuksie), pomija TYLKO ten element z ostrzeżeniem, a nie całą kopię (wcześniej KB-1306 → Failed przy każdym biegu).</summary>
    [Fact, Trait("BL", "BL-22"), Trait("AC", "AC-08.1")]
    public async Task File_with_a_name_the_tree_format_rejects_is_skipped_not_fatal()
    {
        if (OperatingSystem.IsWindows()) return; // na Windows znak „\\” nie może wystąpić w nazwie
        File.WriteAllText(Path.Combine(Src, "dobry.txt"), "ok");
        File.WriteAllText(Path.Combine(Src, "zly\\plik.txt"), "nazwa z backslashem");
        await using var s = await Open();
        var r = await new BackupPipeline(_keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(new FileSystemSource(Src), "job-name", _host), _ct);

        Assert.NotEqual(BackupStatus.Failed, r.Status);
        Assert.Equal(1, r.Stats.Files); // dobry.txt
        Assert.Equal(1L, r.Skipped);
        Assert.NotNull(r.SnapId);
    }

    [Fact, Trait("BL", "BL-22"), Trait("AC", "AC-08.1")]
    public async Task Incremental_without_changes_uploads_no_new_packs()
    {
        MakeSource();
        await using var s = await Open();
        var p = new BackupPipeline(_keys, new VerifiedUploader(s));
        var full = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host), _ct);
        var snap = await MetadataStore.LoadSnapshotAsync(s, _keys, full.SnapId!, _ct);
        var baseline = new List<BaselineIndex>();
        foreach (var (id, _) in snap.Indexes)
        {
            var (idx, sha) = await MetadataStore.LoadIndexAsync(s, _keys, id, _ct);
            baseline.Add(new BaselineIndex(id, sha, idx));
        }
        var inc = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host, SnapshotMode.Incremental, baseline, full.SnapId), _ct);
        Assert.Equal(BackupStatus.Succeeded, inc.Status);
        Assert.Equal(0, inc.Stats.PacksNew);
        Assert.Equal(0, inc.Stats.BytesNew);
        var incSnap = await MetadataStore.LoadSnapshotAsync(s, _keys, inc.SnapId!, _ct);
        Assert.Equal(snap.RootTreeId, incSnap.RootTreeId);
        Assert.Equal(full.SnapId, incSnap.ParentSnapId);
    }

    [Theory, Trait("AC", "AC-08.4"), Trait("D", "D3.14"), Trait("KB", "KB-1402")]
    [InlineData("data/", 1)]
    [InlineData("data/", 2)]
    [InlineData("index/", 1)]
    [InlineData("snapshots/", 1)]
    public async Task Repo_write_failure_fails_run_without_snapshot_then_next_run_succeeds(string prefix, int failAt)
    {
        MakeSource();
        await using (var inner = await Open())
        {
            var faulty = new FailingSession(inner, prefix, failAt);
            var r = await new BackupPipeline(_keys, new VerifiedUploader(faulty)).RunAsync(
                new BackupRequest(new FileSystemSource(Src), "job-1", _host, PackTargetSize: 1024 * 1024), _ct);
            Assert.Equal(BackupStatus.Failed, r.Status);
            Assert.Equal(KbCode.RepoWriteFailed, r.Code);
            Assert.Null(r.SnapId);
            Assert.Equal(0, SnapshotCount());
        }
        await using var s = await Open();
        var ok = await new BackupPipeline(_keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host), _ct);
        Assert.Equal(BackupStatus.Succeeded, ok.Status);
        Assert.Equal(1, SnapshotCount());
    }

    [Fact, Trait("D", "D3.14"), Trait("KB", "KB-1405")]
    public async Task Missing_source_root_is_failed_not_warning()
    {
        var src = new FileSystemSource(Src);
        Directory.Delete(Src);
        await using var s = await Open();
        var r = await new BackupPipeline(_keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(src, "job-1", _host), _ct);
        Assert.Equal(BackupStatus.Failed, r.Status);
        Assert.Equal(KbCode.SourceUnreadable, r.Code);
        Assert.Equal(0, SnapshotCount());
    }

    /// <summary>Dekorator: N-ty zapis pod prefiksem kończy się „brakiem miejsca” (CE-14).</summary>
    private sealed class FailingSession(IStorageSession inner, string prefix, int failAt) : IStorageSession
    {
        private int _n;

        public CredentialRole Role => inner.Role;

        public ProviderCapabilities Capabilities => inner.Capabilities;

        public ServerIdentity? PresentedIdentity => inner.PresentedIdentity;

        public ValueTask<StatResult?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

        public IAsyncEnumerable<StatResult> ListAsync(string prefix2, CancellationToken ct) => inner.ListAsync(prefix2, ct);

        public ValueTask<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct) => inner.OpenReadAsync(path, offset, length, ct);

        public ValueTask<IObjectWriter> CreateAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal) && ++_n == failAt)
                throw new StorageException(ErrorClass.Transient, KbCode.DestinationFull, "No space left (simulated).");
            return inner.CreateAsync(path, expectedLength, mode, resumeFrom, ct);
        }

        public ValueTask<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);

        public ValueTask DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

        public ValueTask<byte[]?> ServerHashAsync(string path, System.Security.Cryptography.HashAlgorithmName algorithm, CancellationToken ct) => inner.ServerHashAsync(path, algorithm, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
