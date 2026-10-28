using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using Kobian.Domain;

namespace Kobian.Format.Pack;

/// <summary>
/// Czytnik packa (D3.10) dla danych NIEZAUFANYCH: stopka → nagłówek → commitTag (stały czas) → trailer (AEAD)
/// → ścisła walidacja układu (ramki ciągłe, w kolejności, w granicach pliku). Limity R2.3.8.
/// </summary>
public sealed class PackReader
{
    private readonly Stream _in;
    private readonly IAuthenticatedCipher _cipher;
    private readonly byte[] _repoId;
    private readonly byte[] _packId;

    private PackReader(Stream input, IAuthenticatedCipher cipher, byte[] repoId, byte[] packId, IReadOnlyList<PackEntry> entries)
    {
        _in = input;
        _cipher = cipher;
        _repoId = repoId;
        _packId = packId;
        Entries = entries;
    }

    public IReadOnlyList<PackEntry> Entries { get; }

    public static PackReader Open(Stream input, IAuthenticatedCipher packCipher, ReadOnlySpan<byte> repoId, ReadOnlySpan<byte> expectedPackId, ReadOnlySpan<byte> expectedCommitTag)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(packCipher);
        if (!input.CanSeek || !input.CanRead) throw new ArgumentException("Pack input must be seekable and readable.", nameof(input));
        if (repoId.Length != PackLayout.IdSize || expectedPackId.Length != PackLayout.IdSize || expectedCommitTag.Length != PackLayout.CommitTagSize)
            throw new ArgumentException("Invalid identifier lengths.");

        var fileLength = input.Length;
        if (fileLength < PackLayout.HeaderSize + PackLayout.FrameLengthSize + packCipher.TagSize + PackLayout.FooterSize)
            throw Layout("Pack too short.");

