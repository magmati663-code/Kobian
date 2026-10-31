using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;

namespace Kobian.Engine.Restore;

/// <summary>Rodzaj wpisu w przeglądarce kopii (SCR-06).</summary>
public enum BrowseEntryKind
{
    File = 0,
    Directory = 1,
    Link = 2,
}

/// <summary>Wpis folderu kopii: nazwa (niezaufana — z repozytorium, tylko do wyświetlenia), rodzaj, rozmiar pliku, czas modyfikacji (UTC, jeśli zapisany).</summary>
public sealed record BrowseEntry(string Name, BrowseEntryKind Kind, long Size, DateTimeOffset? ModifiedUtc);

/// <summary>Wynik listowania jednego folderu kopii; <see cref="Truncated"/> — więcej wpisów niż limit (reszta przez filtr ścieżki w odtwarzaniu).</summary>
public sealed record BrowseListing(string Path, IReadOnlyList<BrowseEntry> Entries, bool Truncated);

/// <summary>
/// Przeglądarka kopii (BL-63, SCR-06, AC-07.1): listowanie JEDNEGO folderu snapshotu po ścieżce względnej „a/b/c” bez odczytu danych plików
/// (tylko drzewa). Ścieżka z klienta jest walidowana segment po segmencie (<see cref="TreeCodec.ValidateName"/>: bez „..”, „\”, NUL), głębokość ≤ 64,
/// segment musi być folderem w kopii — inaczej KB-5003 (bez ścieżki w komunikacie, NFR-11). Wpisy: foldery przed plikami, porządek porządkowy nazw,
/// limit <see cref="MaxEntries"/>. Wpis z niepoprawną nazwą w repo (uszkodzone/złośliwe drzewo) jest pomijany — tak samo, jak przy odtwarzaniu.
/// </summary>
public sealed class SnapshotBrowser(RepoReader reader)
{
    public const int MaxEntries = 5000;
    public const int MaxDepth = 64;
    public const int MaxPathChars = 4096;

    private readonly RepoReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>Normalizacja ścieżki z klienta: „/” i „\” jako separator, bez pustych segmentów; "" = korzeń kopii.</summary>
    public static IReadOnlyList<string> SplitPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return [];
        if (path.Length > MaxPathChars) throw NotFound();
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > MaxDepth) throw NotFound();
        foreach (var p in parts)
        {
            try
            {
                TreeCodec.ValidateName(p);
            }
            catch (KobianException)
            {
                throw NotFound();
            }
        }
        return parts;
    }

    public async Task<BrowseListing> ListAsync(SnapshotObject snapshot, string? path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var segments = SplitPath(path);
        var tree = snapshot.RootTreeId;
        foreach (var seg in segments)
        {
            ct.ThrowIfCancellationRequested();
            var entries = await _reader.ReadTreeAsync(tree, ct).ConfigureAwait(false);
            var dir = entries.FirstOrDefault(e => string.Equals(e.Name, seg, StringComparison.Ordinal));
            if (dir is not { Kind: EntryKind.Directory, SubtreeId: { } sub }) throw NotFound();
            tree = sub;
        }

        var list = new List<BrowseEntry>();
        foreach (var e in await _reader.ReadTreeAsync(tree, ct).ConfigureAwait(false))
        {
            try
            {
                TreeCodec.ValidateName(e.Name);
            }
            catch (KobianException)
            {
                continue;
            }
            var kind = e.Kind switch
            {
                EntryKind.Directory when e.SubtreeId is not null => BrowseEntryKind.Directory,
                EntryKind.File => BrowseEntryKind.File,
                _ => BrowseEntryKind.Link,
            };
            DateTimeOffset? mtime = e.MtimeNs is { } ns && ns >= 0 && ns / 100 <= DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks
                ? DateTimeOffset.UnixEpoch.AddTicks(ns / 100)
                : null;
            list.Add(new BrowseEntry(e.Name, kind, kind == BrowseEntryKind.File ? Math.Max(0, e.Size) : 0, mtime));
        }
        var ordered = list
            .OrderBy(e => e.Kind == BrowseEntryKind.Directory ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        var truncated = ordered.Count > MaxEntries;
        return new BrowseListing(string.Join('/', segments), truncated ? ordered[..MaxEntries] : ordered, truncated);
    }

    private static KobianException NotFound() =>
        new(KbCode.SnapshotPathNotFound, ErrorClass.Permanent, "The snapshot has no folder at the given path.");
}
