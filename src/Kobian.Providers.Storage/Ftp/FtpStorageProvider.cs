using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Sftp;

namespace Kobian.Providers.Storage.Ftp;

/// <summary>
/// Dostawca „ftps” (domyślny, D4.3: Explicit AUTH TLS → Implicit 990) albo „ftp” (jawny — tylko z opcją <c>insecure=1</c>, KB-2115) — BL-34, KOSZT-22.
/// Ochrona jak SFTP (D4.6, D4.7): korzeń walidowany, ścieżki repo tylko w korzeniu, odmowa przejścia przez dowiązania, brak nadpisywania
/// (sprawdzenie istnienia przed STOR/RNTO — FTP nie ma flagi „no-replace”; okno wyścigu w docs/admin/cel-ftps.md), usuwanie tylko w sesji Maintenance.
/// Poświadczenia, tryb TLS i pinning SPKI należą do <see cref="IFtpConnector"/> (sekret przez SecretRef — AC-01.3).
/// </summary>
public sealed class FtpStorageProviderFactory : IStorageProviderFactory
{
    /// <summary>Opcja celu „insecure” = „1” — jawne potwierdzenie FTP bez szyfrowania (D4.3; poświadczenia lecą otwartym tekstem).</summary>
    public const string InsecureOption = "insecure";

    private readonly IFtpConnector _connector;

    /// <param name="scheme">„ftps” albo „ftp”.</param>
    public FtpStorageProviderFactory(IFtpConnector connector, string scheme = "ftps")
    {
        ArgumentNullException.ThrowIfNull(connector);
        if (scheme is not ("ftps" or "ftp")) throw new ArgumentException("Scheme must be 'ftps' or 'ftp'.", nameof(scheme));
        _connector = connector;
        Scheme = scheme;
    }

    public string Scheme { get; }

    /// <summary>Opóźnienie między ponowieniami SIZE po zapisie (D4.14: do 3 × co 1 s); podmieniane w testach.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public async ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.Equals(config.Scheme, Scheme, StringComparison.Ordinal))
            throw new ArgumentException("Scheme mismatch.", nameof(config));
        // D4.3: jawny FTP tylko z flagą — sprawdzane PRZED połączeniem, żeby hasło nie poszło otwartym tekstem (adapter sprawdza ponownie).
        if (Scheme == "ftp" && !IsInsecureConfirmed(config))
            throw FtpErrorMap.Map(new FtpProtocolException(FtpFailure.InsecureNotConfirmed, "Unencrypted FTP requires the 'insecure' option."));
        var root = SftpStorageProviderFactory.NormalizeRoot(config.Root);
        IFtpTransport? transport = null;
        try
        {
            transport = await _connector.ConnectAsync(config, role, ct).ConfigureAwait(false);
            var st = await transport.StatAsync(root, ct).ConfigureAwait(false);
            if (st is null || st.Kind != FtpEntryKind.Directory)
                throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination root does not exist or is not a directory.");
            return new FtpStorageSession(transport, root, role, Delay);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            throw FtpErrorMap.Map(ex);
        }
    }

    internal static bool IsInsecureConfirmed(DestinationConfig config) =>
        config.Options is not null && config.Options.TryGetValue(InsecureOption, out var v) && v == "1";
}

internal sealed class FtpStorageSession : StorageSessionBase
{
    private const long PackSize = 16 * 1024 * 1024;

    /// <summary>
    /// PROWIZORYCZNIE Qb69: 2 sesje (serwery FTP często limitują połączenia na IP — vsftpd max_per_ip, NAS); AIMD (D4.16) i tak
    /// zmniejsza przy KB-2112. Alternatywa: 3 jak SFTP. Koszt zmiany: stała.
    /// </summary>
    private const int Parallelism = 2;

    private const int SizeAttempts = 3;
    private readonly IFtpTransport _t;
    private readonly string _root;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly HashSet<string> _verifiedDirs = new(StringComparer.Ordinal);

