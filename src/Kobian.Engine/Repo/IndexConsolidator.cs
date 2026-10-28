using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Format.Metadata;
using Kobian.Format.Repo;

namespace Kobian.Engine.Repo;

/// <summary>
/// Progi konsolidacji (D3.16): uruchamiana, gdy aktywnych indeksów &gt; <see cref="MaxActive"/> (wtedy scalane wszystkie) albo małych (&lt; <see cref="SmallBytes"/>)
/// &gt; <see cref="MaxSmall"/> (wtedy scalane tylko małe — bez przepisywania dużego indeksu skonsolidowanego co kilkanaście uruchomień).
/// Limity jednego nowego indeksu (R2.3.5/R2.3.8: obiekt ≤ 256 MiB po dekompresji, ≤ 100 000 packów, ≤ 10 000 zastąpionych): przekroczenie = kolejny indeks
/// (podział po CAŁYCH indeksach wejściowych — relacja „zastąpiony przez” zawsze w jednym kroku).
/// </summary>
public sealed record IndexConsolidationOptions(
    int MaxActive = 32,
    int MaxSmall = 16,
    long SmallBytes = 1024 * 1024,
    int MaxPacksPerIndex = 50_000,
    long MaxEntriesPerIndex = 2_000_000,
    int MaxSupersededPerIndex = 5_000);

/// <summary>
/// Wynik konsolidacji: <see cref="Code"/> ≠ null = nie wykonano lub przerwano (KB-1601 nieczytelny żywy snapshot/indeks; KB-1604 zapis/weryfikacja nieudane,
/// niespójne wejście) — repozytorium pozostaje czytelne i kompletne. <see cref="Needed"/> = przekroczono próg; <see cref="Superseded"/> = indeksy wejściowe
/// zastąpione przez <see cref="Written"/> (hex); <see cref="ActiveBefore"/>/<see cref="ActiveAfter"/> = liczba aktywnych (żywych, niezastąpionych) indeksów.
/// </summary>
public sealed record IndexConsolidation(string? Code, bool Needed, int ActiveBefore, int ActiveAfter, int Superseded, IReadOnlyList<string> Written)
{
    public static IndexConsolidation None(int active) => new(null, false, active, active, 0, []);
}

/// <summary>
/// Konsolidacja indeksów (D3.16, L4, PM-17; BL-25 dokończenie) — zadanie utrzymaniowe, zgodne z append-only: TYLKO zapisuje nowe <c>index/&lt;new&gt;.idx</c>.
/// <list type="number">
/// <item>Warunki: trzymana WYŁĄCZNA dzierżawa (D3.13 — jak prune; wejście zależy od zbioru żywych snapshotów, więc nie może biec współbieżnie z backupem
/// ani zapominaniem). Usuwanie zastąpionych plików robi wyłącznie prune dwufazowy po karencji (D3.12) — tutaj nic nie jest usuwane.</item>
/// <item>Wejście = indeksy AKTYWNE: rozwiązane z odwołań żywych snapshotów (nie zapomnianych w katalogu; nieznane katalogowi są żywe — CE-18) przez
/// <see cref="IndexResolver"/>. Indeksy martwe (sieroty, tylko zapomniane snapshoty) nie są przenoszone — prune je usunie.</item>
/// <item>Nowy indeks: unia packów wejść (pack o tym samym id musi mieć ten sam SHA-256, rozmiar i wpisy — inaczej KB-1604, nic nie zapisano) oraz relacja
/// „zastąpiony przez” (klucz 2) ze skrótami (klucz 32) i numerami packów (klucz 33) dla: każdego wejścia i — przechodnio — każdego żywego odwołania, które
/// wejście skonsolidowane rozwiązywało. Packi spoza żywych odwołań wejścia skonsolidowanego są pomijane (odzysk miejsca jak bez konsolidacji).</item>
/// <item>Zapis: <see cref="VerifiedUploader"/> (V1-a rozmiar, V1-b SHA-256 odczytu) + ponowne otwarcie i dekodowanie z porównaniem zawartości.
/// Awaria na dowolnym kroku zostawia repo czytelne: stare indeksy są nietknięte, a niepełny nowy jest sierotą (czytelnik i prune go pomijają/usuwają).</item>
/// </list>
/// </summary>
public static class IndexConsolidator
{
    public static async Task<IndexConsolidation> RunAsync(IStorageSession session, RepoKeys keys, RepoLease lease, IReadOnlySet<string> forgotten, byte[] hostId,
        IndexConsolidationOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(forgotten);
        ArgumentNullException.ThrowIfNull(hostId);
        var opt = options ?? new IndexConsolidationOptions();
        // PROWIZORYCZNIE Qb96: blokada wyłączna (D3.16 mówi „współdzielona”) — wejście zależy od zbioru żywych snapshotów.
        if (!lease.Exclusive) throw new InvalidOperationException("Index consolidation requires an exclusive repository lock (D3.13, D3.16).");
        await lease.RenewIfDueAsync(ct).ConfigureAwait(false);

        var resolver = new IndexResolver(session, keys, cacheObjects: false);
        await resolver.EnsureMapAsync(ct).ConfigureAwait(false);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var o in session.ListAsync(RepoPaths.IndexDir, ct).ConfigureAwait(false))
            if (RepoPaths.TryParseId(o.Path, RepoPaths.IndexDir, ".idx") is { } id) sizes[Hex(id)] = o.Length;

