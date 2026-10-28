using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kobian.Tests.Support;

/// <summary>Przedział rozmiaru i waga w rozkładzie (D10.9).</summary>
public sealed record SizeBucket([property: JsonPropertyName("max")] long Max, [property: JsonPropertyName("weight")] int Weight);

/// <summary>Specyfikacja korpusu (JSON zamiast YAML — ADR-0001: brak zależności od parsera YAML).</summary>
public sealed record CorpusSpec(
    [property: JsonPropertyName("seed")] string Seed,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("dirs")] int Dirs,
    [property: JsonPropertyName("maxDepth")] int MaxDepth,
    [property: JsonPropertyName("sizes")] IReadOnlyList<SizeBucket> Sizes,
    [property: JsonPropertyName("plNamesPct")] int PlNamesPct,
    [property: JsonPropertyName("emojiPct")] int EmojiPct,
    [property: JsonPropertyName("longNamePct")] int LongNamePct,
    [property: JsonPropertyName("duplicatePct")] int DuplicatePct)
{
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static CorpusSpec Load(string json) =>
        JsonSerializer.Deserialize<CorpusSpec>(json, Options) ?? throw new InvalidDataException("Empty corpus spec.");
}

public sealed record CorpusFile(string RelativePath, long Size, string Sha256);

/// <summary>
/// Deterministyczny generator drzewa plików (D10.9): ta sama specyfikacja + ziarno = bajtowo identyczne drzewo.
/// Losowość i treść z HMAC-SHA256(seed, etykieta ‖ licznik) — bez zależności; brak danych osobowych.
/// Elementy specyficzne dla NTFS (ADS, sparse, junction, ACL deny) — tylko na VM Windows (osobny tryb, kolejka W).
/// </summary>
public sealed class CorpusGenerator
{
    private static readonly string[] PlWords = ["faktura", "zażółć", "gęślą", "jaźń", "umowa", "źródło", "księgowość", "Łódź", "środa", "ćwiczenia"];
    private static readonly string[] Emoji = ["📁", "✅", "🧾", "🔒"];
    private static readonly string[] Ext = [".txt", ".pdf", ".xlsx", ".docx", ".jpg", ".bak", ".csv", ""];
    private readonly byte[] _seed;
    private readonly CorpusSpec _spec;
    private ulong _counter;

