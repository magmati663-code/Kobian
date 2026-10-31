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
    private readonly Compressor _probe = new(1);
    private readonly Decompressor _unzstd;
    private byte[] _out = []; // bufor wyjściowy kompresora: rośnie do największego bloku i znika razem z kompresorem (ArrayPool.Shared trzymał bufory 8 MiB poza kontrolą po przebiegu)

    /// <summary>Bloki poniżej tego rozmiaru kompresujemy bez próby (próba kosztuje tyle co kompresja).</summary>
    public const int ProbeMinBlock = 64 * 1024;

    private const int ProbeSlice = 4096;
    private const int ProbeSlices = 4;

    public BlockCompressor(int level = DefaultLevel)
    {
        _zstd = new Compressor(level);
        _unzstd = new Decompressor();
    }

    /// <summary>
    /// Szybka próba (runda 8 planu): cztery wycinki po 4 KiB (początek, 1/3, 2/3, koniec) skompresowane zstd poziom 1 — jeśli zysk &lt; ~3%, cały blok to dane
    /// nieściśliwe (zip, jpg, mp4, zaszyfrowane, losowe) i pomijamy kompresję poziomu 3 na 4 MiB. Pomyłka kosztuje co najwyżej miejsce (blok zapisany bez kompresji), nigdy poprawność.
    /// </summary>
    public bool LooksIncompressible(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < ProbeMinBlock) return false;
        const int total = ProbeSlice * ProbeSlices;
        Span<byte> sample = stackalloc byte[total];
        var last = raw.Length - ProbeSlice;
        for (var i = 0; i < ProbeSlices; i++) raw.Slice((int)((long)last * i / (ProbeSlices - 1)), ProbeSlice).CopyTo(sample.Slice(i * ProbeSlice, ProbeSlice));
        Span<byte> outBuf = stackalloc byte[Compressor.GetCompressBound(total)];
        var n = _probe.Wrap(sample, outBuf);
        return n >= total - (total >> 5);
    }

    /// <summary>Kompresuje; zwraca algorytm i ładunek. Gdy zysk &lt; 1/64 rozmiaru — zapis bez kompresji (oszczędność CPU przy odczycie).</summary>
    public (CompressionAlgorithm Algorithm, byte[] Payload) Compress(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0) return (CompressionAlgorithm.None, []);
        if (LooksIncompressible(raw)) return (CompressionAlgorithm.None, raw.ToArray());
        var bound = Compressor.GetCompressBound(raw.Length);
        if (_out.Length < bound) _out = new byte[bound]; // jedna tablica na kompresor, nie nowa ~4 MiB (LOH) na każdy blok
        var n = _zstd.Wrap(raw, _out);
        return n < raw.Length - (raw.Length >> 6)
            ? (CompressionAlgorithm.Zstd, _out.AsSpan(0, n).ToArray())
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
        _probe.Dispose();
        _unzstd.Dispose();
    }
}
