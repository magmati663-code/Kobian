using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Engine.Restore;
using Kobian.Format.Metadata;
using Kobian.Providers.Storage.Local;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>RD-73 (it. 118): równoległe wysyłki packów przez osobne sesje — limit z AIMD, I-2 per pack, indeks po wszystkich packach, KB-2112 → sygnał. Dane syntetyczne.</summary>
public sealed class ParallelUploadTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-pu-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly RepoKeys _keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
    private readonly byte[] _host = RandomNumberGenerator.GetBytes(16);
    private readonly List<IStorageSession> _sessions = [];

    public ParallelUploadTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Repo);
        for (var i = 0; i < 16; i++) File.WriteAllBytes(Path.Combine(Src, $"f{i:D2}.bin"), RandomNumberGenerator.GetBytes(90_000 + i));
    }

    private string Src => Path.Combine(_tmp, "src");

    private string Repo => Path.Combine(_tmp, "repo");

    public void Dispose()
    {
        foreach (var s in _sessions) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _keys.Dispose();
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private sealed class Probe
    {
        private int _now;

        public int Max;

        public void Enter()
        {
            var n = Interlocked.Increment(ref _now);
            int m;
            while ((m = Volatile.Read(ref Max)) < n && Interlocked.CompareExchange(ref Max, n, m) != m) { }
        }

        public void Leave() => Interlocked.Decrement(ref _now);
    }

    /// <summary>Sesja-nakładka: liczy wysyłki packów w toku (z opóźnieniem, by się nakładały); opcjonalnie odmawia jak serwer z limitem sesji.</summary>
    private sealed class ProbeSession(IStorageSession inner, Probe probe, bool limitRefusal = false) : IStorageSession
    {
        private int _busy;

        public CredentialRole Role => inner.Role;

        public ProviderCapabilities Capabilities => inner.Capabilities;

        public ServerIdentity? PresentedIdentity => inner.PresentedIdentity;

        public ValueTask<StatResult?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

        public IAsyncEnumerable<StatResult> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);

        public ValueTask<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct) => inner.OpenReadAsync(path, offset, length, ct);

        public async ValueTask<IObjectWriter> CreateAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) throw new InvalidOperationException("session used concurrently"); // sesja jednowątkowa (D4.10)
            if (limitRefusal && path.StartsWith("data/", StringComparison.Ordinal))
            {
                Volatile.Write(ref _busy, 0);
                throw new StorageException(ErrorClass.Transient, KbCode.ServerSessionLimit, "Destination refused a new session (simulated).");
            }
            probe.Enter();
            await Task.Delay(30, ct);
            return new W(await inner.CreateAsync(path, expectedLength, mode, resumeFrom, ct), () => { probe.Leave(); Volatile.Write(ref _busy, 0); });
        }

        public ValueTask<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);

        public ValueTask DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

        public ValueTask<byte[]?> ServerHashAsync(string path, HashAlgorithmName algorithm, CancellationToken ct) => inner.ServerHashAsync(path, algorithm, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class W(IObjectWriter w, Action done) : IObjectWriter
        {
            public long CommittedOffset => w.CommittedOffset;

            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => w.WriteAsync(data, ct);

            public async ValueTask<StatResult> CommitAsync(CancellationToken ct)
            {
                try { return await w.CommitAsync(ct); }
                finally { done(); }
            }

            public async ValueTask AbortAsync(CancellationToken ct)
            {
                try { await w.AbortAsync(ct); }
                finally { done(); }
            }

            public ValueTask DisposeAsync() => w.DisposeAsync();
        }
    }

    private sealed class FixedParallelism(int current) : IUploadParallelism
    {
        public int Current { get; } = current;

        public int Successes;

        public int Signals;

        public bool OnSuccess()
        {
            Successes++;
            return false;
        }

        public bool OnLimitSignal()
        {
            Signals++;
            return true;
        }
    }

    private async Task<IStorageSession> Open()
    {
        var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", Repo), CredentialRole.Backup, _ct);
        _sessions.Add(s);
        return s;
    }

    private async Task<(BackupPipeline P, Probe Probe)> Pipeline(int uploaders, IUploadParallelism? par, bool refuse = false, long budget = BackupPipeline.DefaultPackBufferBudget)
    {
        var probe = new Probe();
        var list = new List<VerifiedUploader>();
        for (var i = 0; i < uploaders; i++) list.Add(new VerifiedUploader(new ProbeSession(await Open(), probe, refuse && i == 0)));
        return (new BackupPipeline(_keys, new VerifiedUploader(await Open())) { PackUploaders = list, Parallelism = par, PackBufferBudget = budget }, probe);
    }

    [Fact, Trait("RD", "RD-73"), Trait("Plan", "D4.16")]
    public async Task Parallel_packs_give_the_same_snapshot_and_a_complete_index()
    {
        var seq = await new BackupPipeline(_keys, new VerifiedUploader(await Open())).RunAsync(
            new BackupRequest(new FileSystemSource(Src), "job-1", _host, PackTargetSize: 150_000), _ct);
        var par = new FixedParallelism(3);
        var (p, probe) = await Pipeline(3, par);
        var r = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host, PackTargetSize: 150_000), _ct);
        Assert.Equal(BackupStatus.Succeeded, r.Status);
        Assert.InRange(probe.Max, 2, 3); // równolegle, ale nie ponad limit
        Assert.True(r.Stats.PacksNew >= 8);
        Assert.Equal(r.Stats.PacksNew, par.Successes);

        await using var s = await Open();
        var a = await MetadataStore.LoadSnapshotAsync(s, _keys, seq.SnapId!, _ct);
        var b = await MetadataStore.LoadSnapshotAsync(s, _keys, r.SnapId!, _ct);
        Assert.Equal(a.RootTreeId, b.RootTreeId); // ta sama treść
        var (idx, _) = await MetadataStore.LoadIndexAsync(s, _keys, b.Indexes.Single().IndexId, _ct);
        Assert.Equal(r.Stats.PacksNew, idx.Packs.Count);
        foreach (var pk in idx.Packs) Assert.NotNull(await s.StatAsync(Kobian.Format.Repo.RepoPaths.Pack(pk.PackId), _ct)); // każdy pack w indeksie jest na celu

        // przyrost od kopii równoległej: 0 nowych packów = indeks pełny (żaden blok nie zginął)
        var stream = await BaselineSet.LoadAsync([.. b.Indexes.Select(i => i.IndexId)], async (id, t) => await MetadataStore.LoadIndexAsync(s, _keys, id, t), _ct);
        var inc = await new BackupPipeline(_keys, new VerifiedUploader(s)).RunAsync(
            new BackupRequest(new FileSystemSource(Src), "job-1", _host, SnapshotMode.Incremental, null, r.SnapId, BaselineStream: stream), _ct);
        Assert.Equal(0, inc.Stats.PacksNew);
    }

    [Fact, Trait("RD", "RD-73"), Trait("Plan", "D4.16")]
    public async Task Aimd_limit_one_serializes_uploads()
    {
        var (p, probe) = await Pipeline(3, new FixedParallelism(1));
        var r = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host, PackTargetSize: 150_000), _ct);
        Assert.Equal(BackupStatus.Succeeded, r.Status);
        Assert.Equal(1, probe.Max);
    }

    [Fact, Trait("RD", "RD-73"), Trait("KB", "KB-2112")]
    public async Task Server_session_limit_fails_the_run_without_snapshot_and_signals_aimd()
    {
        var par = new FixedParallelism(2);
        var (p, _) = await Pipeline(2, par, refuse: true);
        var r = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host, PackTargetSize: 150_000), _ct);
        Assert.Equal(BackupStatus.Failed, r.Status);
        Assert.Equal(KbCode.RepoWriteFailed, r.Code);
        Assert.Null(r.SnapId);
        Assert.Equal(1, par.Signals);
        Assert.False(Directory.Exists(Path.Combine(Repo, "snapshots")) && Directory.EnumerateFiles(Path.Combine(Repo, "snapshots")).Any());
    }

    // ---------- RD-75 (it. 120): budżet pamięci buforów packów ----------

    private const long Target = 150_000;

    /// <summary>Odtworzenie snapshotu i porównanie bajt w bajt ze źródłem.</summary>
    private async Task AssertRestoresBitForBit(byte[] snapId)
    {
        await using var s = await Open();
        var snap = await MetadataStore.LoadSnapshotAsync(s, _keys, snapId, _ct);
        using var reader = new RepoReader(s, _keys);
        await reader.LoadIndexesAsync(snap, _ct);
        var target = Path.Combine(_tmp, "restore-" + Convert.ToHexStringLower(snapId));
        Directory.CreateDirectory(target);
        var r = await new RestoreExecutor(reader).RestoreAsync(snap, target, null, _ct);
        Assert.Equal(RestoreStatus.Succeeded, r.Status);
        var restored = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(16, restored.Count);
        foreach (var f in Directory.EnumerateFiles(Src))
        {
            var copy = Directory.EnumerateFiles(target, Path.GetFileName(f), SearchOption.AllDirectories).Single();
            Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(copy));
        }
    }

    [Fact, Trait("RD", "RD-75"), Trait("NFR", "NFR-05"), Trait("Plan", "D4.16")]
    public async Task Pack_buffer_budget_caps_buffers_and_uploads_in_flight_without_changing_the_backup()
    {
        var seq = await new BackupPipeline(_keys, new VerifiedUploader(await Open())).RunAsync(
            new BackupRequest(new FileSystemSource(Src), "job-1", _host, PackTargetSize: Target), _ct);
        var cap = BackupPipeline.PackBufferCapacity(Target);
        var (p, probe) = await Pipeline(3, new FixedParallelism(3), budget: 3L * cap); // 3 bufory: budowany + 2 w toku
        var r = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host, PackTargetSize: Target), _ct);
        Assert.Equal(BackupStatus.Succeeded, r.Status);
        Assert.True(r.Stats.PacksNew >= 8);
        Assert.InRange(probe.Max, 1, 2); // AIMD i sesje pozwalają na 3 — budżet na 2
        Assert.InRange(p.PeakPackBuffers, 2, 3); // sonda: (w toku + budowany) × pojemność ≤ budżet
        Assert.InRange(p.PackBuffersCreated, 2, 3); // pozostałe packi na buforach z puli

        await using var s = await Open();
        var a = await MetadataStore.LoadSnapshotAsync(s, _keys, seq.SnapId!, _ct);
        var b = await MetadataStore.LoadSnapshotAsync(s, _keys, r.SnapId!, _ct);
        Assert.Equal(a.RootTreeId, b.RootTreeId); // ta sama treść (drzewa niosą SHA-256 plików)
        await AssertRestoresBitForBit(r.SnapId!);
    }

    [Fact, Trait("RD", "RD-75"), Trait("NFR", "NFR-05")]
    public async Task Budget_smaller_than_two_buffers_still_uploads_one_pack_at_a_time()
    {
        var (p, probe) = await Pipeline(3, null, budget: BackupPipeline.PackBufferCapacity(Target) / 2);
        var r = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host, PackTargetSize: Target), _ct);
        Assert.Equal(BackupStatus.Succeeded, r.Status);
        Assert.Equal(1, probe.Max); // minimum: jedna wysyłka
        Assert.InRange(p.PeakPackBuffers, 1, 2); // wysyłany + budowany
        await AssertRestoresBitForBit(r.SnapId!);
    }

    [Fact, Trait("RD", "RD-75"), Trait("NFR", "NFR-05")]
    public async Task Sequential_path_reuses_a_single_pack_buffer()
    {
        var p = new BackupPipeline(_keys, new VerifiedUploader(await Open()));
        var r = await p.RunAsync(new BackupRequest(new FileSystemSource(Src), "job-1", _host, PackTargetSize: Target), _ct);
        Assert.Equal(BackupStatus.Succeeded, r.Status);
        Assert.True(r.Stats.PacksNew >= 8);
        Assert.Equal(1, p.PeakPackBuffers);
        Assert.Equal(1, p.PackBuffersCreated); // jeden bufor z puli dla wszystkich packów (bez nowej tablicy na pack)
        await AssertRestoresBitForBit(r.SnapId!);
    }

    [Fact, Trait("RD", "RD-75")]
    public void Pack_buffer_capacity_holds_target_plus_one_block_and_is_bounded()
    {
        Assert.Equal((16 << 20) + BackupPipeline.ChunkSize + (512 << 10) + (64 << 10), BackupPipeline.PackBufferCapacity(16 << 20));
        Assert.True(BackupPipeline.PackBufferCapacity(512L << 20) <= Array.MaxLength);
        Assert.Equal(Array.MaxLength, BackupPipeline.PackBufferCapacity(long.MaxValue / 64));
        // domyślny budżet przy packu 16 MiB nie ogranicza dzisiejszych 2–4 sesji (Local 2, SMB 4, SFTP 3)
        Assert.True(BackupPipeline.DefaultPackBufferBudget / BackupPipeline.PackBufferCapacity(16 << 20) - 1 >= 4);
    }
}
