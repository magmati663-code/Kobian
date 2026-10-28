using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Compression;
using Kobian.Format.Metadata;
using Kobian.Format.Pack;
using Kobian.Format.Repo;

namespace Kobian.Engine.Repo;

/// <summary>Położenie bloku: pack (id, oczekiwany rozmiar i SHA-256 z indeksu) i wpis.</summary>
public sealed record BlockLocation(byte[] PackId, long PackSize, byte[] PackSha256, PackEntry Entry);

/// <summary>
/// Odczyt repozytorium (restore jest parserem niezaufanych danych — 3.x, SEC-3.3): indeksy snapshotu → mapa bloków,
/// odczyt bloku z uwierzytelnionego packa, dekompresja z limitem, kontrola <c>chunkId = HMAC(K_id, raw)</c> w czasie stałym.
/// Packi buforowane w pamięci w pamięci podręcznej LRU o budżecie <see cref="PackCacheBudgetBytes"/> (zawsze co najmniej ostatnio użyty), żeby plik z blokami
/// w wielu packach nie pobierał ich wielokrotnie z celu zdalnego; odczyty zakresowe (R2.8.1) — późniejsza optymalizacja.
/// Czytnik nie jest bezpieczny wątkowo (jak dotąd): jeden odczyt naraz.
/// </summary>
public sealed class RepoReader(IStorageSession session, RepoKeys keys) : IDisposable
{
    private readonly BlockCompressor _zstd = new();
    private readonly Dictionary<string, BlockLocation> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<CachedPack> _packs = new();
    private long _packBytes;
    private IndexResolver? _resolver;

    /// <summary>Budżet pamięci podręcznej packów (bajty packów w pamięci); ostatnio użyty pack jest trzymany nawet ponad budżet.</summary>
    public const long PackCacheBudgetBytes = 256L * 1024 * 1024;

    private sealed record CachedPack(string Path, long Size, PackReader Reader, AesGcmCipher Cipher);

    public int KnownBlocks => _map.Count;

    /// <summary>
    /// Indeksy snapshotu (klucz 11) przez <see cref="IndexResolver"/> (D3.16): odwołanie do indeksu zastąpionego przez konsolidację rozwiązuje uwierzytelniony indeks
    /// skonsolidowany z tym samym SHA-256 w kluczu 32 (widok: packi zastąpionego indeksu). Indeks skonsolidowany jest czytany raz na wywołanie; duplikaty wpisów
    /// w różnych indeksach są tolerowane (<see cref="AddIndex"/> — pierwszy wygrywa; każdy blok i tak weryfikowany AEAD i HMAC przy odczycie).
    /// </summary>
    public async Task LoadIndexesAsync(SnapshotObject snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await foreach (var (_, r, err) in Resolver.ResolveAllAsync(snapshot.Indexes, ct).ConfigureAwait(false))
        {
            err?.Throw();
            AddIndex(r!.Index);
        }
    }

    /// <summary>Resolver odwołań do indeksów współdzielony przez kolejne wczytania tego czytnika (mapa relacji budowana najwyżej raz).</summary>
    public IndexResolver Resolver => _resolver ??= new IndexResolver(session, keys);

