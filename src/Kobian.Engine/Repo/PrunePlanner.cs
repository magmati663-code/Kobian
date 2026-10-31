using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Format.Repo;

namespace Kobian.Engine.Repo;

/// <summary>
/// Spis obiektów repozytorium na celu (tylko odczyt) i — dla każdego snapshotu, który ZOSTAJE — zbiór indeksów i packów, do których się odwołuje.
/// Identyfikatory to hex małymi literami. <see cref="Unreadable"/> ≠ null = obiekt zachowywanego snapshotu nie dał się odczytać (prune wstrzymany, KB-1601).
/// </summary>
public sealed record PruneInventory(
    IReadOnlyDictionary<string, long> Packs,
    IReadOnlyDictionary<string, long> Indexes,
    IReadOnlySet<string> Snapshots,
    IReadOnlySet<string> LiveSnapshots,
    IReadOnlySet<string> LiveIndexes,
    IReadOnlySet<string> LivePacks,
    string? Unreadable,
    string? UnreadableCode);

/// <summary>Obiekt na liście „do usunięcia”: <c>Kind</c> = snapshot | index | pack, <c>Id</c> = hex, <c>Size</c> = bajty na celu.</summary>
public sealed record PruneObject(string Kind, string Id, long Size)
{
    /// <summary>Klucz obiektu „rodzaj:hex” — taki jak <see cref="CheckIssue.Target"/> (bez nazw plików, NFR-11).</summary>
    public string Key => Kind + ":" + Id;

    public string Path => Kind switch
    {
        "pack" => RepoPaths.Pack(Convert.FromHexString(Id)),
        "index" => RepoPaths.Index(Convert.FromHexString(Id)),
        _ => RepoPaths.Snapshot(Convert.FromHexString(Id)),
    };
}

/// <summary>
/// Plan prune dwufazowego (BL-25 cz. 3, D3.7, D3.12 „Prune (MVP)”): <see cref="Dead"/> = wszystkie nieżywe obiekty teraz; <see cref="NewMarks"/> = nieżywe,
/// jeszcze nieoznaczone (faza 1 zapisze je z <c>markAt</c>); <see cref="Deletable"/> = oznaczone co najmniej <c>grace</c> temu i NADAL nieżywe (faza 2);
/// <see cref="Unmark"/> = oznaczone, ale znów żywe albo już nieobecne na celu (oznaczenie do zdjęcia). <see cref="BlockedCode"/> ≠ null ⇒ nic nie jest usuwane.
/// </summary>
public sealed record PrunePlan(
    string? BlockedCode,
    string? BlockedObject,
    int LiveSnapshots,
    int LivePacks,
    long LiveBytes,
    IReadOnlyList<PruneObject> Dead,
    IReadOnlyList<PruneObject> NewMarks,
    IReadOnlyList<PruneObject> Deletable,
    IReadOnlyList<string> Unmark)
{
    public long DeadBytes => Dead.Sum(o => o.Size);

    public long DeletableBytes => Deletable.Sum(o => o.Size);
}

