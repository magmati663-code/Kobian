using System.Text;

namespace Kobian.Format.Pack;

/// <summary>Stałe układu packa (D3.10) i limity parsera (R2.3.8, SEC-3.3).</summary>
public static class PackLayout
{
    public const ushort FormatVersion = 1;
    public const int HeaderSize = 64;
    public const int FooterSize = 16;
    public const int FrameLengthSize = 4;
    public const int BlobHeaderSize = 8;
    public const int IdSize = 16;
    public const int ChunkIdSize = 32;
    public const int CommitTagSize = 32;
    public const byte BlobVersion = 1;

    /// <summary>KB-1301: frameLength ≤ 8 MiB + 64.</summary>
    public const int MaxFrameLength = (8 * 1024 * 1024) + 64;
    /// <summary>KB-1308: ramek w packu ≤ 65 536.</summary>
    public const int MaxFrames = 65_536;
    /// <summary>KB-1302: rawLength danych ≤ 4 MiB.</summary>
    public const int MaxRawLengthData = 4 * 1024 * 1024;
    /// <summary>KB-1302: rawLength drzewa ≤ 16 MiB (R2.3.8; stronicowanie D3.18 w osobnym zadaniu).</summary>
    public const int MaxRawLengthTree = 16 * 1024 * 1024;
    /// <summary>Trailer: ≤ MaxFrames wpisów po ≤ ~60 B CBOR; twardy limit rozmiaru szyfrogramu trailera.</summary>
    public const int MaxTrailerLength = 8 * 1024 * 1024;

    public static ReadOnlySpan<byte> Magic => "KOBPACK1"u8;
    public static ReadOnlySpan<byte> FooterMagic => "KTRL"u8;
    public static ReadOnlySpan<byte> TrailerAadSuffix => "trailer"u8;

    internal static readonly Encoding Ascii = Encoding.ASCII;
}

public enum BlobType : byte
{
    Data = 1,
    Tree = 2,
}

public enum CompressionAlgorithm : byte
{
    None = 0,
    Zstd = 1,
    Brotli = 2,
}

/// <summary>Wpis trailera: [chunkId, offset, frameLength, blobType, rawLength] (D3.10). Offset wskazuje pole frameLength ramki.</summary>
public sealed record PackEntry(byte[] ChunkId, long Offset, int FrameLength, BlobType BlobType, int RawLength);

/// <summary>Odszyfrowany blob: nagłówek bloku (3.4) i ładunek (jeszcze skompresowany).</summary>
public sealed record PackBlob(BlobType BlobType, CompressionAlgorithm Algorithm, byte Flags, int RawLength, byte[] Payload);