        // Żywe odwołania: obiekt aktywny (oryginał albo kandydat skonsolidowany) → { id z odwołania → SHA-256 z odwołania }.
        var active = new SortedDictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        var snaps = new List<string>();
        await foreach (var o in session.ListAsync(RepoPaths.SnapshotsDir, ct).ConfigureAwait(false))
            if (RepoPaths.TryParseId(o.Path, RepoPaths.SnapshotsDir, ".snap") is { } id && !forgotten.Contains(Hex(id))) snaps.Add(Hex(id));
        foreach (var s in snaps.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            SnapshotObject snap;
            try
            {
                snap = await MetadataStore.LoadSnapshotAsync(session, keys, Convert.FromHexString(s), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KobianException or StorageException)
            {
                return new IndexConsolidation(KbCode.PruneBlockedUnreadable, false, active.Count, active.Count, 0, []);
            }
            foreach (var (indexId, sha) in snap.Indexes)
            {
                var rh = Hex(indexId);
                var target = resolver.Candidate(rh, sha) ?? rh;
                if (!active.TryGetValue(target, out var refs)) active[target] = refs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                if (refs.TryGetValue(rh, out var known) && !CryptographicOperations.FixedTimeEquals(known, sha))
                    return new IndexConsolidation(KbCode.IndexConsolidationIncomplete, false, active.Count, active.Count, 0, []); // dwa różne skróty jednego indeksu
                refs[rh] = sha;
            }
        }

        var count = active.Count;
        var small = active.Keys.Where(h => sizes.TryGetValue(h, out var l) && l < opt.SmallBytes).ToList();
        // PROWIZORYCZNIE Qb98: „suma małych > 16” = liczba indeksów < 1 MiB > 16; wtedy scalane tylko małe.
        List<string> inputs;
        if (count > opt.MaxActive) inputs = [.. active.Keys];
        else if (small.Count > opt.MaxSmall) inputs = small;
        else return IndexConsolidation.None(count);
        if (inputs.Count < 2) return IndexConsolidation.None(count);

        var written = new List<string>();
        var superseded = 0;
        var batch = new Batch();
        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            await lease.RenewIfDueAsync(ct).ConfigureAwait(false);
            Part part;
            try
            {
                part = await LoadPartAsync(session, keys, resolver, input, active[input], ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KobianException or StorageException)
            {
                // Żywy indeks nieczytelny albo niezgodny z odwołaniem: konsolidacja niczego nie zapisuje (prune i tak się wstrzyma — KB-1601).
                return new IndexConsolidation(KbCode.PruneBlockedUnreadable, true, count, count - superseded + written.Count, superseded, written);
            }
            if (batch.Inputs > 0 && !batch.Fits(part, opt))
            {
                if (batch.Inputs >= 2)
                {
                    var w = await WriteAsync(session, keys, batch, hostId, ct).ConfigureAwait(false);
                    if (w is null) return new IndexConsolidation(KbCode.IndexConsolidationIncomplete, true, count, count - superseded + written.Count, superseded, written);
                    written.Add(w);
                    superseded += batch.Inputs;
                }
                batch = new Batch();
            }
            if (!batch.TryAdd(part)) return new IndexConsolidation(KbCode.IndexConsolidationIncomplete, true, count, count - superseded + written.Count, superseded, written);
        }
        if (batch.Inputs >= 2)
        {
            var w = await WriteAsync(session, keys, batch, hostId, ct).ConfigureAwait(false);
            if (w is null) return new IndexConsolidation(KbCode.IndexConsolidationIncomplete, true, count, count - superseded + written.Count, superseded, written);
            written.Add(w);
            superseded += batch.Inputs;
        }
        return new IndexConsolidation(null, true, count, count - superseded + written.Count, superseded, written);
    }

    /// <summary>Wejście konsolidacji: packi do przeniesienia i wpisy relacji (id → skrót, packi jako id hex).</summary>
    private sealed record Part(string Hex, IReadOnlyList<IndexPack> Packs, IReadOnlyList<(string Id, byte[] Sha, IReadOnlyList<string> Packs)> Entries, long EntryCount);

