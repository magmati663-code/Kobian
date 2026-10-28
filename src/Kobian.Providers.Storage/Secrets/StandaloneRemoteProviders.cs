using Kobian.Application.Storage;
using Kobian.Providers.Storage.Ftp;
using Kobian.Providers.Storage.Sftp;

namespace Kobian.Providers.Storage.Secrets;

/// <summary>
/// Dostawcy zdalni dla samodzielnego <c>kobian-restore</c> (BL-43/US-43, it. 126): SFTP i FTPS bez Agenta i katalogu — poświadczenia z pliku
/// w katalogu sekretów (ten sam format co Agent: <see cref="FileSftpCredentialProvider"/>), tożsamość serwera wyłącznie z argumentów
/// (<c>--hostkey</c> / <c>--spki</c>; brak katalogu tożsamości, D4.11). Jawny FTP nie jest dostępny w narzędziu awaryjnym (D4.3).
/// SMB: wymaga konektora Windows (WNet) — poza zakresem tej wersji narzędzia, patrz docs/kobian-restore.md.
/// </summary>
public static class StandaloneRemoteProviders
{
    public static IReadOnlyCollection<string> Schemes { get; } = ["sftp", "ftps"];

    /// <summary>Fabryka dla schematu albo null, gdy schemat nie jest obsługiwany w narzędziu samodzielnym.</summary>
    public static IStorageProviderFactory? Create(string secretsDir, string scheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsDir);
        return scheme switch
        {
            "sftp" => new SftpStorageProviderFactory(new SshNetSftpConnector(new FileSftpCredentialProvider(secretsDir))),
            "ftps" => new FtpStorageProviderFactory(new FluentFtpConnector(new FileFtpCredentialProvider(secretsDir)), "ftps"),
            _ => null,
        };
    }
}
