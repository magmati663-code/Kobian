using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Kobian.Application.Storage;

/// <summary>
/// Klasa bazowa sesji (R2.4.1 „Ryzyka”): walidacja ścieżek, egzekwowanie roli przy usuwaniu,
/// wykrywanie użycia współbieżnego (sesja jest jednowątkowa).
/// </summary>
public abstract class StorageSessionBase : IStorageSession
{
    private int _busy;

    protected StorageSessionBase(CredentialRole role) => Role = role;

    public CredentialRole Role { get; }

    public abstract ProviderCapabilities Capabilities { get; }

    public virtual ServerIdentity? PresentedIdentity => null;

    public async ValueTask<StatResult?> StatAsync(string path, CancellationToken ct)
    {
        StoragePath.Require(path);
        using var _ = Enter();
        return await StatCoreAsync(path, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StatResult> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!StoragePath.IsValidPrefix(prefix)) StoragePath.Require(prefix);
        await foreach (var item in ListCoreAsync(prefix, ct).ConfigureAwait(false))
            yield return item;
    }

    public async ValueTask<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct)
    {
        StoragePath.Require(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length is < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (offset > 0 && (Capabilities.Caps & StorageCaps.RangeRead) == 0)
            throw new NotSupportedException("Range read not supported by this destination.");
        using var _ = Enter();
        return await OpenReadCoreAsync(path, offset, length, ct).ConfigureAwait(false);
    }

    public async ValueTask<IObjectWriter> CreateAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct)
    {
        StoragePath.Require(path);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        ArgumentOutOfRangeException.ThrowIfNegative(resumeFrom);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(resumeFrom, expectedLength);
        if (resumeFrom > 0 && (Capabilities.Caps & StorageCaps.ResumeUpload) == 0)
            throw new NotSupportedException("Resume not supported by this destination.");
        if (mode == WriteMode.StagedRename && (Capabilities.Caps & (StorageCaps.AtomicRename | StorageCaps.RenameAllowed)) != (StorageCaps.AtomicRename | StorageCaps.RenameAllowed))
            throw new NotSupportedException("StagedRename requires AtomicRename and RenameAllowed.");
        using var _ = Enter();
        return await CreateCoreAsync(path, expectedLength, mode, resumeFrom, ct).ConfigureAwait(false);
    }

    public ValueTask<long?> GetFreeSpaceAsync(CancellationToken ct) => GetFreeSpaceCoreAsync(ct);

    public async ValueTask DeleteAsync(string path, CancellationToken ct)
    {
        // D4.6 zasada 3: nie ufamy samemu serwerowi — sesja Backup nigdy nie usuwa.
        if (Role != CredentialRole.Maintenance)
            throw new InvalidOperationException("Delete is not allowed in a backup-credential session (D4.1).");
        StoragePath.Require(path);
        using var _ = Enter();
        await DeleteCoreAsync(path, ct).ConfigureAwait(false);
    }

    public virtual ValueTask<byte[]?> ServerHashAsync(string path, HashAlgorithmName algorithm, CancellationToken ct)
    {
        StoragePath.Require(path);
        return ValueTask.FromResult<byte[]?>(null);
    }

    public abstract ValueTask DisposeAsync();

    protected abstract ValueTask<StatResult?> StatCoreAsync(string path, CancellationToken ct);

    protected abstract IAsyncEnumerable<StatResult> ListCoreAsync(string prefix, CancellationToken ct);

    protected abstract ValueTask<Stream> OpenReadCoreAsync(string path, long offset, long? length, CancellationToken ct);

    protected abstract ValueTask<IObjectWriter> CreateCoreAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct);

    protected abstract ValueTask<long?> GetFreeSpaceCoreAsync(CancellationToken ct);

    protected abstract ValueTask DeleteCoreAsync(string path, CancellationToken ct);

    private Guard Enter()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
            throw new InvalidOperationException("Storage session is single-threaded (D4.6 rule 1).");
        return new Guard(this);
    }

    private readonly struct Guard(StorageSessionBase owner) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref owner._busy, 0);
    }
}
