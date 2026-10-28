using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Local;

namespace Kobian.Providers.Storage.Smb;

/// <summary>
/// Dostawca „smb” (BL-73, D4.2): korzeń <c>\\serwer\udział\ścieżka</c>; połączenie z udziałem z jawnym poświadczeniem roli (D4.1) przez
/// <see cref="ISmbConnector"/> — bez litery dysku, zamykane razem z sesją (AC-04.3). Operacje na plikach = dostawca lokalny na ścieżce UNC
/// (te same zasady: tylko w korzeniu, odmowa dowiązań, brak nadpisywania KB-2301, usuwanie tylko w sesji Maintenance — D4.6).
/// Serializacja różnych kont do jednego serwera (błąd 1219) należy do kolejki uruchomień (D7.4, zasób <c>smb:&lt;serwer&gt;:&lt;konto&gt;</c>);
/// jeśli mimo to wystąpi — KB-2101 bez ponowień.
/// </summary>
public sealed class SmbStorageProviderFactory(ISmbConnector connector, ISmbCredentialProvider credentials) : IStorageProviderFactory
{
    /// <summary>Równoległość domyślna celu SMB (4.7).</summary>
    public const int Parallelism = 4;

    public string Scheme => "smb";

    public async ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.Equals(config.Scheme, Scheme, StringComparison.Ordinal))
            throw new ArgumentException("Scheme mismatch.", nameof(config));
        var share = UncPath.Parse(config.Root);
        if (string.IsNullOrEmpty(config.SecretRef))
            throw new StorageException(ErrorClass.Permanent, KbCode.AuthRejected, "Destination has no credential reference.");
        SmbCredential credential;
        try
        {
            credential = await credentials.ResolveAsync(config.SecretRef, role, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or StorageException))
        {
            throw new StorageException(ErrorClass.Permanent, KbCode.AuthRejected, "Credential for the destination could not be resolved.", ex);
        }

        ISmbConnection connection;
        try
        {
            connection = await connector.ConnectAsync(share, credential, ct).ConfigureAwait(false);
        }
        catch (SmbConnectException ex)
        {
            throw SmbErrorMap.MapConnect(ex.Win32Error, ex);
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(connection.LocalRoot);
            var info = new DirectoryInfo(root);
            if (!info.Exists)
                throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination folder on the share does not exist.");
            if (info.LinkTarget is not null)
                throw new StorageException(ErrorClass.Security, KbCode.ReparsePointRefused, "Destination root is a link; refusing.");
            return new LocalStorageSession(root, role, Parallelism, connection);
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            if (ex is IOException or UnauthorizedAccessException) throw LocalErrorMap.Map(ex);
            throw;
        }
    }
}
