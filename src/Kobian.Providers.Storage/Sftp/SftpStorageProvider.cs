using System.Runtime.CompilerServices;
using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Sftp;

/// <summary>
/// Dostawca „sftp” (BL-33, D4.4, D4.7). Ochrona: korzeń walidowany, ścieżki repo tylko w korzeniu, odmowa przejścia przez
/// dowiązania symboliczne, brak nadpisywania (zwykły rename, nigdy posix-rename — K24.3), usuwanie tylko w sesji Maintenance.
/// Poświadczenia i pinning klucza hosta należą do <see cref="ISftpConnector"/> (sekret przez SecretRef — AC-01.3).
/// </summary>
public sealed class SftpStorageProviderFactory(ISftpConnector connector) : IStorageProviderFactory
{
    public string Scheme => "sftp";

    public async ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.Equals(config.Scheme, Scheme, StringComparison.Ordinal))
            throw new ArgumentException("Scheme mismatch.", nameof(config));
        var root = NormalizeRoot(config.Root);
        ISftpTransport? transport = null;
        try
        {
            transport = await connector.ConnectAsync(config, role, ct).ConfigureAwait(false);
            var st = await transport.LstatAsync(root, ct).ConfigureAwait(false);
            if (st is null || st.Kind != SftpEntryKind.Directory)
                throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination root does not exist or is not a directory.");
            return new SftpStorageSession(transport, root, role);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            throw SftpErrorMap.Map(ex);
        }
    }

    /// <summary>Korzeń: bezwzględna ścieżka POSIX, bez „..”, „.” i znaków sterujących; bez końcowego „/”.</summary>
    internal static string NormalizeRoot(string? root)
    {
        if (string.IsNullOrEmpty(root) || root.Length > 1024 || root[0] != '/' || root.Any(char.IsControl))
            throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination root must be an absolute POSIX path.");
        var segs = root.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Any(s => s is "." or ".."))
            throw new StorageException(ErrorClass.Security, KbCode.InvalidObjectName, "Destination root must not contain relative segments.");
        return segs.Length == 0 ? "/" : "/" + string.Join('/', segs);
    }
}

internal sealed class SftpStorageSession : StorageSessionBase, IRemoteSourceReader
{
    private const long PackSize = 16 * 1024 * 1024;
    private readonly ISftpTransport _t;
    private readonly string _root;
    private readonly HashSet<string> _verifiedDirs = new(StringComparer.Ordinal);

    public SftpStorageSession(ISftpTransport transport, string root, CredentialRole role)
        : base(role)
    {
        _t = transport;
        _root = root;
        var caps = StorageCaps.AtomicRename | StorageCaps.ResumeUpload | StorageCaps.RangeRead | StorageCaps.RenameAllowed | StorageCaps.Mtime;
        if ((transport.Features & SftpFeatures.StatVfs) != 0) caps |= StorageCaps.FreeSpace;
        if (role == CredentialRole.Maintenance) caps |= StorageCaps.DeleteAllowed;
        Capabilities = new ProviderCapabilities(caps, 255, 3, PackSize, null, DateTimeOffset.UtcNow);
    }

    public override ProviderCapabilities Capabilities { get; }

    public override ServerIdentity? PresentedIdentity => _t.Identity;

    internal string Full(string relative) => _root == "/" ? "/" + relative : _root + "/" + relative;