    public FtpStorageSession(IFtpTransport transport, string root, CredentialRole role, Func<TimeSpan, CancellationToken, Task> delay)
        : base(role)
    {
        _t = transport;
        _root = root;
        _delay = delay;
        // PROWIZORYCZNIE Qb71. StagedRename: tmp + RNTO poprzedzone sprawdzeniem istnienia (D4.7). Bez RenameNoReplace (D4.12 — bit poza kontraktem v1): RNTO na serwerach
        // uniksowych nadpisuje, więc gwarancja „bez nadpisania” ma okno wyścigu — tak samo jak sprawdzenie przed STOR w DirectFinal.
        var caps = StorageCaps.AtomicRename | StorageCaps.RenameAllowed | StorageCaps.ResumeUpload;
        var f = transport.Features;
        if ((f & FtpFeatures.Rest) != 0) caps |= StorageCaps.RangeRead;
        if ((f & FtpFeatures.Mlsd) != 0) caps |= StorageCaps.Mtime;
        if ((f & FtpFeatures.Avbl) != 0) caps |= StorageCaps.FreeSpace;
        if ((f & FtpFeatures.HashSha256) != 0) caps |= StorageCaps.ServerSideHash;
        if (role == CredentialRole.Maintenance) caps |= StorageCaps.DeleteAllowed;
        Capabilities = new ProviderCapabilities(caps, 255, Parallelism, PackSize,
            (f & FtpFeatures.HashSha256) != 0 ? HashAlgorithmName.SHA256 : null, DateTimeOffset.UtcNow);
    }

    public override ProviderCapabilities Capabilities { get; }

    public override ServerIdentity? PresentedIdentity => _t.Identity;

    internal IFtpTransport Transport => _t;

    internal Func<TimeSpan, CancellationToken, Task> Delay => _delay;

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
            var st = await _t.StatAsync(Full(current), ct).ConfigureAwait(false);
            if (st is null)
            {
                if (!create) return;
                try
                {
                    await _t.CreateDirectoryAsync(Full(current), ct).ConfigureAwait(false);
                }
                catch (StorageException)
                {
                    // Dwie równoległe sesje (Parallelism = 2) tworzą ten sam katalog: druga dostaje 550 „exists”, które klasyfikator czyta jako brak uprawnień (trwały).
                    // Jeśli katalog już jest (utworzony przez drugą sesję) — to nie błąd; w przeciwnym razie oryginalny wyjątek.
                    var again = await _t.StatAsync(Full(current), ct).ConfigureAwait(false);
                    if (again is null || again.Kind != FtpEntryKind.Directory) throw;
                }
            }
            else if (st.Kind == FtpEntryKind.Link)
            {
                throw new StorageException(ErrorClass.Security, KbCode.ReparsePointRefused, "Repository path contains a link; refusing.");
            }
            else if (st.Kind != FtpEntryKind.Directory)
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
            var st = await _t.StatAsync(Full(path), ct).ConfigureAwait(false);
            if (st is null) return null;
            if (st.Kind == FtpEntryKind.Link)
                throw new StorageException(ErrorClass.Security, KbCode.ReparsePointRefused, "Repository path is a link; refusing.");
            if (st.Kind == FtpEntryKind.Directory) return new StatResult(path, 0, st.Modified, true);
            // D4.14: rozmiar z SIZE w TYPE I (MLST „size” bywa nieobecny albo przybliżony na starszych serwerach).
            var size = (_t.Features & FtpFeatures.Size) != 0 ? await _t.SizeAsync(Full(path), ct).ConfigureAwait(false) ?? st.Length : st.Length;
            return new StatResult(path, size, st.Modified, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw FtpErrorMap.Map(ex);
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
            IReadOnlyList<FtpEntry> entries;
            try
            {
                var st = await _t.StatAsync(dir.Length == 0 ? _root : Full(dir), ct).ConfigureAwait(false);
                if (st is null || st.Kind != FtpEntryKind.Directory) continue;
                entries = await _t.ListDirectoryAsync(dir.Length == 0 ? _root : Full(dir), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw FtpErrorMap.Map(ex);
            }
            foreach (var e in entries)
            {
                if (e.Name is "." or "..") continue;
                var rel = dir.Length == 0 ? e.Name : dir + "/" + e.Name;
                if (!StoragePath.IsValid(rel)) continue; // obce pliki (nie-repo) pomijane
                if (e.Kind == FtpEntryKind.Directory) stack.Push(rel);
                else if (e.Kind == FtpEntryKind.File) yield return new StatResult(rel, e.Length, e.Modified, false);
                // dowiązania i inne typy: pomijane (D8.9)
            }
        }
    }

