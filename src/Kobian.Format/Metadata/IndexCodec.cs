using System.Formats.Cbor;
using Kobian.Domain;
using Kobian.Format.Pack;

namespace Kobian.Format.Metadata;

/// <summary>Pack opisany w indeksie: identyfikator, SHA-256 całego pliku, rozmiar i wpisy trailera.</summary>
public sealed record IndexPack(byte[] PackId, byte[] Sha256, long Size, IReadOnlyList<PackEntry> Entries);

/// <summary>
/// Indeks (R2.3.5): <c>{1: [pack…], 2: [zastąpione indexId], 3: hostId, 4: runId, 32?: [SHA-256 zastąpionego obiektu…]}</c>.
/// Klucz 32 (D3.16, konsolidacja): równoległa do klucza 2 lista SHA-256 zaszyfrowanych plików zastąpionych indeksów — czytelnik przyjmuje indeks skonsolidowany
/// w miejsce odwołania <c>(indexId, sha256)</c> ze snapshotu (klucz 11) wyłącznie, gdy zapisany tu skrót jest równy skrótowi z odwołania (kotwica łańcucha zaufania).
/// Klucz 33: równoległa do klucza 2 lista numerów packów (pozycje w kluczu 1), które zawierał zastąpiony indeks — „widok” zastąpionego indeksu; dzięki temu
/// dedup, check i prune widzą dla odwołania DOKŁADNIE te packi co przed konsolidacją (odzysk miejsca bez zmian, Qb26).
/// PROWIZORYCZNIE Qb97: klucze rozszerzenia ≥ 32 (starsze czytniki je pomijają); alternatywa — pary w kluczu 2 (zmiana formatu v1).
/// </summary>
public sealed record IndexObject(IReadOnlyList<IndexPack> Packs, IReadOnlyList<byte[]> Supersedes, byte[] HostId, byte[] RunId,
    IReadOnlyList<byte[]>? SupersededSha256 = null, IReadOnlyList<IReadOnlyList<int>>? SupersededPacks = null);

/// <summary>Kanoniczny kodek indeksu (D3.8 — te same reguły co drzewo: Canonical CBOR, limity, odrzucanie nieznanych kluczy &lt; 32).</summary>
public static class IndexCodec
{
    public const int MaxPacks = 100_000;
    public const int MaxSupersedes = 10_000;

    // PROWIZORYCZNIE Qb97: relacja w kluczach rozszerzenia 32/33 zamiast zmiany klucza 2 (format v1 bez zmian dla starszych czytników).
    /// <summary>Klucz rozszerzenia z SHA-256 zastąpionych indeksów (D3.16).</summary>
    public const uint SupersededShaKey = 32;

    /// <summary>Klucz rozszerzenia z numerami packów każdego zastąpionego indeksu (D3.16).</summary>
    public const uint SupersededPacksKey = 33;

    /// <summary>Limit łącznej liczby numerów packów w kluczu 33 (DoS — SEC-3.3).</summary>
    public const int MaxSupersededPackRefs = 1_000_000;

