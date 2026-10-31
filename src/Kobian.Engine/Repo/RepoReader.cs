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
public sealed record BlockLocation(byte[] PackId, long PackSize, byte[] PackSha256, PackEntry Entry, int EntryIndex = -1);

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
    private readonly Dictionary<Kobian.Engine.Backup.ChunkKey, BlockLocation> _map = []; // klucz 32 B (struktura) zamiast 64-znakowego tekstu (~150 B) na każdy blok
    private readonly LinkedList<CachedPack> _packs = new();
    private long _packBytes;
    private IndexResolver? _resolver;

    /// <summary>Budżet pamięci podręcznej packów (bajty packów w pamięci); ostatnio użyty pack jest trzymany nawet ponad budżet.</summary>
    public const long PackCacheBudgetBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Odczyt pojedynczych bloków przez zakres bajtów (sama ramka, ~rozmiar bloku) zamiast pobierania i haszowania całego packa. Dla kontroli i czytnika drzew poprzedniej
    /// migawki: drzewa są rozsiane po wszystkich packach, więc odczyt całych packów kosztował 16 MB na drzewo (300 tys. plików: weryfikacja 17 s → 220 s przy małym cache).
    /// Uwierzytelnienie: AEAD ramki (repo, pack, pozycja, chunkId w danych dodatkowych) + HMAC chunkId po dekompresji; pominięty jest tylko skrót całego packa — tu nieistotny,
    /// bo czytamy jedną ramkę, a obecność i rozmiar packów sprawdza osobno Stat. Cel bez odczytu zakresowego → odczyt całego packa jak dotąd.
    /// </summary>
    public bool UseRangeReads { get; init; }

    private readonly Dictionary<string, AesGcmCipher> _frameCiphers = new(StringComparer.Ordinal);

    /// <summary>Budżet tego czytnika (domyślnie <see cref="PackCacheBudgetBytes"/>); czytnik drzew poprzedniej migawki w trakcie kopii używa małego.</summary>
    public long PackCacheBudget { get; init; } = PackCacheBudgetBytes;

    private sealed record CachedPack(string Path, long Size, PackReader Reader, AesGcmCipher Cipher)
    {
        /// <summary>Indeks chunkId → pozycja we stopce packa, budowany przy pierwszym wyszukiwaniu (wcześniej liniowy skan na KAŻDY blok: O(n²) dla packów z tysiącami małych bloków).</summary>
        public Dictionary<string, int>? ByChunk { get; set; }
    }

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

    /// <summary>
    /// Jak <see cref="LoadIndexesAsync"/>, ale do mapy trafiają WYŁĄCZNIE bloki drzew (katalogów) — wystarcza do przejścia po poprzedniej migawce w kopii przyrostowej
    /// i kosztuje ułamek pamięci pełnej mapy bloków (drzew jest ~1 na katalog, bloków danych miliony).
    /// </summary>
    public async Task LoadTreeIndexAsync(SnapshotObject snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await foreach (var (_, r, err) in Resolver.ResolveAllAsync(snapshot.Indexes, ct).ConfigureAwait(false))
        {
            err?.Throw();
            foreach (var pk in r!.Index.Packs)
                for (var i = 0; i < pk.Entries.Count; i++)
                    if (pk.Entries[i].BlobType == BlobType.Tree) _map.TryAdd(new Kobian.Engine.Backup.ChunkKey(pk.Entries[i].ChunkId), new BlockLocation(pk.PackId, pk.Size, pk.Sha256, pk.Entries[i], i));
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
            for (var i = 0; i < pk.Entries.Count; i++)
                _map.TryAdd(new Kobian.Engine.Backup.ChunkKey(pk.Entries[i].ChunkId), new BlockLocation(pk.PackId, pk.Size, pk.Sha256, pk.Entries[i], i));
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

    public bool TryLocate(byte[] chunkId, out BlockLocation? location)
    {
        ArgumentNullException.ThrowIfNull(chunkId);
        location = null;
        return chunkId.Length == PackLayout.ChunkIdSize && _map.TryGetValue(new Kobian.Engine.Backup.ChunkKey(chunkId), out location); // inna długość = nie ma takiego bloku
    }

    public async Task<byte[]> ReadBlockAsync(byte[] chunkId, BlobType expectedType, CancellationToken ct)
    {
        if (!TryLocate(chunkId, out var loc) || loc is null)
            throw new KobianException(KbCode.ReferenceIntegrityFailed, ErrorClass.Integrity, "Block not found in snapshot indexes.");
        if (loc.Entry.BlobType != expectedType) throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Unexpected blob type.");
        PackBlob blob;
        if (UseRangeReads && loc.EntryIndex >= 0 && (session.Capabilities.Caps & StorageCaps.RangeRead) != 0 && RangePlausible(loc))
        {
            blob = await ReadFrameAsync(loc, ct).ConfigureAwait(false);
        }
        else
        {
            var reader = await OpenPackAsync(loc, ct).ConfigureAwait(false);
            var i = FindEntry(reader, chunkId);
            blob = reader.ReadBlob(i);
        }
        if (blob.BlobType != expectedType) throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Unexpected blob type.");
        var max = expectedType == BlobType.Tree ? PackLayout.MaxRawLengthTree : PackLayout.MaxRawLengthData;
        var raw = _zstd.Decompress(blob.Algorithm, blob.Payload, blob.RawLength, max);
        if (!CryptographicOperations.FixedTimeEquals(keys.ChunkId(raw), chunkId))
            throw new KobianException(KbCode.AuthenticationFailed, ErrorClass.Integrity, "Block content does not match its identifier.");
        return raw;
    }

    public async Task<IReadOnlyList<TreeEntry>> ReadTreeAsync(byte[] treeId, CancellationToken ct) =>
        TreeCodec.Decode(await ReadBlockAsync(treeId, BlobType.Tree, ct).ConfigureAwait(false));

    private int FindEntry(PackReader reader, byte[] chunkId)
    {
        var cached = _packs.FirstOrDefault(p => ReferenceEquals(p.Reader, reader));
        if (cached is null)
        {
            for (var i = 0; i < reader.Entries.Count; i++)
                if (reader.Entries[i].ChunkId.AsSpan().SequenceEqual(chunkId)) return i;
        }
        else
        {
            if (cached.ByChunk is null)
            {
                var map = new Dictionary<string, int>(reader.Entries.Count, StringComparer.Ordinal);
                for (var i = 0; i < reader.Entries.Count; i++) map.TryAdd(Convert.ToHexString(reader.Entries[i].ChunkId), i); // pierwszy wpis wygrywa, jak w skanie liniowym
                cached.ByChunk = map;
            }
            if (cached.ByChunk.TryGetValue(Convert.ToHexString(chunkId), out var found)) return found;
        }
        throw new KobianException(KbCode.ReferenceIntegrityFailed, ErrorClass.Integrity, "Block missing from pack trailer.");
    }

    /// <summary>Wpis z indeksu musi mieścić się w packu (indeks jest uwierzytelniony, ale to tanie zabezpieczenie przed odczytem poza plikiem).</summary>
    private static bool RangePlausible(BlockLocation loc)
    {
        var e = loc.Entry;
        return e.Offset >= PackLayout.HeaderSize && e.FrameLength is > 0 and <= PackLayout.MaxFrameLength && e.Offset + PackLayout.FrameLengthSize + e.FrameLength <= loc.PackSize;
    }

    private async Task<PackBlob> ReadFrameAsync(BlockLocation loc, CancellationToken ct)
    {
        var e = loc.Entry;
        var total = PackLayout.FrameLengthSize + e.FrameLength;
        var buf = new byte[total];
        await using (var s = await session.OpenReadAsync(RepoPaths.Pack(loc.PackId), e.Offset, total, ct).ConfigureAwait(false))
            await s.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        if (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buf) != e.FrameLength)
            throw new KobianException(KbCode.PackLayoutInvalid, ErrorClass.Integrity, "Frame length mismatch.");
        var hex = Convert.ToHexString(loc.PackId);
        if (!_frameCiphers.TryGetValue(hex, out var cipher))
        {
            if (_frameCiphers.Count >= 16) // niewiele packów naraz; reszta wyprowadzana ponownie (HKDF jest tani)
            {
                foreach (var c in _frameCiphers.Values) c.Dispose();
                _frameCiphers.Clear();
            }
            var key = keys.DerivePackKey(loc.PackId);
            try { cipher = new AesGcmCipher(key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            _frameCiphers[hex] = cipher;
        }
        return PackReader.OpenFrame(cipher, keys.RepoId, loc.PackId, loc.EntryIndex, e, buf.AsSpan(PackLayout.FrameLengthSize).ToArray());
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
        while (_packs.Last is { } oldest && _packBytes + bytes.Length > PackCacheBudget)
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
        foreach (var c in _frameCiphers.Values) c.Dispose();
        _frameCiphers.Clear();
        _packs.Clear();
        _packBytes = 0;
    }
}
