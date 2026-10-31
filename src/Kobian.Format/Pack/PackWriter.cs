using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using Kobian.Domain;

namespace Kobian.Format.Pack;

/// <summary>
/// Zapis packa wg D3.10: nagłówek 64 B (blobCount uzupełniany przy <see cref="Finish"/>, dlatego strumień musi być
/// przeszukiwalny), ramki AEAD, zaszyfrowany trailer, stopka 16 B. Nie kompresuje — ładunek przychodzi gotowy.
/// </summary>
public sealed class PackWriter
{
    private readonly Stream _out;
    private readonly IAuthenticatedCipher _cipher;
    private readonly byte[] _repoId;
    private readonly byte[] _packId;
    private readonly long _start;
    private readonly List<PackEntry> _entries = [];
    private bool _finished;

    private readonly PackScratch? _scratch;

    /// <param name="scratch">Opcjonalne bufory robocze współdzielone przez kolejne packi tego samego przebiegu (bez dwóch nowych tablic rozmiaru bloku na każdy blok).</param>
    public PackWriter(Stream output, IAuthenticatedCipher packCipher, ReadOnlySpan<byte> repoId, ReadOnlySpan<byte> packId, ReadOnlySpan<byte> commitTag, PackScratch? scratch = null)
    {
        _scratch = scratch;
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(packCipher);
        if (!output.CanSeek || !output.CanWrite) throw new ArgumentException("Pack output must be seekable and writable.", nameof(output));
        if (repoId.Length != PackLayout.IdSize) throw new ArgumentException("repoId must be 16 bytes.", nameof(repoId));
        if (packId.Length != PackLayout.IdSize) throw new ArgumentException("packId must be 16 bytes.", nameof(packId));
        if (commitTag.Length != PackLayout.CommitTagSize) throw new ArgumentException("commitTag must be 32 bytes.", nameof(commitTag));
        _out = output;
        _cipher = packCipher;
        _repoId = repoId.ToArray();
        _packId = packId.ToArray();
        _start = output.Position;

        Span<byte> header = stackalloc byte[PackLayout.HeaderSize];
        header.Clear();
        PackLayout.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], PackLayout.FormatVersion);
        // flags (10..12) = 0, blobCount (12..16) uzupełniany w Finish
        packId.CopyTo(header[16..]);
        commitTag.CopyTo(header[32..]);
        _out.Write(header);
    }

    public int BlobCount => _entries.Count;

    /// <summary>Bieżący rozmiar packa w bajtach (do decyzji o zamknięciu przy targetSize).</summary>
    public long Length => _out.Position - _start;

    public void AddBlob(ReadOnlySpan<byte> chunkId, BlobType blobType, CompressionAlgorithm algorithm, int rawLength, ReadOnlySpan<byte> payload)
    {
        if (_finished) throw new InvalidOperationException("Pack already finished.");
        if (chunkId.Length != PackLayout.ChunkIdSize) throw new ArgumentException("chunkId must be 32 bytes.", nameof(chunkId));
        if (!Enum.IsDefined(blobType)) throw new ArgumentOutOfRangeException(nameof(blobType));
        if (!Enum.IsDefined(algorithm)) throw new ArgumentOutOfRangeException(nameof(algorithm));
        var maxRaw = blobType == BlobType.Tree ? PackLayout.MaxRawLengthTree : PackLayout.MaxRawLengthData;
        if (rawLength < 0 || rawLength > maxRaw)
            throw new KobianException(KbCode.BlockTooLarge, ErrorClass.Permanent, "Block raw length exceeds limit.");
        if (algorithm == CompressionAlgorithm.None && payload.Length != rawLength)
            throw new ArgumentException("Uncompressed payload length must equal rawLength.", nameof(payload));
        if (_entries.Count >= PackLayout.MaxFrames)
            throw new KobianException(KbCode.TooManyFrames, ErrorClass.Permanent, "Too many frames in pack.");

        var plainLen = PackLayout.BlobHeaderSize + payload.Length;
        var frameLength = checked(plainLen + _cipher.TagSize);
        if (frameLength > PackLayout.MaxFrameLength)
            throw new KobianException(KbCode.FrameTooLarge, ErrorClass.Permanent, "Frame exceeds limit.");

        var index = (ulong)_entries.Count;
        // Bufory robocze przebiegu (jeśli podano) zamiast dwóch nowych tablic ~rozmiaru bloku (LOH) na KAŻDY blok; jawny tekst czyszczony po każdym bloku.
        var plainArr = _scratch?.Plain(plainLen) ?? new byte[plainLen];
        var sealedArr = _scratch?.Sealed(frameLength) ?? new byte[frameLength];
        var plain = plainArr.AsSpan(0, plainLen);
        var sealedBuf = sealedArr.AsSpan(0, frameLength);
        try
        {
            plain[0] = PackLayout.BlobVersion;
            plain[1] = (byte)blobType;
            plain[2] = (byte)algorithm;
            plain[3] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(plain[4..], (uint)rawLength);
            payload.CopyTo(plain[PackLayout.BlobHeaderSize..]);

            Span<byte> nonce = stackalloc byte[12];
            NonceForBlob(index, nonce);
            Span<byte> aad = stackalloc byte[FrameAadSize];
            FrameAad(_repoId, _packId, index, chunkId, aad);
            _cipher.Seal(nonce, plain, aad, sealedBuf);

            var offset = Length;
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, frameLength);
            _out.Write(len);
            _out.Write(sealedBuf);
            _entries.Add(new PackEntry(chunkId.ToArray(), offset, frameLength, blobType, rawLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Zapisuje trailer i stopkę, uzupełnia blobCount. Zwraca wpisy (do indeksu).</summary>
    public IReadOnlyList<PackEntry> Finish()
    {
        if (_finished) throw new InvalidOperationException("Pack already finished.");
        _finished = true;

        var cbor = EncodeTrailer(_entries);
        var sealedBuf = new byte[cbor.Length + _cipher.TagSize];
        Span<byte> nonce = stackalloc byte[12];
        nonce.Fill(0xFF);
        _cipher.Seal(nonce, cbor, TrailerAad(_repoId, _packId), sealedBuf);

        var trailerOffset = Length;
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, sealedBuf.Length);
        _out.Write(len);
        _out.Write(sealedBuf);

        Span<byte> footer = stackalloc byte[PackLayout.FooterSize];
        BinaryPrimitives.WriteUInt64LittleEndian(footer, (ulong)trailerOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[8..], (uint)sealedBuf.Length);
        PackLayout.FooterMagic.CopyTo(footer[12..]);
        _out.Write(footer);

        var end = _out.Position;
        _out.Position = _start + 12;
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)_entries.Count);
        _out.Write(count);
        _out.Position = end;
        _out.Flush();
        return _entries.AsReadOnly();
    }

    internal const int FrameAadSize = PackLayout.IdSize + PackLayout.IdSize + 8 + PackLayout.ChunkIdSize + 2;

    /// <summary>AAD ramki = repoId ‖ packId ‖ u64le(i) ‖ chunkId ‖ u16le(formatVersion).</summary>
    internal static void FrameAad(ReadOnlySpan<byte> repoId, ReadOnlySpan<byte> packId, ulong index, ReadOnlySpan<byte> chunkId, Span<byte> aad)
    {
        repoId.CopyTo(aad);
        packId.CopyTo(aad[16..]);
        BinaryPrimitives.WriteUInt64LittleEndian(aad[32..], index);
        chunkId.CopyTo(aad[40..]);
        BinaryPrimitives.WriteUInt16LittleEndian(aad[72..], PackLayout.FormatVersion);
    }

    internal static byte[] TrailerAad(ReadOnlySpan<byte> repoId, ReadOnlySpan<byte> packId)
    {
        var aad = new byte[32 + PackLayout.TrailerAadSuffix.Length];
        repoId.CopyTo(aad);
        packId.CopyTo(aad.AsSpan(16));
        PackLayout.TrailerAadSuffix.CopyTo(aad.AsSpan(32));
        return aad;
    }

    /// <summary>Nonce blobu: 00 00 00 00 ‖ u64be(i) (D3.9) — powielone z Kobian.Crypto.Nonces (brak referencji Format→Crypto).</summary>
    internal static void NonceForBlob(ulong index, Span<byte> nonce)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], index);
    }

    internal static byte[] EncodeTrailer(IReadOnlyList<PackEntry> entries)
    {
        var w = new CborWriter(CborConformanceMode.Canonical);
        w.WriteStartArray(entries.Count);
        foreach (var e in entries)
        {
            w.WriteStartArray(5);
            w.WriteByteString(e.ChunkId);
            w.WriteUInt64((ulong)e.Offset);
            w.WriteUInt64((ulong)e.FrameLength);
            w.WriteUInt64((byte)e.BlobType);
            w.WriteUInt64((ulong)e.RawLength);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        return w.Encode();
    }
}