    public static byte[] Encode(IndexObject index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.Packs.Count > MaxPacks || index.Supersedes.Count > MaxSupersedes)
            throw new KobianException(KbCode.MetadataTooLarge, ErrorClass.Permanent, "Index too large.");
        Id(index.HostId, 16); Id(index.RunId, 16);
        if ((index.SupersededSha256 is null) != (index.SupersededPacks is null)
            || (index.SupersededSha256 is { } shas && (shas.Count != index.Supersedes.Count || index.SupersededPacks!.Count != index.Supersedes.Count)))
            throw new ArgumentException("SupersededSha256 and SupersededPacks must both parallel Supersedes.", nameof(index));
        var w = new CborWriter(CborConformanceMode.Canonical);
        w.WriteStartMap(index.SupersededSha256 is null ? 4 : 6);
        w.WriteUInt32(1);
        w.WriteStartArray(index.Packs.Count);
        foreach (var p in index.Packs)
        {
            Id(p.PackId, 16); Id(p.Sha256, 32);
            if (p.Entries.Count > PackLayout.MaxFrames) throw new KobianException(KbCode.MetadataTooLarge, ErrorClass.Permanent, "Too many pack entries.");
            w.WriteStartMap(4);
            w.WriteUInt32(1); w.WriteByteString(p.PackId);
            w.WriteUInt32(2); w.WriteByteString(p.Sha256);
            w.WriteUInt32(3); w.WriteUInt64(checked((ulong)p.Size));
            w.WriteUInt32(4);
            w.WriteStartArray(p.Entries.Count);
            foreach (var e in p.Entries)
            {
                Id(e.ChunkId, PackLayout.ChunkIdSize);
                w.WriteStartArray(5);
                w.WriteByteString(e.ChunkId);
                w.WriteUInt64(checked((ulong)e.Offset));
                w.WriteUInt32(checked((uint)e.FrameLength));
                w.WriteUInt32((uint)e.BlobType);
                w.WriteUInt32(checked((uint)e.RawLength));
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndMap();
        }
        w.WriteEndArray();
        w.WriteUInt32(2);
        w.WriteStartArray(index.Supersedes.Count);
        foreach (var s in index.Supersedes) { Id(s, 16); w.WriteByteString(s); }
        w.WriteEndArray();
        w.WriteUInt32(3); w.WriteByteString(index.HostId);
        w.WriteUInt32(4); w.WriteByteString(index.RunId);
        if (index.SupersededSha256 is { } ss)
        {
            w.WriteUInt32(SupersededShaKey);
            w.WriteStartArray(ss.Count);
            foreach (var h in ss) { Id(h, 32); w.WriteByteString(h); }
            w.WriteEndArray();
            w.WriteUInt32(SupersededPacksKey);
            w.WriteStartArray(index.SupersededPacks!.Count);
            foreach (var list in index.SupersededPacks)
            {
                if (list.Count > MaxPacks) throw new KobianException(KbCode.MetadataTooLarge, ErrorClass.Permanent, "Index too large.");
                w.WriteStartArray(list.Count);
                foreach (var ord in list)
                {
                    if ((uint)ord >= (uint)index.Packs.Count) throw new ArgumentException("Superseded pack ordinal out of range.", nameof(index));
                    w.WriteUInt32((uint)ord);
                }
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        w.WriteEndMap();
        return w.Encode();
    }

    public static IndexObject Decode(byte[] cbor)
    {
        ArgumentNullException.ThrowIfNull(cbor);
        try
        {
            var r = new CborReader(cbor, CborConformanceMode.Canonical);
            var n = r.ReadStartMap() ?? throw Malformed("Indefinite map.");
            List<IndexPack>? packs = null; List<byte[]>? sup = null, supSha = null; List<IReadOnlyList<int>>? supPacks = null; byte[]? host = null, run = null;
            for (var i = 0; i < n; i++)
            {
                var key = r.ReadUInt32();
                switch (key)
                {
                    case 1: packs = ReadPacks(r); break;
                    case 2:
                        var m = r.ReadStartArray() ?? throw Malformed("Indefinite array.");
                        if (m > MaxSupersedes) throw Malformed("Too many superseded indexes.");
                        sup = new List<byte[]>(m);
                        for (var j = 0; j < m; j++) sup.Add(ReadId(r, 16));
                        r.ReadEndArray();
                        break;
                    case 3: host = ReadId(r, 16); break;
                    case 4: run = ReadId(r, 16); break;
                    case SupersededShaKey:
                        var q = r.ReadStartArray() ?? throw Malformed("Indefinite array.");
                        if (q > MaxSupersedes) throw Malformed("Too many superseded hashes.");
                        supSha = new List<byte[]>(q);
                        for (var j = 0; j < q; j++) supSha.Add(ReadId(r, 32));
                        r.ReadEndArray();
                        break;
                    case SupersededPacksKey:
                        var g = r.ReadStartArray() ?? throw Malformed("Indefinite array.");
                        if (g > MaxSupersedes) throw Malformed("Too many superseded pack lists.");
                        supPacks = new List<IReadOnlyList<int>>(g);
                        var total = 0;
                        for (var j = 0; j < g; j++)
                        {
                            var c = r.ReadStartArray() ?? throw Malformed("Indefinite array.");
                            total += c;
                            if (c > MaxPacks || total > MaxSupersededPackRefs) throw Malformed("Too many superseded pack references.");
                            var ords = new int[c];
                            for (var k = 0; k < c; k++) ords[k] = checked((int)r.ReadUInt32());
                            r.ReadEndArray();
                            supPacks.Add(ords);
                        }
                        r.ReadEndArray();
                        break;
                    default:
                        if (key < 32) throw Malformed("Unknown index key.");
                        r.SkipValue();
                        break;
                }
            }
            r.ReadEndMap();
            if (r.BytesRemaining != 0) throw Malformed("Trailing bytes.");
            if (packs is null || sup is null || host is null || run is null) throw Malformed("Missing index field.");
            if ((supSha is null) != (supPacks is null)) throw Malformed("Superseded hashes and pack lists must appear together.");
            if (supSha is not null && (supSha.Count != sup.Count || supPacks!.Count != sup.Count)) throw Malformed("Superseded hashes do not match superseded indexes.");
            if (supPacks is not null && supPacks.Any(l => l.Any(o => o >= packs.Count))) throw Malformed("Superseded pack ordinal out of range.");
            return new IndexObject(packs, sup, host, run, supSha, supPacks);
        }
        catch (CborContentException ex) { throw Wrap(ex); }
        catch (InvalidOperationException ex) { throw Wrap(ex); }
        catch (OverflowException ex) { throw Wrap(ex); }
    }

    private static List<IndexPack> ReadPacks(CborReader r)
    {
        var n = r.ReadStartArray() ?? throw Malformed("Indefinite array.");
        if (n > MaxPacks) throw Malformed("Too many packs.");
        var list = new List<IndexPack>(Math.Min(n, 1024));
        for (var i = 0; i < n; i++)
        {
            if (r.ReadStartMap() != 4) throw Malformed("Pack entry must have 4 fields.");
            if (r.ReadUInt32() != 1) throw Malformed("Key 1 expected.");
            var packId = ReadId(r, 16);
            if (r.ReadUInt32() != 2) throw Malformed("Key 2 expected.");
            var sha = ReadId(r, 32);
            if (r.ReadUInt32() != 3) throw Malformed("Key 3 expected.");
            var size = checked((long)r.ReadUInt64());
            if (r.ReadUInt32() != 4) throw Malformed("Key 4 expected.");
            var m = r.ReadStartArray() ?? throw Malformed("Indefinite array.");
            if (m > PackLayout.MaxFrames) throw Malformed("Too many entries.");
            var entries = new List<PackEntry>(Math.Min(m, 4096));
            for (var j = 0; j < m; j++)
            {
                if (r.ReadStartArray() != 5) throw Malformed("Entry must have 5 items.");
                var chunk = ReadId(r, PackLayout.ChunkIdSize);
                var offset = checked((long)r.ReadUInt64());
                var frame = checked((int)r.ReadUInt32());
                var type = r.ReadUInt32();
                if (type is not (1 or 2)) throw Malformed("Unknown blob type.");
                var raw = checked((int)r.ReadUInt32());
                if (frame > PackLayout.MaxFrameLength || offset < 0 || offset > size) throw Malformed("Entry out of range.");
                r.ReadEndArray();
                entries.Add(new PackEntry(chunk, offset, frame, (BlobType)type, raw));
            }
            r.ReadEndArray();
            r.ReadEndMap();
            list.Add(new IndexPack(packId, sha, size, entries));
        }
        r.ReadEndArray();
        return list;
    }

    internal static byte[] ReadId(CborReader r, int len)
    {
        var b = r.ReadByteString();
        if (b.Length != len) throw Malformed("Identifier length invalid.");
        return b;
    }

    internal static void Id(byte[]? id, int len)
    {
        if (id is null || id.Length != len) throw new ArgumentException($"Identifier must be {len} bytes.");
    }

    internal static KobianException Malformed(string m) => new(KbCode.MetadataMalformed, ErrorClass.Integrity, m);

    private static KobianException Wrap(Exception ex) => new(KbCode.MetadataMalformed, ErrorClass.Integrity, "Index CBOR invalid or non-canonical.", ex);
}