    protected override async ValueTask<Stream> OpenReadCoreAsync(string path, long offset, long? length, CancellationToken ct)
    {
        try
        {
            await EnsureDirectoryChainAsync(DirOf(path), create: false, ct).ConfigureAwait(false);
            var st = await _t.StatAsync(Full(path), ct).ConfigureAwait(false);
            if (st is null) throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Object not found.");
            if (st.Kind != FtpEntryKind.File)
                throw new StorageException(ErrorClass.Security, KbCode.ReparsePointRefused, "Repository path is not a regular file; refusing.");
            var size = await _t.SizeAsync(Full(path), ct).ConfigureAwait(false) ?? st.Length;
            if (offset > size) throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Read offset beyond end of object.");
            var stream = await _t.OpenReadAsync(Full(path), offset, ct).ConfigureAwait(false);
            return length is null ? stream : new BoundedReadStream(stream, Math.Min(length.Value, size - offset));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw FtpErrorMap.Map(ex);
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
                if (resumeFrom != 0) throw new NotSupportedException("FTP resumes only in DirectFinal mode.");
                if (await _t.StatAsync(final, ct).ConfigureAwait(false) is not null) throw Exists();
                var tmp = final + ".tmp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
                if (await _t.StatAsync(tmp, ct).ConfigureAwait(false) is not null) throw Exists();
                var up = await _t.OpenUploadAsync(tmp, 0, ct).ConfigureAwait(false);
                return new FtpObjectWriter(this, up, tmp, final, expectedLength, 0, mode, path);
            }
            if (resumeFrom == 0)
            {
                // STOR nadpisuje — jedyną ochroną jest sprawdzenie tuż przed (okno wyścigu jak przy RNTO).
                if (await _t.StatAsync(final, ct).ConfigureAwait(false) is not null) throw Exists();
                var up = await _t.OpenUploadAsync(final, 0, ct).ConfigureAwait(false);
                return new FtpObjectWriter(this, up, final, final, expectedLength, 0, mode, path);
            }
            var existing = await _t.StatAsync(final, ct).ConfigureAwait(false);
            var size = existing is { Kind: FtpEntryKind.File } ? await _t.SizeAsync(final, ct).ConfigureAwait(false) : null;
            if (size != resumeFrom)
                throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Server offset differs from committed offset; abandon object.");
            var rh = await _t.OpenUploadAsync(final, resumeFrom, ct).ConfigureAwait(false);
            return new FtpObjectWriter(this, rh, final, final, expectedLength, resumeFrom, mode, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NotSupportedException)
        {
            throw FtpErrorMap.Map(ex);
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
            throw FtpErrorMap.Map(ex);
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
            throw FtpErrorMap.Map(ex);
        }
    }

    /// <summary>HASH SHA-256 (V1-a, D4.14), jeśli serwer wspiera; inaczej null (V1-b).</summary>
    public override async ValueTask<byte[]?> ServerHashAsync(string path, HashAlgorithmName algorithm, CancellationToken ct)
    {
        StoragePath.Require(path);
        if (algorithm != HashAlgorithmName.SHA256 || (Capabilities.Caps & StorageCaps.ServerSideHash) == 0) return null;
        try
        {
            await EnsureDirectoryChainAsync(DirOf(path), create: false, ct).ConfigureAwait(false);
            return await _t.HashSha256Async(Full(path), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw FtpErrorMap.Map(ex);
        }
    }

    /// <summary>D4.14: SIZE (TYPE I) ponawiane do 3 × co 1 s, gdy różni się od oczekiwanego (serwery z asynchronicznym zapisem); potem KB-2401.</summary>
    internal async ValueTask<long> SizeWithRetryAsync(string fullPath, long expected, CancellationToken ct)
    {
        long? size = null;
        for (var attempt = 1; attempt <= SizeAttempts; attempt++)
        {
            size = await _t.SizeAsync(fullPath, ct).ConfigureAwait(false);
            if (size == expected) return expected;
            if (attempt < SizeAttempts) await _delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Object length after write differs from expected.");
    }

    public override ValueTask DisposeAsync() => _t.DisposeAsync();

    internal static StorageException Exists() =>
        new(ErrorClass.Permanent, KbCode.ObjectExists, "Object already exists on destination; never overwritten.");
}
