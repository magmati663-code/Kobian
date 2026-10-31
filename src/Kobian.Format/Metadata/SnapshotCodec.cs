using System.Formats.Cbor;
using Kobian.Domain;

namespace Kobian.Format.Metadata;

public enum SnapshotMode
{
    Full = 0,
    Incremental = 1,
    Differential = 2,
    Database = 3,
}

/// <summary>Klucz 10 snapshotu.</summary>
public sealed record SnapshotStats(long Files, long Dirs, long BytesRaw, long BytesNew, long PacksNew);

/// <summary>Snapshot (R2.3.5, D3.14 pkt 4). Pola opcjonalne null = nieobecne.</summary>
public sealed record SnapshotObject(
    byte[] SnapId,
    byte[] HostId,
    string JobId,
    byte[] RunId,
    DateTimeOffset Time,
    SnapshotMode Mode,
    byte[]? ParentSnapId,
    byte[]? BaseFullSnapId,
    byte[] RootTreeId,
    SnapshotStats Stats,
    IReadOnlyList<(byte[] IndexId, byte[] Sha256)> Indexes,
    byte[]? HeaderSha256,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string>? DbMeta,
    IReadOnlyList<string> SourceRoots,
    long Skipped = 0,
    long Unstable = 0)
{
    public const int FormatVersion = 1;
}

/// <summary>Kanoniczny kodek snapshotu. Klucze ≥ 32 ignorowane (informacyjne), nieznane &lt; 32 odrzucane (KB-1304).</summary>
public static class SnapshotCodec
{
    public const int MaxIndexes = 100_000;
    public const int MaxStrings = 4_096;
    public const int MaxStringBytes = 32 * 1024;

    public static byte[] Encode(SnapshotObject s)
    {
        ArgumentNullException.ThrowIfNull(s);
        IndexCodec.Id(s.SnapId, 16); IndexCodec.Id(s.HostId, 16); IndexCodec.Id(s.RunId, 16); IndexCodec.Id(s.RootTreeId, 32);
        if (s.Indexes.Count > MaxIndexes || s.Tags.Count > MaxStrings || s.SourceRoots.Count > MaxStrings || (s.DbMeta?.Count ?? 0) > MaxStrings)
            throw new KobianException(KbCode.MetadataTooLarge, ErrorClass.Permanent, "Snapshot too large.");
        var w = new CborWriter(CborConformanceMode.Canonical);
        w.WriteStartMap(13 + (s.ParentSnapId is null ? 0 : 1) + (s.BaseFullSnapId is null ? 0 : 1) + (s.HeaderSha256 is null ? 0 : 1) + (s.DbMeta is null ? 0 : 1));
        w.WriteUInt32(1); w.WriteByteString(s.SnapId);
        w.WriteUInt32(2); w.WriteByteString(s.HostId);
        w.WriteUInt32(3); Text(w, s.JobId);
        w.WriteUInt32(4); w.WriteByteString(s.RunId);
        w.WriteUInt32(5); w.WriteTag(CborTag.UnixTimeSeconds); w.WriteInt64(s.Time.ToUnixTimeSeconds());
        w.WriteUInt32(6); w.WriteUInt32((uint)s.Mode);
        if (s.ParentSnapId is not null) { IndexCodec.Id(s.ParentSnapId, 16); w.WriteUInt32(7); w.WriteByteString(s.ParentSnapId); }
        if (s.BaseFullSnapId is not null) { IndexCodec.Id(s.BaseFullSnapId, 16); w.WriteUInt32(8); w.WriteByteString(s.BaseFullSnapId); }
        w.WriteUInt32(9); w.WriteByteString(s.RootTreeId);
        w.WriteUInt32(10);
        w.WriteStartMap(5);
        w.WriteUInt32(1); w.WriteUInt64(checked((ulong)s.Stats.Files));
        w.WriteUInt32(2); w.WriteUInt64(checked((ulong)s.Stats.Dirs));
        w.WriteUInt32(3); w.WriteUInt64(checked((ulong)s.Stats.BytesRaw));
        w.WriteUInt32(4); w.WriteUInt64(checked((ulong)s.Stats.BytesNew));
        w.WriteUInt32(5); w.WriteUInt64(checked((ulong)s.Stats.PacksNew));
        w.WriteEndMap();
        w.WriteUInt32(11);
        w.WriteStartArray(s.Indexes.Count);
        foreach (var (id, sha) in s.Indexes)
        {
            IndexCodec.Id(id, 16); IndexCodec.Id(sha, 32);
            w.WriteStartArray(2); w.WriteByteString(id); w.WriteByteString(sha); w.WriteEndArray();
        }
        w.WriteEndArray();
        if (s.HeaderSha256 is not null) { IndexCodec.Id(s.HeaderSha256, 32); w.WriteUInt32(12); w.WriteByteString(s.HeaderSha256); }
        w.WriteUInt32(13); Strings(w, s.Tags);
        if (s.DbMeta is not null)
        {
            w.WriteUInt32(14);
            w.WriteStartMap(s.DbMeta.Count);
            foreach (var kv in s.DbMeta) { Text(w, kv.Key); Text(w, kv.Value); }
            w.WriteEndMap();
        }
        w.WriteUInt32(15); w.WriteUInt32(SnapshotObject.FormatVersion);
        w.WriteUInt32(16); Strings(w, s.SourceRoots);
        w.WriteUInt32(17);
        w.WriteStartMap(2);
        w.WriteUInt32(1); w.WriteUInt64(checked((ulong)s.Skipped));
        w.WriteUInt32(2); w.WriteUInt64(checked((ulong)s.Unstable));
        w.WriteEndMap();
        w.WriteEndMap();
        return w.Encode();
    }

