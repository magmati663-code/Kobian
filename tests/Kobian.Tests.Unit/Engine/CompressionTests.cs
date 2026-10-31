using System.Security.Cryptography;
using Kobian.Domain;
using Kobian.Engine.Compression;
using Kobian.Format.Pack;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

public sealed class CompressionTests
{
    [Fact]
    public void Compressible_block_roundtrips_with_zstd()
    {
        using var c = new BlockCompressor();
        var raw = new byte[1 << 20];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 13);
        var (algo, payload) = c.Compress(raw);
        Assert.Equal(CompressionAlgorithm.Zstd, algo);
        Assert.True(payload.Length < raw.Length / 10);
        Assert.Equal(raw, c.Decompress(algo, payload, raw.Length, PackLayout.MaxRawLengthData));
    }

    [Fact]
    public void Incompressible_block_is_stored_raw()
    {
        using var c = new BlockCompressor();
        var raw = RandomNumberGenerator.GetBytes(64 * 1024);
        var (algo, payload) = c.Compress(raw);
        Assert.Equal(CompressionAlgorithm.None, algo);
        Assert.Equal(raw, payload);
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Decompression_bomb_is_rejected()
    {
        using var c = new BlockCompressor();
        var (_, bomb) = c.Compress(new byte[4 * 1024 * 1024]); // 4 MiB zer → kilkaset bajtów
        var ex = Assert.Throws<KobianException>(() => c.Decompress(CompressionAlgorithm.Zstd, bomb, 1024, PackLayout.MaxRawLengthData));
        Assert.Equal(KbCode.BlockTooLarge, ex.Code);
        Assert.Throws<KobianException>(() => c.Decompress(CompressionAlgorithm.Zstd, bomb, PackLayout.MaxRawLengthData + 1, PackLayout.MaxRawLengthData));
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Garbage_compressed_payload_maps_to_domain_error()
    {
        using var c = new BlockCompressor();
        var rnd = new Random(5);
        for (var i = 0; i < 2000; i++)
        {
            var junk = new byte[rnd.Next(1, 200)];
            rnd.NextBytes(junk);
            if (i % 2 == 0) { junk[0] = 0x28; if (junk.Length > 3) { junk[1] = 0xB5; junk[2] = 0x2F; junk[3] = 0xFD; } } // magia zstd
            try
            {
                c.Decompress(CompressionAlgorithm.Zstd, junk, 100, PackLayout.MaxRawLengthData);
            }
            catch (KobianException)
            {
            }
        }
    }

    [Fact]
    public void Probe_skips_random_data_but_never_text_zeros_or_mixed_blocks()
    {
        using var c = new BlockCompressor();
        Assert.True(c.LooksIncompressible(RandomNumberGenerator.GetBytes(4 << 20)));
        Assert.False(c.LooksIncompressible(RandomNumberGenerator.GetBytes(BlockCompressor.ProbeMinBlock - 1))); // za mały na próbę — kompresja jak dotąd

        var zeros = new byte[4 << 20];
        Assert.False(c.LooksIncompressible(zeros));
        Assert.Equal(CompressionAlgorithm.Zstd, c.Compress(zeros).Algorithm);

        var text = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Zażółć gęślą jaźń — kopia zapasowa. ", 100_000)));
        Assert.False(c.LooksIncompressible(text));
        Assert.Equal(CompressionAlgorithm.Zstd, c.Compress(text).Algorithm);

        // blok „losowy z przodu, zera z tyłu” (np. archiwum z dopełnieniem): próbki z końca ratują kompresję
        var mixed = new byte[4 << 20];
        RandomNumberGenerator.Fill(mixed.AsSpan(0, 1 << 20));
        var (algo, payload) = c.Compress(mixed);
        Assert.Equal(CompressionAlgorithm.Zstd, algo);
        Assert.Equal(mixed, c.Decompress(algo, payload, mixed.Length, PackLayout.MaxRawLengthData));
    }

    [Fact]
    public void Probe_is_deterministic_and_incompressible_blocks_round_trip_raw()
    {
        using var c = new BlockCompressor();
        var raw = RandomNumberGenerator.GetBytes(4 << 20);
        var a = c.Compress(raw);
        var b = c.Compress(raw);
        Assert.Equal(CompressionAlgorithm.None, a.Algorithm);
        Assert.Equal(a.Algorithm, b.Algorithm);
        Assert.Equal(a.Payload, b.Payload);
        Assert.Equal(raw, c.Decompress(a.Algorithm, a.Payload, raw.Length, PackLayout.MaxRawLengthData));
    }
}
