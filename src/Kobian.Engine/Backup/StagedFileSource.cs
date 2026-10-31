namespace Kobian.Engine.Backup;

/// <summary>
/// Źródło jednego pliku ze stagingu (D5.10) — np. <c>.bak</c> z <c>BACKUP … COPY_ONLY</c>. Snapshot zawiera drzewo z jednym wpisem
/// o nazwie <paramref name="entryName"/>; ścieżka stagingu (losowa) nie trafia do snapshotu — tylko etykieta logiczna.
/// </summary>
public sealed class StagedFileSource : ISourceTree
{
    private readonly string _file;
    private readonly string _entry;

    public StagedFileSource(string stagedFile, string entryName, string displayRoot)
    {
        ArgumentNullException.ThrowIfNull(stagedFile);
        ArgumentException.ThrowIfNullOrEmpty(entryName);
        if (entryName.Contains('/', StringComparison.Ordinal) || entryName.Contains('\\', StringComparison.Ordinal) || entryName is "." or "..")
            throw new ArgumentException("Entry name must be a single segment.", nameof(entryName));
        _file = Path.GetFullPath(stagedFile);
        _entry = entryName;
        DisplayRoot = displayRoot ?? throw new ArgumentNullException(nameof(displayRoot));
    }

    public string DisplayRoot { get; }

    public IEnumerable<SourceNode> List(string relativeDir)
    {
        if (relativeDir.Length != 0) throw new DirectoryNotFoundException("Staged source has only a root.");
        var fi = new FileInfo(_file);
        if (!fi.Exists) throw new FileNotFoundException("Staged file missing.");
        if (fi.LinkTarget is not null) throw new UnauthorizedAccessException("Staged file is a link; refusing.");
        return [new SourceNode(_entry, SourceKind.File, fi.Length, (fi.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100)];
    }

    public Stream OpenRead(string relativePath)
    {
        if (!string.Equals(relativePath, _entry, StringComparison.Ordinal)) throw new FileNotFoundException("Unknown entry.");
        return new FileStream(_file, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = 0 });
    }
}