    public static SnapshotObject Decode(byte[] cbor)
    {
        ArgumentNullException.ThrowIfNull(cbor);
        try
        {
            var r = new CborReader(cbor, CborConformanceMode.Canonical);
            var n = r.ReadStartMap() ?? throw IndexCodec.Malformed("Indefinite map.");
            return ReadBody(r, n);
        }
        catch (CborContentException ex) { throw Wrap(ex); }
        catch (InvalidOperationException ex) { throw Wrap(ex); }
        catch (OverflowException ex) { throw Wrap(ex); }
        catch (ArgumentException ex) { throw Wrap(ex); }
    }

    private static SnapshotObject ReadBody(CborReader r, int count)
    {
        byte[]? snap = null, host = null, run = null, parent = null, baseFull = null, root = null, header = null;
        string? job = null; DateTimeOffset? time = null; SnapshotMode? mode = null; SnapshotStats? stats = null;
        List<(byte[], byte[])>? idx = null; List<string>? tags = null, roots = null; Dictionary<string, string>? db = null;
        long skipped = 0, unstable = 0; uint? fv = null;
        for (var field = 0; field < count; field++)
        {
            var key = r.ReadUInt32();
            switch (key)
            {
                case 1: snap = IndexCodec.ReadId(r, 16); break;
                case 2: host = IndexCodec.ReadId(r, 16); break;
                case 3: job = ReadText(r); break;
                case 4: run = IndexCodec.ReadId(r, 16); break;
                case 5:
                    if (r.ReadTag() != CborTag.UnixTimeSeconds) throw IndexCodec.Malformed("Time tag expected.");
                    time = DateTimeOffset.FromUnixTimeSeconds(r.ReadInt64());
                    break;
                case 6:
                    var m = r.ReadUInt32();
                    if (m > 3) throw IndexCodec.Malformed("Unknown mode.");
                    mode = (SnapshotMode)m;
                    break;
                case 7: parent = IndexCodec.ReadId(r, 16); break;
                case 8: baseFull = IndexCodec.ReadId(r, 16); break;
                case 9: root = IndexCodec.ReadId(r, 32); break;
                case 10:
                    if (r.ReadStartMap() != 5) throw IndexCodec.Malformed("Stats must have 5 fields.");
                    var v = new long[5];
                    for (var i = 0; i < 5; i++)
                    {
                        if (r.ReadUInt32() != i + 1) throw IndexCodec.Malformed("Stats keys 1..5 expected.");
                        v[i] = checked((long)r.ReadUInt64());
                    }
                    r.ReadEndMap();
                    stats = new SnapshotStats(v[0], v[1], v[2], v[3], v[4]);
                    break;
                case 11:
                    var c = r.ReadStartArray() ?? throw IndexCodec.Malformed("Indefinite array.");
                    if (c > MaxIndexes) throw IndexCodec.Malformed("Too many indexes.");
                    idx = new List<(byte[], byte[])>(Math.Min(c, 1024));
                    for (var i = 0; i < c; i++)
                    {
                        if (r.ReadStartArray() != 2) throw IndexCodec.Malformed("Index ref must have 2 items.");
                        idx.Add((IndexCodec.ReadId(r, 16), IndexCodec.ReadId(r, 32)));
                        r.ReadEndArray();
                    }
                    r.ReadEndArray();
                    break;
                case 12: header = IndexCodec.ReadId(r, 32); break;
                case 13: tags = ReadStrings(r); break;
                case 14:
                    var d = r.ReadStartMap() ?? throw IndexCodec.Malformed("Indefinite map.");
                    if (d > MaxStrings) throw IndexCodec.Malformed("dbMeta too large.");
                    db = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (var i = 0; i < d; i++) db[ReadText(r)] = ReadText(r);
                    r.ReadEndMap();
                    break;
                case 15: fv = r.ReadUInt32(); break;
                case 16: roots = ReadStrings(r); break;
                case 17:
                    if (r.ReadStartMap() != 2 || r.ReadUInt32() != 1) throw IndexCodec.Malformed("Key 17 invalid.");
                    skipped = checked((long)r.ReadUInt64());
                    if (r.ReadUInt32() != 2) throw IndexCodec.Malformed("Key 17 invalid.");
                    unstable = checked((long)r.ReadUInt64());
                    r.ReadEndMap();
                    break;
                default:
                    if (key < 32) throw IndexCodec.Malformed("Unknown snapshot key.");
                    r.SkipValue();
                    break;
            }
        }
        r.ReadEndMap();
        if (r.BytesRemaining != 0) throw IndexCodec.Malformed("Trailing bytes.");
        if (fv != SnapshotObject.FormatVersion) throw IndexCodec.Malformed("Unsupported snapshot format version.");
        if (snap is null || host is null || job is null || run is null || time is null || mode is null || root is null || stats is null
            || idx is null || tags is null || roots is null)
            throw IndexCodec.Malformed("Missing snapshot field.");
        return new SnapshotObject(snap, host, job, run, time.Value, mode.Value, parent, baseFull, root, stats, idx, header, tags, db, roots, skipped, unstable);
    }

