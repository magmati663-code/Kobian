using System.Diagnostics;
using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Engine.Restore;
using Kobian.Format.Metadata;
using Kobian.Format.Pack;
using Kobian.Format.Repo;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>
/// D3.16 / L4 / PM-17 (BL-25 dokończenie): konsolidacja indeksów na rzeczywistym potoku (MemoryStorage, dane syntetyczne).
/// Właściwości: konsolidacja nie zmienia zbioru żywych packów ani odtwarzalności (bit w bit) żadnego żywego snapshotu; po prune liczba plików .idx ≤ próg;
/// awaria na każdym kroku (zapis nowego indeksu, przed usunięciem, w trakcie usuwania) zostawia repo czytelne i kompletne, a kolejny przebieg dokańcza;
/// przyrost po konsolidacji deduplikuje (widzi bloki z indeksu skonsolidowanego).
/// </summary>
public sealed class IndexConsolidationTests : IDisposable
{
    private static readonly IndexConsolidationOptions Small = new(MaxActive: 6, MaxSmall: 10_000);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kobian-consol-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly RepoKeys _keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
    private readonly MemoryStorage _store = new();
    private readonly byte[] _host = RandomNumberGenerator.GetBytes(16);

    public void Dispose()
    {
        _keys.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record Snap(string Job, string Id, Dictionary<string, string> Files, SnapshotStats Stats);

    private string Src(string job) => Path.Combine(_root, "src-" + job);

    private static void Mutate(string dir, Random rnd, int step, int maxSize = 40_000)
    {
        Directory.CreateDirectory(dir);
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        if (files.Count > 3 && rnd.Next(3) == 0) File.Delete(files[rnd.Next(files.Count)]);
        if (files.Count > 0 && rnd.Next(2) == 0) File.WriteAllBytes(files[rnd.Next(files.Count)], Bytes(rnd, rnd.Next(500, maxSize)));
        var sub = Path.Combine(dir, "k" + rnd.Next(3));
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, $"n{step}.bin"), Bytes(rnd, rnd.Next(500, maxSize)));
    }

    private static byte[] Bytes(Random rnd, int n)
    {
        var b = new byte[n];
        rnd.NextBytes(b);
        return b;
    }

    private static Dictionary<string, string> State(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);

    /// <summary>Kopia jak w Agencie (CatalogJobService): baza przyrostu z odwołań rodzica przez IndexResolver (D3.16).</summary>
    private async Task<Snap> Backup(string job, Snap? parent, bool full)
    {
        var s = _store.Open();
        BaselineSet? baseline = null;
        if (!full && parent is not null)
        {
            var p = await MetadataStore.LoadSnapshotAsync(s, _keys, Convert.FromHexString(parent.Id), _ct);
            baseline = await BaselineSet.LoadResolvedAsync(p.Indexes, new IndexResolver(s, _keys, cacheObjects: false), _ct);
        }
        var r = await new BackupPipeline(_keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(new FileSystemSource(Src(job)), job, _host,
            baseline is null ? SnapshotMode.Full : SnapshotMode.Incremental, null, baseline is null ? null : Convert.FromHexString(parent!.Id),
            PackTargetSize: 32 * 1024, BaselineStream: baseline), _ct);
        Assert.Equal(BackupStatus.Succeeded, r.Status);
        return new Snap(job, Convert.ToHexStringLower(r.SnapId!), State(Src(job)), r.Stats);
    }

    private async Task AssertRestores(Snap s)
    {
        var session = _store.Open();
        var snap = await MetadataStore.LoadSnapshotAsync(session, _keys, Convert.FromHexString(s.Id), _ct);
        using var reader = new RepoReader(session, _keys);
        await reader.LoadIndexesAsync(snap, _ct);
        var target = Path.Combine(_root, "restore-" + s.Id);
        Directory.CreateDirectory(target);
        var r = await new RestoreExecutor(reader).RestoreAsync(snap, target, null, _ct);
        Assert.Equal(RestoreStatus.Succeeded, r.Status);
        Assert.Equal(s.Files, State(target));
        Directory.Delete(target, recursive: true);
    }

    private async Task AssertAllReadable(IEnumerable<Snap> live, bool allowOrphanWarning = false)
    {
        var check = await new RepoChecker(_store.Open(), _keys).CheckAsync(new CheckOptions(100), _ct);
        Assert.True(check.Status == CheckStatus.Succeeded || (allowOrphanWarning && check.Status == CheckStatus.Warning && check.Issues.All(i => i.Code == KbCode.CheckOrphanObject)),
            string.Join(", ", check.Issues.Select(i => i.Code + " " + i.Target)));
        foreach (var s in live)
        {
            Assert.Equal(CheckStatus.Succeeded, (await new RepoChecker(_store.Open(), _keys).CheckSnapshotAsync(Convert.FromHexString(s.Id), _ct)).Status);
            await AssertRestores(s);
        }
    }

    private int IndexFiles() => _store.Objects.Keys.Count(k => k.StartsWith(RepoPaths.IndexDir + "/", StringComparison.Ordinal));

    private async Task<IndexConsolidation> Consolidate(IReadOnlySet<string> forgotten, IndexConsolidationOptions options, IStorageSession? session = null)
    {
        session ??= _store.Open(CredentialRole.Maintenance);
        await using var lease = await RepoLocks.AcquireExclusiveAsync(session, _keys, RepoLockOwner.For(_host), TimeProvider.System, null, (_, _) => Task.CompletedTask, _ct);
        var r = await IndexConsolidator.RunAsync(session, _keys, lease, forgotten, _host, options, _ct);
        await lease.ReleaseAsync(_ct);
        return r;
    }

    /// <summary>Pełny prune dwufazowy z karencją „minęła” (oznaczenia sprzed 25 h) — usuwa zastąpione indeksy i obiekty zapomnianych snapshotów.</summary>
    private async Task<PruneDeletion> Prune(IReadOnlySet<string> forgotten)
    {
        var now = DateTimeOffset.UtcNow;
        var session = _store.Open(CredentialRole.Maintenance);
        var plan = PrunePlanner.Plan(await PrunePlanner.CollectAsync(session, _keys, forgotten, _ct), new Dictionary<string, DateTimeOffset>(), now, PrunePlanner.DefaultGrace);
        Assert.Null(plan.BlockedCode);
        var marks = plan.Dead.ToDictionary(o => o.Key, _ => now - TimeSpan.FromHours(25), StringComparer.Ordinal);
        await using var lease = await RepoLocks.AcquireExclusiveAsync(session, _keys, RepoLockOwner.For(_host), TimeProvider.System, null, (_, _) => Task.CompletedTask, _ct);
        var d = await PruneExecutor.DeleteAsync(session, _keys, lease, forgotten, marks, plan.Dead, TimeProvider.System, PrunePlanner.DefaultGrace, _ct);
        await lease.ReleaseAsync(_ct);
        Assert.Null(d.StopCode);
        return d;
    }

    private async Task<(List<Snap> History, Snap LastA, Snap? LastB)> History(Random rnd, int steps, int maxSize = 40_000)
    {
        var history = new List<Snap>();
        Snap? lastA = null, lastB = null;
        for (var step = 0; step < steps; step++)
        {
            var job = rnd.Next(3) == 0 ? "B" : "A"; // dwa zadania w jednym repozytorium (CE-18)
            Mutate(Src(job), rnd, step, maxSize);
            var last = job == "A" ? lastA : lastB;
            var s = await Backup(job, last, full: last is null || rnd.Next(5) == 0);
            history.Add(s);
            if (job == "A") lastA = s; else lastB = s;
        }
        return (history, lastA!, lastB);
    }

    [Theory, Trait("D", "D3.16"), Trait("PM", "PM-17"), Trait("AC", "AC-10.3"), Trait("Category", "Property")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Consolidation_keeps_live_packs_and_bit_exact_restores_and_prune_brings_index_count_under_threshold(int seed)
    {
        var rnd = new Random(seed * 104_729);
        var (history, lastA, lastB) = await History(rnd, 12);
        var newest = new[] { lastA, lastB }.Where(x => x is not null).Select(x => x!.Id).ToHashSet(StringComparer.Ordinal);
        var forgotten = history.Where(h => !newest.Contains(h.Id) && rnd.Next(4) == 0).Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var live = history.Where(h => !forgotten.Contains(h.Id)).ToList();
        var before = await PrunePlanner.CollectAsync(_store.Open(), _keys, forgotten, _ct);
        Assert.True(before.LiveIndexes.Count > Small.MaxActive);

        var c = await Consolidate(forgotten, Small);
        Assert.Null(c.Code);
        Assert.True(c.Needed);
        Assert.Single(c.Written);
        Assert.Equal(before.LiveIndexes.Count, c.Superseded);
        Assert.Equal(1, c.ActiveAfter);

        // Właściwość: zbiór żywych packów bez zmian (odzysk miejsca jak bez konsolidacji); żywy jest tylko nowy indeks, zastąpione są nieżywe (czekają na prune).
        var after = await PrunePlanner.CollectAsync(_store.Open(), _keys, forgotten, _ct);
        Assert.Equal(before.LivePacks.Order(StringComparer.Ordinal), after.LivePacks.Order(StringComparer.Ordinal));
        Assert.Equal(c.Written, after.LiveIndexes.Order(StringComparer.Ordinal));
        await AssertAllReadable(live); // przed usunięciem: oryginały i nowy indeks obok siebie — bez sierot, bez błędów

        await Prune(forgotten);
        Assert.True(IndexFiles() <= Small.MaxActive, $"{IndexFiles()} index files");
        Assert.Equal(1, IndexFiles());
        await AssertAllReadable(live);

        // Przyrost po konsolidacji: baza rozwiązana przez indeks skonsolidowany — bez zmian w źródle nie powstaje żaden nowy pack (dedup widzi bloki).
        var same = await Backup("A", lastA, full: false);
        Assert.Equal(0, same.Stats.PacksNew);
        Mutate(Src("A"), rnd, 99);
        var inc = await Backup("A", same, full: false);
        live.AddRange([same, inc]);
        await AssertAllReadable(live);

        // Drugi łańcuch konsolidacji (przechodniość relacji): nowe indeksy + poprzedni indeks skonsolidowany → kolejny; prune usuwa poprzedni.
        for (var i = 0; i < Small.MaxActive; i++)
        {
            Mutate(Src("A"), rnd, 100 + i);
            inc = await Backup("A", inc, full: false);
            live.Add(inc);
        }
        var c2 = await Consolidate(forgotten, Small);
        Assert.Null(c2.Code);
        Assert.Single(c2.Written);
        var before2 = after.LivePacks.Count;
        await Prune(forgotten);
        Assert.Equal(1, IndexFiles());
        Assert.True((await PrunePlanner.CollectAsync(_store.Open(), _keys, forgotten, _ct)).LivePacks.Count >= before2);
        await AssertAllReadable(live);
    }

    [Fact, Trait("D", "D3.16"), Trait("PM", "PM-17")]
    public async Task Default_thresholds_from_plan_are_respected()
    {
        var rnd = new Random(5);
        var (history, _, _) = await History(rnd, 20, maxSize: 4_000);
        var none = await Consolidate(new HashSet<string>(), new IndexConsolidationOptions(MaxSmall: 10_000));
        Assert.False(none.Needed); // ≤ 32 aktywnych i reguła małych wyłączona
        Assert.Equal(20, none.ActiveBefore);
        var c = await Consolidate(new HashSet<string>(), new IndexConsolidationOptions()); // 20 małych (< 1 MiB) > 16
        Assert.True(c.Needed);
        Assert.Null(c.Code);
        Assert.Equal(20, c.Superseded);
        Assert.False((await Consolidate(new HashSet<string>(), new IndexConsolidationOptions())).Needed); // jeden aktywny — nic do zrobienia
        await Prune(new HashSet<string>());
        Assert.Equal(1, IndexFiles());
        await AssertAllReadable(history);
    }

    [Fact, Trait("D", "D3.16"), Trait("RD", "D3.13")]
    public async Task Consolidation_requires_exclusive_lock()
    {
        var rnd = new Random(9);
        await History(rnd, 2);
        var s = _store.Open();
        await using var shared = await RepoLocks.AcquireSharedAsync(s, _keys, RepoLockOwner.For(_host), TimeProvider.System, null, _ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => IndexConsolidator.RunAsync(s, _keys, shared, new HashSet<string>(), _host, Small, _ct));
        // a backup trzymający współdzieloną blokadę wyklucza konsolidację (blokada wyłączna niedostępna)
        var busy = await Assert.ThrowsAsync<KobianException>(() => Consolidate(new HashSet<string>(), Small));
        Assert.Equal(KbCode.RepoLocked, busy.Code);
    }

    [Theory, Trait("D", "D3.16"), Trait("NFR", "NFR-26"), Trait("Category", "Property")]
    [InlineData(FaultKind.NoSpaceOnCreate, true)]
    [InlineData(FaultKind.DisconnectMidWrite, false)]
    [InlineData(FaultKind.ProcessKill, false)]
    [InlineData(FaultKind.SilentTruncate, true)]
    [InlineData(FaultKind.SilentTruncate, false)]
    public async Task Fault_while_writing_new_index_leaves_repo_complete_and_next_run_finishes(FaultKind fault, bool atomic)
    {
        var store = atomic ? _store : new MemoryStorage(atomicRename: false);
        var rnd = new Random(77);
        var history = await HistoryIn(store, rnd, 8);
        var files = Files(store);

        // pierwszy zapis w sesji konsolidacji to blokada (1), drugi — nowy indeks (2)
        var session = store.Open(CredentialRole.Maintenance, faultAt: 2, fault: fault);
        IndexConsolidation? r = null;
        try
        {
            await using var lease = await RepoLocks.AcquireExclusiveAsync(session, _keys, RepoLockOwner.For(_host), TimeProvider.System, null, (_, _) => Task.CompletedTask, _ct);
            r = await IndexConsolidator.RunAsync(session, _keys, lease, new HashSet<string>(), _host, Small, _ct);
        }
        catch (SimulatedProcessKillException)
        {
            // zabicie procesu: nic nie sprząta
        }
        if (r is not null) Assert.Equal(KbCode.IndexConsolidationIncomplete, r.Code);
        Assert.All(files, f => Assert.True(store.Objects.ContainsKey(f))); // żaden stary indeks nie zniknął
        await AssertAllReadableIn(store, history, allowOrphanWarning: true); // niepełny nowy indeks = sierota (KB-1504), nie błąd

        // kolejny przebieg: blokada przerwanego hosta wygasła/usunięta — konsolidacja + prune dokańczają
        foreach (var l in store.Objects.Keys.Where(k => k.StartsWith(RepoPaths.LocksDir + "/", StringComparison.Ordinal)).ToList()) store.Objects.TryRemove(l, out _);
        var again = await ConsolidateIn(store, Small);
        Assert.Null(again.Code);
        Assert.True(again.Needed);
        await PruneIn(store);
        Assert.Equal(1, store.Objects.Keys.Count(k => k.StartsWith(RepoPaths.IndexDir + "/", StringComparison.Ordinal)));
        await AssertAllReadableIn(store, history);
    }

    [Fact, Trait("D", "D3.16"), Trait("NFR", "NFR-26")]
    public async Task Crash_before_and_during_deletion_and_lost_new_index_keep_repo_complete()
    {
        var rnd = new Random(31);
        var (history, lastA, _) = await History(rnd, 10);
        var old = Files(_store).ToList();
        var c = await Consolidate(new HashSet<string>(), Small);
        Assert.Null(c.Code);
        var newIdx = RepoPaths.Index(Convert.FromHexString(c.Written[0]));

        // (1) awaria przed usunięciem: oryginały + nowy indeks — wszystko czytelne, check bez sierot
        await AssertAllReadable(history);

        // (2) awaria w trakcie usuwania: połowa zastąpionych usunięta
        foreach (var f in old.Where((_, i) => i % 2 == 0)) Assert.True(_store.Objects.TryRemove(f, out _));
        await AssertAllReadable(history);
        var mid = await Backup("A", lastA, full: false); // przyrost w stanie pośrednim
        Assert.Equal(0, mid.Stats.PacksNew);
        history.Add(mid);

        // (3) kolejny przebieg prune dokańcza
        await Prune(new HashSet<string>());
        Assert.Equal([newIdx], Files(_store));
        await AssertAllReadable(history);

        // (4) indeks skonsolidowany uszkodzony, gdy oryginały jeszcze są: czytelnicy wracają do oryginałów, prune NIE uznaje ich za zastąpione
        var rnd2 = new Random(32);
        var (h2, _, _) = await History(rnd2, 8);
        var originals = Files(_store).Where(f => f != newIdx).ToList();
        var c2 = await Consolidate(new HashSet<string>(), Small);
        Assert.Null(c2.Code);
        var c2Path = RepoPaths.Index(Convert.FromHexString(c2.Written[0]));
        var bytes = _store.Objects[c2Path];
        bytes[^1] ^= 0x5A;
        var plan = PrunePlanner.Plan(await PrunePlanner.CollectAsync(_store.Open(), _keys, new HashSet<string>(), _ct), new Dictionary<string, DateTimeOffset>(),
            DateTimeOffset.UtcNow, PrunePlanner.DefaultGrace);
        Assert.Null(plan.BlockedCode);
        Assert.Equal(["index:" + c2.Written[0]], plan.Dead.Select(o => o.Key)); // tylko uszkodzony nowy indeks jest nieżywy
        Assert.All(originals, f => Assert.True(_store.Objects.ContainsKey(f)));
        foreach (var s in history.Concat(h2)) await AssertRestores(s);
    }

    [Fact, Trait("D", "D3.16"), Trait("SEC", "SEC-3.3")]
    public async Task Consolidated_index_is_used_only_when_its_recorded_hash_matches_the_snapshot_reference()
    {
        var rnd = new Random(41);
        var (history, _, _) = await History(rnd, 8);
        var c = await Consolidate(new HashSet<string>(), Small);
        await Prune(new HashSet<string>());
        var s = _store.Open();
        var snap = await MetadataStore.LoadSnapshotAsync(s, _keys, Convert.FromHexString(history[^1].Id), _ct);
        var (id, sha) = snap.Indexes[0];
        var resolver = new IndexResolver(s, _keys);
        var ok = await resolver.ResolveAsync(id, sha, _ct);
        Assert.True(ok.ViaConsolidation);
        Assert.Equal(c.Written[0], ok.Hex);
        var wrong = (byte[])sha.Clone();
        wrong[0] ^= 1;
        var e = await Assert.ThrowsAsync<KobianException>(() => resolver.ResolveAsync(id, wrong, _ct));
        Assert.Equal(KbCode.ReferenceIntegrityFailed, e.Code);
    }

    [Fact, Trait("D", "D3.16")]
    public void Codec_round_trips_relation_keys_and_rejects_inconsistent_ones()
    {
        var pack = new IndexPack(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), 100, [new PackEntry(RandomNumberGenerator.GetBytes(32), 8, 50, BlobType.Data, 40)]);
        var sup = new[] { RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16) };
        var shas = new[] { RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32) };
        var obj = new IndexObject([pack], sup, RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16), shas, [[0], []]);
        var back = IndexCodec.Decode(IndexCodec.Encode(obj));
        Assert.Equal(shas.Select(Convert.ToHexString), back.SupersededSha256!.Select(Convert.ToHexString));
        Assert.Equal([0], back.SupersededPacks![0]);
        Assert.Empty(back.SupersededPacks[1]);
        // indeks bez relacji — kodowanie jak dotąd (klucze 32/33 nieobecne)
        var plain = IndexCodec.Decode(IndexCodec.Encode(obj with { Supersedes = [], SupersededSha256 = null, SupersededPacks = null }));
        Assert.Null(plain.SupersededSha256);
        Assert.Null(plain.SupersededPacks);
        Assert.Throws<ArgumentException>(() => IndexCodec.Encode(obj with { SupersededPacks = [[0]] }));
        Assert.Throws<ArgumentException>(() => IndexCodec.Encode(obj with { SupersededPacks = [[1], []] }));

        // ręcznie zbudowany CBOR z numerem packa spoza zakresu → KB-1304
        var w = new System.Formats.Cbor.CborWriter(System.Formats.Cbor.CborConformanceMode.Canonical);
        w.WriteStartMap(6);
        w.WriteUInt32(1); w.WriteStartArray(0); w.WriteEndArray();
        w.WriteUInt32(2); w.WriteStartArray(1); w.WriteByteString(sup[0]); w.WriteEndArray();
        w.WriteUInt32(3); w.WriteByteString(new byte[16]);
        w.WriteUInt32(4); w.WriteByteString(new byte[16]);
        w.WriteUInt32(32); w.WriteStartArray(1); w.WriteByteString(shas[0]); w.WriteEndArray();
        w.WriteUInt32(33); w.WriteStartArray(1); w.WriteStartArray(1); w.WriteUInt32(0); w.WriteEndArray(); w.WriteEndArray();
        w.WriteEndMap();
        Assert.Equal(KbCode.MetadataMalformed, Assert.Throws<KobianException>(() => IndexCodec.Decode(w.Encode())).Code);
    }

    /// <summary>
    /// Pomiar PM-17 (MemoryStorage, bez RTT — liczba odczytów obiektów jest miarą przenośną na FTP/SFTP): snapshot z odwołaniami do 400 indeksów
    /// (jak po roku codziennych przyrostów) — wczytanie indeksów przed konsolidacją i po niej + prune (mediana z 5 przebiegów).
    /// </summary>
    [Fact, Trait("PM", "PM-17"), Trait("NFR", "NFR-25"), Trait("Category", "Perf")]
    public async Task Measure_index_load_for_400_indexes_before_and_after_consolidation()
    {
        const int n = 400;
        var s = _store.Open();
        var refs = new List<(byte[], byte[])>();
        var rnd = new Random(400);
        for (var i = 0; i < n; i++)
        {
            var packs = new List<IndexPack>();
            for (var p = 0; p < 3; p++)
            {
                var entries = Enumerable.Range(0, 40).Select(e => new PackEntry(RandomNumberGenerator.GetBytes(32), 8 + (e * 100), 90, BlobType.Data, 80)).ToList();
                packs.Add(new IndexPack(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), 10_000, entries));
            }
            var id = RandomNumberGenerator.GetBytes(16);
            var sealedIdx = MetadataStore.Seal(_keys, MetadataKind.Index, id, IndexCodec.Encode(new IndexObject(packs, [], _host, RandomNumberGenerator.GetBytes(16))));
            refs.Add((id, await new VerifiedUploader(s).UploadAsync(RepoPaths.Index(id), sealedIdx, _ct)));
        }
        var snapId = RandomNumberGenerator.GetBytes(16);
        var snap = new SnapshotObject(snapId, _host, "A", RandomNumberGenerator.GetBytes(16), DateTimeOffset.UtcNow, SnapshotMode.Incremental, null, null,
            RandomNumberGenerator.GetBytes(32), new SnapshotStats(0, 0, 0, 0, 0), refs, null, [], null, []);
        await new VerifiedUploader(s).UploadAsync(RepoPaths.Snapshot(snapId), MetadataStore.Seal(_keys, MetadataKind.Snapshot, snapId, SnapshotCodec.Encode(snap)), _ct);

        async Task<(TimeSpan Time, int Reads, int Blocks)> Load()
        {
            var sw = Stopwatch.StartNew();
            using var reader = new RepoReader(_store.Open(), _keys);
            await reader.LoadIndexesAsync(snap, _ct);
            return (sw.Elapsed, reader.Resolver.IndexReads, reader.KnownBlocks);
        }

        async Task<(TimeSpan Time, int Reads, int Blocks)> Median()
        {
            var runs = new List<(TimeSpan Time, int Reads, int Blocks)>();
            for (var i = 0; i < 5; i++) runs.Add(await Load());
            return runs.OrderBy(r => r.Time).ElementAt(2);
        }

        await Load(); // rozgrzewka JIT
        var pre = await Median();
        var c = await Consolidate(new HashSet<string>(), new IndexConsolidationOptions());
        Assert.Equal(n, c.Superseded);
        Assert.Single(c.Written);
        await Prune(new HashSet<string>());
        Assert.Equal(1, IndexFiles());
        var post = await Median();
        Assert.Equal(pre.Blocks, post.Blocks);
        Assert.Equal(n * 3 * 40, post.Blocks);
        Assert.Equal(n, pre.Reads);
        Assert.True(post.Reads <= 3, $"reads after: {post.Reads}"); // nieudany odczyt oryginału + mapa (1 indeks) + indeks skonsolidowany
        TestContext.Current.SendDiagnosticMessage($"PM-17 measure: {n} indexes: {pre.Time.TotalMilliseconds:F0} ms / {pre.Reads} reads -> after consolidation: {post.Time.TotalMilliseconds:F0} ms / {post.Reads} reads");
        Console.WriteLine($"PM-17 measure: {n} indexes: {pre.Time.TotalMilliseconds:F0} ms / {pre.Reads} reads -> after consolidation: {post.Time.TotalMilliseconds:F0} ms / {post.Reads} reads");
    }

    // ---- warianty dla magazynu innego niż _store (tryb DirectFinal) ----

    private static List<string> Files(MemoryStorage store) =>
        [.. store.Objects.Keys.Where(k => k.StartsWith(RepoPaths.IndexDir + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private async Task<List<Snap>> HistoryIn(MemoryStorage store, Random rnd, int steps)
    {
        var history = new List<Snap>();
        Snap? last = null;
        for (var step = 0; step < steps; step++)
        {
            Mutate(Src("A"), rnd, step);
            var s = store.Open();
            BaselineSet? baseline = null;
            if (last is not null)
            {
                var p = await MetadataStore.LoadSnapshotAsync(s, _keys, Convert.FromHexString(last.Id), _ct);
                baseline = await BaselineSet.LoadResolvedAsync(p.Indexes, new IndexResolver(s, _keys, cacheObjects: false), _ct);
            }
            var r = await new BackupPipeline(_keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(new FileSystemSource(Src("A")), "A", _host,
                baseline is null ? SnapshotMode.Full : SnapshotMode.Incremental, null, last is null ? null : Convert.FromHexString(last.Id),
                PackTargetSize: 32 * 1024, BaselineStream: baseline), _ct);
            Assert.Equal(BackupStatus.Succeeded, r.Status);
            last = new Snap("A", Convert.ToHexStringLower(r.SnapId!), State(Src("A")), r.Stats);
            history.Add(last);
        }
        return history;
    }

    private async Task<IndexConsolidation> ConsolidateIn(MemoryStorage store, IndexConsolidationOptions options)
    {
        var session = store.Open(CredentialRole.Maintenance);
        await using var lease = await RepoLocks.AcquireExclusiveAsync(session, _keys, RepoLockOwner.For(_host), TimeProvider.System, null, (_, _) => Task.CompletedTask, _ct);
        var r = await IndexConsolidator.RunAsync(session, _keys, lease, new HashSet<string>(), _host, options, _ct);
        await lease.ReleaseAsync(_ct);
        return r;
    }

    private async Task PruneIn(MemoryStorage store)
    {
        var none = new HashSet<string>();
        var now = DateTimeOffset.UtcNow;
        var session = store.Open(CredentialRole.Maintenance);
        var plan = PrunePlanner.Plan(await PrunePlanner.CollectAsync(session, _keys, none, _ct), new Dictionary<string, DateTimeOffset>(), now, PrunePlanner.DefaultGrace);
        Assert.Null(plan.BlockedCode);
        var marks = plan.Dead.ToDictionary(o => o.Key, _ => now - TimeSpan.FromHours(25), StringComparer.Ordinal);
        await using var lease = await RepoLocks.AcquireExclusiveAsync(session, _keys, RepoLockOwner.For(_host), TimeProvider.System, null, (_, _) => Task.CompletedTask, _ct);
        var d = await PruneExecutor.DeleteAsync(session, _keys, lease, none, marks, plan.Dead, TimeProvider.System, PrunePlanner.DefaultGrace, _ct);
        await lease.ReleaseAsync(_ct);
        Assert.Null(d.StopCode);
    }

    private async Task AssertAllReadableIn(MemoryStorage store, IEnumerable<Snap> live, bool allowOrphanWarning = false)
    {
        var check = await new RepoChecker(store.Open(), _keys).CheckAsync(new CheckOptions(100), _ct);
        Assert.True(check.Status == CheckStatus.Succeeded || (allowOrphanWarning && check.Status == CheckStatus.Warning && check.Issues.All(i => i.Code == KbCode.CheckOrphanObject)),
            string.Join(", ", check.Issues.Select(i => i.Code + " " + i.Target)));
        foreach (var s in live)
        {
            var session = store.Open();
            var snap = await MetadataStore.LoadSnapshotAsync(session, _keys, Convert.FromHexString(s.Id), _ct);
            using var reader = new RepoReader(session, _keys);
            await reader.LoadIndexesAsync(snap, _ct);
            var target = Path.Combine(_root, "restore-" + s.Id);
            Directory.CreateDirectory(target);
            Assert.Equal(RestoreStatus.Succeeded, (await new RestoreExecutor(reader).RestoreAsync(snap, target, null, _ct)).Status);
            Assert.Equal(s.Files, State(target));
            Directory.Delete(target, recursive: true);
        }
    }
}
