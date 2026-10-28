using System.Formats.Cbor;
using System.Text;
using Kobian.Domain;

namespace Kobian.Format.Metadata;

public enum EntryKind
{
    File = 0,
    Directory = 1,
    Symlink = 2,
    Junction = 3,
    HardlinkRef = 4,
}

public sealed record AlternateDataStreamRef(string Name, IReadOnlyList<byte[]> Chunks, long Size);

/// <summary>Wpis drzewa (R2.3.5) — klucze CBOR 1–15. Pola opcjonalne null = nieobecne w mapie.</summary>
public sealed record TreeEntry(
    string Name,
    EntryKind Kind,
    long Size = 0,
    long? MtimeNs = null,
    long? CtimeNs = null,
    long? BtimeNs = null,
    uint? Attributes = null,
    byte[]? FileId = null,
    IReadOnlyList<byte[]>? Chunks = null,
    byte[]? SubtreeId = null,
    string? LinkTarget = null,
    byte[]? SecurityDescriptorRef = null,
    IReadOnlyList<AlternateDataStreamRef>? Ads = null,
    IReadOnlyList<(long Offset, long Length)>? Sparse = null,
    byte[]? FileSha256 = null);

/// <summary>
/// Kanoniczne kodowanie drzewa (D3.8): mapa {1: [entry…]}, wpisy posortowane bajtowo po UTF-8 nazwy, bez duplikatów,
/// klucze map rosnąco (Canonical). Dekoder odrzuca dane niekanoniczne (V-08), nieznane klucze, złe nazwy (KB-1306),
/// zbyt wiele wpisów (KB-1305), zbyt głębokie zagnieżdżenie (KB-1304).
/// </summary>
public static class TreeCodec
{
    public const int MaxEntries = 1_000_000;
    public const int MaxNameBytes = 1024;

