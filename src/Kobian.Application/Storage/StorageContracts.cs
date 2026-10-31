using System.Security.Cryptography;

namespace Kobian.Application.Storage;

// Kontrakt dostawcy magazynu v1 — D4.6 (R2.4.1). Zmiany wyłącznie przez ADR.

/// <summary>Rozdział poświadczeń (D4.1): sesja Backup nigdy nie usuwa.</summary>
public enum CredentialRole
{
    Backup = 1,
    Maintenance = 2,
}

/// <summary>Tryb zapisu obiektu (D4.7).</summary>
public enum WriteMode
{
    StagedRename = 1,
    DirectFinal = 2,
}

[Flags]
public enum StorageCaps : uint
{
    None = 0,
    AtomicRename = 1,
    ResumeUpload = 2,
    RangeRead = 4,
    ServerSideHash = 8,
    FreeSpace = 16,
    DeleteAllowed = 32,
    OverwriteAllowed = 64,
    RenameAllowed = 128,
    Mtime = 256,
}

public sealed record ProviderCapabilities(
    StorageCaps Caps,
    int MaxObjectNameLength,
    int RecommendedParallelism,
    long RecommendedPackSize,
    HashAlgorithmName? ServerHash,
    DateTimeOffset ProbedAt);

public sealed record StatResult(string Path, long Length, DateTimeOffset? Modified, bool IsDirectory);

/// <summary>Tożsamość serwera do pinningu (D4.11): „ssh-ed25519” / „tls-spki”.</summary>
public sealed record ServerIdentity(string Kind, string Fingerprint, string Display);

/// <summary>Konfiguracja celu bez sekretów — sekret przez <see cref="SecretRef"/> (AC-01.3).</summary>
public sealed record DestinationConfig(
    string Id,
    string Scheme,
    string Root,
    string? SecretRef = null,
    IReadOnlyDictionary<string, string>? Options = null);

public interface IStorageProviderFactory
{
    /// <summary>„local”, „smb”, „ftp”, „ftps”, „sftp”.</summary>
    string Scheme { get; }

    ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct);
}

/// <summary>Sesja jednowątkowa; równoległość = wiele sesji (D4.10). Wszystkie błędy jako <see cref="StorageException"/>.</summary>
public interface IStorageSession : IAsyncDisposable
{
    CredentialRole Role { get; }

    ProviderCapabilities Capabilities { get; }

    ServerIdentity? PresentedIdentity { get; }

    ValueTask<StatResult?> StatAsync(string path, CancellationToken ct);

    IAsyncEnumerable<StatResult> ListAsync(string prefix, CancellationToken ct);

    ValueTask<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct);

    ValueTask<IObjectWriter> CreateAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct);

    ValueTask<long?> GetFreeSpaceAsync(CancellationToken ct);

    /// <summary>Tylko <see cref="CredentialRole.Maintenance"/> — inaczej <see cref="InvalidOperationException"/>.</summary>
    ValueTask DeleteAsync(string path, CancellationToken ct);

    ValueTask<byte[]?> ServerHashAsync(string path, HashAlgorithmName algorithm, CancellationToken ct);
}

public interface IObjectWriter : IAsyncDisposable
{
    /// <summary>Bajty potwierdzone przez serwer.</summary>
    long CommittedOffset { get; }

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>StagedRename: rename bez nadpisania; DirectFinal: flush + close. Weryfikuje rozmiar (KB-2401).</summary>
    ValueTask<StatResult> CommitAsync(CancellationToken ct);

    /// <summary>StagedRename: usuwa plik tymczasowy, jeśli wolno; DirectFinal: tylko zamyka.</summary>
    ValueTask AbortAsync(CancellationToken ct);
}
