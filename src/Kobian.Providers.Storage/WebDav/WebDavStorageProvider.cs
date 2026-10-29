using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Sftp;

namespace Kobian.Providers.Storage.WebDav;

/// <summary>
/// Dostawca „webdav” (HTTPS; Nextcloud, ownCloud, Synology, QNAP, Apache mod_dav, nginx, IIS z Basic). Ochrona jak SFTP/FTPS (D4.6, D4.7): korzeń walidowany,
/// ścieżki repo tylko w korzeniu, usuwanie tylko w sesji Maintenance. Brak nadpisywania jest tu silniejszy niż w FTP:
/// StagedRename = PUT do pliku tymczasowego (If-None-Match: *) + MOVE z „Overwrite: F”, więc serwer sam odmawia (412), gdy cel już istnieje — bez okna wyścigu.
/// Brak wznawiania (WebDAV nie ma częściowego PUT w standardzie) — obiekt jest wysyłany w całości (pack ≤ 16 MiB, D4.10).
/// Poświadczenia, TLS i pinning SPKI należą do <see cref="IWebDavConnector"/>.
/// </summary>
public sealed class WebDavStorageProviderFactory(IWebDavConnector connector) : IStorageProviderFactory
{
    private readonly IWebDavConnector _connector = connector ?? throw new ArgumentNullException(nameof(connector));

    public string Scheme => "webdav";

    public async ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.Equals(config.Scheme, Scheme, StringComparison.Ordinal))
            throw new ArgumentException("Scheme mismatch.", nameof(config));
        var root = SftpStorageProviderFactory.NormalizeRoot(config.Root);
        IWebDavTransport? transport = null;
        try
        {
            transport = await _connector.ConnectAsync(config, role, ct).ConfigureAwait(false);
            // Pierwsze żądanie: TLS + weryfikacja certyfikatu (przed wysłaniem hasła), potem uwierzytelnienie i sprawdzenie korzenia.
            var st = await transport.StatAsync(root, ct).ConfigureAwait(false);
            if (st is null || st.Kind != WebDavEntryKind.Collection)
                throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination root does not exist or is not a collection.");
            long? quota = null;
            try
            {
                quota = await transport.QuotaAvailableAsync(root, ct).ConfigureAwait(false);
            }
            catch (StorageException ex) when (ex.ErrorClass != ErrorClass.Security)
            {
                // RFC 4331 jest opcjonalne — brak wsparcia nie blokuje celu.
            }
            return new WebDavStorageSession(transport, root, role, quota is not null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            throw WebDavErrorMap.FromException(ex, null);
        }
    }
}

internal sealed class WebDavStorageSession : StorageSessionBase, IRemoteSourceReader
{
    private const long PackSize = 16 * 1024 * 1024;

    /// <summary>PROWIZORYCZNIE: 3 sesje jak SFTP (serwery HTTP zwykle przyjmują więcej; AIMD i tak zmniejsza przy 429/503 → KB-2112).</summary>
    private const int Parallelism = 3;

    /// <summary>Górna granica obiektu buforowanego w pamięci przed PUT (pack, indeks, snapshot — wszystkie dużo mniejsze).</summary>
    internal const long MaxObjectBytes = 256L * 1024 * 1024;

    private readonly IWebDavTransport _t;
    private readonly string _root;
    private readonly HashSet<string> _verifiedDirs = new(StringComparer.Ordinal);

    public WebDavStorageSession(IWebDavTransport transport, string root, CredentialRole role, bool quota)
        : base(role)
    {
        _t = transport;
        _root = root;
        var caps = StorageCaps.AtomicRename | StorageCaps.RenameAllowed | StorageCaps.RangeRead | StorageCaps.Mtime;
        if (quota) caps |= StorageCaps.FreeSpace;
        if (role == CredentialRole.Maintenance) caps |= StorageCaps.DeleteAllowed;
        Capabilities = new ProviderCapabilities(caps, 255, Parallelism, PackSize, null, DateTimeOffset.UtcNow);
    }

    public override ProviderCapabilities Capabilities { get; }

    public override ServerIdentity? PresentedIdentity => _t.Identity;

    internal IWebDavTransport Transport => _t;

    internal string Full(string relative) => _root == "/" ? "/" + relative : _root + "/" + relative;

