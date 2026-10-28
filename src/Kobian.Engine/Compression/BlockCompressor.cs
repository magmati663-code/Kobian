using Kobian.Domain;
using Kobian.Format.Pack;
using ZstdSharp;

namespace Kobian.Engine.Compression;

/// <summary>
/// Kompresja bloków (D3.3): zstd poziom 3 (ZstdSharp.Port), „none”, gdy kompresja nie zmniejsza bloku.
/// Dekompresja z ochroną przed bombą: bufor wyjściowy = dokładnie rawLength (≤ limit R2.3.8), nadmiar = KB-1302.
/// </summary>
public sealed class BlockCompressor : IDisposable
{
    public const int DefaultLevel = 3;
    private readonly Compressor _zstd;
    private readonly Decompressor _unzstd;

    public BlockCompressor(int level = DefaultLevel)
    {
        _zstd = new Compressor(level);
        _unzstd = new Decompressor();
    }

    /// <summary>Kompresuje; zwraca algorytm i ładunek. Gdy zysk &lt; 1/64 rozmiaru — zapis bez kompresji (oszczędność CPU przy odczycie).</summary>
    public (CompressionAlgorithm Algorithm, byte[] Payload) Compress(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0) return (CompressionAlgorithm.None, []);
        var bound = Compressor.GetCompressBound(raw.Length);
        var buf = new byte[bound];
        var n = _zstd.Wrap(raw, buf);
        return n < raw.Length - (raw.Length >> 6)
            ? (CompressionAlgorithm.Zstd, buf.AsSpan(0, n).ToArray())
            : (CompressionAlgorithm.None, raw.ToArray());
    }

    public byte[] Decompress(CompressionAlgorithm algorithm, ReadOnlySpan<byte> payload, int rawLength, int maxRawLength)
    {
        if (rawLength < 0 || rawLength > maxRawLength)
            throw new KobianException(KbCode.BlockTooLarge, ErrorClass.Integrity, "Raw length exceeds limit.");
        switch (algorithm)
        {
            case CompressionAlgorithm.None:
                if (payload.Length != rawLength) throw new KobianException(KbCode.BlockTooLarge, ErrorClass.Integrity, "Uncompressed length mismatch.");
                return payload.ToArray();
            case CompressionAlgorithm.Zstd:
                var output = new byte[rawLength];
                int written;
                try
                {
                    // Bufor docelowy ma dokładnie rawLength — ramka deklarująca więcej kończy się błędem („bomba”).
                    if (!_unzstd.TryUnwrap(payload, output, out written))
                        throw new KobianException(KbCode.BlockTooLarge, ErrorClass.Integrity, "Decompressed data exceeds declared length.");
                }
                catch (ZstdException ex)
                {
                    throw new KobianException(KbCode.BlockTooLarge, ErrorClass.Integrity, "Invalid compressed block.", ex);
                }
                if (written != rawLength) throw new KobianException(KbCode.BlockTooLarge, ErrorClass.Integrity, "Decompressed length mismatch.");
                return output;
            default:
                throw new KobianException(KbCode.UnsupportedFeature, ErrorClass.Permanent, "Unsupported compression algorithm.");
        }
    }

    public void Dispose()
    {
        _zstd.Dispose();
        _unzstd.Dispose();
    }
}