        // Stopka
        Span<byte> footer = stackalloc byte[PackLayout.FooterSize];
        ReadAt(input, fileLength - PackLayout.FooterSize, footer);
        if (!footer[12..].SequenceEqual(PackLayout.FooterMagic)) throw Layout("Footer magic missing (incomplete pack).");
        var trailerOffset = BinaryPrimitives.ReadUInt64LittleEndian(footer);
        var trailerLen = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]);
        if (trailerLen < (uint)packCipher.TagSize || trailerLen > PackLayout.MaxTrailerLength) throw Layout("Trailer length out of range.");
        if (trailerOffset < PackLayout.HeaderSize || trailerOffset > (ulong)fileLength
            || trailerOffset + PackLayout.FrameLengthSize + trailerLen + PackLayout.FooterSize != (ulong)fileLength)
            throw Layout("Trailer position inconsistent with file length.");

        // Nagłówek
        Span<byte> header = stackalloc byte[PackLayout.HeaderSize];
        ReadAt(input, 0, header);
        if (!header[..8].SequenceEqual(PackLayout.Magic)) throw Layout("Bad pack magic.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (version != PackLayout.FormatVersion)
            throw new KobianException(KbCode.UnsupportedFeature, ErrorClass.Permanent, "Unsupported pack format version.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) != 0) throw Layout("Unknown pack flags.");
        var blobCount = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (blobCount > PackLayout.MaxFrames)
            throw new KobianException(KbCode.TooManyFrames, ErrorClass.Integrity, "Too many frames.");
        if (!header[16..32].SequenceEqual(expectedPackId)) throw Layout("Pack id does not match file name.");
        if (!CryptographicOperations.FixedTimeEquals(header[32..64], expectedCommitTag))
            throw new KobianException(KbCode.PackCommitMismatch, ErrorClass.Security, "Pack commit tag mismatch.");

        // Trailer
        Span<byte> lenBuf = stackalloc byte[4];
        ReadAt(input, (long)trailerOffset, lenBuf);
        if (BinaryPrimitives.ReadUInt32LittleEndian(lenBuf) != trailerLen) throw Layout("Trailer frame length mismatch.");
        var sealedTrailer = new byte[trailerLen];
        ReadAt(input, (long)trailerOffset + 4, sealedTrailer);
        var plain = new byte[trailerLen - packCipher.TagSize];
        Span<byte> nonce = stackalloc byte[12];
        nonce.Fill(0xFF);
        if (!packCipher.TryOpen(nonce, sealedTrailer, PackWriter.TrailerAad(repoId, expectedPackId), plain))
            throw new KobianException(KbCode.AuthenticationFailed, ErrorClass.Integrity, "Trailer authentication failed.");

        var entries = DecodeTrailer(plain);
        if (entries.Count != blobCount) throw Layout("blobCount does not match trailer.");
        ValidateContiguity(entries, (long)trailerOffset);
        return new PackReader(input, packCipher, repoId.ToArray(), expectedPackId.ToArray(), entries);
    }

    /// <summary>Odczyt i odszyfrowanie ramki <paramref name="index"/>; ładunek pozostaje skompresowany.</summary>
    public PackBlob ReadBlob(int index)
    {
        if ((uint)index >= (uint)Entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var e = Entries[index];
        Span<byte> lenBuf = stackalloc byte[4];
        ReadAt(_in, e.Offset, lenBuf);
        if (BinaryPrimitives.ReadInt32LittleEndian(lenBuf) != e.FrameLength) throw Layout("Frame length mismatch.");
        var sealedBuf = new byte[e.FrameLength];
        ReadAt(_in, e.Offset + 4, sealedBuf);
        var plain = new byte[e.FrameLength - _cipher.TagSize];
        Span<byte> nonce = stackalloc byte[12];
        PackWriter.NonceForBlob((ulong)index, nonce);
        Span<byte> aad = stackalloc byte[PackWriter.FrameAadSize];
        PackWriter.FrameAad(_repoId, _packId, (ulong)index, e.ChunkId, aad);
        if (!_cipher.TryOpen(nonce, sealedBuf, aad, plain))
            throw new KobianException(KbCode.AuthenticationFailed, ErrorClass.Integrity, "Frame authentication failed.");
        try
        {
            if (plain.Length < PackLayout.BlobHeaderSize || plain[0] != PackLayout.BlobVersion) throw Layout("Bad blob header.");
            var type = (BlobType)plain[1];
            var algo = (CompressionAlgorithm)plain[2];
            if (!Enum.IsDefined(type) || type != e.BlobType) throw Layout("Blob type mismatch.");
            if (!Enum.IsDefined(algo)) throw Layout("Unknown compression algorithm.");
            var raw = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(4));
            if (raw != (uint)e.RawLength) throw Layout("rawLength mismatch.");
            var payload = plain.AsSpan(PackLayout.BlobHeaderSize).ToArray();
            if (algo == CompressionAlgorithm.None && payload.Length != e.RawLength) throw Layout("Uncompressed length mismatch.");
            return new PackBlob(type, algo, plain[3], e.RawLength, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static List<PackEntry> DecodeTrailer(byte[] cbor)
    {
        try
        {
            var r = new CborReader(cbor, CborConformanceMode.Canonical);
            var count = r.ReadStartArray() ?? throw Malformed("Indefinite-length array.");
            if (count > PackLayout.MaxFrames)
                throw new KobianException(KbCode.TooManyFrames, ErrorClass.Integrity, "Too many trailer entries.");
            var list = new List<PackEntry>(count);
            for (var i = 0; i < count; i++)
            {
                if (r.ReadStartArray() != 5) throw Malformed("Trailer entry must have 5 fields.");
                var chunkId = r.ReadByteString();
                if (chunkId.Length != PackLayout.ChunkIdSize) throw Malformed("chunkId length.");
                var offset = r.ReadUInt64();
                var frameLength = r.ReadUInt64();
                var blobType = r.ReadUInt64();
                var rawLength = r.ReadUInt64();
                r.ReadEndArray();
                if (frameLength > PackLayout.MaxFrameLength)
                    throw new KobianException(KbCode.FrameTooLarge, ErrorClass.Integrity, "Frame too large.");
                if (blobType is not ((ulong)BlobType.Data or (ulong)BlobType.Tree)) throw Malformed("Unknown blob type.");
                var maxRaw = blobType == (ulong)BlobType.Tree ? PackLayout.MaxRawLengthTree : PackLayout.MaxRawLengthData;
                if (rawLength > (ulong)maxRaw)
                    throw new KobianException(KbCode.BlockTooLarge, ErrorClass.Integrity, "Raw length too large.");
                if (offset > long.MaxValue) throw Malformed("Offset out of range.");
                list.Add(new PackEntry(chunkId, (long)offset, (int)frameLength, (BlobType)blobType, (int)rawLength));
            }
            r.ReadEndArray();
            if (r.BytesRemaining != 0) throw Malformed("Trailing bytes after trailer.");
            return list;
        }
        catch (CborContentException ex)
        {
            throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Trailer CBOR invalid or non-canonical.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Trailer CBOR has unexpected structure.", ex);
        }
        catch (OverflowException ex)
        {
            // znalezisko fuzzingu it. 11: liczba ujemna (typ główny 1) w miejscu uint → OverflowException z CborReader
            throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Trailer CBOR integer out of range.", ex);
        }
    }

    private static void ValidateContiguity(List<PackEntry> entries, long trailerOffset)
    {
        long expected = PackLayout.HeaderSize;
        foreach (var e in entries)
        {
            if (e.Offset != expected || e.FrameLength < PackLayout.BlobHeaderSize + 16) throw Layout("Frames not contiguous.");
            expected = checked(expected + PackLayout.FrameLengthSize + e.FrameLength);
        }
        if (expected != trailerOffset) throw Layout("Frames do not end at trailer.");
    }

    private static void ReadAt(Stream s, long offset, Span<byte> buffer)
    {
        s.Position = offset;
        s.ReadExactly(buffer);
    }

    private static KobianException Layout(string message) => new(KbCode.PackLayoutInvalid, ErrorClass.Integrity, message);

    private static KobianException Malformed(string message) => new(KbCode.MetadataMalformed, ErrorClass.Integrity, message);
}