    /// <summary>
    /// D3.10 „brak indeksu → trailery” (it. 110): jak <see cref="LoadIndexesAsync"/>, ale indeks snapshotu brakujący/uszkodzony/z innym skrótem (i bez indeksu
    /// skonsolidowanego, który by go zastąpił — D3.16) nie przerywa — wtedy do mapy trafiają WSZYSTKIE pozostałe czytelne indeksy repozytorium (w tym „recovered”
    /// z <see cref="RepoIndexRecovery"/>). Bezpieczne dla integralności: każdy blok jest weryfikowany AEAD i HMAC chunkId przy odczycie, a brak bloku kończy się
    /// błędem jak dotąd. Zwraca true, gdy użyto obejścia (→ KB-1505).
    /// </summary>
    public async Task<bool> LoadIndexesWithFallbackAsync(SnapshotObject snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var missing = false;
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var (_, r, err) in Resolver.ResolveAllAsync(snapshot.Indexes, ct).ConfigureAwait(false))
        {
            if (err is not null)
            {
                missing = true;
                continue;
            }
            AddIndex(r!.Index);
            loaded.Add(r.Hex);
        }
        if (!missing) return false;
        await foreach (var o in session.ListAsync(RepoPaths.IndexDir, ct).ConfigureAwait(false))
        {
            if (RepoPaths.TryParseId(o.Path, RepoPaths.IndexDir, ".idx") is not { } other || loaded.Contains(Convert.ToHexStringLower(other))) continue;
            try
            {
                AddIndex((await MetadataStore.LoadIndexAsync(session, keys, other, ct).ConfigureAwait(false)).Index);
            }
            catch (Exception ex) when (ex is KobianException or StorageException)
            {
                // inny indeks nieczytelny — pomijamy (bloki z niego będą brakujące, jeśli nikt inny ich nie ma)
            }
        }
        return true;
    }

    /// <summary>Dodaje wpisy już zweryfikowanego indeksu do mapy bloków (np. <see cref="RepoChecker"/>, który sam sprawdza hash względem snapshotu).</summary>
    public void AddIndex(IndexObject index)
    {
        ArgumentNullException.ThrowIfNull(index);
        foreach (var pk in index.Packs)
            foreach (var e in pk.Entries)
                _map.TryAdd(Convert.ToHexString(e.ChunkId), new BlockLocation(pk.PackId, pk.Size, pk.Sha256, e));
    }

    /// <summary>
    /// V3 (3.7) dla jednego packa: pobranie całości, rozmiar i SHA-256 względem indeksu, uwierzytelnienie stopki (commitTag), odczyt KAŻDEGO bloku
    /// (AEAD ramki), dekompresja z limitem i <c>chunkId = HMAC(K_id, raw)</c> w czasie stałym; każdy wpis indeksu musi być w stopce packa.
    /// Zwraca liczbę zweryfikowanych bloków; pierwszy błąd → <see cref="KobianException"/> z kodem KB.
    /// </summary>
    public async Task<int> VerifyPackAsync(IndexPack pack, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var any = pack.Entries.Count > 0 ? pack.Entries[0] : new PackEntry(new byte[32], 0, 0, BlobType.Data, 0);
        var reader = await OpenPackAsync(new BlockLocation(pack.PackId, pack.Size, pack.Sha256, any), ct).ConfigureAwait(false);
        foreach (var e in pack.Entries) FindEntry(reader, e.ChunkId);
        for (var i = 0; i < reader.Entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = reader.Entries[i];
            var blob = reader.ReadBlob(i);
            if (blob.BlobType != entry.BlobType) throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Unexpected blob type.");
            var max = entry.BlobType == BlobType.Tree ? PackLayout.MaxRawLengthTree : PackLayout.MaxRawLengthData;
            var raw = _zstd.Decompress(blob.Algorithm, blob.Payload, blob.RawLength, max);
            if (!CryptographicOperations.FixedTimeEquals(keys.ChunkId(raw), entry.ChunkId))
                throw new KobianException(KbCode.AuthenticationFailed, ErrorClass.Integrity, "Block content does not match its identifier.");
        }
        return reader.Entries.Count;
    }

    public bool TryLocate(byte[] chunkId, out BlockLocation? location) => _map.TryGetValue(Convert.ToHexString(chunkId), out location);

    public async Task<byte[]> ReadBlockAsync(byte[] chunkId, BlobType expectedType, CancellationToken ct)
    {
        if (!TryLocate(chunkId, out var loc) || loc is null)
            throw new KobianException(KbCode.ReferenceIntegrityFailed, ErrorClass.Integrity, "Block not found in snapshot indexes.");
        if (loc.Entry.BlobType != expectedType) throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Unexpected blob type.");
        var reader = await OpenPackAsync(loc, ct).ConfigureAwait(false);
        var i = FindEntry(reader, chunkId);
        var blob = reader.ReadBlob(i);
        if (blob.BlobType != expectedType) throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Unexpected blob type.");
        var max = expectedType == BlobType.Tree ? PackLayout.MaxRawLengthTree : PackLayout.MaxRawLengthData;
        var raw = _zstd.Decompress(blob.Algorithm, blob.Payload, blob.RawLength, max);
        if (!CryptographicOperations.FixedTimeEquals(keys.ChunkId(raw), chunkId))
            throw new KobianException(KbCode.AuthenticationFailed, ErrorClass.Integrity, "Block content does not match its identifier.");
        return raw;
    }

    public async Task<IReadOnlyList<TreeEntry>> ReadTreeAsync(byte[] treeId, CancellationToken ct) =>
        TreeCodec.Decode(await ReadBlockAsync(treeId, BlobType.Tree, ct).ConfigureAwait(false));

    private static int FindEntry(PackReader reader, byte[] chunkId)
    {
        for (var i = 0; i < reader.Entries.Count; i++)
            if (reader.Entries[i].ChunkId.AsSpan().SequenceEqual(chunkId)) return i;
        throw new KobianException(KbCode.ReferenceIntegrityFailed, ErrorClass.Integrity, "Block missing from pack trailer.");
    }

    private async Task<PackReader> OpenPackAsync(BlockLocation loc, CancellationToken ct)
    {
        var path = RepoPaths.Pack(loc.PackId);
        for (var node = _packs.First; node is not null; node = node.Next)
        {
            if (node.Value.Path != path) continue;
            _packs.Remove(node);
            _packs.AddFirst(node);
            return node.Value.Reader;
        }
        if (loc.PackSize > 2L * 1024 * 1024 * 1024) throw new KobianException(KbCode.MetadataTooLarge, ErrorClass.Integrity, "Pack too large.");
        var bytes = await MetadataStore.ReadAllAsync(session, path, loc.PackSize, ct).ConfigureAwait(false);
        if (bytes.Length != loc.PackSize || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), loc.PackSha256))
            throw new KobianException(KbCode.ReferenceIntegrityFailed, ErrorClass.Integrity, "Pack differs from index (size or hash).");
        AesGcmCipher cipher;
        var key = keys.DerivePackKey(loc.PackId);
        try { cipher = new AesGcmCipher(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
        PackReader reader;
        try
        {
            reader = PackReader.Open(new MemoryStream(bytes, writable: false), cipher, keys.RepoId, loc.PackId, keys.CommitTag(loc.PackId));
        }
        catch
        {
            cipher.Dispose();
            throw;
        }
        // Wypychamy najstarsze, aż nowy pack zmieści się w budżecie (poprzednio używany pack zostaje, jeśli się mieści).
        while (_packs.Last is { } oldest && _packBytes + bytes.Length > PackCacheBudgetBytes)
        {
            _packs.RemoveLast();
            _packBytes -= oldest.Value.Size;
            oldest.Value.Cipher.Dispose();
        }
        _packs.AddFirst(new CachedPack(path, bytes.Length, reader, cipher));
        _packBytes += bytes.Length;
        return reader;
    }

    public void Dispose()
    {
        _zstd.Dispose();
        foreach (var p in _packs) p.Cipher.Dispose();
        _packs.Clear();
        _packBytes = 0;
    }
}
