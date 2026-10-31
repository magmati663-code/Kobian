using System.Buffers.Binary;
using Kobian.Domain;

namespace Kobian.Format.Metadata;

public enum MetadataKind
{
    Index = 1,
    Snapshot = 2,
}

/// <summary>
/// Obiekt metadanych (R2.3.5): nagłówek 48 B jawny (magic ‖ u16le formatVersion ‖ u16le flags ‖ u32 reserved ‖ objectId[16] ‖ nonce[12] ‖ reserved[4])
/// ‖ AES-256-GCM(K_obj, nonce, AAD = nagłówek ‖ repoId, plaintext). Plaintext = zstd(CBOR) — kompresję wykonuje Engine.
/// Szyfr <paramref name="cipher"/> musi używać K_obj wyprowadzonego dla tego objectId i typu (D3.9).
/// </summary>
public static class MetadataEnvelope
{
    public const int HeaderSize = 48;
    public const int MaxPayload = 256 * 1024 * 1024;

    public static ReadOnlySpan<byte> MagicIndex => "KOBIDX01"u8;
    public static ReadOnlySpan<byte> MagicSnapshot => "KOBSNP01"u8;

    public static byte[] Seal(MetadataKind kind, ReadOnlySpan<byte> objectId, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> repoId, IAuthenticatedCipher cipher, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        if (objectId.Length != 16 || nonce.Length != 12 || repoId.Length != 16) throw new ArgumentException("Invalid identifier lengths.");
        if (plaintext.Length > MaxPayload) throw new KobianException(KbCode.MetadataTooLarge, ErrorClass.Permanent, "Metadata object too large.");
        var output = new byte[HeaderSize + plaintext.Length + cipher.TagSize];
        var header = output.AsSpan(0, HeaderSize);
        (kind == MetadataKind.Index ? MagicIndex : MagicSnapshot).CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], 1);
        objectId.CopyTo(header[16..]);
        nonce.CopyTo(header[32..]);
        cipher.Seal(nonce, plaintext, Aad(header, repoId), output.AsSpan(HeaderSize));
        return output;
    }

    public static byte[] Open(ReadOnlySpan<byte> data, MetadataKind expectedKind, ReadOnlySpan<byte> expectedObjectId, ReadOnlySpan<byte> repoId, IAuthenticatedCipher cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        if (data.Length < HeaderSize + cipher.TagSize) throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Metadata object too short.");
        if (data.Length - HeaderSize - cipher.TagSize > MaxPayload) throw new KobianException(KbCode.MetadataTooLarge, ErrorClass.Integrity, "Metadata object too large.");
        var header = data[..HeaderSize];
        var magic = expectedKind == MetadataKind.Index ? MagicIndex : MagicSnapshot;
        if (!header[..8].SequenceEqual(magic)) throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Wrong metadata magic.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != 1)
            throw new KobianException(KbCode.UnsupportedFeature, ErrorClass.Permanent, "Unsupported metadata format version.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) != 0 || !header[12..16].SequenceEqual(stackalloc byte[4]) || !header[44..48].SequenceEqual(stackalloc byte[4]))
            throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Reserved fields must be zero.");
        if (!header[16..32].SequenceEqual(expectedObjectId)) throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Object id does not match name.");
        var plain = new byte[data.Length - HeaderSize - cipher.TagSize];
        if (!cipher.TryOpen(header[32..44], data[HeaderSize..], Aad(header, repoId), plain))
            throw new KobianException(KbCode.AuthenticationFailed, ErrorClass.Integrity, "Metadata authentication failed.");
        return plain;
    }

    private static byte[] Aad(ReadOnlySpan<byte> header, ReadOnlySpan<byte> repoId)
    {
        var aad = new byte[HeaderSize + 16];
        header.CopyTo(aad);
        repoId.CopyTo(aad.AsSpan(HeaderSize));
        return aad;
    }
}
