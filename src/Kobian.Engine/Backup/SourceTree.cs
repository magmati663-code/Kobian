using Kobian.Domain;

namespace Kobian.Engine.Backup;

public enum SourceKind
{
    File = 0,
    Directory = 1,
    Symlink = 2,

    /// <summary>FIFO, gniazdo, urządzenie (Linux/macOS) — nie jest kopiowane: otwarcie FIFO blokuje się, urządzenie daje strumień bez końca.</summary>
    Special = 3,
}

/// <summary>Element źródła: nazwa (jeden segment), rodzaj, rozmiar, mtime w ns od epoki, cel dowiązania (nie podążamy — D3.6).</summary>
public sealed record SourceNode(string Name, SourceKind Kind, long Size, long MtimeNs, string? LinkTarget = null, uint? UnixMode = null);

/// <summary>Port źródła plików (platform-neutralny). Adapter Windows (VSS, uchwyty, ADS, ACL) — BL-23, poza tym portem.</summary>
public interface ISourceTree
{
    /// <summary>Etykieta źródła do snapshotu (klucz 16) — tylko wewnątrz zaszyfrowanego obiektu.</summary>
    string DisplayRoot { get; }

    /// <summary>Dzieci katalogu względnego ("" = korzeń). Błąd dostępu = <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>.</summary>
    IEnumerable<SourceNode> List(string relativeDir);

    Stream OpenRead(string relativePath);

    /// <summary>
    /// Czy rozmiar i czas modyfikacji wpisu wiarygodnie mówią „plik bez zmian” (system plików z dokładnym mtime). Domyślnie nie: serwery zdalne (listing FTP z dokładnością
    /// do minuty), pliki tymczasowe (.bak) — dla nich kopia przyrostowa zawsze czyta treść. Tylko wtedy działa skrót „plik bez zmian” (<see cref="ParentTree"/>).
    /// </summary>
    bool MetadataReliable => false;
}

/// <summary>Źródło z systemu plików (System.IO). Dowiązania zapisywane jako Symlink, nigdy nie są śledzone.</summary>
public sealed class FileSystemSource : ISourceTree
{
    private readonly string _root;

    public FileSystemSource(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Source root does not exist.");
    }

    public string DisplayRoot => _root;

    public bool MetadataReliable => true;

    public IEnumerable<SourceNode> List(string relativeDir)
    {
        var dir = new DirectoryInfo(Full(relativeDir));
        var unix = !OperatingSystem.IsWindows();
        foreach (var fsi in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }))
        {
            // Windows: wyliczanie katalogu podaje czas i rozmiar z WPISU katalogu NTFS, który bywa nieaktualny dla świeżo zapisanych plików
            // (aktualizuje się przy kolejnym otwarciu — np. przez samą kopię), więc następna kopia widziałaby „zmianę” i wysyłała nowe drzewo.
            // Refresh czyta rekord pliku (GetFileAttributesEx) — stabilne wartości między kopiami.
            if (!unix) fsi.Refresh();
            var mtime = (fsi.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100;
            // Linux/macOS: typ wpisu i uprawnienia z lstat (System.IO traktuje FIFO/gniazda/urządzenia jak zwykłe pliki).
            var st = unix ? UnixFileStat.TryLStat(fsi.FullName) : null;
            if (fsi.LinkTarget is not null)
                yield return new SourceNode(fsi.Name, SourceKind.Symlink, 0, mtime, fsi.LinkTarget);
            else if (fsi is DirectoryInfo)
                yield return new SourceNode(fsi.Name, SourceKind.Directory, 0, mtime, UnixMode: st?.Mode);
            else if (st is { Type: UnixFileType.Fifo or UnixFileType.Socket or UnixFileType.CharDevice or UnixFileType.BlockDevice })
                yield return new SourceNode(fsi.Name, SourceKind.Special, 0, mtime);
            else
                yield return new SourceNode(fsi.Name, SourceKind.File, ((FileInfo)fsi).Length, mtime, UnixMode: st?.Mode);
        }
    }

    public Stream OpenRead(string relativePath) =>
        new FileStream(Full(relativePath), new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 0 });

    private string Full(string rel)
    {
        if (rel.Length == 0) return _root;
        var full = Path.GetFullPath(Path.Join(_root, rel));
        if (!Kobian.Domain.PathBoundary.IsUnder(_root, full))
            throw new UnauthorizedAccessException("Path escapes source root.");
        return full;
    }
}
