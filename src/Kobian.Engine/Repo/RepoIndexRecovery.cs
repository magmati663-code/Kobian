using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Format.Metadata;
using Kobian.Format.Pack;
using Kobian.Format.Repo;

namespace Kobian.Engine.Repo;

/// <summary>Wynik odbudowy indeksu: packi-sieroty (bez wpisu w żadnym czytelnym indeksie), odzyskane z trailerów, nieczytelne; nowy indeks (albo null).</summary>
public sealed record IndexRecoveryResult(int OrphanPacks, int RecoveredPacks, int UnreadablePacks, byte[]? IndexId);

/// <summary>
/// D8.10 „packsOnTarget \ indexed → rebuildFromTrailer(p) → nowy indeks recovered” (D3.10; it. 110): pack-sierota pobierany, stopka i commitTag (stały czas) oraz
/// trailer (AEAD) uwierzytelnione kluczami repozytorium (<see cref="PackReader"/>) — lista bloków z trailera trafia do NOWEGO obiektu indeksu (append-only,
/// sesja Backup tworzy tylko nowe obiekty; nic nie jest nadpisywane ani usuwane — SEC-18.5). Treść bloków jest i tak weryfikowana HMAC przy odczycie.
/// </summary>
public sealed class RepoIndexRecovery(IStorageSession session, RepoKeys keys, byte[] hostId)
{
    /// <summary>Największy pack, jaki zapisze zadanie: cel ≤ 512 MiB (JobDefinition.MaxPackTarget) + jedna ramka bloku ponad cel + trailer + nagłówek/stopka (+ zapas).
    /// it. 120 (przegląd rundy 8, Niskie): wcześniej 2 GiB — rozmiar z listingu celu (niezaufany) ponad Array.MaxLength kończył repair wyjątkiem spoza kontraktu,
    /// a pod nim wymuszał alokację do 2 GiB na jeden fałszywy pack.</summary>
    public const long MaxPackSize = (512L << 20) + PackLayout.FrameLengthSize + PackLayout.MaxFrameLength + PackLayout.MaxTrailerLength
                                    + PackLayout.HeaderSize + PackLayout.FooterSize + (64 * 1024);

    private readonly IStorageSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly RepoKeys _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly byte[] _hostId = hostId ?? throw new ArgumentNullException(nameof(hostId));

    public async Task<IndexRecoveryResult> RecoverAsync(bool write, CancellationToken ct)
    {
        var indexed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var o in _session.ListAsync(RepoPaths.IndexDir, ct).ConfigureAwait(false))
        {
            if (RepoPaths.TryParseId(o.Path, RepoPaths.IndexDir, ".idx") is not { } id) continue;
            try
            {
                var (idx, _) = await MetadataStore.LoadIndexAsync(_session, _keys, id, ct).ConfigureAwait(false);
                foreach (var pk in idx.Packs) indexed.Add(Convert.ToHexStringLower(pk.PackId));
            }
            catch (Exception ex) when (ex is KobianException or StorageException)
            {
                // indeks nieczytelny — jego packi wyjdą jako sieroty i zostaną odzyskane z trailerów
            }
        }
        var orphans = new List<(string Hex, long Length)>();
        await foreach (var o in _session.ListAsync(RepoPaths.DataDir, ct).ConfigureAwait(false))
            if (RepoChecker.TryParsePackPath(o.Path) is { } hex && !indexed.Contains(hex)) orphans.Add((hex, o.Length));
        orphans.Sort((a, b) => string.CompareOrdinal(a.Hex, b.Hex));

        var recovered = new List<IndexPack>();
        var unreadable = 0;
        foreach (var (hex, length) in orphans)
        {
            ct.ThrowIfCancellationRequested();
            if (length <= 0 || length > MaxPackSize)
            {
                unreadable++;
                continue;
            }
            var packId = Convert.FromHexString(hex);
            try
            {
                var bytes = await MetadataStore.ReadAllAsync(_session, RepoPaths.Pack(packId), length, ct).ConfigureAwait(false);
                var key = _keys.DerivePackKey(packId);
                AesGcmCipher cipher;
                try { cipher = new AesGcmCipher(key); }
                finally { CryptographicOperations.ZeroMemory(key); }
                using (cipher)
                {
                    var reader = PackReader.Open(new MemoryStream(bytes, writable: false), cipher, _keys.RepoId, packId, _keys.CommitTag(packId));
                    recovered.Add(new IndexPack(packId, SHA256.HashData(bytes), bytes.Length, [.. reader.Entries]));
                }
            }
            catch (Exception ex) when (ex is KobianException or StorageException)
            {
                unreadable++; // obcy/uszkodzony pack — nie ufamy jego zawartości
            }
        }
        byte[]? indexId = null;
        if (write && recovered.Count > 0)
        {
            indexId = RandomNumberGenerator.GetBytes(16);
            var sealedIdx = MetadataStore.Seal(_keys, MetadataKind.Index, indexId, IndexCodec.Encode(new IndexObject(recovered, [], _hostId, RandomNumberGenerator.GetBytes(16))));
            await new VerifiedUploader(_session).UploadAsync(RepoPaths.Index(indexId), sealedIdx, ct).ConfigureAwait(false);
        }
        return new IndexRecoveryResult(orphans.Count, recovered.Count, unreadable, indexId);
    }
}
