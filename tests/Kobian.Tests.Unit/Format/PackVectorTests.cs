using System.Text.Json;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Format.Pack;
using Xunit;

namespace Kobian.Tests.Unit.Format;

/// <summary>Wektory V-01, V-02, V-05, V-06 (R2.3.8) wygenerowane niezależną implementacją tools/vectors/ref_v1.py.</summary>
public sealed class PackVectorTests
{
    private static readonly JsonElement V = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "v1.json"))).RootElement;

    private static byte[] Hex(JsonElement e) => Convert.FromHexString(e.GetString()!);
    private static byte[] In(string name) => Hex(V.GetProperty("inputs").GetProperty(name));
    private static byte[] V01(string name) => Hex(V.GetProperty("V-01").GetProperty(name));

    private static RepoKeys Keys() => RepoKeys.Derive(In("MK"), In("repoId"));

    [Fact, Trait("V", "V-01")]
    public void V01_key_hierarchy_matches_reference()
    {
        using var keys = Keys();
        Assert.Equal(V01("K_data"), keys.KData.ToArray());
        Assert.Equal(V01("K_meta"), keys.KMeta.ToArray());
        Assert.Equal(V01("K_id"), keys.KId.ToArray());
        Assert.Equal(V01("K_mac"), keys.KMac.ToArray());
        Assert.Equal(V01("K_pack"), keys.DerivePackKey(In("packId")));
        Assert.Equal(V01("commitTag"), keys.CommitTag(In("packId")));
        Assert.Equal(V01("K_obj_index"), keys.DeriveObjectKey(In("objectId"), MetadataObjectType.Index));
        Assert.Equal(V01("K_obj_snapshot"), keys.DeriveObjectKey(In("objectId"), MetadataObjectType.Snapshot));
    }

    [Fact, Trait("V", "V-02")]
    public void V02_nonce_aad_and_frame_ciphertext_match_reference()
    {
        using var keys = Keys();
        using var cipher = new AesGcmCipher(keys.DerivePackKey(In("packId")));
        foreach (var c in V.GetProperty("V-02").EnumerateArray())
        {
            var i = c.GetProperty("i").GetUInt64();
            var nonce = new byte[12];
            Nonces.Blob(i, nonce);
            Assert.Equal(Hex(c.GetProperty("nonce")), nonce);
            var chunkId = Hex(c.GetProperty("chunkId"));
            var plaintext = Hex(c.GetProperty("plaintext"));
            Assert.Equal(chunkId, keys.ChunkId(plaintext.AsSpan(8)));
            var aad = new byte[PackWriter.FrameAadSize];
            PackWriter.FrameAad(In("repoId"), In("packId"), i, chunkId, aad);
            Assert.Equal(Hex(c.GetProperty("aad")), aad);
            var sealedBuf = new byte[plaintext.Length + 16];
            cipher.Seal(nonce, plaintext, aad, sealedBuf);
            Assert.Equal(Hex(c.GetProperty("sealed")), sealedBuf);
        }
    }

    [Fact, Trait("V", "V-05")]
    public void V05_writer_produces_reference_bytes()
    {
        using var keys = Keys();
        using var cipher = new AesGcmCipher(keys.DerivePackKey(In("packId")));
        var v05 = V.GetProperty("V-05");
        var entries = v05.GetProperty("entries").EnumerateArray().ToArray();
        var payloads = v05.GetProperty("payloads").EnumerateArray().Select(Hex).ToArray();
        using var ms = new MemoryStream();
        var w = new PackWriter(ms, cipher, In("repoId"), In("packId"), keys.CommitTag(In("packId")));
        var algos = new[] { CompressionAlgorithm.None, CompressionAlgorithm.Zstd, CompressionAlgorithm.None };
        for (var i = 0; i < 3; i++)
        {
            var e = entries[i];
            w.AddBlob(Hex(e[0]), (BlobType)e[3].GetInt32(), algos[i], e[4].GetInt32(), payloads[i]);
        }
        w.Finish();
        Assert.Equal(Hex(v05.GetProperty("pack")), ms.ToArray());
    }

    [Fact, Trait("V", "V-05")]
    public void V05_reader_parses_reference_pack()
    {
        using var keys = Keys();
        using var cipher = new AesGcmCipher(keys.DerivePackKey(In("packId")));
        var v05 = V.GetProperty("V-05");
        using var ms = new MemoryStream(Hex(v05.GetProperty("pack")));
        var r = PackReader.Open(ms, cipher, In("repoId"), In("packId"), keys.CommitTag(In("packId")));
        Assert.Equal(3, r.Entries.Count);
        var payloads = v05.GetProperty("payloads").EnumerateArray().Select(Hex).ToArray();
        for (var i = 0; i < 3; i++)
            Assert.Equal(payloads[i], r.ReadBlob(i).Payload);
        Assert.Equal(CompressionAlgorithm.Zstd, r.ReadBlob(1).Algorithm);
        Assert.Equal(BlobType.Tree, r.ReadBlob(2).BlobType);
    }

    [Fact, Trait("V", "V-06")]
    public void V06_truncated_pack_is_rejected_as_incomplete()
    {
        using var keys = Keys();
        using var cipher = new AesGcmCipher(keys.DerivePackKey(In("packId")));
        using var ms = new MemoryStream(Hex(V.GetProperty("V-06").GetProperty("packTruncated")));
        var ex = Assert.Throws<KobianException>(() => PackReader.Open(ms, cipher, In("repoId"), In("packId"), keys.CommitTag(In("packId"))));
        Assert.Equal(KbCode.PackLayoutInvalid, ex.Code);
    }
}
