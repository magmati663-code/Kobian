using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Format.Metadata;
using Kobian.Format.Repo;
using ZstdSharp;

namespace Kobian.Engine.Repo;

/// <summary>
/// Zapis i odczyt obiektów metadanych (R2.3.5): plaintext = zstd(CBOR), koperta AES-256-GCM z K_obj (D3.9), losowy nonce.
/// Dekompresja ograniczona <see cref="MetadataEnvelope.MaxPayload"/> (ochrona przed bombą — SEC-3.3).
/// </summary>
public static class MetadataStore
{
    public static byte[] Seal(RepoKeys keys, MetadataKind kind, byte[] objectId, byte[] cbor)
    {
        ArgumentNullException.ThrowIfNull(keys);
        byte[] compressed;
        using (var z = new Compressor(3)) compressed = z.Wrap(cbor).ToArray();
        var key = keys.DeriveObjectKey(objectId, kind == MetadataKind.Index ? MetadataObjectType.Index : MetadataObjectType.Snapshot);
        try
        {
            using var cipher = new AesGcmCipher(key);
            return MetadataEnvelope.Seal(kind, objectId, RandomNumberGenerator.GetBytes(12), keys.RepoId, cipher, compressed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] Open(RepoKeys keys, MetadataKind kind, byte[] objectId, byte[] sealedObject)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var key = keys.DeriveObjectKey(objectId, kind == MetadataKind.Index ? MetadataObjectType.Index : MetadataObjectType.Snapshot);
        byte[] compressed;
        try
        {
            using var cipher = new AesGcmCipher(key);
            compressed = MetadataEnvelope.Open(sealedObject, kind, objectId, keys.RepoId, cipher);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        try
        {
            var size = Decompressor.GetDecompressedSize(compressed);
            if (size > (ulong)MetadataEnvelope.MaxPayload)
                throw new KobianException(KbCode.MetadataTooLarge, ErrorClass.Integrity, "Metadata declares too large size.");
            var output = new byte[(int)size];
            using var dz = new Decompressor();
            if (!dz.TryUnwrap(compressed, output, out var written) || written != output.Length)
                throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Metadata decompression mismatch.");
            return output;
        }
        catch (ZstdException ex)
        {
            throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Metadata compression invalid.", ex);
        }
    }

    public static async Task<byte[]> ReadAllAsync(IStorageSession session, string path, long maxLength, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var st = await session.StatAsync(path, ct).ConfigureAwait(false)
                 ?? throw new KobianException(KbCode.ReferenceIntegrityFailed, ErrorClass.Integrity, "Repository object missing.");
        // it. 120 (przegląd rundy 8, Niskie): rozmiar z celu jest niezaufany — powyżej Array.MaxLength alokacja dawała OverflowException/OutOfMemoryException
        // (poza kontraktem KobianException/StorageException), a obiekt krótszy niż zgłoszony — EndOfStreamException.
        if (st.Length > maxLength || st.Length < 0 || st.Length > Array.MaxLength)
            throw new KobianException(KbCode.MetadataTooLarge, ErrorClass.Integrity, "Repository object too large.");
        var buf = new byte[st.Length];
        await using var s = await session.OpenReadAsync(path, 0, st.Length, ct).ConfigureAwait(false);
        try
        {
            await s.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Repository object shorter than its reported size.", ex);
        }
        return buf;
    }

    public static async Task<(IndexObject Index, byte[] Sha256)> LoadIndexAsync(IStorageSession session, RepoKeys keys, byte[] indexId, CancellationToken ct)
    {
        var raw = await ReadAllAsync(session, RepoPaths.Index(indexId), MetadataEnvelope.MaxPayload, ct).ConfigureAwait(false);
        return (IndexCodec.Decode(Open(keys, MetadataKind.Index, indexId, raw)), SHA256.HashData(raw));
    }

    public static async Task<SnapshotObject> LoadSnapshotAsync(IStorageSession session, RepoKeys keys, byte[] snapId, CancellationToken ct)
    {
        var raw = await ReadAllAsync(session, RepoPaths.Snapshot(snapId), MetadataEnvelope.MaxPayload, ct).ConfigureAwait(false);
        var s = SnapshotCodec.Decode(Open(keys, MetadataKind.Snapshot, snapId, raw));
        if (!s.SnapId.AsSpan().SequenceEqual(snapId)) throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Snapshot id mismatch.");
        return s;
    }
}