    private static async Task<Part> LoadPartAsync(IStorageSession session, RepoKeys keys, IndexResolver resolver, string input,
        Dictionary<string, byte[]> liveRefs, CancellationToken ct)
    {
        if (resolver.Consolidated.TryGetValue(input, out var info))
        {
            var obj = await resolver.LoadConsolidatedAsync(input, ct).ConfigureAwait(false);
            var keep = new SortedSet<int>();
            var entries = new List<(string, byte[], IReadOnlyList<string>)>();
            foreach (var (rh, sha) in liveRefs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                IReadOnlyList<int> ords = rh == input ? [.. Enumerable.Range(0, obj.Packs.Count)] : info.Supersedes[rh].PackOrdinals;
                keep.UnionWith(ords);
                if (rh != input) entries.Add((rh, sha, [.. ords.Select(o => Hex(obj.Packs[o].PackId))]));
            }
            var packs = keep.Select(o => obj.Packs[o]).ToList();
            entries.Add((input, info.Sha256, [.. packs.Select(p => Hex(p.PackId))]));
            return new Part(input, packs, entries, packs.Sum(p => (long)p.Entries.Count));
        }
        var (idx, fileSha) = await MetadataStore.LoadIndexAsync(session, keys, Convert.FromHexString(input), ct).ConfigureAwait(false);
        if (!liveRefs.TryGetValue(input, out var expected) || !CryptographicOperations.FixedTimeEquals(expected, fileSha))
            throw new KobianException(KbCode.ReferenceIntegrityFailed, ErrorClass.Integrity, "Index hash differs from snapshot reference.");
        return new Part(input, idx.Packs, [(input, fileSha, [.. idx.Packs.Select(p => Hex(p.PackId))])], idx.Packs.Sum(p => (long)p.Entries.Count));
    }

    private sealed class Batch
    {
        private readonly Dictionary<string, int> _ordinal = new(StringComparer.Ordinal);
        private readonly HashSet<string> _entryIds = new(StringComparer.Ordinal);

        public List<IndexPack> Packs { get; } = [];

        public List<(byte[] Id, byte[] Sha, List<int> Ords)> Entries { get; } = [];

        public int Inputs { get; private set; }

        public long EntryCount { get; private set; }

        public bool Fits(Part p, IndexConsolidationOptions o) =>
            Packs.Count + p.Packs.Count <= o.MaxPacksPerIndex && EntryCount + p.EntryCount <= o.MaxEntriesPerIndex
            && Entries.Count + p.Entries.Count <= o.MaxSupersededPerIndex;

        /// <summary>false = niespójne wejście (ten sam pack z innym skrótem/rozmiarem/wpisami albo ten sam indeks z innym skrótem).</summary>
        public bool TryAdd(Part p)
        {
            foreach (var pk in p.Packs)
            {
                var h = Hex(pk.PackId);
                if (_ordinal.TryGetValue(h, out var at))
                {
                    if (!SamePack(Packs[at], pk)) return false;
                    continue;
                }
                _ordinal[h] = Packs.Count;
                Packs.Add(pk);
                EntryCount += pk.Entries.Count;
            }
            foreach (var (id, sha, packs) in p.Entries)
            {
                if (!_entryIds.Add(id))
                {
                    var prev = Entries.First(e => Hex(e.Id) == id);
                    if (!CryptographicOperations.FixedTimeEquals(prev.Sha, sha)) return false;
                    continue;
                }
                Entries.Add((Convert.FromHexString(id), sha, [.. packs.Select(h => _ordinal[h])]));
            }
            Inputs++;
            return true;
        }

        private static bool SamePack(IndexPack a, IndexPack b)
        {
            if (a.Size != b.Size || !a.Sha256.AsSpan().SequenceEqual(b.Sha256) || a.Entries.Count != b.Entries.Count) return false;
            for (var i = 0; i < a.Entries.Count; i++)
                if (a.Entries[i] is var x && b.Entries[i] is var y
                    && (!x.ChunkId.AsSpan().SequenceEqual(y.ChunkId) || x.Offset != y.Offset || x.FrameLength != y.FrameLength || x.BlobType != y.BlobType || x.RawLength != y.RawLength))
                    return false;
            return true;
        }
    }

    /// <summary>Zapis i weryfikacja nowego indeksu; null = zapis albo weryfikacja nieudane (nic poza ewentualną sierotą nie zostało zmienione).</summary>
    private static async Task<string?> WriteAsync(IStorageSession session, RepoKeys keys, Batch b, byte[] hostId, CancellationToken ct)
    {
        var id = RandomNumberGenerator.GetBytes(16);
        var obj = new IndexObject(b.Packs, [.. b.Entries.Select(e => e.Id)], hostId, RandomNumberGenerator.GetBytes(16),
            [.. b.Entries.Select(e => e.Sha)], [.. b.Entries.Select(e => (IReadOnlyList<int>)e.Ords)]);
        var sealedIdx = MetadataStore.Seal(keys, MetadataKind.Index, id, IndexCodec.Encode(obj));
        try
        {
            var sha = await new VerifiedUploader(session).UploadAsync(RepoPaths.Index(id), sealedIdx, ct).ConfigureAwait(false);
            var (back, backSha) = await MetadataStore.LoadIndexAsync(session, keys, id, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(sha, backSha) || back.Packs.Count != obj.Packs.Count || back.Supersedes.Count != obj.Supersedes.Count
                || back.Packs.Sum(p => (long)p.Entries.Count) != b.EntryCount)
                return null;
            return Hex(id);
        }
        catch (Exception ex) when (ex is RepoWriteException or KobianException or StorageException)
        {
            return null;
        }
    }

    private static string Hex(byte[] id) => Convert.ToHexStringLower(id);
}