/// <summary>
/// Prune jako czysta funkcja nad spisem repozytorium (PM-18: „prune usuwa pack nadal żywy” — ryzyko krytyczne). Reguły:
/// <list type="number">
/// <item>Snapshot ŻYWY = obecny na celu i nie zapomniany w katalogu. Snapshot nieznany katalogowi jest żywy (AC-10.3/CE-18: repozytorium współdzielone przez zadania).</item>
/// <item>PROWIZORYCZNIE Qb26 (ADR-0015): pack/indeks żywy = wymieniony w KTÓRYMKOLWIEK indeksie z klucza 11 żywego snapshotu (osiągalność przez indeksy, nie przez drzewa).
/// Dzięki temu deduplikacja kolejnej kopii (baza = indeksy rodzica) nigdy nie wskaże usuniętego packa, a pełne <c>check</c> po prune jest zielone bez przepisywania
/// indeksów. Koszt: w łańcuchu przyrostów bez kopii pełnej odzysk miejsca jest zerowy, dopóki nie powstanie nowy łańcuch (kompaktowanie indeksów D3.16 — później).</item>
/// <item>D3.16: odwołanie z klucza 11 rozwiązywane przez <see cref="IndexResolver"/> — gdy obecny, czytelny indeks skonsolidowany zastępuje indeks z odwołania
/// (zgodny SHA-256 w kluczu 32), żywy jest indeks skonsolidowany, a packi żywe = WIDOK zastąpionego indeksu (klucz 33, ten sam zbiór co przed konsolidacją);
/// oryginał staje się nieżywy (usuwany po karencji).</item>
/// <item>Nieżywe: snapshoty zapomniane, indeksy i packi poza zbiorem żywym (w tym sieroty po przerwanych zadaniach — D3.14).</item>
/// <item>Bezpieczniki: nieczytelny obiekt żywego snapshotu → KB-1601 (nie wiadomo, co jest żywe); brak żywego snapshotu przy niepustym repo → KB-1602 (zły katalog/repo).</item>
/// <item>Dwufazowość: usunięcie tylko po karencji od oznaczenia (domyślnie 24 h) i tylko gdy obiekt jest nieżywy w BIEŻĄCYM spisie.</item>
/// </list>
/// </summary>
public static class PrunePlanner
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromHours(24);

    public static PrunePlan Plan(PruneInventory inv, IReadOnlyDictionary<string, DateTimeOffset> marks, DateTimeOffset now, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(inv);
        ArgumentNullException.ThrowIfNull(marks);
        if (grace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(grace), "Grace must not be negative.");
        var liveBytes = inv.LivePacks.Sum(p => inv.Packs.TryGetValue(p, out var l) ? l : 0);
        if (inv.UnreadableCode is not null)
            return Blocked(KbCode.PruneBlockedUnreadable, inv.Unreadable, inv, liveBytes);
        if (inv.LiveSnapshots.Count == 0 && (inv.Snapshots.Count > 0 || inv.Packs.Count > 0 || inv.Indexes.Count > 0))
            return Blocked(KbCode.PruneWouldEmptyRepository, null, inv, liveBytes);

        var dead = new List<PruneObject>();
        foreach (var s in inv.Snapshots.Order(StringComparer.Ordinal))
            if (!inv.LiveSnapshots.Contains(s)) dead.Add(new PruneObject("snapshot", s, 0));
        foreach (var (id, size) in inv.Indexes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (!inv.LiveIndexes.Contains(id)) dead.Add(new PruneObject("index", id, size));
        foreach (var (id, size) in inv.Packs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (!inv.LivePacks.Contains(id)) dead.Add(new PruneObject("pack", id, size));

        var deadKeys = dead.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var newMarks = dead.Where(o => !marks.ContainsKey(o.Key)).ToList();
        // Zegar cofnięty (markAt w przyszłości) nie skraca karencji: warunek now − markAt ≥ grace jest wtedy fałszywy (SEC-17.4).
        var deletable = dead.Where(o => marks.TryGetValue(o.Key, out var at) && now - at >= grace).ToList();
        var unmark = marks.Keys.Where(k => !deadKeys.Contains(k)).Order(StringComparer.Ordinal).ToList();
        return new PrunePlan(null, null, inv.LiveSnapshots.Count, inv.LivePacks.Count, liveBytes, dead, newMarks, deletable, unmark);
    }

    private static PrunePlan Blocked(string code, string? obj, PruneInventory inv, long liveBytes) =>
        new(code, obj, inv.LiveSnapshots.Count, inv.LivePacks.Count, liveBytes, [], [], [], []);

    /// <summary>
    /// Spis repozytorium (tylko odczyt, sesja Backup wystarcza): lista <c>snapshots/</c>, <c>index/</c>, <c>data/</c>; każdy żywy snapshot jest odczytany i uwierzytelniony,
    /// a jego indeksy — odczytane i porównane z SHA-256 z klucza 11 (jak w restore). Snapshoty z <paramref name="forgotten"/> nie są czytane.
    /// Pierwszy błąd odczytu obiektu żywego snapshotu kończy spis z <see cref="PruneInventory.UnreadableCode"/> (planer wstrzyma prune).
    /// </summary>
    public static async Task<PruneInventory> CollectAsync(IStorageSession session, RepoKeys keys, IReadOnlySet<string> forgotten, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(forgotten);
        var snaps = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var o in session.ListAsync(RepoPaths.SnapshotsDir, ct).ConfigureAwait(false))
            if (RepoPaths.TryParseId(o.Path, RepoPaths.SnapshotsDir, ".snap") is { } id) snaps.Add(Hex(id));
        var indexes = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var o in session.ListAsync(RepoPaths.IndexDir, ct).ConfigureAwait(false))
            if (RepoPaths.TryParseId(o.Path, RepoPaths.IndexDir, ".idx") is { } id) indexes[Hex(id)] = o.Length;
        var packs = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var o in session.ListAsync(RepoPaths.DataDir, ct).ConfigureAwait(false))
            if (RepoChecker.TryParsePackPath(o.Path) is { } hex) packs[hex] = o.Length;

        var live = snaps.Where(s => !forgotten.Contains(s)).Order(StringComparer.Ordinal).ToList();
        var liveIdx = new HashSet<string>(StringComparer.Ordinal);
        var livePacks = new HashSet<string>(StringComparer.Ordinal);
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        // D3.16: relacja „zastąpiony przez” z obecnych indeksów skonsolidowanych. Odwołanie rozwiązywane przez indeks skonsolidowany czyni ŻYWYM ten indeks,
        // a oryginał (zastąpiony, z tym samym SHA-256) — nieżywym: faza 1 go oznacza, faza 2 usuwa po karencji (D3.12), tylko jeśli nadal jest zastąpiony.
        var resolver = new IndexResolver(session, keys, cacheObjects: false);
        await resolver.EnsureMapAsync(ct).ConfigureAwait(false);
        foreach (var s in live)
        {
            ct.ThrowIfCancellationRequested();
            var obj = "snapshot:" + s;
            try
            {
                var snap = await MetadataStore.LoadSnapshotAsync(session, keys, Convert.FromHexString(s), ct).ConfigureAwait(false);
                var pending = new List<(byte[] IndexId, byte[] Sha256)>();
                foreach (var (indexId, expectedSha) in snap.Indexes)
                {
                    var rh = Hex(indexId);
                    liveIdx.Add(resolver.Candidate(rh, expectedSha) ?? rh);
                    if (loaded.Add(rh)) pending.Add((indexId, expectedSha));
                }
                await foreach (var (pos, r, err) in resolver.ResolveAllAsync(pending, ct).ConfigureAwait(false))
                {
                    obj = "index:" + Hex(pending[pos].IndexId);
                    err?.Throw();
                    foreach (var pk in r!.Index.Packs) livePacks.Add(Hex(pk.PackId));
                }
            }
            catch (Exception ex) when (ex is KobianException or StorageException)
            {
                var code = ex is KobianException k ? k.Code : ((StorageException)ex).Code;
                return new PruneInventory(packs, indexes, snaps, live.ToHashSet(StringComparer.Ordinal), liveIdx, livePacks, obj, code);
            }
        }
        return new PruneInventory(packs, indexes, snaps, live.ToHashSet(StringComparer.Ordinal), liveIdx, livePacks, null, null);
    }

    private static string Hex(byte[] id) => Convert.ToHexStringLower(id);
}
