using System.Formats.Cbor;
using System.Security.Cryptography;
using Kobian.Domain;
using Kobian.Format.Metadata;
using Kobian.Format.Pack;
using Kobian.Format.Repo;
using Xunit;

namespace Kobian.Tests.Unit.Format;

public sealed class IndexSnapshotCodecTests
{
    private static byte[] R(int n) => RandomNumberGenerator.GetBytes(n);

    private static IndexObject SampleIndex() => new(
        [new IndexPack(R(16), R(32), 1234, [new PackEntry(R(32), 64, 100, BlobType.Data, 90), new PackEntry(R(32), 164, 50, BlobType.Tree, 40)])],
        [R(16)], R(16), R(16));

    private static SnapshotObject SampleSnapshot(bool optional) => new(
        R(16), R(16), "job-1", R(16), DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), SnapshotMode.Incremental,
        optional ? R(16) : null, optional ? R(16) : null, R(32), new SnapshotStats(10, 2, 1000, 500, 1),
        [(R(16), R(32))], optional ? R(32) : null, ["suspect"], optional ? new Dictionary<string, string> { ["db"] = "Firma" } : null,
        ["C:\\Dane"], 3, 1);

    [Fact, Trait("D", "R2.3.5")]
    public void Index_round_trips_and_is_deterministic()
    {
        var idx = SampleIndex();
        var a = IndexCodec.Encode(idx);
        Assert.Equal(a, IndexCodec.Encode(idx));
        var back = IndexCodec.Decode(a);
        Assert.Equal(idx.Packs[0].PackId, back.Packs[0].PackId);
        Assert.Equal(idx.Packs[0].Entries[1].ChunkId, back.Packs[0].Entries[1].ChunkId);
        Assert.Equal(BlobType.Tree, back.Packs[0].Entries[1].BlobType);
        Assert.Equal(a, IndexCodec.Encode(back));
    }

    [Theory, Trait("D", "R2.3.5")]
    [InlineData(true)]
    [InlineData(false)]
    public void Snapshot_round_trips_with_and_without_optional_fields(bool optional)
    {
        var s = SampleSnapshot(optional);
        var a = SnapshotCodec.Encode(s);
        var back = SnapshotCodec.Decode(a);
        Assert.Equal(a, SnapshotCodec.Encode(back));
        Assert.Equal(s.JobId, back.JobId);
        Assert.Equal(s.Time, back.Time);
        Assert.Equal(3, back.Skipped);
        Assert.Equal(optional, back.DbMeta is not null);
    }

    [Fact, Trait("D", "D3.14")]
    public void Snapshot_informational_keys_from_32_are_ignored_but_unknown_low_keys_rejected()
    {
        var a = SnapshotCodec.Encode(SampleSnapshot(false));
        Assert.NotNull(SnapshotCodec.Decode(AppendKey(a, 40)));
        var ex = Assert.Throws<KobianException>(() => SnapshotCodec.Decode(AppendKey(a, 20)));
        Assert.Equal(KbCode.MetadataMalformed, ex.Code);
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Index_rejects_truncation_trailing_bytes_and_random_mutations()
    {
        var a = IndexCodec.Encode(SampleIndex());
        Assert.Throws<KobianException>(() => IndexCodec.Decode(a[..^1]));
        Assert.Throws<KobianException>(() => IndexCodec.Decode([.. a, 0]));
        var rnd = new Random(31);
        for (var i = 0; i < 3000; i++)
        {
            var m = (byte[])a.Clone();
            m[rnd.Next(m.Length)] ^= (byte)(1 << rnd.Next(8));
            try { IndexCodec.Decode(m); }
            catch (KobianException) { }
        }
    }

    [Fact, Trait("D", "R2.3")]
    public void Repo_paths_follow_layout_and_parse_back()
    {
        var id = Convert.FromHexString("00112233445566778899aabbccddeeff");
        Assert.Equal("data/00/00112233445566778899aabbccddeeff.pack", RepoPaths.Pack(id));
        Assert.Equal("snapshots/00112233445566778899aabbccddeeff.snap.part", RepoPaths.SnapshotPart(id));
        Assert.Equal(id, RepoPaths.TryParseId(RepoPaths.Snapshot(id), RepoPaths.SnapshotsDir, ".snap"));
        Assert.Null(RepoPaths.TryParseId("snapshots/../x.snap", RepoPaths.SnapshotsDir, ".snap"));
        Assert.Null(RepoPaths.TryParseId(RepoPaths.SnapshotPart(id), RepoPaths.SnapshotsDir, ".snap"));
    }

    /// <summary>Dokleja parę (klucz, 0) do mapy kanonicznej: przepisuje mapę z licznikiem +1.</summary>
    private static byte[] AppendKey(byte[] cbor, uint key)
    {
        var r = new CborReader(cbor, CborConformanceMode.Canonical);
        var n = r.ReadStartMap()!.Value;
        var w = new CborWriter(CborConformanceMode.Canonical);
        w.WriteStartMap(n + 1);
        for (var i = 0; i < n; i++)
        {
            w.WriteEncodedValue(r.ReadEncodedValue().Span);
            w.WriteEncodedValue(r.ReadEncodedValue().Span);
        }
        w.WriteUInt32(key); w.WriteUInt32(0);
        w.WriteEndMap();
        return w.Encode();
    }

    [Fact, Trait("SEC", "SEC-3.3")]
    public void Fuzzed_index_and_snapshot_only_throw_domain_errors()
    {
        var rnd = new Random(4545);
        foreach (var (good, decode) in new (byte[], Action<byte[]>)[]
                 {
                     (IndexCodec.Encode(SampleIndex()), b => IndexCodec.Decode(b)),
                     (SnapshotCodec.Encode(SampleSnapshot(true)), b => SnapshotCodec.Decode(b)),
                     (SnapshotCodec.Encode(SampleSnapshot(false)), b => SnapshotCodec.Decode(b)),
                 })
        {
            for (var i = 0; i < 20_000; i++)
            {
                var b = (byte[])good.Clone();
                for (var k = rnd.Next(1, 4); k > 0; k--) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
                if (rnd.Next(4) == 0) b = b[..rnd.Next(b.Length)];
                try { decode(b); }
                catch (KobianException) { }
            }
        }
    }
}
