namespace Kobian.Engine.Restore;

/// <summary>Ramka potrzebna do odtworzenia: pozycja w packu (offset pola frameLength) i długość całej ramki z prefiksem.</summary>
public sealed record FrameRef(int FrameIndex, long Offset, long Length);

public sealed record ReadRange(long Offset, long Length, IReadOnlyList<int> FrameIndexes);

public sealed record PackStep(string PackId, IReadOnlyList<ReadRange> Ranges, int NeededFrames);

/// <summary>
/// Część planera D8.1/R2.8.1 bez I/O: łączenie sąsiednich ramek w odczyty zakresowe (przerwa ≤ 1 MiB) i kolejność packów
/// (RangeRead: po packId; bez RangeRead: najpierw packi „najgęstsze” w potrzebne bloki).
/// </summary>
public static class RestoreRangePlanner
{
    public const long DefaultMaxGap = 1024 * 1024;

    public static IReadOnlyList<ReadRange> MergeAdjacent(IEnumerable<FrameRef> frames, long maxGap = DefaultMaxGap)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(maxGap);
        var sorted = frames.OrderBy(f => f.Offset).ToList();
        var result = new List<ReadRange>();
        long start = -1, end = -1;
        var idx = new List<int>();
        foreach (var f in sorted)
        {
            if (f.Length <= 0 || f.Offset < 0) throw new ArgumentException("Invalid frame reference.", nameof(frames));
            if (start < 0)
            {
                (start, end) = (f.Offset, f.Offset + f.Length);
                idx.Add(f.FrameIndex);
                continue;
            }
            if (f.Offset < end) throw new ArgumentException("Overlapping frames.", nameof(frames));
            if (f.Offset - end <= maxGap)
            {
                end = f.Offset + f.Length;
                idx.Add(f.FrameIndex);
            }
            else
            {
                result.Add(new ReadRange(start, end - start, idx));
                (start, end) = (f.Offset, f.Offset + f.Length);
                idx = [f.FrameIndex];
            }
        }
        if (start >= 0) result.Add(new ReadRange(start, end - start, idx));
        return result;
    }

    public static IReadOnlyList<PackStep> OrderPacks(IReadOnlyDictionary<string, IReadOnlyList<FrameRef>> needByPack, bool rangeRead, long maxGap = DefaultMaxGap)
    {
        ArgumentNullException.ThrowIfNull(needByPack);
        var steps = needByPack.Select(kv => new PackStep(kv.Key, rangeRead ? MergeAdjacent(kv.Value, maxGap) : WholePack(kv.Value), kv.Value.Count));
        return rangeRead
            ? steps.OrderBy(s => s.PackId, StringComparer.Ordinal).ToList()
            : steps.OrderByDescending(s => s.NeededFrames).ThenBy(s => s.PackId, StringComparer.Ordinal).ToList();
    }

    private static List<ReadRange> WholePack(IReadOnlyList<FrameRef> frames) =>
        [new ReadRange(0, -1, frames.Select(f => f.FrameIndex).Order().ToList())]; // Length −1 = cały obiekt
}
