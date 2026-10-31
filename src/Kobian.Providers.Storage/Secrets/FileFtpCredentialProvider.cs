using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Ftp;
using Kobian.Providers.Storage.Sftp;

namespace Kobian.Providers.Storage.Secrets;

/// <summary>
/// Poświadczenia celu FTP/FTPS z katalogu sekretów Agenta (BL-34): ten sam format i te same kontrole co SFTP/SMB (<see cref="FileSftpCredentialProvider"/>,
/// PROWIZORYCZNIE Qb16 / ADR-0007): <c>{"backup":{"user":"kopie","password":"…"},"maintenance":{…}}</c>. Klucz prywatny nie ma sensu dla FTP —
/// wpis bez hasła = odmowa KB-2102. Komunikaty bez wartości sekretów i ścieżek (NFR-11). Docelowo DPAPI (Windows, BL-24 / W-21).
/// </summary>
public sealed class FileFtpCredentialProvider(string secretsDir) : IFtpCredentialProvider
{
    private readonly FileSftpCredentialProvider _store = new(secretsDir);

    public async ValueTask<FtpCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct)
    {
        SftpCredential c;
        try
        {
            c = await _store.ResolveAsync(secretRef, role, ct).ConfigureAwait(false);
        }
        catch (SftpProtocolException ex)
        {
            throw new StorageException(ErrorClass.Permanent, KbCode.AuthRejected, ex.Message, ex);
        }
        if (c.PrivateKey is { } key) System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        if (string.IsNullOrEmpty(c.Password))
            throw new StorageException(ErrorClass.Permanent, KbCode.AuthRejected, "FTP credential entry has no password.");
        return new FtpCredential(c.UserName, c.Password);
    }
}
