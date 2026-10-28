using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Format.Metadata;
using Kobian.Format.Repo;

namespace Kobian.Engine.Repo;

/// <summary>
/// Wynik rozwiązania odwołania <c>(indexId, sha256)</c> ze snapshotu: <see cref="IndexId"/>/<see cref="Sha256"/> = obiekt faktycznie wczytany (oryginał albo indeks
/// skonsolidowany — D3.16), <see cref="Index"/> = packi odwołania (dla indeksu skonsolidowanego — WIDOK: tylko packi, które miał zastąpiony indeks, klucz 33).
/// </summary>
public sealed record ResolvedIndex(byte[] IndexId, IndexObject Index, byte[] Sha256, bool ViaConsolidation)
{
    public string Hex => Convert.ToHexStringLower(IndexId);
}

/// <summary>Wpis relacji „zastąpiony przez”: SHA-256 pliku zastąpionego indeksu (klucz 32) i numery jego packów w indeksie skonsolidowanym (klucz 33).</summary>
public sealed record SupersededEntry(byte[] Sha256, IReadOnlyList<int> PackOrdinals);

/// <summary>Indeks skonsolidowany obecny na celu: id (hex), skrót pliku, rozmiar i zastąpione indeksy (hex → wpis).</summary>
public sealed record ConsolidatedIndexInfo(string Hex, byte[] Sha256, long Size, IReadOnlyDictionary<string, SupersededEntry> Supersedes);

/// <summary>
/// Rozwiązywanie odwołań do indeksów (D3.16 „czytelnik rozwiązuje indexId przez zamknięcie relacji «zastąpiony przez»”; BL-25 dokończenie).
/// Snapshot (klucz 11) jest niezmienny i wskazuje indeksy sprzed konsolidacji; po konsolidacji (i usunięciu starych plików przez prune po karencji) odwołanie
/// rozwiązuje uwierzytelniony indeks skonsolidowany, który w kluczach 2/32/33 zapisał <c>indexId</c>, SHA-256 RÓWNY skrótowi z odwołania i listę packów.
/// Konsolidator przenosi relację przechodnio (nowy indeks zastępuje też to, co zastępowały jego wejścia), więc wystarcza jeden krok.
/// Kolejność: gdy mapa relacji jest już znana i ma kandydata — indeks skonsolidowany (jeden odczyt zamiast wielu, PM-17); inaczej oryginał;
/// brak/uszkodzenie oryginału → zbudowanie mapy (lista <c>index/</c>, każdy indeks czytany raz) i kandydat z mapy; brak kandydata → pierwotny błąd.
/// Kandydat: preferowany niezastąpiony przez inny obecny indeks skonsolidowany, potem najmniejszy id (deterministycznie — prune i konsolidacja wybierają tak samo).
/// Bezpieczeństwo: indeks skonsolidowany jest otwierany kluczem K_obj (AEAD) — bez klucza repozytorium nie da się podstawić relacji; odwołanie bez zgodnego skrótu
/// nie jest rozwiązywane (SEC-3.3, integralność łańcucha snapshot → indeks).
/// </summary>
/// <param name="cacheObjects">true = obiekty indeksów skonsolidowanych trzymane w pamięci po zbudowaniu mapy; false (baza przyrostu BL-83, prune) = tylko skróty,
/// obiekt czytany ponownie przy rozwiązaniu (<see cref="ResolveAllAsync"/> czyta każdy indeks skonsolidowany raz na wywołanie).</param>
public sealed class IndexResolver(IStorageSession session, RepoKeys keys, bool cacheObjects = true)
{
    private readonly IStorageSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly RepoKeys _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly Dictionary<string, IndexObject> _objects = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>>? _supersededBy;
    private Dictionary<string, ConsolidatedIndexInfo>? _infos;

    /// <summary>Liczba odczytów obiektów indeksu wykonanych przez resolver (pomiar PM-17).</summary>
    public int IndexReads { get; private set; }

    /// <summary>Czy mapa relacji została już zbudowana.</summary>
    public bool MapLoaded => _supersededBy is not null;

