using Kobian.Agent.Jobs;
using Kobian.Providers.Storage.Secrets;
using Kobian.Application.Storage;
using Kobian.Providers.Storage.Ftp;
using Kobian.Providers.Storage.Local;
using Kobian.Providers.Storage.Sftp;
using Kobian.Providers.Storage.Smb;
using Kobian.Providers.Storage.Throttling;
using Kobian.Providers.Storage.WebDav;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Rejestr dostawców celu w Agencie: local, sftp (SSH.NET + poświadczenia z katalogu sekretów — PROWIZORYCZNIE Qb16), ftps/ftp (FluentFTP, BL-34; „ftp” jawny
/// tylko z opcją insecure — D4.3, KB-2115), webdav (HTTPS na HttpClient; poświadczenie użytkownik + hasło w formacie FTP) i smb (tylko Windows, BL-73).
/// Cele sieciowe (sftp, ftps, ftp, smb) otwierają sesje przez wspólny <see cref="SessionOpenGovernor"/> (D4.16 pkt 3, SEC-24.4 — BL-77); dysk lokalny bez dławienia.
/// </summary>
public static class AgentProviders
{
    /// <param name="smbConnector">
    /// BL-73: konektor SMB (Windows: <c>WNetSmbConnector</c>, opakowany we wspólne połączenia <see cref="SharedSmbConnector"/>); null = cel „smb” niedostępny
    /// na tej platformie (poza Windows brak natywnego klienta — D4.2).
    /// </param>
    /// <param name="governor">Wspólny ogranicznik otwarć sesji; null = nowy z zegarem systemowym (jeden na proces Agenta).</param>
    /// <param name="identities">
    /// BL-35 (D4.11, SEC-14.5): katalog tożsamości serwerów — piny SFTP/FTPS z katalogu (zatwierdzane przez Administratora), nie z definicji zadania
    /// (<see cref="ServerIdentityGate"/>). Null = piny wyłącznie z konfiguracji celu (testy dostawców, narzędzia bez katalogu).
    /// </param>
    public static Dictionary<string, IStorageProviderFactory> Create(string secretsDir, ISmbConnector? smbConnector = null, SessionOpenGovernor? governor = null,
        ServerIdentityStore? identities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsDir);
        governor ??= new SessionOpenGovernor(TimeProvider.System);
        var sftp = new GovernedStorageProviderFactory(new SftpStorageProviderFactory(new SshNetSftpConnector(new FileSftpCredentialProvider(secretsDir))), governor);
        var map = new Dictionary<string, IStorageProviderFactory>(StringComparer.Ordinal)
        {
            [new LocalStorageProviderFactory().Scheme] = new LocalStorageProviderFactory(),
            [sftp.Scheme] = sftp,
        };
        var ftpConnector = new FluentFtpConnector(new FileFtpCredentialProvider(secretsDir));
        foreach (var scheme in new[] { "ftps", "ftp" })
        {
            var ftp = new GovernedStorageProviderFactory(new FtpStorageProviderFactory(ftpConnector, scheme), governor);
            map[ftp.Scheme] = ftp;
        }
        var webdav = new GovernedStorageProviderFactory(new WebDavStorageProviderFactory(new HttpWebDavConnector(new FileFtpCredentialProvider(secretsDir))), governor);
        map[webdav.Scheme] = webdav;
        if (smbConnector is not null)
        {
            var smb = new GovernedStorageProviderFactory(new SmbStorageProviderFactory(smbConnector, new FileSmbCredentialProvider(secretsDir)), governor);
            map[smb.Scheme] = smb;
        }
        return identities is null ? map : ServerIdentityGate.Wrap(map, identities);
    }
}
