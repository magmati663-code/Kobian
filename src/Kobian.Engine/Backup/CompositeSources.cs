using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Engine.Backup;

/// <summary>Źródło z kilkoma korzeniami (snapshot zapisuje listę etykiet źródeł zamiast jednej).</summary>
public interface IMultiRootSource
{
    IReadOnlyList<string> DisplayRoots { get; }
}

/// <summary>
/// Kilka źródeł w jednym zadaniu: korzeń kopii zawiera po jednym katalogu na źródło (nazwa = ostatni segment ścieżki, przy powtórzeniu „ (2)”, „ (3)” …),
/// więc w przeglądarce kopii i przy odtwarzaniu każde źródło jest osobnym folderem. Kolejność i nazwy zależą tylko od listy źródeł (stabilne między kopiami).
/// Zadanie z jednym źródłem lokalnym używa <see cref="FileSystemSource"/> bezpośrednio — układ kopii sprzed tej zmiany się nie zmienia.
/// </summary>
public sealed class CompositeSourceTree : ISourceTree, IMultiRootSource
{
    private readonly List<(string Name, ISourceTree Tree)> _parts = [];

    public CompositeSourceTree(IReadOnlyList<(string Label, ISourceTree Tree)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) throw new ArgumentException("At least one source is required.", nameof(sources));
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, tree) in sources)
        {
            var baseName = FolderName(label);
            var name = baseName;
            for (var i = 2; !used.Add(name); i++) name = baseName + " (" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            _parts.Add((name, tree));
        }
        DisplayRoots = [.. sources.Select(s => s.Tree.DisplayRoot)];
    }

    public string DisplayRoot => string.Join("; ", DisplayRoots);

    public bool MetadataReliable => _parts.TrueForAll(p => p.Tree.MetadataReliable);

    public IReadOnlyList<string> DisplayRoots { get; }

    /// <summary>Nazwy katalogów najwyższego poziomu w kolejności źródeł (testy, podgląd).</summary>
    public IReadOnlyList<string> FolderNames => [.. _parts.Select(p => p.Name)];

    /// <summary>Ostatni segment ścieżki (Windows, POSIX, URL); korzeń dysku „C:\” → „C”, pusty → „źródło”. Znaki niedozwolone w nazwie drzewa zastępowane „_”.</summary>
    internal static string FolderName(string label)
    {
        var t = label.Trim().TrimEnd('/', '\\');
        var cut = t.LastIndexOfAny(['/', '\\']);
        var name = cut >= 0 ? t[(cut + 1)..] : t;
        name = name.TrimEnd(':');
        name = new string([.. name.Select(c => c is '/' or '\\' or '\0' || char.IsControl(c) ? '_' : c)]);
        return name is "" or "." or ".." ? "zrodlo" : name;
    }

    public IEnumerable<SourceNode> List(string relativeDir)
    {
        if (relativeDir.Length == 0)
        {
            foreach (var (name, tree) in _parts)
            {
                // znacznik czasu katalogu-źródła z jego korzenia nie jest dostępny jednolicie (zdalne) — 0 = nieznany
                yield return new SourceNode(name, SourceKind.Directory, 0, 0);
            }
            yield break;
        }
        var (part, rest) = Route(relativeDir);
        foreach (var n in part.List(rest)) yield return n;
    }

    public Stream OpenRead(string relativePath)
    {
        var (part, rest) = Route(relativePath);
        return part.OpenRead(rest);
    }

    private (ISourceTree Part, string Remainder) Route(string relative)
    {
        var slash = relative.IndexOf('/', StringComparison.Ordinal);
        var head = slash < 0 ? relative : relative[..slash];
        var rest = slash < 0 ? "" : relative[(slash + 1)..];
        foreach (var (name, tree) in _parts)
            if (string.Equals(name, head, StringComparison.Ordinal)) return (tree, rest);
        throw new DirectoryNotFoundException("Unknown source folder.");
    }
}

/// <summary>
/// Serwer (SFTP, FTP/FTPS, WebDAV) jako źródło kopii — adapter <see cref="IRemoteSourceReader"/> na synchroniczny port <see cref="ISourceTree"/>.
/// Błędy: brak dostępu / nie ma pliku (trwałe) → <see cref="IOException"/>, więc element jest pomijany z ostrzeżeniem jak lokalnie;
/// zerwane połączenie, limit sesji, tożsamość serwera (przejściowe/bezpieczeństwo) → <see cref="KobianException"/>, więc uruchomienie kończy się błędem
/// z kodem KB i jest ponawiane — a nie „udaną” kopią z tysiącami pominiętych plików.
/// </summary>
public sealed class RemoteSourceTree(IRemoteSourceReader reader, string displayRoot) : ISourceTree
{
    private readonly IRemoteSourceReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public string DisplayRoot { get; } = displayRoot ?? throw new ArgumentNullException(nameof(displayRoot));

    public IEnumerable<SourceNode> List(string relativeDir)
    {
        IReadOnlyList<RemoteSourceEntry> entries;
        try
        {
            entries = _reader.ListDirectoryAsync(relativeDir, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch (StorageException ex)
        {
            throw Map(ex);
        }
        foreach (var e in entries)
        {
            var mtime = e.Modified is { } m ? (m.UtcDateTime - DateTime.UnixEpoch).Ticks * 100 : 0;
            yield return e.Kind switch
            {
                RemoteSourceKind.Directory => new SourceNode(e.Name, SourceKind.Directory, 0, mtime),
                RemoteSourceKind.File => new SourceNode(e.Name, SourceKind.File, e.Length, mtime),
                _ => new SourceNode(e.Name, SourceKind.Special, 0, mtime), // dowiązania i inne typy: pominięte z ostrzeżeniem
            };
        }
    }

    public Stream OpenRead(string relativePath)
    {
        try
        {
            return _reader.OpenFileAsync(relativePath, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch (StorageException ex)
        {
            throw Map(ex);
        }
    }

    private static Exception Map(StorageException ex) =>
        ex.ErrorClass == ErrorClass.Permanent && ex.Code is KbCode.AccessDenied or KbCode.DestinationNotFound or KbCode.ReparsePointRefused or KbCode.InvalidObjectName
            ? new IOException("Source item is not readable.", ex)
            : new KobianException(ex.Code, ex.ErrorClass, "Source server error.", ex);
}
