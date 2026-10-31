using Kobian.Engine.Repo;
using Kobian.Format.Metadata;

namespace Kobian.Engine.Backup;

/// <summary>
/// Baza przyrostu ładowana strumieniowo (BL-83, RD-64; it. 113): indeksy bazowe czytane po jednym — identyfikatory bloków trafiają od razu do
/// <see cref="DedupTable"/> (32 B/blok), a obiekt indeksu (IndexPack/PackEntry: ~100+ B/blok) jest porzucany przed wczytaniem następnego.
/// Zostają tylko pary (id indeksu, SHA-256) do klucza 11 snapshotu i licznik bloków. Zachowanie dedup identyczne jak dla <see cref="BaselineIndex"/>.
/// Tabela należy do JEDNEGO uruchomienia (zamrażana przy pierwszym odwołaniu) — zestawu nie używa się ponownie.
/// </summary>
public sealed class BaselineSet
{
    private int _taken;

    private BaselineSet(IReadOnlyList<(byte[] IndexId, byte[] Sha256)> refs, DedupTable table, long blocks)
    {
        Refs = refs;
        Table = table;
        Blocks = blocks;
    }

    public IReadOnlyList<(byte[] IndexId, byte[] Sha256)> Refs { get; }

    public long Blocks { get; }

    internal DedupTable Table { get; }

    /// <summary>Przejęcie tabeli przez potok; drugie przejęcie = błąd programisty (tabela jest już zamrożona i rozszerzona o bloki nowe).</summary>
    internal DedupTable Take() =>
        Interlocked.Exchange(ref _taken, 1) == 0 ? Table : throw new InvalidOperationException("BaselineSet already used by a backup run.");

    /// <param name="indexIds">Indeksy bazowe (z poprzedniego snapshotu, w jego kolejności).</param>
    /// <param name="load">Wczytanie i uwierzytelnienie jednego indeksu (np. MetadataStore.LoadIndexAsync).</param>
    public static async Task<BaselineSet> LoadAsync(IReadOnlyList<byte[]> indexIds, Func<byte[], CancellationToken, Task<(IndexObject Index, byte[] Sha256)>> load,
        CancellationToken ct, int bloomThreshold = DedupTable.DefaultBloomThreshold)
    {
        ArgumentNullException.ThrowIfNull(indexIds);
        ArgumentNullException.ThrowIfNull(load);
        var table = new DedupTable(0, bloomThreshold);
        var refs = new List<(byte[], byte[])>(indexIds.Count);
        long blocks = 0;
        foreach (var id in indexIds)
        {
            ct.ThrowIfCancellationRequested();
            var (idx, sha) = await load(id, ct).ConfigureAwait(false);
            var n = 0;
            foreach (var pk in idx.Packs) n += pk.Entries.Count;
            table.Reserve(n);
            foreach (var pk in idx.Packs)
                foreach (var e in pk.Entries) table.AddConfirmed(e.ChunkId);
            blocks += n;
            refs.Add((id, sha));
            // idx wychodzi z zasięgu — przed wczytaniem następnego indeksu GC może go zwolnić
        }
        return new BaselineSet(refs, table, blocks);
    }

    /// <summary>
    /// Baza z odwołań poprzedniego snapshotu (klucz 11) rozwiązywanych przez <see cref="IndexResolver"/> (D3.16): odwołanie do indeksu zastąpionego przez
    /// konsolidację daje widok indeksu skonsolidowanego (SHA-256 z odwołania musi być równy zapisanemu w nim skrótowi; packi = dokładnie te z zastąpionego
    /// indeksu), indeks skonsolidowany czytany raz. Nowy snapshot dziedziczy odwołania rodzica BEZ ZMIAN (te same id i skróty) — zakres dedup, zależności
    /// i odzysk miejsca przez prune są identyczne jak bez konsolidacji. Skrót oryginału jest teraz porównywany z odwołaniem (wcześniej przyjmowany z obiektu).
    /// </summary>
    public static async Task<BaselineSet> LoadResolvedAsync(IReadOnlyList<(byte[] IndexId, byte[] Sha256)> parentRefs, IndexResolver resolver, CancellationToken ct,
        int bloomThreshold = DedupTable.DefaultBloomThreshold)
    {
        ArgumentNullException.ThrowIfNull(parentRefs);
        ArgumentNullException.ThrowIfNull(resolver);
        var table = new DedupTable(0, bloomThreshold);
        var refs = new List<(byte[], byte[])>(parentRefs.Count);
        long blocks = 0;
        await foreach (var (pos, r, err) in resolver.ResolveAllAsync(parentRefs, ct).ConfigureAwait(false))
        {
            err?.Throw();
            var n = 0;
            foreach (var pk in r!.Index.Packs) n += pk.Entries.Count;
            table.Reserve(n);
            foreach (var pk in r.Index.Packs)
                foreach (var e in pk.Entries) table.AddConfirmed(e.ChunkId);
            blocks += n;
            refs.Add(parentRefs[pos]);
        }
        return new BaselineSet(refs, table, blocks);
    }

    /// <summary>Zestaw z obiektów już wczytanych (testy, zgodność z <see cref="BaselineIndex"/>).</summary>
    public static Task<BaselineSet> FromIndexes(IReadOnlyList<BaselineIndex> baseline, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var map = baseline.ToDictionary(b => Convert.ToHexStringLower(b.IndexId), b => (b.Index, b.Sha256), StringComparer.Ordinal);
        return LoadAsync([.. baseline.Select(b => b.IndexId)], (id, _) => Task.FromResult(map[Convert.ToHexStringLower(id)]), ct);
    }
}
