using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kobian.Engine.Backup;

/// <summary>Identyfikator bloku (HMAC-SHA256, 32 B) jako wartość — bez ciągu hex na stercie (RD-22, D3.15 warstwa 1).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ChunkKey : IEquatable<ChunkKey>, IComparable<ChunkKey>
{
    private readonly ulong _a, _b, _c, _d;

    public ChunkKey(ReadOnlySpan<byte> id)
    {
        if (id.Length != 32) throw new ArgumentException("Chunk id must be 32 bytes.", nameof(id));
        _a = BinaryPrimitives.ReadUInt64LittleEndian(id);
        _b = BinaryPrimitives.ReadUInt64LittleEndian(id[8..]);
        _c = BinaryPrimitives.ReadUInt64LittleEndian(id[16..]);
        _d = BinaryPrimitives.ReadUInt64LittleEndian(id[24..]);
    }

    public bool Equals(ChunkKey other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;

    public override bool Equals(object? obj) => obj is ChunkKey k && Equals(k);

    /// <summary>Id jest wyjściem HMAC (jednorodny rozkład) — pierwsze 8 B wystarczą jako skrót; klucz HMAC zna tylko repozytorium, więc kolizji nie da się wymusić z zewnątrz.</summary>
    public override int GetHashCode() => (int)(_a ^ (_a >> 32));

    public static bool operator ==(ChunkKey l, ChunkKey r) => l.Equals(r);

    public static bool operator !=(ChunkKey l, ChunkKey r) => !l.Equals(r);

    /// <summary>Porządek leksykograficzny po słowach (bez znaczenia semantycznego) — do posortowanej tablicy bazowej (it. 102).</summary>
    public int CompareTo(ChunkKey other)
    {
        var c = _a.CompareTo(other._a);
        if (c != 0) return c;
        c = _b.CompareTo(other._b);
        if (c != 0) return c;
        c = _c.CompareTo(other._c);
        return c != 0 ? c : _d.CompareTo(other._d);
    }

    public static bool operator <(ChunkKey l, ChunkKey r) => l.CompareTo(r) < 0;

    public static bool operator <=(ChunkKey l, ChunkKey r) => l.CompareTo(r) <= 0;

    public static bool operator >(ChunkKey l, ChunkKey r) => l.CompareTo(r) > 0;

    public static bool operator >=(ChunkKey l, ChunkKey r) => l.CompareTo(r) >= 0;

    /// <summary>Dwa niezależne skróty do filtra Blooma (podwójne haszowanie Kirscha–Mitzenmachera) — słowa HMAC są jednorodne.</summary>
    internal (ulong H1, ulong H2) BloomHashes => (_b, _c | 1);
}

/// <summary>
/// Filtr Blooma (D3.15 warstwa 2; it. 102, RD-22 w2): <see cref="BitsPerEntry"/> bitów na wpis, k = 7, FP ≈ 1% [szacunek z wzoru; zmierzone w Bench].
/// Wynik „nie ma” jest pewny; „może być” wymaga potwierdzenia w strukturze dokładnej. Tylko odczyt po zbudowaniu.
/// </summary>
public sealed class ChunkBloomFilter
{
    public const int BitsPerEntry = 10;
    public const int HashCount = 7;

    private readonly ulong[] _bits;
    private readonly ulong _size;

    public ChunkBloomFilter(int entries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entries);
        _size = (ulong)Math.Max(64L, (long)entries * BitsPerEntry);
        _bits = new ulong[(_size + 63) / 64];
    }

    public long SizeBytes => _bits.LongLength * 8;

    public void Add(ChunkKey key)
    {
        var (h1, h2) = key.BloomHashes;
        for (ulong i = 0; i < HashCount; i++)
        {
            var bit = (h1 + i * h2) % _size;
            _bits[bit >> 6] |= 1UL << (int)(bit & 63);
        }
    }

    public bool MayContain(ChunkKey key)
    {
        var (h1, h2) = key.BloomHashes;
        for (ulong i = 0; i < HashCount; i++)
        {
            var bit = (h1 + i * h2) % _size;
            if ((_bits[bit >> 6] & (1UL << (int)(bit & 63))) == 0) return false;
        }
        return true;
    }
}

/// <summary>
/// Tabela dedup jednego uruchomienia (RD-22; it. 87 warstwa 1, it. 102 warstwa 2 „w pamięci”).
/// Bloki z indeksów bazowych (potwierdzone wcześniej) trafiają do POSORTOWANEJ tablicy <see cref="ChunkKey"/> (32 B/blok, wyszukiwanie binarne),
/// opcjonalnie z filtrem Blooma przed nią (10 bit/blok) — zamrażana przy pierwszym <see cref="Reference"/>. Dla bloków bazowych NIE trzeba śledzić „Referenced”:
/// są potwierdzone z definicji, więc I-3 sprawdza wyłącznie bloki nowe w tym uruchomieniu (słownik ChunkKey → flagi).
/// <see cref="AddConfirmed"/> po zamrożeniu (poza potokiem) trafia do słownika — zachowanie identyczne jak wcześniej.
/// Warstwa 3 D3.15 (repo_chunk_index w katalogu) — dalej (it. 102: pomiar i decyzja w pomiary.md).
/// </summary>
public sealed class DedupTable
{
    [Flags]
    private enum F : byte
    {
        None = 0,
        Known = 1,
        Confirmed = 2,
        Referenced = 4,
    }