    /// <summary>Indeksy skonsolidowane obecne i czytelne na celu (po <see cref="EnsureMapAsync"/>).</summary>
    public IReadOnlyDictionary<string, ConsolidatedIndexInfo> Consolidated =>
        _infos ?? throw new InvalidOperationException("Call EnsureMapAsync first.");

    /// <summary>Jedno odwołanie (indeks skonsolidowany czytany ponownie, gdy <c>cacheObjects</c> = false — dla wielu odwołań użyj <see cref="ResolveAllAsync"/>).</summary>
    public async Task<ResolvedIndex> ResolveAsync(byte[] indexId, byte[] expectedSha, CancellationToken ct)
    {
        await foreach (var r in ResolveAllAsync([(indexId, expectedSha)], ct).ConfigureAwait(false))
        {
            r.Error?.Throw();
            return r.Index!;
        }
        throw new InvalidOperationException("No result."); // nieosiągalne
    }

    /// <summary>
    /// Rozwiązuje listę odwołań; każdy indeks skonsolidowany jest czytany najwyżej raz na wywołanie. Wynik dla każdego odwołania (kolejność: najpierw rozwiązane
    /// bezpośrednio, potem przez indeksy skonsolidowane): <c>RefPosition</c> = pozycja na liście wejściowej, <c>Index</c> albo <c>Error</c>.
    /// </summary>
    public async IAsyncEnumerable<(int RefPosition, ResolvedIndex? Index, ExceptionDispatchInfo? Error)> ResolveAllAsync(
        IReadOnlyList<(byte[] IndexId, byte[] Sha256)> refs, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refs);
        var groups = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < refs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (id, expected) = refs[i];
            var hex = Convert.ToHexStringLower(id);
            if (Candidate(hex, expected) is { } early)
            {
                Add(groups, early, i);
                continue;
            }
            ExceptionDispatchInfo first;
            ResolvedIndex? direct = null;
            try
            {
                IndexReads++;
                var (idx, sha) = await MetadataStore.LoadIndexAsync(_session, _keys, id, ct).ConfigureAwait(false);
                direct = CryptographicOperations.FixedTimeEquals(sha, expected) ? new ResolvedIndex(id, idx, sha, false) : null;
                first = ExceptionDispatchInfo.Capture(new KobianException(KbCode.ReferenceIntegrityFailed, ErrorClass.Integrity, "Index hash differs from snapshot reference."));
            }
            catch (Exception ex) when (ex is KobianException or StorageException)
            {
                first = ExceptionDispatchInfo.Capture(ex);
            }
            if (direct is not null)
            {
                yield return (i, direct, null);
                continue;
            }
            if (!MapLoaded)
            {
                await EnsureMapAsync(ct).ConfigureAwait(false);
                if (Candidate(hex, expected) is { } late)
                {
                    Add(groups, late, i);
                    continue;
                }
            }
            yield return (i, null, first);
        }
        foreach (var (by, positions) in groups)
        {
            IndexObject? obj = null;
            ExceptionDispatchInfo? err = null;
            try
            {
                obj = await LoadConsolidatedAsync(by, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KobianException or StorageException)
            {
                err = ExceptionDispatchInfo.Capture(ex);
            }
            var info = _infos![by];
            foreach (var p in positions)
            {
                if (obj is null)
                {
                    yield return (p, null, err);
                    continue;
                }
                var entry = info.Supersedes[Convert.ToHexStringLower(refs[p].IndexId)];
                // PROWIZORYCZNIE Qb99: widok packów zastąpionego indeksu (nie cała unia) — zakres dedup i odzysku miejsca bez zmian (Qb26).
                var view = new IndexObject([.. entry.PackOrdinals.Select(o => obj.Packs[o])], [], obj.HostId, obj.RunId);
                yield return (p, new ResolvedIndex(Convert.FromHexString(by), view, info.Sha256, true), null);
            }
        }
    }

    /// <summary>Pełny obiekt indeksu skonsolidowanego (z pamięci albo ponownie z celu — skrót musi się zgadzać z mapą).</summary>
    public async Task<IndexObject> LoadConsolidatedAsync(string hex, CancellationToken ct)
    {
        if (_objects.TryGetValue(hex, out var cached)) return cached;
        var info = Consolidated[hex];
        IndexReads++;
        var (idx, sha) = await MetadataStore.LoadIndexAsync(_session, _keys, Convert.FromHexString(hex), ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(sha, info.Sha256))
            throw new KobianException(KbCode.ReferenceIntegrityFailed, ErrorClass.Integrity, "Consolidated index changed while in use.");
        if (cacheObjects) _objects[hex] = idx;
        return idx;
    }

    /// <summary>
    /// Mapa relacji „zastąpiony przez”: każdy obecny indeks czytany raz; nieczytelne pomijane (nie są kandydatami); w pamięci zostają tylko wpisy relacji
    /// (i — gdy <c>cacheObjects</c> — obiekty indeksów skonsolidowanych). Wywoływane leniwie przy pierwszym brakującym oryginale albo jawnie (prune, konsolidacja, check).
    /// </summary>
    public async Task EnsureMapAsync(CancellationToken ct)
    {
        if (_supersededBy is not null) return;
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var infos = new Dictionary<string, ConsolidatedIndexInfo>(StringComparer.Ordinal);
        var files = new List<(byte[] Id, long Size)>();
        await foreach (var o in _session.ListAsync(RepoPaths.IndexDir, ct).ConfigureAwait(false))
            if (RepoPaths.TryParseId(o.Path, RepoPaths.IndexDir, ".idx") is { } id) files.Add((id, o.Length));
        foreach (var (id, size) in files)
        {
            ct.ThrowIfCancellationRequested();
            IndexObject idx;
            byte[] sha;
            try
            {
                IndexReads++;
                (idx, sha) = await MetadataStore.LoadIndexAsync(_session, _keys, id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KobianException or StorageException)
            {
                continue;
            }
            if (idx.Supersedes.Count == 0 || idx.SupersededSha256 is not { } shas || idx.SupersededPacks is not { } ords) continue;
            var hex = Convert.ToHexStringLower(id);
            if (cacheObjects) _objects[hex] = idx;
            var sup = new Dictionary<string, SupersededEntry>(StringComparer.Ordinal);
            for (var i = 0; i < idx.Supersedes.Count; i++)
            {
                var s = Convert.ToHexStringLower(idx.Supersedes[i]);
                if (s == hex || !sup.TryAdd(s, new SupersededEntry(shas[i], ords[i]))) continue; // samo-odwołanie / duplikat — ignorowane
                if (!map.TryGetValue(s, out var list)) map[s] = list = [];
                list.Add(hex);
            }
            infos[hex] = new ConsolidatedIndexInfo(hex, sha, size, sup);
        }
        _supersededBy = map;
        _infos = infos;
    }

    /// <summary>Czy indeks <paramref name="hex"/> o skrócie pliku <paramref name="sha"/> jest zastąpiony przez obecny, czytelny indeks skonsolidowany.</summary>
    public bool IsSuperseded(string hex, byte[] sha) =>
        _supersededBy is not null && _supersededBy.TryGetValue(hex, out var list)
        && list.Any(c => CryptographicOperations.FixedTimeEquals(_infos![c].Supersedes[hex].Sha256, sha));

    /// <summary>Indeks skonsolidowany (hex), który rozwiązuje odwołanie, albo null — bez odczytu oryginału. Null również przed zbudowaniem mapy.</summary>
    public string? Candidate(string hex, byte[] expectedSha)
    {
        if (_supersededBy is null || !_supersededBy.TryGetValue(hex, out var list)) return null;
        string? best = null;
        var bestSuperseded = true;
        foreach (var by in list)
        {
            if (!CryptographicOperations.FixedTimeEquals(_infos![by].Supersedes[hex].Sha256, expectedSha)) continue;
            var superseded = IsSuperseded(by, _infos[by].Sha256);
            if (best is null || (bestSuperseded && !superseded) || (bestSuperseded == superseded && string.CompareOrdinal(by, best) < 0))
            {
                best = by;
                bestSuperseded = superseded;
            }
        }
        return best;
    }

    private static void Add(SortedDictionary<string, List<int>> groups, string by, int position)
    {
        if (!groups.TryGetValue(by, out var list)) groups[by] = list = [];
        list.Add(position);
    }
}
