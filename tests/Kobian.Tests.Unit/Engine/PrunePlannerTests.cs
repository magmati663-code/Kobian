using System.Security.Cryptography;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Engine.Restore;
using Kobian.Format.Metadata;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>
/// BL-25 cz. 3 (D3.7, D3.12 „Prune (MVP)”, PM-18, CE-18): plan prune dwufazowego na repozytorium z prawdziwego potoku (MemoryStorage, dane syntetyczne).
/// Właściwość: po usunięciu wszystkiego, co plan uznał za usuwalne, KAŻDY żywy snapshot przechodzi V2/V3 i odtwarza się bit w bit, a kolejna kopia przyrostowa
/// (deduplikacja z indeksów rodzica) nie wskazuje usuniętego packa.
/// </summary>
public sealed class PrunePlannerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kobian-prune-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly RepoKeys _keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
    private readonly MemoryStorage _store = new();
    private readonly byte[] _host = RandomNumberGenerator.GetBytes(16);

    public void Dispose()
    {
        _keys.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record Snap(string Job, string Id, Dictionary<string, string> Files);

    private string Src(string job) => Path.Combine(_root, "src-" + job);

    private static void Mutate(string dir, Random rnd, int step)
    {
        Directory.CreateDirectory(dir);
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        if (files.Count > 2 && rnd.Next(3) == 0) File.Delete(files[rnd.Next(files.Count)]);
        if (files.Count > 0 && rnd.Next(2) == 0) File.WriteAllBytes(files[rnd.Next(files.Count)], Bytes(rnd, rnd.Next(1_000, 120_000)));
        var sub = Path.Combine(dir, "k" + rnd.Next(3));
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, $"n{step}.bin"), Bytes(rnd, rnd.Next(1_000, 150_000)));
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

    private async Task<Snap> Backup(string job, Snap? parent, bool full)
    {
        var s = _store.Open();
        var pipeline = new BackupPipeline(_keys, new VerifiedUploader(s));
        List<BaselineIndex>? baseline = null;
        if (!full && parent is not null)
        {
            baseline = [];
            var p = await MetadataStore.LoadSnapshotAsync(s, _keys, Convert.FromHexString(parent.Id), _ct);
            foreach (var (id, _) in p.Indexes)
            {
                var (idx, sha) = await MetadataStore.LoadIndexAsync(s, _keys, id, _ct);
                baseline.Add(new BaselineIndex(id, sha, idx));
            }
        }
        var r = await pipeline.RunAsync(new BackupRequest(new FileSystemSource(Src(job)), job, _host, baseline is null ? SnapshotMode.Full : SnapshotMode.Incremental,
            baseline, baseline is null ? null : Convert.FromHexString(parent!.Id), PackTargetSize: 96 * 1024), _ct);
        Assert.Equal(BackupStatus.Succeeded, r.Status);
        return new Snap(job, Convert.ToHexStringLower(r.SnapId!), State(Src(job)));
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

    private static Dictionary<string, DateTimeOffset> MarkedLongAgo(PrunePlan plan) => plan.Dead.ToDictionary(o => o.Key, _ => Now - TimeSpan.FromHours(25), StringComparer.Ordinal);

    private Task<PruneInventory> Collect(IEnumerable<Snap> forgotten) =>
        PrunePlanner.CollectAsync(_store.Open(), _keys, forgotten.Select(f => f.Id).ToHashSet(StringComparer.Ordinal), _ct);

    [Theory, Trait("AC", "AC-10.3"), Trait("PM", "PM-18"), Trait("Category", "Property")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task After_prune_every_live_snapshot_passes_check_and_restores_and_next_backup_is_consistent(int seed)
    {
        var rnd = new Random(seed * 7919);
        var history = new List<Snap>();
        Snap? lastA = null, lastB = null;
        for (var step = 0; step < 9; step++)
        {
            var job = rnd.Next(2) == 0 ? "A" : "B"; // dwa zadania w jednym repozytorium (CE-18)
            Mutate(Src(job), rnd, step);
            var last = job == "A" ? lastA : lastB;
            var s = await Backup(job, last, full: last is null || rnd.Next(3) == 0);
            history.Add(s);
            if (job == "A") lastA = s; else lastB = s;
        }
        // zapomniane: losowy podzbiór bez najnowszego snapshotu każdego zadania (D3.12: newest zawsze zostaje)
        var newest = new[] { lastA, lastB }.Where(x => x is not null).Select(x => x!.Id).ToHashSet(StringComparer.Ordinal);
        var forgotten = history.Where(h => !newest.Contains(h.Id) && rnd.Next(3) != 0).ToList();
        var live = history.Except(forgotten).ToList();

        var plan1 = PrunePlanner.Plan(await Collect(forgotten), new Dictionary<string, DateTimeOffset>(), Now, PrunePlanner.DefaultGrace);
        Assert.Null(plan1.BlockedCode);
        Assert.Empty(plan1.Deletable); // faza 1: nic nie jest usuwalne bez wcześniejszego oznaczenia
        Assert.Equal(plan1.Dead, plan1.NewMarks);
        Assert.Equal(forgotten.Select(f => f.Id).Order(StringComparer.Ordinal), plan1.Dead.Where(o => o.Kind == "snapshot").Select(o => o.Id));

        var plan2 = PrunePlanner.Plan(await Collect(forgotten), MarkedLongAgo(plan1), Now, PrunePlanner.DefaultGrace);
        Assert.Equal(plan1.Dead, plan2.Deletable);
        foreach (var o in plan2.Deletable) Assert.True(_store.Objects.TryRemove(o.Path, out _), o.Key);

        var check = await new RepoChecker(_store.Open(), _keys).CheckAsync(new CheckOptions(100), _ct);
        Assert.Equal(CheckStatus.Succeeded, check.Status); // bez KB-1501 i bez sierot KB-1504
        Assert.Equal(live.Count, check.Snapshots);
        foreach (var s in live)
        {
            Assert.Equal(CheckStatus.Succeeded, (await new RepoChecker(_store.Open(), _keys).CheckSnapshotAsync(Convert.FromHexString(s.Id), _ct)).Status);
            await AssertRestores(s);
        }

        // PM-18: przyrost po prune deduplikuje z indeksów rodzica — każdy blok musi nadal istnieć
        Mutate(Src("A"), rnd, 99);
        var after = await Backup("A", lastA, full: false);
        Assert.Equal(CheckStatus.Succeeded, (await new RepoChecker(_store.Open(), _keys).CheckSnapshotAsync(Convert.FromHexString(after.Id), _ct)).Status);
        await AssertRestores(after);

        // drugi przebieg prune na tym samym katalogu: nic nowego do usunięcia
        var plan3 = PrunePlanner.Plan(await Collect(forgotten), new Dictionary<string, DateTimeOffset>(), Now, PrunePlanner.DefaultGrace);
        Assert.Empty(plan3.Dead);
    }

    [Fact, Trait("AC", "AC-10.3"), Trait("PM", "PM-18")]
    public async Task Full_backup_chain_reset_makes_forgotten_chain_reclaimable_and_unknown_snapshots_stay_live()
    {
        var rnd = new Random(42);
        Mutate(Src("A"), rnd, 0);
        var a1 = await Backup("A", null, full: true);
        Mutate(Src("A"), rnd, 1);
        var a2 = await Backup("A", a1, full: false);
        Mutate(Src("A"), rnd, 2);
        var a3 = await Backup("A", a2, full: true); // nowy łańcuch
        Mutate(Src("B"), rnd, 3);
        var b1 = await Backup("B", null, full: true); // zadanie B: katalog nic o nim „nie wie” (żaden snapshot B nie jest zapomniany)

        var plan = PrunePlanner.Plan(await Collect([a1, a2]), new Dictionary<string, DateTimeOffset>(), Now, PrunePlanner.DefaultGrace);
        Assert.Null(plan.BlockedCode);
        Assert.Equal(2, plan.LiveSnapshots);
        Assert.Equal(new[] { a1.Id, a2.Id }.Order(StringComparer.Ordinal), plan.Dead.Where(o => o.Kind == "snapshot").Select(o => o.Id));
        Assert.Contains(plan.Dead, o => o.Kind == "pack");
        Assert.Contains(plan.Dead, o => o.Kind == "index");
        Assert.True(plan.DeadBytes > 0);

        // zapomniany sam początek łańcucha przyrostów: a2 (żywy) wymienia indeks a1 w kluczu 11, więc indeksy i packi a1 zostają (PROWIZORYCZNIE Qb26)
        var inc = PrunePlanner.Plan(await Collect([a1]), new Dictionary<string, DateTimeOffset>(), Now, PrunePlanner.DefaultGrace);
        Assert.Equal(["snapshot:" + a1.Id], inc.Dead.Select(o => o.Key));
        Assert.Equal(3, inc.LiveSnapshots);
        Assert.NotNull(b1);
    }

    [Fact, Trait("PM", "PM-18")]
    public async Task Grace_period_unmark_and_backwards_clock_are_respected()
    {
        var rnd = new Random(7);
        Mutate(Src("A"), rnd, 0);
        var a1 = await Backup("A", null, full: true);
        Mutate(Src("A"), rnd, 1);
        var a2 = await Backup("A", null, full: true);
        var inv = await Collect([a1]);
        var dead = PrunePlanner.Plan(inv, new Dictionary<string, DateTimeOffset>(), Now, PrunePlanner.DefaultGrace).Dead;
        Assert.NotEmpty(dead);
        var key = dead[0].Key;

        Assert.Empty(PrunePlanner.Plan(inv, new Dictionary<string, DateTimeOffset> { [key] = Now - TimeSpan.FromHours(23) }, Now, PrunePlanner.DefaultGrace).Deletable);
        Assert.Single(PrunePlanner.Plan(inv, new Dictionary<string, DateTimeOffset> { [key] = Now - TimeSpan.FromHours(24) }, Now, PrunePlanner.DefaultGrace).Deletable);
        // oznaczenie „z przyszłości” (zegar cofnięty) nie skraca karencji
        Assert.Empty(PrunePlanner.Plan(inv, new Dictionary<string, DateTimeOffset> { [key] = Now + TimeSpan.FromDays(3) }, Now, PrunePlanner.DefaultGrace).Deletable);

        // obiekt oznaczony, ale znów żywy (snapshot „odzapomniany”) albo nieobecny → zdjęcie oznaczenia, nie usunięcie
        var liveKey = "snapshot:" + a2.Id;
        var p = PrunePlanner.Plan(inv, new Dictionary<string, DateTimeOffset> { [liveKey] = Now - TimeSpan.FromDays(9), ["pack:" + new string('0', 32)] = Now - TimeSpan.FromDays(9) }, Now, PrunePlanner.DefaultGrace);
        Assert.DoesNotContain(p.Deletable, o => o.Key == liveKey);
        Assert.Equal(new[] { "pack:" + new string('0', 32), liveKey }, p.Unmark);
    }

    [Fact, Trait("PM", "PM-18")]
    public async Task Unreadable_live_index_blocks_prune_with_KB_1601_and_collect_is_read_only()
    {
        var rnd = new Random(8);
        Mutate(Src("A"), rnd, 0);
        var a1 = await Backup("A", null, full: true);
        Mutate(Src("A"), rnd, 1);
        var a2 = await Backup("A", a1, full: false);
        var before = _store.Objects.ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value)));
        var ok = PrunePlanner.Plan(await Collect([]), new Dictionary<string, DateTimeOffset>(), Now, PrunePlanner.DefaultGrace);
        Assert.Null(ok.BlockedCode);
        Assert.Empty(ok.Dead);
        Assert.Equal(before, _store.Objects.ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value))));

        // uszkodzony indeks żywego snapshotu: nie wiadomo, które packi są żywe → nic nie jest usuwane
        var idx = _store.Objects.Keys.First(k => k.StartsWith("index/", StringComparison.Ordinal));
        _store.Objects[idx] = _store.Objects[idx][..^3];
        var marks = new Dictionary<string, DateTimeOffset> { ["snapshot:" + a1.Id] = Now - TimeSpan.FromDays(5) };
        var blocked = PrunePlanner.Plan(await Collect([a1]), marks, Now, PrunePlanner.DefaultGrace);
        Assert.Equal(KbCode.PruneBlockedUnreadable, blocked.BlockedCode);
        Assert.StartsWith("index:", blocked.BlockedObject, StringComparison.Ordinal);
        Assert.Empty(blocked.Dead);
        Assert.Empty(blocked.Deletable);
        Assert.Empty(blocked.Unmark);
        _ = a2;
    }

    [Fact, Trait("PM", "PM-18")]
    public async Task Forgetting_every_snapshot_blocks_prune_with_KB_1602()
    {
        var rnd = new Random(9);
        Mutate(Src("A"), rnd, 0);
        var a1 = await Backup("A", null, full: true);
        var p = PrunePlanner.Plan(await Collect([a1]), new Dictionary<string, DateTimeOffset> { ["snapshot:" + a1.Id] = Now - TimeSpan.FromDays(5) }, Now, PrunePlanner.DefaultGrace);
        Assert.Equal(KbCode.PruneWouldEmptyRepository, p.BlockedCode);
        Assert.Empty(p.Deletable);

        // puste repozytorium (świeży init) — nie jest błędem
        var empty = PrunePlanner.Plan(await PrunePlanner.CollectAsync(new MemoryStorage().Open(), _keys, new HashSet<string>(), _ct), new Dictionary<string, DateTimeOffset>(), Now, PrunePlanner.DefaultGrace);
        Assert.Null(empty.BlockedCode);
        Assert.Empty(empty.Dead);
    }
}
