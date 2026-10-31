using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Sftp;

namespace Kobian.Providers.Storage.Secrets;

/// <summary>
/// Poświadczenia celu SMB z katalogu sekretów Agenta (BL-73): ten sam format i te same kontrole co SFTP (<see cref="FileSftpCredentialProvider"/>,
/// PROWIZORYCZNIE Qb16 / ADR-0007): <c>{"backup":{"user":"DOMENA\\konto","password":"…"},"maintenance":{…}}</c>. Klucz prywatny nie ma sensu dla SMB —
/// wpis bez hasła = odmowa KB-2102. Komunikaty bez wartości sekretów i ścieżek (NFR-11). Docelowo DPAPI (Windows, BL-24 / W-21).
/// </summary>
public sealed class FileSmbCredentialProvider(string secretsDir) : ISmbCredentialProvider
{
    private readonly FileSftpCredentialProvider _store = new(secretsDir);

    public async ValueTask<SmbCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct)
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
            throw new StorageException(ErrorClass.Permanent, KbCode.AuthRejected, "SMB credential entry has no password.");
        return new SmbCredential(c.UserName, c.Password);
    }
}
