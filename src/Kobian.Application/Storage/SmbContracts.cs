namespace Kobian.Application.Storage;

// Port SMB (BL-73, D4.2): dostawca „smb” zna tylko ten port. Adapter Windows (WNetAddConnection2 bez litery dysku,
// WNetCancelConnection2 po sesji) leży w Kobian.Platform.Windows; w testach — fałszywy konektor na katalogu tymczasowym.

/// <summary>Udział SMB po walidacji ścieżki: serwer, udział i ścieżka repo względem udziału (bez separatorów na brzegach).</summary>
public sealed record SmbShare(string Server, string Share, string RelativePath)
{
    /// <summary><c>\\serwer\udział</c> — zasób przekazywany do WNetAddConnection2 (połączenie bez litery dysku).</summary>
    public string ShareUnc => @"\\" + Server + @"\" + Share;

    /// <summary>Pełna ścieżka UNC korzenia repozytorium.</summary>
    public string RootUnc => RelativePath.Length == 0 ? ShareUnc : ShareUnc + @"\" + RelativePath;
}

/// <summary>
/// Poświadczenie celu SMB (D4.1: osobne dla roli Backup i Maintenance). <see cref="User"/> w postaci „DOMENA\konto”, „konto@domena” albo „konto”.
/// Nigdy nie trafia do logów, wyjątków ani katalogu (NFR-11, AC-01.3).
/// </summary>
public sealed record SmbCredential(string User, string Password)
{
    public override string ToString() => "SmbCredential(" + User + ", ***)";
}

/// <summary>Rozwiązuje <c>SecretRef</c> celu do poświadczenia SMB dla roli (brak wpisu dla roli = odmowa, bez cichego przejścia na drugą rolę).</summary>
public interface ISmbCredentialProvider
{
    ValueTask<SmbCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct);
}

/// <summary>
/// Otwarte połączenie z udziałem. <see cref="LocalRoot"/> to ścieżka systemu plików, pod którą widać korzeń repozytorium
/// (Windows: ścieżka UNC; test: katalog tymczasowy). Zwolnienie = rozłączenie (WNetCancelConnection2, bez wymuszania — D4.16 pkt 4).
/// </summary>
public interface ISmbConnection : IAsyncDisposable
{
    string LocalRoot { get; }
}

/// <summary>Nawiązuje połączenie z udziałem z jawnym poświadczeniem. Błąd Win32 → <see cref="SmbConnectException"/> (mapowanie D4.8 w dostawcy).</summary>
public interface ISmbConnector
{
    ValueTask<ISmbConnection> ConnectAsync(SmbShare share, SmbCredential credential, CancellationToken ct);
}

/// <summary>Błąd nawiązania połączenia z kodem Win32 (np. 1326, 1219, 53). Komunikat bez nazw serwera, kont i haseł.</summary>
public sealed class SmbConnectException : Exception
{
    public SmbConnectException(int win32Error, string message, Exception? inner = null)
        : base(message, inner) => Win32Error = win32Error;

    public SmbConnectException()
        : this(0, "SMB connection failed")
    {
    }

    public SmbConnectException(string message)
        : this(0, message)
    {
    }

    public SmbConnectException(string message, Exception innerException)
        : this(0, message, innerException)
    {
    }

    public int Win32Error { get; }
}