    /// <summary>Każdy istniejący katalog w drodze do <paramref name="relativeDir"/> musi być zwykłym katalogiem (nie dowiązaniem).</summary>
    private async ValueTask EnsureDirectoryChainAsync(string relativeDir, bool create, CancellationToken ct)
    {
        if (relativeDir.Length == 0) return;
        var current = "";
        foreach (var seg in relativeDir.Split('/'))
        {
            current = current.Length == 0 ? seg : current + "/" + seg;
            if (_verifiedDirs.Contains(current)) continue;
            var st = await _t.LstatAsync(Full(current), ct).ConfigureAwait(false);
            if (st is null)
            {
                if (!create) return;
                await _t.CreateDirectoryAsync(Full(current), ct).ConfigureAwait(false);
            }
            else if (st.Kind == SftpEntryKind.Symlink)
            {
                throw new StorageException(ErrorClass.Security, KbCode.ReparsePointRefused, "Repository path contains a link; refusing.");
            }
            else if (st.Kind != SftpEntryKind.Directory)
            {
                throw new StorageException(ErrorClass.Permanent, KbCode.InvalidObjectName, "Repository path component is not a directory.");
            }
            _verifiedDirs.Add(current);
        }
    }

    private static string DirOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    protected override async ValueTask<StatResult?> StatCoreAsync(string path, CancellationToken ct)
    {
        try
        {
            await EnsureDirectoryChainAsync(DirOf(path), create: false, ct).ConfigureAwait(false);
            var st = await _t.LstatAsync(Full(path), ct).ConfigureAwait(false);
            if (st is null) return null;
            if (st.Kind == SftpEntryKind.Symlink)
                throw new StorageException(ErrorClass.Security, KbCode.ReparsePointRefused, "Repository path is a link; refusing.");
            return new StatResult(path, st.Kind == SftpEntryKind.Directory ? 0 : st.Length, st.Modified, st.Kind == SftpEntryKind.Directory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw SftpErrorMap.Map(ex);
        }
    }

    protected override async IAsyncEnumerable<StatResult> ListCoreAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(prefix);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IReadOnlyList<SftpEntry> entries;
            try
            {
                var st = await _t.LstatAsync(dir.Length == 0 ? _root : Full(dir), ct).ConfigureAwait(false);
                if (st is null || st.Kind != SftpEntryKind.Directory) continue;
                entries = await _t.ListDirectoryAsync(dir.Length == 0 ? _root : Full(dir), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw SftpErrorMap.Map(ex);
            }
            foreach (var e in entries)
            {
                if (e.Name is "." or "..") continue;
                var rel = dir.Length == 0 ? e.Name : dir + "/" + e.Name;
                if (!StoragePath.IsValid(rel)) continue; // obce pliki (nie-repo) pomijane
                if (e.Kind == SftpEntryKind.Directory) stack.Push(rel);
                else if (e.Kind == SftpEntryKind.File) yield return new StatResult(rel, e.Length, e.Modified, false);
                // dowiązania i inne typy: pomijane (D8.9)
            }
        }
    }

    protected override async ValueTask<Stream> OpenReadCoreAsync(string path, long offset, long? length, CancellationToken ct)
    {
        try
        {
            await EnsureDirectoryChainAsync(DirOf(path), create: false, ct).ConfigureAwait(false);
            var st = await _t.LstatAsync(Full(path), ct).ConfigureAwait(false);
            if (st is null) throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Object not found.");
            if (st.Kind != SftpEntryKind.File)
                throw new StorageException(ErrorClass.Security, KbCode.ReparsePointRefused, "Repository path is not a regular file; refusing.");
            if (offset > st.Length) throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Read offset beyond end of object.");
            var stream = await _t.OpenReadAsync(Full(path), offset, ct).ConfigureAwait(false);
            return length is null ? stream : new BoundedReadStream(stream, Math.Min(length.Value, st.Length - offset));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw SftpErrorMap.Map(ex);
        }
    }