    /// <summary>Od tylu bloków bazowych filtr Blooma przed tablicą (domyślnie z pomiaru it. 102 — patrz pomiary.md).</summary>
    public const int DefaultBloomThreshold = int.MaxValue;

    private readonly Dictionary<ChunkKey, F> _run = new();
    private readonly List<ChunkKey> _pending = [];
    private readonly int _bloomThreshold;
    private List<ChunkKey>? _build;
    private ChunkKey[] _base = [];
    private ChunkBloomFilter? _bloom;

    /// <param name="capacity">Przewidywana liczba bloków bazowych (bez nadmiarowej rezerwy listy).</param>
    /// <param name="bloomThreshold">Filtr Blooma, gdy bloków bazowych ≥ próg.</param>
    public DedupTable(int capacity = 0, int bloomThreshold = DefaultBloomThreshold)
    {
        _build = new List<ChunkKey>(capacity);
        _bloomThreshold = bloomThreshold;
    }

    public int Count => _base.Length + (_build?.Count ?? 0) + _run.Count;

    /// <summary>Liczba bloków w tablicy bazowej (po zamrożeniu) i czy działa filtr Blooma — diagnostyka/Bench.</summary>
    public (int BaseBlocks, bool Bloom) Shape => (_base.Length, _bloom is not null);

    /// <summary>Rezerwa na <paramref name="more"/> kolejnych bloków bazowych (ładowanie strumieniowe — BaselineSet); po zamrożeniu bez skutku.</summary>
    public void Reserve(int more)
    {
        if (_build is null || more <= 0) return;
        var need = _build.Count + more;
        if (need > _build.Capacity) _build.Capacity = Math.Max(need, (int)Math.Min(int.MaxValue, _build.Capacity + (long)_build.Capacity / 2)); // wzrost ×1,5 zamiast ×2
    }

    /// <summary>Blok z indeksu bazowego (zweryfikowany wcześniej).</summary>
    public void AddConfirmed(ReadOnlySpan<byte> id)
    {
        var key = new ChunkKey(id);
        if (_build is not null)
        {
            _build.Add(key);
            return;
        }
        if (InBase(key)) return;
        ref var f = ref CollectionsMarshal.GetValueRefOrAddDefault(_run, key, out _);
        f |= F.Known | F.Confirmed;
    }

    /// <summary>Odnotowuje użycie bloku; true = blok nowy (trzeba go zapisać w packu — od teraz Known, ale niepotwierdzony do <see cref="ConfirmPending"/>).</summary>
    public bool Reference(ReadOnlySpan<byte> id)
    {
        Freeze();
        var key = new ChunkKey(id);
        ref var f = ref CollectionsMarshal.GetValueRefOrNullRef(_run, key);
        if (!Unsafe.IsNullRef(ref f))
        {
            f |= F.Referenced; // każdy wpis słownika jest Known (nowy w tym uruchomieniu albo dodany po zamrożeniu)
            return false;
        }
        if (InBase(key)) return false; // blok bazowy: potwierdzony, I-3 go nie dotyczy — bez wpisu w słowniku
        _run[key] = F.Known | F.Referenced;
        _pending.Add(key);
        return true;
    }

    /// <summary>
    /// Wysyłki równoległe (RD-73): bloki oczekujące zamkniętego packa odłączane jako partia — potwierdzane przez <see cref="Confirm"/> dopiero po weryfikacji
    /// TEGO packa (I-2). Wywołania tylko z wątku potoku (tabela nie jest bezpieczna wątkowo).
    /// </summary>
    public List<ChunkKey> TakePending()
    {
        var batch = new List<ChunkKey>(_pending);
        _pending.Clear();
        return batch;
    }

    public void Confirm(List<ChunkKey> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var k in batch) CollectionsMarshal.GetValueRefOrNullRef(_run, k) |= F.Confirmed;
    }

    /// <summary>Pack z oczekującymi blokami zweryfikowany na celu (I-2).</summary>
    public void ConfirmPending()
    {
        foreach (var k in _pending) CollectionsMarshal.GetValueRefOrNullRef(_run, k) |= F.Confirmed;
        _pending.Clear();
    }

    /// <summary>I-3: każdy użyty blok jest potwierdzony (w zweryfikowanym packu albo w indeksie bazowym).</summary>
    public bool AllReferencedConfirmed()
    {
        foreach (var f in _run.Values)
            if ((f & F.Referenced) != 0 && (f & F.Confirmed) == 0) return false;
        return true;
    }

    private void Freeze()
    {
        if (_build is null) return;
        var span = CollectionsMarshal.AsSpan(_build);
        span.Sort(static (x, y) => x.CompareTo(y));
        var n = 0;
        for (var i = 0; i < span.Length; i++)
            if (n == 0 || span[i] != span[n - 1]) span[n++] = span[i];
        _base = span[..n].ToArray();
        _build = null; // lista budowy zwolniona — w szczycie zamrożenia przez chwilę 2 × 32 B/blok
        if (_base.Length >= _bloomThreshold)
        {
            _bloom = new ChunkBloomFilter(_base.Length);
            foreach (var k in _base) _bloom.Add(k);
        }
    }

    private bool InBase(ChunkKey key)
    {
        if (_base.Length == 0) return false;
        if (_bloom is not null && !_bloom.MayContain(key)) return false;
        return _base.AsSpan().BinarySearch(key) >= 0; // ChunkKey : IComparable<ChunkKey> — bez wywołań interfejsu w pętli
    }
}
