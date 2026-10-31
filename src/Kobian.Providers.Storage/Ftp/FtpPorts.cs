using Kobian.Application.Storage;

namespace Kobian.Providers.Storage.Ftp;

// Port transportu FTP/FTPS (BL-34, D4.3, D4.14). Dostawca (FtpStorageSession) zna tylko ten port; adapter FluentFTP (AsyncFtpClient)
// jest osobnym, cienkim tłumaczem odpowiedzi serwera i wyjątków biblioteki na FtpProtocolException. Wzór: Sftp/SftpPorts.cs.

/// <summary>Typ wpisu (MLST/MLSD „type=”; dowiązania — „OS.unix=symlink” lub „l” w LIST) — bez podążania za dowiązaniami.</summary>
public enum FtpEntryKind
{
    File = 1,
    Directory = 2,
    Link = 3,
    Other = 4,
}

public sealed record FtpEntry(string Name, FtpEntryKind Kind, long Length, DateTimeOffset? Modified);

/// <summary>Rozszerzenia serwera z FEAT (RFC 2389/3659, draft-bryan-ftpext-hash, draft-peterson-streamlined-ftp-command-extensions).</summary>
[Flags]
public enum FtpFeatures
{
    None = 0,

    /// <summary>MLST/MLSD — maszynowe listingi (bez parsowania LIST).</summary>
    Mlsd = 1,

    /// <summary>SIZE — wyłącznie w trybie TYPE I (D4.14, CE-22).</summary>
    Size = 2,

    /// <summary>REST STREAM — odczyt od offsetu (RangeRead).</summary>
    Rest = 4,

    /// <summary>HASH z SHA-256 (V1-a, D4.14).</summary>
    HashSha256 = 8,

    /// <summary>AVBL — wolne miejsce.</summary>
    Avbl = 16,
}

/// <summary>Klasyfikacja odpowiedzi/awarii FTP istotna dla mapy D4.8 (wiersze „FTP” i „FTP / FTPS”).</summary>
public enum FtpFailure
{
    /// <summary>4xx (425, 426, 450, 451…), zerwane połączenie, timeout — KB-2001.</summary>
    Transient = 1,

    /// <summary>421 z informacją o limicie połączeń — KB-2112 (D4.16).</summary>
    SessionLimit = 2,

    /// <summary>530 — KB-2102 (bez ponowień: blokada konta).</summary>
    AuthRejected = 3,

    /// <summary>452 / 552 — KB-2201.</summary>
    NoSpace = 4,

    /// <summary>550 przy odczycie/stat korzenia — KB-2106.</summary>
    NotFound = 5,

    /// <summary>550 przy STOR/MKD/RNTO, inne trwałe 5xx — KB-2105.</summary>
    PermissionDenied = 6,

    /// <summary>553 — nazwa niedozwolona — KB-2302.</summary>
    NameNotAllowed = 7,

    /// <summary>Cel już istnieje (rename/STOR bez nadpisania) — KB-2301.</summary>
    AlreadyExists = 8,

    /// <summary>Błąd walidacji TLS lub niezgodny SPKI (D4.11) — KB-2109.</summary>
    TlsIdentityRejected = 9,

    /// <summary>Odmowa AUTH TLS przy wymaganym FTPS — KB-2110.</summary>
    TlsNotSupported = 10,

    /// <summary>Jawny FTP bez flagi „insecure” — KB-2115 (D4.3; PROWIZORYCZNIE Qb73 — nowy kod zamiast KB-2110).</summary>
    InsecureNotConfirmed = 11,
}

public sealed class FtpProtocolException : Exception
{
    public FtpProtocolException(FtpFailure failure, string message, Exception? inner = null, int? replyCode = null)
        : base(message, inner)
    {
        Failure = failure;
        ReplyCode = replyCode;
    }

    public FtpProtocolException()
        : this(FtpFailure.Transient, "FTP error")
    {
    }

    public FtpProtocolException(string message)
        : this(FtpFailure.Transient, message)
    {
    }

    public FtpProtocolException(string message, Exception inner)
        : this(FtpFailure.Transient, message, inner)
    {
    }

