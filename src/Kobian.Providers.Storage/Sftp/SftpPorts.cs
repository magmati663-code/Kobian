using Kobian.Application.Storage;

namespace Kobian.Providers.Storage.Sftp;

// Port transportu SFTP (BL-33, D4.4). Dostawca (SftpStorageSession) zna tylko ten port; adapter SSH.NET (SftpClient,
// bez ScpClient) jest osobnym, cienkim tłumaczem wyjątków biblioteki na SftpProtocolException.

/// <summary>Typ wpisu — bez podążania za dowiązaniami (lstat).</summary>
public enum SftpEntryKind
{
    File = 1,
    Directory = 2,
    Symlink = 3,
    Other = 4,
}

public sealed record SftpEntry(string Name, SftpEntryKind Kind, long Length, DateTimeOffset? Modified);

/// <summary>Rozszerzenia serwera (OpenSSH): fsync, statvfs, limits (R3.4 SP-21).</summary>
[Flags]
public enum SftpFeatures
{
    None = 0,
    Fsync = 1,
    StatVfs = 2,
    Limits = 4,
}

/// <summary>Kody statusu SFTP (draft-ietf-secsh-filexfer) istotne dla klasyfikacji D4.8.</summary>
public enum SftpStatus
{
    Failure = 4,
    NoConnection = 6,
    ConnectionLost = 7,
    OpUnsupported = 8,
    NoSuchFile = 2,
    PermissionDenied = 3,
    FileAlreadyExists = 11,
    NoSpaceOnFilesystem = 14,
    QuotaExceeded = 15,
    /// <summary>Serwer odmówił otwarcia kanału/sesji (MaxSessions, MaxStartups) — KB-2112.</summary>
    SessionLimit = 1001,
    /// <summary>Uwierzytelnienie odrzucone — KB-2102.</summary>
    AuthRejected = 1002,
    /// <summary>Klucz hosta niezgodny z przypiętym albo brak przypięcia — KB-2104.</summary>
    HostKeyMismatch = 1003,
}

public sealed class SftpProtocolException : Exception
{
    public SftpProtocolException(SftpStatus status, string message, Exception? inner = null)
        : base(message, inner) => Status = status;

    public SftpProtocolException()
        : this(SftpStatus.Failure, "SFTP error")
    {
    }

    public SftpProtocolException(string message)
        : this(SftpStatus.Failure, message)
    {
    }

    public SftpProtocolException(string message, Exception inner)
        : this(SftpStatus.Failure, message, inner)
    {
    }

    public SftpStatus Status { get; }

    /// <summary>Klucz hosta przedstawiony przez serwer, gdy nie był zgodny z przypiętym (HostKeyMismatch) — publiczny odcisk do zatwierdzenia (D4.11).</summary>
    public ServerIdentity? PresentedIdentity { get; init; }
}

public interface ISftpWriteHandle : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>fsync@openssh.com, jeśli serwer wspiera; inaczej no-op.</summary>
    ValueTask FlushAsync(CancellationToken ct);
}

public enum SftpOpenMode
{
    /// <summary>O_CREAT|O_EXCL — błąd, gdy istnieje.</summary>
    CreateNew = 1,

    /// <summary>Otwarcie istniejącego do dopisywania od offsetu (wznowienie).</summary>
    OpenExisting = 2,
}

/// <summary>Jedno połączenie SSH/SFTP. Ścieżki są bezwzględne po stronie serwera (korzeń + ścieżka repo).</summary>
public interface ISftpTransport : IAsyncDisposable
{
    ServerIdentity? Identity { get; }

    SftpFeatures Features { get; }

    /// <summary>lstat; null gdy brak.</summary>
    ValueTask<SftpEntry?> LstatAsync(string path, CancellationToken ct);

    ValueTask<IReadOnlyList<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct);

    ValueTask<Stream> OpenReadAsync(string path, long offset, CancellationToken ct);

    ValueTask<ISftpWriteHandle> OpenWriteAsync(string path, SftpOpenMode mode, long offset, CancellationToken ct);

    /// <summary>Zwykły SSH_FXP_RENAME — NIE posix-rename (ten nadpisuje cel; K24.3). Cel istnieje → błąd.</summary>
    ValueTask RenameNoReplaceAsync(string source, string destination, CancellationToken ct);

    ValueTask CreateDirectoryAsync(string path, CancellationToken ct);

    ValueTask DeleteFileAsync(string path, CancellationToken ct);

    /// <summary>statvfs@openssh.com (bajty wolne dla nieuprzywilejowanych) albo null.</summary>
    ValueTask<long?> FreeSpaceAsync(string path, CancellationToken ct);
}

/// <summary>Nawiązuje połączenie dla celu (adapter SSH.NET: pinning klucza hosta D4.11; test: serwer w pamięci).</summary>
public interface ISftpConnector
{
    ValueTask<ISftpTransport> ConnectAsync(DestinationConfig config, CredentialRole role, CancellationToken ct);
}
