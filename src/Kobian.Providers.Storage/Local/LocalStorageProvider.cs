using System.Runtime.CompilerServices;
using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Local;

/// <summary>
/// Dostawca „local” (dysk lokalny / USB; KOSZT-19 — bez GUID wolumenu i wysuwania, które wymagają Windows).
/// Ochrona: ścieżki tylko wewnątrz korzenia, odmowa przejścia przez dowiązania (reparse) w drzewie repo,
/// brak nadpisywania (KB-2301), usuwanie tylko w sesji Maintenance (D4.1).
/// </summary>
public sealed class LocalStorageProviderFactory : IStorageProviderFactory
{
    public string Scheme => "local";

    public ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.Equals(config.Scheme, Scheme, StringComparison.Ordinal))
            throw new ArgumentException("Scheme mismatch.", nameof(config));
        if (!Path.IsPathFullyQualified(config.Root))
            throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination root must be an absolute path.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.Root));
        var info = new DirectoryInfo(root);
        if (!info.Exists)
            throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination root does not exist.");
        if (info.LinkTarget is not null)
            throw new StorageException(ErrorClass.Security, KbCode.ReparsePointRefused, "Destination root is a link; refusing.");
        return ValueTask.FromResult<IStorageSession>(new LocalStorageSession(root, role));
    }
}

internal sealed class LocalStorageSession : StorageSessionBase
{
    private const long PackSize = 16 * 1024 * 1024;
    private readonly string _root;
    private readonly IAsyncDisposable? _owner;

    /// <param name="parallelism">Zalecana równoległość (4.7: Local 2, SMB 4).</param>
    /// <param name="owner">Zasób zwalniany razem z sesją (SMB: połączenie z udziałem — BL-73).</param>
    public LocalStorageSession(string root, CredentialRole role, int parallelism = 2, IAsyncDisposable? owner = null)
        : base(role)
    {
        _root = root;
        _owner = owner;
        var caps = StorageCaps.AtomicRename | StorageCaps.ResumeUpload | StorageCaps.RangeRead | StorageCaps.FreeSpace
                   | StorageCaps.RenameAllowed | StorageCaps.Mtime;
        if (role == CredentialRole.Maintenance) caps |= StorageCaps.DeleteAllowed;
        Capabilities = new ProviderCapabilities(caps, 255, parallelism, PackSize, null, DateTimeOffset.UtcNow);
    }

    public override ProviderCapabilities Capabilities { get; }

