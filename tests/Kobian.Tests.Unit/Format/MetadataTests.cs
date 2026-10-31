using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text.Json;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Format.Metadata;
using Xunit;

namespace Kobian.Tests.Unit.Format;

public sealed class TreeCodecTests
{
    private static readonly JsonElement V = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "v1.json"))).RootElement;

    private static List<TreeEntry> V07Entries()
    {
        var names = V.GetProperty("V-07").GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToList();
        var list = new List<TreeEntry>();
        for (var i = 0; i < names.Count; i++)
        {
            var n = names[i];
            var dir = n.StartsWith("📁", StringComparison.Ordinal);
            list.Add(new TreeEntry(n, dir ? EntryKind.Directory : EntryKind.File, dir ? 0 : 1000 * i, 1_760_000_000_000_000_000 + i, Attributes: 32,
                Chunks: dir ? null : [Enumerable.Repeat((byte)i, 32).ToArray()],
                SubtreeId: dir ? Enumerable.Repeat((byte)0x55, 32).ToArray() : null,
                FileSha256: dir ? null : Enumerable.Repeat((byte)(0xA0 + i), 32).ToArray()));
        }
        // kolejność wejściowa odwrócona — koder sam sortuje bajtowo
        list.Reverse();
        return list;
    }

    [Fact, Trait("V", "V-07")]
    public void V07_canonical_tree_and_treeId_match_reference()
    {
        var cbor = TreeCodec.Encode(V07Entries());
        Assert.Equal(Convert.FromHexString(V.GetProperty("V-07").GetProperty("tree").GetString()!), cbor);
        using var keys = RepoKeys.Derive(Convert.FromHexString(V.GetProperty("inputs").GetProperty("MK").GetString()!), Convert.FromHexString(V.GetProperty("inputs").GetProperty("repoId").GetString()!));
        Assert.Equal(Convert.FromHexString(V.GetProperty("V-07").GetProperty("treeId").GetString()!), keys.ChunkId(cbor));
        var back = TreeCodec.Decode(cbor);
        Assert.Equal(6, back.Count);
        Assert.Equal("zażółć gęślą jaźń.txt", back[3].Name);
    }

    [Fact, Trait("V", "V-08")]
    public void V08_non_canonical_encoding_of_same_content_is_rejected()
    {
        var bad = Convert.FromHexString(V.GetProperty("V-08").GetProperty("nonCanonicalTree").GetString()!);
        Assert.Equal(KbCode.MetadataMalformed, Assert.Throws<KobianException>(() => TreeCodec.Decode(bad)).Code);
    }

    [Fact]
    public void Unsorted_or_duplicate_entries_are_rejected()
    {
        var w = new CborWriter(CborConformanceMode.Canonical);
        w.WriteStartMap(1); w.WriteUInt32(1); w.WriteStartArray(2);
        foreach (var n in new[] { "b", "a" })
        {
            w.WriteStartMap(3); w.WriteUInt32(1); w.WriteTextString(n); w.WriteUInt32(2); w.WriteUInt32(0); w.WriteUInt32(3); w.WriteUInt32(0); w.WriteEndMap();
        }
        w.WriteEndArray(); w.WriteEndMap();
        Assert.Equal(KbCode.MetadataMalformed, Assert.Throws<KobianException>(() => TreeCodec.Decode(w.Encode())).Code);
        Assert.Equal(KbCode.TreeNameInvalid, Assert.Throws<KobianException>(() => TreeCodec.Encode([new("x", EntryKind.File), new("x", EntryKind.File)])).Code);
    }

    [Theory, Trait("SEC", "SEC-3.3")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a\0")]
    public void Invalid_names_are_rejected(string name) =>
        Assert.Equal(KbCode.TreeNameInvalid, Assert.Throws<KobianException>(() => TreeCodec.Encode([new(name, EntryKind.File)])).Code);

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Lone_surrogate_name_is_rejected() =>
        Assert.Equal(KbCode.TreeNameInvalid, Assert.Throws<KobianException>(() => TreeCodec.Encode([new(new string([(char)0xD800, 'x']), EntryKind.File)])).Code);

    [Fact, Trait("AC", "AC-08.7")]
    public void Case_differing_names_are_both_kept()
    {
        var back = TreeCodec.Decode(TreeCodec.Encode([new("plik.txt", EntryKind.File), new("Plik.txt", EntryKind.File)]));
        Assert.Equal(["Plik.txt", "plik.txt"], back.Select(e => e.Name));
    }

    [Fact]
    public void All_optional_fields_roundtrip()
    {
        var e = new TreeEntry("dane.bin", EntryKind.File, 5_000_000, 1, -2, 3, 0x20, new byte[16], [new byte[32]], null, null, new byte[32],
            [new AlternateDataStreamRef("Zone.Identifier", [new byte[32]], 26)], [(0, 4096), (1 << 20, 4096)], new byte[32]);
        var back = TreeCodec.Decode(TreeCodec.Encode([e])).Single();
        Assert.Equal(e.CtimeNs, back.CtimeNs);
        Assert.Equal("Zone.Identifier", back.Ads![0].Name);
        Assert.Equal(2, back.Sparse!.Count);
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Fuzzed_trees_only_throw_domain_errors()
    {
        var good = TreeCodec.Encode(V07Entries());
        var rnd = new Random(42);
        for (var i = 0; i < 20_000; i++)
        {
            var b = (byte[])good.Clone();
            for (var k = rnd.Next(1, 4); k > 0; k--) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
            if (rnd.Next(4) == 0) b = b[..rnd.Next(b.Length)];
            try
            {
                TreeCodec.Decode(b);
            }
            catch (KobianException)
            {
            }
        }
    }
}

public sealed class MetadataEnvelopeTests
{
    [Fact]
    public void Seal_open_roundtrip_and_tamper_detection()
    {
        var repoId = RandomNumberGenerator.GetBytes(16);
        var objectId = RandomNumberGenerator.GetBytes(16);
        using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), repoId);
        using var cipher = new AesGcmCipher(keys.DeriveObjectKey(objectId, MetadataObjectType.Snapshot));
        var payload = RandomNumberGenerator.GetBytes(500);
        var sealedObj = MetadataEnvelope.Seal(MetadataKind.Snapshot, objectId, RandomNumberGenerator.GetBytes(12), repoId, cipher, payload);
        Assert.Equal(payload, MetadataEnvelope.Open(sealedObj, MetadataKind.Snapshot, objectId, repoId, cipher));

        var flipped = (byte[])sealedObj.Clone();
        flipped[2] ^= 1; // magia
        Assert.Throws<KobianException>(() => MetadataEnvelope.Open(flipped, MetadataKind.Snapshot, objectId, repoId, cipher));
        var nonceFlip = (byte[])sealedObj.Clone();
        nonceFlip[33] ^= 1;
        Assert.Equal(KbCode.AuthenticationFailed, Assert.Throws<KobianException>(() => MetadataEnvelope.Open(nonceFlip, MetadataKind.Snapshot, objectId, repoId, cipher)).Code);
        // ten sam obiekt odczytany jako indeks / z innego repo / pod inną nazwą — odrzucony
        Assert.Throws<KobianException>(() => MetadataEnvelope.Open(sealedObj, MetadataKind.Index, objectId, repoId, cipher));
        Assert.Throws<KobianException>(() => MetadataEnvelope.Open(sealedObj, MetadataKind.Snapshot, objectId, RandomNumberGenerator.GetBytes(16), cipher));
        Assert.Throws<KobianException>(() => MetadataEnvelope.Open(sealedObj, MetadataKind.Snapshot, RandomNumberGenerator.GetBytes(16), repoId, cipher));
    }
}