    public CorpusGenerator(CorpusSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Files is < 0 or > 10_000_000 || spec.Dirs < 1 || spec.MaxDepth is < 1 or > 64 || spec.Sizes.Count == 0)
            throw new ArgumentException("Invalid corpus spec.", nameof(spec));
        _spec = spec;
        _seed = Convert.FromHexString(spec.Seed);
    }

    public IReadOnlyList<CorpusFile> Generate(string root)
    {
        Directory.CreateDirectory(root);
        var dirs = new List<string> { "" };
        for (var i = 0; i < _spec.Dirs; i++)
        {
            var parent = dirs[Next(dirs.Count)];
            var depth = parent.Length == 0 ? 0 : parent.Count(c => c == '/') + 1;
            if (depth >= _spec.MaxDepth) parent = "";
            var name = UniqueName(parent, dirs, isDir: true);
            var rel = parent.Length == 0 ? name : parent + "/" + name;
            Directory.CreateDirectory(Path.Combine(root, rel));
            dirs.Add(rel);
        }
        var files = new List<CorpusFile>(_spec.Files);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[]? lastContentKey = null;
        long lastSize = 0;
        for (var i = 0; i < _spec.Files; i++)
        {
            var dir = dirs[Next(dirs.Count)];
            string rel;
            do
            {
                var n = FileName();
                rel = dir.Length == 0 ? n : dir + "/" + n;
            } while (!taken.Add(rel));
            // Duplikaty treści (dedup US-22a): kopia poprzedniego pliku.
            var duplicate = lastContentKey is not null && Next(100) < _spec.DuplicatePct;
            var size = duplicate ? lastSize : PickSize();
            var contentKey = duplicate ? lastContentKey! : Label("content", (ulong)i);
            var sha = WriteContent(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), contentKey, size);
            files.Add(new CorpusFile(rel, size, sha));
            (lastContentKey, lastSize) = (contentKey, size);
        }
        return files;
    }

    /// <summary>Manifest (posortowany Ordinal) — do porównań między uruchomieniami i z odtworzeniem.</summary>
    public static string Manifest(IEnumerable<CorpusFile> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            sb.Append(f.RelativePath).Append('\t').Append(f.Size.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(f.Sha256).Append('\n');
        return sb.ToString();
    }

    private string FileName()
    {
        var sb = new StringBuilder();
        if (Next(100) < _spec.PlNamesPct) sb.Append(PlWords[Next(PlWords.Length)]).Append('_');
        sb.Append("plik").Append(Next(1_000_000).ToString(CultureInfo.InvariantCulture));
        if (Next(100) < _spec.EmojiPct) sb.Append(' ').Append(Emoji[Next(Emoji.Length)]);
        if (Next(100) < _spec.LongNamePct) sb.Append('_').Append(new string('d', 180));
        sb.Append(Ext[Next(Ext.Length)]);
        return sb.ToString();
    }

    private string UniqueName(string parent, List<string> existing, bool isDir)
    {
        _ = isDir;
        while (true)
        {
            var name = (Next(100) < _spec.PlNamesPct ? PlWords[Next(PlWords.Length)] : "katalog") + Next(10_000).ToString(CultureInfo.InvariantCulture);
            var rel = parent.Length == 0 ? name : parent + "/" + name;
            if (!existing.Contains(rel, StringComparer.OrdinalIgnoreCase)) return name;
        }
    }

    private long PickSize()
    {
        var total = _spec.Sizes.Sum(b => b.Weight);
        var r = Next(total);
        long min = 0;
        foreach (var b in _spec.Sizes)
        {
            if (r < b.Weight) return b.Max <= min ? b.Max : min + (long)(NextU64() % (ulong)(b.Max - min + 1));
            r -= b.Weight;
            min = b.Max + 1;
        }
        return 0;
    }

    private static string WriteContent(string path, byte[] key, long size)
    {
        // Strumień treści: AES-256 w trybie licznika (ECB na blokach licznika) z kluczem z HMAC(seed, …) — szybki i deterministyczny.
        // Dane testowe, nie kryptografia produktu. Co czwarty segment 4 KiB wyzerowany (kompresowalność).
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        using var aes = Aes.Create();
        aes.Key = key;
        const int Chunk = 64 * 1024;
        var counter = new byte[Chunk];
        var output = new byte[Chunk];
        ulong blockNo = 0;
        var remaining = size;
        while (remaining > 0)
        {
            for (var o = 0; o < Chunk; o += 16)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(counter.AsSpan(o), blockNo++);
            }
            aes.EncryptEcb(counter, output, PaddingMode.None);
            for (var seg = 0; seg < Chunk; seg += 4096)
            {
                if ((output[seg] & 3) == 0) Array.Clear(output, seg, 4096);
            }
            var n = (int)Math.Min(remaining, Chunk);
            fs.Write(output, 0, n);
            sha.AppendData(output, 0, n);
            remaining -= n;
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private byte[] Label(string label, ulong n)
    {
        var l = Encoding.ASCII.GetBytes(label);
        var msg = new byte[l.Length + 8];
        l.CopyTo(msg, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(msg.AsSpan(l.Length), n);
        return HMACSHA256.HashData(_seed, msg);
    }

    private ulong NextU64() => BinaryPrimitives.ReadUInt64LittleEndian(Label("rng", _counter++));

    private int Next(int maxExclusive) => maxExclusive <= 1 ? 0 : (int)(NextU64() % (ulong)maxExclusive);
}