    /// <summary>Mapuje ścieżkę repo na pełną ścieżkę lokalną; odrzuca wyjście poza korzeń i dowiązania w drodze.</summary>
    internal string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Join(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathBoundary.IsUnder(_root, full))
            throw new StorageException(ErrorClass.Security, KbCode.InvalidObjectName, "Path escapes destination root.");
        // Każdy istniejący składnik między korzeniem a celem nie może być dowiązaniem (D8.9 — ta sama zasada dla celu).
        var current = _root;
        foreach (var seg in relative.Split('/'))
        {
            current = Path.Join(current, seg);
            FileSystemInfo fsi = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (fsi.Exists && fsi.LinkTarget is not null)
                throw new StorageException(ErrorClass.Security, KbCode.ReparsePointRefused, "Repository path contains a link; refusing.");
        }
        return full;
    }

    protected override ValueTask<StatResult?> StatCoreAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        return Guard(() =>
        {
            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                return (StatResult?)new StatResult(path, fi.Length, fi.LastWriteTimeUtc, false);
            }
            if (Directory.Exists(full))
                return new StatResult(path, 0, Directory.GetLastWriteTimeUtc(full), true);
            return null;
        });
    }

    protected override async IAsyncEnumerable<StatResult> ListCoreAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var dir = prefix.Length == 0 ? _root : Resolve(prefix);
        if (!Directory.Exists(dir)) yield break;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*", options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw LocalErrorMap.Map(ex);
        }
        // Enumeracja jest leniwa: wyjątki (plik przeniesiony przez równoległy StagedRename, odłączony nośnik, brak uprawnień do podkatalogu) lecą z MoveNext,
        // a nie z EnumerateFiles — kontrakt wymaga StorageException. Ręczny enumerator, bo yield nie może stać w try/catch.
        using var en = files.GetEnumerator();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string f;
            try
            {
                // Wyjątek z MoveNext (podkatalog zniknął w trakcie rekursji, odłączony nośnik) NIE jest „plik zniknął”: enumerator po nim jest zwolniony, a ciche pominięcie kończyłoby
                // listę bez błędu i niepełną — RepoChecker/prune uznałyby istniejące obiekty za nieistniejące. Zawsze błąd magazynu.
                if (!en.MoveNext()) break;
                f = en.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw LocalErrorMap.Map(ex);
            }
            var rel = Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/');
            if (!StoragePath.IsValid(rel)) continue; // obce pliki (nie-repo) pomijane
            StatResult item;
            try
            {
                var fi = new FileInfo(f);
                item = new StatResult(rel, fi.Length, fi.LastWriteTimeUtc, false);
            }
            catch (FileNotFoundException)
            {
                continue; // ten jeden plik zniknął między wyliczeniem a odczytem rozmiaru (np. tymczasowy plik po rename)
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw LocalErrorMap.Map(ex);
            }
            yield return item;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    protected override ValueTask<Stream> OpenReadCoreAsync(string path, long offset, long? length, CancellationToken ct)
    {
        var full = Resolve(path);
        return Guard(() =>
        {
            var fs = new FileStream(full, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous });
            try
            {
                if (offset > fs.Length) throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Read offset beyond end of object.");
                fs.Position = offset;
                return (Stream)(length is null ? fs : new BoundedReadStream(fs, Math.Min(length.Value, fs.Length - offset)));
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        });
    }

    protected override ValueTask<IObjectWriter> CreateCoreAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct)
    {
        var final = Resolve(path);
        return Guard(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (mode == WriteMode.StagedRename)
            {
                if (resumeFrom != 0) throw new NotSupportedException("Local provider resumes only in DirectFinal mode.");
                if (File.Exists(final)) throw Exists();
                var tmp = final + ".tmp-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
                var fs = Open(tmp, FileMode.CreateNew);
                return (IObjectWriter)new LocalObjectWriter(fs, tmp, final, expectedLength, 0, mode, path);
            }
            if (resumeFrom == 0)
            {
                return new LocalObjectWriter(Open(final, FileMode.CreateNew), final, final, expectedLength, 0, mode, path);
            }
            var existing = Open(final, FileMode.Open);
            if (existing.Length != resumeFrom)
            {
                existing.Dispose();
                throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Server offset differs from committed offset; abandon object.");
            }
            existing.Position = resumeFrom;
            return new LocalObjectWriter(existing, final, final, expectedLength, resumeFrom, mode, path);
        });
    }

    protected override ValueTask<long?> GetFreeSpaceCoreAsync(CancellationToken ct) =>
        Guard(() => (long?)new DriveInfo(_root).AvailableFreeSpace);

    protected override ValueTask DeleteCoreAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        return Guard(() =>
        {
            File.Delete(full);
            return true;
        }).AsNonGeneric();
    }

    public override ValueTask DisposeAsync() => _owner?.DisposeAsync() ?? ValueTask.CompletedTask;

    private static FileStream Open(string full, FileMode mode) =>
        new(full, new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous });

    internal static StorageException Exists() =>
        new(ErrorClass.Permanent, KbCode.ObjectExists, "Object already exists on destination; never overwritten.");

    private static ValueTask<T> Guard<T>(Func<T> action)
    {
        try
        {
            return ValueTask.FromResult(action());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw LocalErrorMap.Map(ex);
        }
    }
}

internal static class ValueTaskExtensions
{
    public static async ValueTask AsNonGeneric<T>(this ValueTask<T> task) => await task.ConfigureAwait(false);
}