    /// <summary>Każdy istniejący element w drodze do <paramref name="relativeDir"/> musi być kolekcją; brakujące tworzone przez MKCOL.</summary>
    private async ValueTask EnsureCollectionChainAsync(string relativeDir, bool create, CancellationToken ct)
    {
        if (relativeDir.Length == 0) return;
        var current = "";
        foreach (var seg in relativeDir.Split('/'))
        {
            current = current.Length == 0 ? seg : current + "/" + seg;
            if (_verifiedDirs.Contains(current)) continue;
            var st = await _t.StatAsync(Full(current), ct).ConfigureAwait(false);
            if (st is null)
            {
                if (!create) return;
                // 405 = zasób już jest (np. równoległa sesja utworzyła go chwilę wcześniej) — wtedy musi to być kolekcja.
                if (!await _t.MakeCollectionAsync(Full(current), ct).ConfigureAwait(false)
                    && await _t.StatAsync(Full(current), ct).ConfigureAwait(false) is not { Kind: WebDavEntryKind.Collection })
                    throw new StorageException(ErrorClass.Permanent, KbCode.InvalidObjectName, "Repository path component is not a collection.");
            }
            else if (st.Kind != WebDavEntryKind.Collection)
            {
                throw new StorageException(ErrorClass.Permanent, KbCode.InvalidObjectName, "Repository path component is not a collection.");
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
            var st = await _t.StatAsync(Full(path), ct).ConfigureAwait(false);
            if (st is null) return null;
            return new StatResult(path, st.Kind == WebDavEntryKind.Collection ? 0 : st.Length, st.Modified, st.Kind == WebDavEntryKind.Collection);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WebDavErrorMap.FromException(ex, null);
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
            IReadOnlyList<WebDavEntry> entries;
            try
            {
                var full = dir.Length == 0 ? _root : Full(dir);
                var st = await _t.StatAsync(full, ct).ConfigureAwait(false);
                if (st is null || st.Kind != WebDavEntryKind.Collection) continue;
                entries = await _t.ListAsync(full, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw WebDavErrorMap.FromException(ex, null);
            }
            foreach (var e in entries)
            {
                var rel = dir.Length == 0 ? e.Name : dir + "/" + e.Name;
                if (!StoragePath.IsValid(rel)) continue; // obce pliki (nie-repo, w tym pliki tymczasowe) pomijane
                if (e.Kind == WebDavEntryKind.Collection) stack.Push(rel);
                else yield return new StatResult(rel, e.Length, e.Modified, false);
            }
        }
    }

    protected override async ValueTask<Stream> OpenReadCoreAsync(string path, long offset, long? length, CancellationToken ct)
    {
        try
        {
            var st = await _t.StatAsync(Full(path), ct).ConfigureAwait(false);
            if (st is null) throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Object not found.");
            if (st.Kind != WebDavEntryKind.File)
                throw new StorageException(ErrorClass.Permanent, KbCode.InvalidObjectName, "Repository path is not a regular file.");
            if (offset > st.Length) throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Read offset beyond end of object.");
            var stream = await _t.GetAsync(Full(path), offset, ct).ConfigureAwait(false);
            return new BoundedReadStream(stream, Math.Min(length ?? long.MaxValue, st.Length - offset));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WebDavErrorMap.FromException(ex, null);
        }
    }

    protected override async ValueTask<IObjectWriter> CreateCoreAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct)
    {
        if (expectedLength > MaxObjectBytes)
            throw new StorageException(ErrorClass.Permanent, KbCode.DestinationObjectTooLarge, "Object too large for a single WebDAV upload.");
        try
        {
            await EnsureCollectionChainAsync(DirOf(path), create: true, ct).ConfigureAwait(false);
            var final = Full(path);
            if (await _t.StatAsync(final, ct).ConfigureAwait(false) is not null) throw Exists();
            var write = mode == WriteMode.StagedRename ? final + ".tmp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) : final;
            return new WebDavObjectWriter(_t, write, final, expectedLength, mode, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WebDavErrorMap.FromException(ex, null);
        }
    }

    protected override async ValueTask<long?> GetFreeSpaceCoreAsync(CancellationToken ct)
    {
        if ((Capabilities.Caps & StorageCaps.FreeSpace) == 0) return null;
        try
        {
            return await _t.QuotaAvailableAsync(_root, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WebDavErrorMap.FromException(ex, null);
        }
    }

    protected override async ValueTask DeleteCoreAsync(string path, CancellationToken ct)
    {
        try
        {
            // DELETE na kolekcji usuwa całe poddrzewo (RFC 4918 §9.6.1) — repozytorium usuwa wyłącznie pliki.
            if (await _t.StatAsync(Full(path), ct).ConfigureAwait(false) is { Kind: WebDavEntryKind.Collection })
                throw new StorageException(ErrorClass.Permanent, KbCode.InvalidObjectName, "Refusing to delete a collection.");
            await _t.DeleteAsync(Full(path), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WebDavErrorMap.FromException(ex, null);
        }
    }

    public override ValueTask DisposeAsync() => _t.DisposeAsync();

    // ---- serwer jako ŹRÓDŁO kopii (IRemoteSourceReader): dowolne nazwy pod korzeniem ----

    public async ValueTask<IReadOnlyList<RemoteSourceEntry>> ListDirectoryAsync(string relativeDir, CancellationToken ct)
    {
        try
        {
            var full = RemoteSourcePath.Join(_root, relativeDir);
            var st = await _t.StatAsync(full, ct).ConfigureAwait(false)
                     ?? throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Source folder not found.");
            if (st.Kind != WebDavEntryKind.Collection)
                throw new StorageException(ErrorClass.Permanent, KbCode.InvalidObjectName, "Source path is not a folder.");
            var entries = await _t.ListAsync(full, ct).ConfigureAwait(false);
            return [.. entries.Select(e => new RemoteSourceEntry(e.Name, e.Kind == WebDavEntryKind.Collection ? RemoteSourceKind.Directory : RemoteSourceKind.File, e.Length, e.Modified))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WebDavErrorMap.FromException(ex, null);
        }
    }

    public async ValueTask<Stream> OpenFileAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            var full = RemoteSourcePath.Join(_root, relativePath);
            var st = await _t.StatAsync(full, ct).ConfigureAwait(false)
                     ?? throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Source file not found.");
            if (st.Kind != WebDavEntryKind.File)
                throw new StorageException(ErrorClass.Permanent, KbCode.InvalidObjectName, "Source path is not a file.");
            return await _t.GetAsync(full, 0, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WebDavErrorMap.FromException(ex, null);
        }
    }

    internal static StorageException Exists() =>
        new(ErrorClass.Permanent, KbCode.ObjectExists, "Object already exists on destination; never overwritten.");
}

/// <summary>
/// Zapis obiektu WebDAV: dane zbierane w pamięci, wysyłane jednym PUT przy Commit (żądanie powtarzalne — bez strumienia, którego nie da się odtworzyć).
/// Abort przed Commit nie zostawia niczego na serwerze. Po zapisie rozmiar sprawdzany przez PROPFIND (D4.14, KB-2401).
/// </summary>
internal sealed class WebDavObjectWriter : IObjectWriter
{
    private readonly IWebDavTransport _t;
    private readonly string _writePath;
    private readonly string _finalPath;
    private readonly WriteMode _mode;
    private readonly string _relative;
    private readonly byte[] _buffer;
    private bool _closed;

    public WebDavObjectWriter(IWebDavTransport t, string writePath, string finalPath, long expected, WriteMode mode, string relative)
    {
        _t = t;
        _writePath = writePath;
        _finalPath = finalPath;
        _mode = mode;
        _relative = relative;
        _buffer = new byte[expected];
    }

    /// <summary>Bajty przyjęte do bufora (serwer potwierdza dopiero Commit — wznowienie nie jest obsługiwane).</summary>
    public long CommittedOffset { get; private set; }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (CommittedOffset + data.Length > _buffer.Length)
            throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Write exceeds expected object length.");
        data.Span.CopyTo(_buffer.AsSpan((int)CommittedOffset));
        CommittedOffset += data.Length;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<StatResult> CommitAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _closed = true;
        if (CommittedOffset != _buffer.Length)
            throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Object length differs from expected.");
        try
        {
            await _t.PutAsync(_writePath, _buffer, ifNoneMatch: true, ct).ConfigureAwait(false);
            if (_mode == WriteMode.StagedRename)
            {
                // Serwer odmawia MOVE na istniejący cel (Overwrite: F → 412 = KB-2301) — rename bez nadpisania bez okna wyścigu.
                // Plik tymczasowy po nieudanym MOVE zostaje: sesja Backup nie usuwa (D4.1) — porządkuje prune/Maintenance.
                await _t.MoveNoReplaceAsync(_writePath, _finalPath, ct).ConfigureAwait(false);
            }
            var fin = await _t.StatAsync(_finalPath, ct).ConfigureAwait(false);
            if (fin is null || fin.Kind != WebDavEntryKind.File || fin.Length != _buffer.Length)
                throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Committed object length differs from expected.");
            return new StatResult(_relative, fin.Length, fin.Modified, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw WebDavErrorMap.FromException(ex, null);
        }
    }

    public ValueTask AbortAsync(CancellationToken ct)
    {
        _closed = true; // nic nie zostało wysłane przed Commit
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => AbortAsync(CancellationToken.None);
}