    private static void Text(CborWriter w, string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (System.Text.Encoding.UTF8.GetByteCount(s) > MaxStringBytes) throw new KobianException(KbCode.MetadataTooLarge, ErrorClass.Permanent, "String too long.");
        w.WriteTextString(s);
    }

    private static void Strings(CborWriter w, IReadOnlyList<string> list)
    {
        w.WriteStartArray(list.Count);
        foreach (var s in list) Text(w, s);
        w.WriteEndArray();
    }

    private static string ReadText(CborReader r)
    {
        var s = r.ReadTextString();
        if (System.Text.Encoding.UTF8.GetByteCount(s) > MaxStringBytes) throw IndexCodec.Malformed("String too long.");
        return s;
    }

    private static List<string> ReadStrings(CborReader r)
    {
        var n = r.ReadStartArray() ?? throw IndexCodec.Malformed("Indefinite array.");
        if (n > MaxStrings) throw IndexCodec.Malformed("Too many strings.");
        var l = new List<string>(n);
        for (var i = 0; i < n; i++) l.Add(ReadText(r));
        r.ReadEndArray();
        return l;
    }

    private static KobianException Wrap(Exception ex) => new(KbCode.MetadataMalformed, ErrorClass.Integrity, "Snapshot CBOR invalid or non-canonical.", ex);
}