    public FtpFailure Failure { get; }

    /// <summary>Kod odpowiedzi serwera (np. 550) — do logu jako „ftp=550” (SEC-4.6: nigdy treść komend USER/PASS).</summary>
    public int? ReplyCode { get; }

    /// <summary>Tożsamość TLS przedstawiona przez serwer przy odmowie (KB-2109) — publiczny odcisk SPKI do zatwierdzenia (D4.11).</summary>
    public ServerIdentity? PresentedIdentity { get; init; }
}

/// <summary>Otwarty transfer STOR/APPE. Sesja FTP ma jeden kanał sterowania — do <see cref="CompleteAsync"/>/Dispose inne komendy są niedozwolone.</summary>
public interface IFtpUpload : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Zamyka kanał danych i czyta odpowiedź końcową (226/250). 226 NIE jest dowodem zapisu (D4.14) — rozmiar sprawdza sesja przez SIZE.</summary>
    ValueTask CompleteAsync(CancellationToken ct);
}

/// <summary>
/// Jedno połączenie sterujące FTP/FTPS (z kanałami danych w trybie pasywnym). Ścieżki są bezwzględne po stronie serwera (korzeń + ścieżka repo).
/// Implementacja wysyła <c>TYPE I</c> przed każdą operacją danych i przed <c>SIZE</c> (D4.14).
/// </summary>
public interface IFtpTransport : IAsyncDisposable
{
    /// <summary>„tls-spki” dla FTPS; null dla jawnego FTP.</summary>
    ServerIdentity? Identity { get; }

    FtpFeatures Features { get; }

    /// <summary>MLST (lub wpis z listingu katalogu nadrzędnego) bez podążania za dowiązaniem; null, gdy brak (550).</summary>
    ValueTask<FtpEntry?> StatAsync(string path, CancellationToken ct);

    /// <summary>MLSD (bez „.”/„..”).</summary>
    ValueTask<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string path, CancellationToken ct);

    /// <summary>SIZE w TYPE I; null, gdy pliku nie ma (550).</summary>
    ValueTask<long?> SizeAsync(string path, CancellationToken ct);

    /// <summary>REST + RETR (TYPE I). Zwrócony strumień trzeba zamknąć przed kolejną komendą (czyta odpowiedź 226/426).</summary>
    ValueTask<Stream> OpenReadAsync(string path, long offset, CancellationToken ct);

    /// <summary><paramref name="resumeFrom"/> = 0: STOR (wywołujący sprawdził, że plik nie istnieje); &gt; 0: APPE (dopisanie — wznowienie D4.7).</summary>
    ValueTask<IFtpUpload> OpenUploadAsync(string path, long resumeFrom, CancellationToken ct);

    /// <summary>
    /// RNFR/RNTO bez nadpisania: sprawdzenie istnienia celu tuż przed RNTO (FTP nie ma flagi „no-replace”; na serwerach uniksowych RNTO = rename(2),
    /// które nadpisuje). Okno wyścigu między sprawdzeniem a RNTO opisane w docs/admin/cel-ftps.md. Cel istnieje → <see cref="FtpFailure.AlreadyExists"/>.
    /// </summary>
    ValueTask RenameNoReplaceAsync(string source, string destination, CancellationToken ct);

    ValueTask CreateDirectoryAsync(string path, CancellationToken ct);

    ValueTask DeleteFileAsync(string path, CancellationToken ct);

    /// <summary>AVBL (bajty wolne) albo null.</summary>
    ValueTask<long?> FreeSpaceAsync(string path, CancellationToken ct);

    /// <summary>HASH SHA-256 (RFC draft-bryan-ftpext-hash) albo null, gdy serwer nie wspiera.</summary>
    ValueTask<byte[]?> HashSha256Async(string path, CancellationToken ct);
}

/// <summary>Nawiązuje połączenie dla celu (adapter FluentFTP: tryb TLS wg D4.3 i pinning SPKI D4.11; test: serwer w pamięci).</summary>
public interface IFtpConnector
{
    ValueTask<IFtpTransport> ConnectAsync(DestinationConfig config, CredentialRole role, CancellationToken ct);
}