    public static byte[] Encode(IReadOnlyList<TreeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaxEntries) throw new KobianException(KbCode.TreeTooManyEntries, ErrorClass.Permanent, "Too many entries in one tree.");
        var sorted = entries.OrderBy(e => Encoding.UTF8.GetBytes(e.Name), ByteComparer.Instance).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            ValidateName(sorted[i].Name);
            if (i > 0 && sorted[i].Name == sorted[i - 1].Name)
                throw new KobianException(KbCode.TreeNameInvalid, ErrorClass.Permanent, "Duplicate name in tree.");
        }
        var w = new CborWriter(CborConformanceMode.Canonical);
        w.WriteStartMap(1);
        w.WriteUInt32(1);
        w.WriteStartArray(sorted.Count);
        foreach (var e in sorted) WriteEntry(w, e);
        w.WriteEndArray();
        w.WriteEndMap();
        return w.Encode();
    }

    public static IReadOnlyList<TreeEntry> Decode(byte[] cbor)
    {
        ArgumentNullException.ThrowIfNull(cbor);
        try
        {
            var r = new CborReader(cbor, CborConformanceMode.Canonical);
            if (r.ReadStartMap() != 1 || r.ReadUInt32() != 1) throw Malformed("Tree must be {1: [...]}");
            var n = r.ReadStartArray() ?? throw Malformed("Indefinite array.");
            if (n > MaxEntries) throw new KobianException(KbCode.TreeTooManyEntries, ErrorClass.Integrity, "Too many entries.");
            var list = new List<TreeEntry>(Math.Min(n, 4096));
            byte[]? prev = null;
            for (var i = 0; i < n; i++)
            {
                var e = ReadEntry(r);
                var nb = Encoding.UTF8.GetBytes(e.Name);
                if (prev is not null && ByteComparer.Instance.Compare(prev, nb) >= 0) throw Malformed("Entries not sorted or duplicated.");
                prev = nb;
                list.Add(e);
            }
            r.ReadEndArray();
            r.ReadEndMap();
            if (r.BytesRemaining != 0) throw Malformed("Trailing bytes.");
            return list;
        }
        catch (CborContentException ex)
        {
            throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Tree CBOR invalid or non-canonical.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Tree CBOR has unexpected structure.", ex);
        }
        catch (OverflowException ex)
        {
            throw new KobianException(KbCode.MetadataMalformed, ErrorClass.Integrity, "Tree CBOR integer out of range.", ex);
        }
    }

    /// <summary>Nazwa: niepusta, ≤ 1 024 B UTF-8, bez „/”, „\”, NUL, nie „.” ani „..” (R2.3.8 KB-1306).</summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
            throw new KobianException(KbCode.TreeNameInvalid, ErrorClass.Integrity, "Invalid entry name.");
        // nieparzyste surogaty nie mają reprezentacji UTF-8 — odrzucane (kanoniczność)
        if (!IsWellFormedUtf16(name)) throw new KobianException(KbCode.TreeNameInvalid, ErrorClass.Integrity, "Invalid UTF-16 in name.");
    }

    private static bool IsWellFormedUtf16(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static void WriteEntry(CborWriter w, TreeEntry e)
    {
        var fields = new List<(int Key, Action Write)>
        {
            (1, () => w.WriteTextString(e.Name)),
            (2, () => w.WriteUInt32((uint)e.Kind)),
            (3, () => w.WriteUInt64(checked((ulong)e.Size))),
        };
        if (e.MtimeNs is { } m) fields.Add((4, () => w.WriteInt64(m)));
        if (e.CtimeNs is { } c) fields.Add((5, () => w.WriteInt64(c)));
        if (e.BtimeNs is { } b) fields.Add((6, () => w.WriteInt64(b)));
        if (e.Attributes is { } a) fields.Add((7, () => w.WriteUInt32(a)));
        if (e.FileId is { } fid) fields.Add((8, () => w.WriteByteString(Require(fid, 16))));
        if (e.Chunks is { } ch) fields.Add((9, () => WriteIds(w, ch)));
        if (e.SubtreeId is { } st) fields.Add((10, () => w.WriteByteString(Require(st, 32))));
        if (e.LinkTarget is { } lt) fields.Add((11, () => w.WriteTextString(lt)));
        if (e.SecurityDescriptorRef is { } sd) fields.Add((12, () => w.WriteByteString(Require(sd, 32))));
        if (e.Ads is { } ads) fields.Add((13, () =>
        {
            w.WriteStartArray(ads.Count);
            foreach (var s in ads)
            {
                w.WriteStartArray(3);
                w.WriteTextString(s.Name);
                WriteIds(w, s.Chunks);
                w.WriteUInt64(checked((ulong)s.Size));
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }));
        if (e.Sparse is { } sp) fields.Add((14, () =>
        {
            w.WriteStartArray(sp.Count);
            foreach (var (o, l) in sp)
            {
                w.WriteStartArray(2);
                w.WriteUInt64(checked((ulong)o));
                w.WriteUInt64(checked((ulong)l));
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }));
        if (e.FileSha256 is { } h) fields.Add((15, () => w.WriteByteString(Require(h, 32))));
        w.WriteStartMap(fields.Count);
        foreach (var (k, write) in fields)
        {
            w.WriteUInt32((uint)k);
            write();
        }
        w.WriteEndMap();
    }

    private static TreeEntry ReadEntry(CborReader r)
    {
        var count = r.ReadStartMap() ?? throw Malformed("Indefinite map.");
        if (count is < 3 or > 15) throw Malformed("Entry field count.");
        string? name = null;
        var kind = (EntryKind)(-1);
        long size = -1;
        long? mt = null, ct = null, bt = null;
        uint? attrs = null;
        byte[]? fid = null, sub = null, sd = null, sha = null;
        IReadOnlyList<byte[]>? chunks = null;
        string? link = null;
        IReadOnlyList<AlternateDataStreamRef>? ads = null;
        IReadOnlyList<(long, long)>? sparse = null;
        for (var i = 0; i < count; i++)
        {
            switch (r.ReadUInt32())
            {
                case 1: name = r.ReadTextString(); break;
                case 2:
                    var k = r.ReadUInt32();
                    if (k > 4) throw Malformed("Unknown kind.");
                    kind = (EntryKind)k;
                    break;
                case 3: size = checked((long)r.ReadUInt64()); break;
                case 4: mt = r.ReadInt64(); break;
                case 5: ct = r.ReadInt64(); break;
                case 6: bt = r.ReadInt64(); break;
                case 7: attrs = r.ReadUInt32(); break;
                case 8: fid = Require(r.ReadByteString(), 16); break;
                case 9: chunks = ReadIds(r); break;
                case 10: sub = Require(r.ReadByteString(), 32); break;
                case 11: link = r.ReadTextString(); break;
                case 12: sd = Require(r.ReadByteString(), 32); break;
                case 13:
                    var na = r.ReadStartArray() ?? throw Malformed("Indefinite array.");
                    if (na > 1024) throw Malformed("Too many ADS.");
                    var al = new List<AlternateDataStreamRef>(na);
                    for (var j = 0; j < na; j++)
                    {
                        if (r.ReadStartArray() != 3) throw Malformed("ADS shape.");
                        al.Add(new AlternateDataStreamRef(r.ReadTextString(), ReadIds(r), checked((long)r.ReadUInt64())));
                        r.ReadEndArray();
                    }
                    r.ReadEndArray();
                    ads = al;
                    break;
                case 14:
                    var ns = r.ReadStartArray() ?? throw Malformed("Indefinite array.");
                    if (ns > 1_000_000) throw Malformed("Too many sparse ranges.");
                    var sl = new List<(long, long)>(Math.Min(ns, 4096));
                    for (var j = 0; j < ns; j++)
                    {
                        if (r.ReadStartArray() != 2) throw Malformed("Sparse shape.");
                        sl.Add((checked((long)r.ReadUInt64()), checked((long)r.ReadUInt64())));
                        r.ReadEndArray();
                    }
                    r.ReadEndArray();
                    sparse = sl;
                    break;
                case 15: sha = Require(r.ReadByteString(), 32); break;
                default: throw Malformed("Unknown entry key.");
            }
        }
        r.ReadEndMap();
        if (name is null || (int)kind < 0 || size < 0) throw Malformed("Missing required entry fields.");
        ValidateName(name);
        return new TreeEntry(name, kind, size, mt, ct, bt, attrs, fid, chunks, sub, link, sd, ads, sparse, sha);
    }

    private static void WriteIds(CborWriter w, IReadOnlyList<byte[]> ids)
    {
        w.WriteStartArray(ids.Count);
        foreach (var id in ids) w.WriteByteString(Require(id, 32));
        w.WriteEndArray();
    }

    private static List<byte[]> ReadIds(CborReader r)
    {
        var n = r.ReadStartArray() ?? throw Malformed("Indefinite array.");
        if (n > 4_194_304) throw Malformed("Too many chunk ids."); // 16 TiB / 4 MiB
        var list = new List<byte[]>(Math.Min(n, 4096));
        for (var i = 0; i < n; i++) list.Add(Require(r.ReadByteString(), 32));
        r.ReadEndArray();
        return list;
    }

    private static byte[] Require(byte[] b, int len) =>
        b.Length == len ? b : throw Malformed("Identifier length.");

    private static KobianException Malformed(string m) => new(KbCode.MetadataMalformed, ErrorClass.Integrity, m);

    private sealed class ByteComparer : IComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
