using System.Formats.Cbor;
using System.Security.Cryptography;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Format.Pack;
using Xunit;

namespace Kobian.Tests.Unit.Format;

/// <summary>SEC-3.3 / R2.3.8: czytnik packa wobec danych zmanipulowanych. Klucze losowe w teście (NFR-11).</summary>
public sealed class PackSecurityTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly byte[] RepoId = RandomNumberGenerator.GetBytes(16);
        public readonly byte[] PackId = RandomNumberGenerator.GetBytes(16);
        public readonly RepoKeys Keys;
        public readonly AesGcmCipher Cipher;
        public readonly byte[] Commit;

        public Fixture()
        {
            var mk = RandomNumberGenerator.GetBytes(32);
            Keys = RepoKeys.Derive(mk, RepoId);
            CryptographicOperations.ZeroMemory(mk);
            Cipher = new AesGcmCipher(Keys.DerivePackKey(PackId));
            Commit = Keys.CommitTag(PackId);
        }

        public byte[] BuildPack(int blobs = 3)
        {
            using var ms = new MemoryStream();
            var w = new PackWriter(ms, Cipher, RepoId, PackId, Commit);
            for (var i = 0; i < blobs; i++)
            {
                var data = RandomNumberGenerator.GetBytes(100 + i);
                w.AddBlob(Keys.ChunkId(data), BlobType.Data, CompressionAlgorithm.None, data.Length, data);
            }
            w.Finish();
            return ms.ToArray();
        }

        public PackReader Open(byte[] pack) => PackReader.Open(new MemoryStream(pack), Cipher, RepoId, PackId, Commit);

        public void Dispose()
        {
            Cipher.Dispose();
            Keys.Dispose();
        }
    }

    private static string CodeOf(Action a) => Assert.Throws<KobianException>(a).Code;

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Roundtrip_many_blobs()
    {
        using var f = new Fixture();
        var r = f.Open(f.BuildPack(50));
        Assert.Equal(50, r.Entries.Count);
        for (var i = 0; i < 50; i++) Assert.Equal(100 + i, r.ReadBlob(i).Payload.Length);
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Flipped_bit_in_frame_fails_authentication()
    {
        using var f = new Fixture();
        var pack = f.BuildPack();
        pack[PackLayout.HeaderSize + 4 + 10] ^= 0x01;
        var r = f.Open(pack);
        Assert.Equal(KbCode.AuthenticationFailed, CodeOf(() => r.ReadBlob(0)));
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Wrong_commit_tag_is_security_error_before_decryption()
    {
        using var f = new Fixture();
        var pack = f.BuildPack();
        var ex = Assert.Throws<KobianException>(() => PackReader.Open(new MemoryStream(pack), f.Cipher, f.RepoId, f.PackId, new byte[32]));
        Assert.Equal(KbCode.PackCommitMismatch, ex.Code);
        Assert.Equal(ErrorClass.Security, ex.ErrorClass);
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Pack_from_other_repository_is_rejected()
    {
        using var a = new Fixture();
        using var b = new Fixture();
        var pack = a.BuildPack();
        Assert.Equal(KbCode.PackLayoutInvalid, CodeOf(() => b.Open(pack))); // packId różny od nazwy pliku
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Swapping_frames_is_detected()
    {
        using var f = new Fixture();
        using var ms = new MemoryStream();
        var w = new PackWriter(ms, f.Cipher, f.RepoId, f.PackId, f.Commit);
        var d0 = new byte[64]; var d1 = new byte[64]; d1[0] = 1;
        w.AddBlob(f.Keys.ChunkId(d0), BlobType.Data, CompressionAlgorithm.None, 64, d0);
        w.AddBlob(f.Keys.ChunkId(d1), BlobType.Data, CompressionAlgorithm.None, 64, d1);
        w.Finish();
        var pack = ms.ToArray();
        var frame = 4 + 64 + 8 + 16;
        var a = pack.AsSpan(PackLayout.HeaderSize, frame).ToArray();
        pack.AsSpan(PackLayout.HeaderSize + frame, frame).CopyTo(pack.AsSpan(PackLayout.HeaderSize));
        a.CopyTo(pack.AsSpan(PackLayout.HeaderSize + frame));
        var r = f.Open(pack);
        Assert.Equal(KbCode.AuthenticationFailed, CodeOf(() => r.ReadBlob(0)));
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Footer_pointing_outside_file_is_rejected()
    {
        using var f = new Fixture();
        var pack = f.BuildPack();
        BitConverter.TryWriteBytes(pack.AsSpan(pack.Length - 16), ulong.MaxValue - 3);
        Assert.Equal(KbCode.PackLayoutInvalid, CodeOf(() => f.Open(pack)));
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Huge_blob_count_in_header_is_rejected()
    {
        using var f = new Fixture();
        var pack = f.BuildPack();
        BitConverter.TryWriteBytes(pack.AsSpan(12), (uint)PackLayout.MaxFrames + 1);
        Assert.Equal(KbCode.TooManyFrames, CodeOf(() => f.Open(pack)));
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Unknown_format_version_is_rejected_with_feature_code()
    {
        using var f = new Fixture();
        var pack = f.BuildPack();
        pack[8] = 2;
        Assert.Equal(KbCode.UnsupportedFeature, CodeOf(() => f.Open(pack)));
    }

    [Theory, Trait("SEC", "SEC-3.3"), Trait("V", "V-08")]
    [InlineData("81851840000000000000000000000000000000000000000000000000000000000000000001010101")] // bstr długości niekanonicznej
    [InlineData("9f8558200000000000000000000000000000000000000000000000000000000000000000000000000000ff")] // tablica nieokreślonej długości
    [InlineData("8182")] // wpis z 2 polami zamiast 5
    [InlineData("818558200000000000000000000000000000000000000000000000000000000000000000201864010101")] // regresja fuzzingu it. 11: offset ujemny (typ 1)
    [InlineData("818558200000000000000000000000000000000000000000000000000000000000000000000000818181818100")] // zagnieżdżenie zamiast liczby
    public void Non_canonical_or_malformed_trailer_cbor_is_rejected(string hex)
    {
        Assert.Equal(KbCode.MetadataMalformed, CodeOf(() => PackReader.DecodeTrailer(Convert.FromHexString(hex))));
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Trailer_with_oversized_frame_length_is_rejected()
    {
        var w = new CborWriter(CborConformanceMode.Canonical);
        w.WriteStartArray(1); w.WriteStartArray(5);
        w.WriteByteString(new byte[32]); w.WriteUInt64(64); w.WriteUInt64(PackLayout.MaxFrameLength + 1UL); w.WriteUInt64(1); w.WriteUInt64(1);
        w.WriteEndArray(); w.WriteEndArray();
        Assert.Equal(KbCode.FrameTooLarge, CodeOf(() => PackReader.DecodeTrailer(w.Encode())));
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Writer_rejects_raw_length_over_limit()
    {
        using var f = new Fixture();
        using var ms = new MemoryStream();
        var w = new PackWriter(ms, f.Cipher, f.RepoId, f.PackId, f.Commit);
        Assert.Equal(KbCode.BlockTooLarge, CodeOf(() => w.AddBlob(new byte[32], BlobType.Data, CompressionAlgorithm.Zstd, PackLayout.MaxRawLengthData + 1, new byte[10])));
    }
}