    protected override async ValueTask<IObjectWriter> CreateCoreAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct)
    {
        try
        {
            await EnsureDirectoryChainAsync(DirOf(path), create: true, ct).ConfigureAwait(false);
            var final = Full(path);
            if (mode == WriteMode.StagedRename)
            {
                if (resumeFrom != 0) throw new NotSupportedException("SFTP resumes only in DirectFinal mode.");
                if (await _t.LstatAsync(final, ct).ConfigureAwait(false) is not null) throw Exists();
                var tmp = final + ".tmp-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
                var h = await _t.OpenWriteAsync(tmp, SftpOpenMode.CreateNew, 0, ct).ConfigureAwait(false);
                return new SftpObjectWriter(_t, h, tmp, final, expectedLength, 0, mode, path);
            }
            if (resumeFrom == 0)
            {
                if (await _t.LstatAsync(final, ct).ConfigureAwait(false) is not null) throw Exists();
                var h = await _t.OpenWriteAsync(final, SftpOpenMode.CreateNew, 0, ct).ConfigureAwait(false);
                return new SftpObjectWriter(_t, h, final, final, expectedLength, 0, mode, path);
            }
            var existing = await _t.LstatAsync(final, ct).ConfigureAwait(false);
            if (existing is null || existing.Kind != SftpEntryKind.File || existing.Length != resumeFrom)
                throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Server offset differs from committed offset; abandon object.");
            var rh = await _t.OpenWriteAsync(final, SftpOpenMode.OpenExisting, resumeFrom, ct).ConfigureAwait(false);
            return new SftpObjectWriter(_t, rh, final, final, expectedLength, resumeFrom, mode, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NotSupportedException)
        {
            throw SftpErrorMap.Map(ex);
        }
    }

    protected override async ValueTask<long?> GetFreeSpaceCoreAsync(CancellationToken ct)
    {
        if ((Capabilities.Caps & StorageCaps.FreeSpace) == 0) return null;
        try
        {
            return await _t.FreeSpaceAsync(_root, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw SftpErrorMap.Map(ex);
        }
    }

    protected override async ValueTask DeleteCoreAsync(string path, CancellationToken ct)
    {
        try
        {
            await EnsureDirectoryChainAsync(DirOf(path), create: false, ct).ConfigureAwait(false);
            await _t.DeleteFileAsync(Full(path), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw SftpErrorMap.Map(ex);
        }
    }

    public override ValueTask DisposeAsync() => _t.DisposeAsync();

    // ---- serwer jako ŹRÓDŁO kopii (IRemoteSourceReader): dowolne nazwy pod korzeniem, bez śledzenia dowiązań ----

    public async ValueTask<IReadOnlyList<RemoteSourceEntry>> ListDirectoryAsync(string relativeDir, CancellationToken ct)
    {
        try
        {
            var full = RemoteSourcePath.Join(_root, relativeDir);
            var st = await _t.LstatAsync(full, ct).ConfigureAwait(false)
                     ?? throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Source folder not found.");
            if (st.Kind != SftpEntryKind.Directory)
                throw new StorageException(ErrorClass.Permanent, KbCode.ReparsePointRefused, "Source path is not a folder (links are not followed).");
            var entries = await _t.ListDirectoryAsync(full, ct).ConfigureAwait(false);
            return [.. entries.Where(e => e.Name is not ("." or "..")).Select(e => new RemoteSourceEntry(e.Name,
                e.Kind switch { SftpEntryKind.File => RemoteSourceKind.File, SftpEntryKind.Directory => RemoteSourceKind.Directory, _ => RemoteSourceKind.Other },
                e.Length, e.Modified))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw SftpErrorMap.Map(ex);
        }
    }

    public async ValueTask<Stream> OpenFileAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            var full = RemoteSourcePath.Join(_root, relativePath);
            var st = await _t.LstatAsync(full, ct).ConfigureAwait(false)
                     ?? throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Source file not found.");
            if (st.Kind != SftpEntryKind.File)
                throw new StorageException(ErrorClass.Permanent, KbCode.ReparsePointRefused, "Source path is not a regular file.");
            return await _t.OpenReadAsync(full, 0, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw SftpErrorMap.Map(ex);
        }
    }

    internal static StorageException Exists() =>
        new(ErrorClass.Permanent, KbCode.ObjectExists, "Object already exists on destination; never overwritten.");
}
