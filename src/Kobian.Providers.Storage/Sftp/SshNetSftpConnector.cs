using System.Security.Cryptography;
using Kobian.Application.Storage;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Kobian.Providers.Storage.Sftp;

/// <summary>Poświadczenia SFTP rozwiązane z SecretRef (AC-01.3). Sekrety nie trafiają do logów ani wyjątków.</summary>
public sealed class SftpCredential
{
    public SftpCredential(string userName, string? pw, byte[]? privateKey, string? passphrase)
    {
        UserName = userName;
        Password = pw;
        PrivateKey = privateKey;
        Passphrase = passphrase;
    }

    public string UserName { get; }

    public string? Password { get; }

    /// <summary>Klucz prywatny (OpenSSH/PEM) w pamięci; zerowany po nawiązaniu połączenia.</summary>
    public byte[]? PrivateKey { get; }

    public string? Passphrase { get; }
}

public interface ISftpCredentialProvider
{
    /// <summary>Rola Backup i Maintenance mają osobne poświadczenia (D4.1).</summary>
    ValueTask<SftpCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct);
}

/// <summary>
/// Adapter SSH.NET (D4.4): wyłącznie <see cref="SftpClient"/>; pinning klucza hosta SHA256 (D4.11) — niezgodny klucz = KB-2104,
/// brak przypięcia = odmowa (PROWIZORYCZNIE Qb15). Zwykły rename (nie posix-rename, K24.3). Niezweryfikowane na prawdziwym
/// serwerze — kolejka W-13.
/// </summary>
public sealed class SshNetSftpConnector(ISftpCredentialProvider credentials) : ISftpConnector
{
    public async ValueTask<ISftpTransport> ConnectAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
    {
        var opt = SshNetOptions.Parse(config);
        if (opt.IdentityProbe) return await ProbeIdentityAsync(opt, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(config.SecretRef))
            throw new SftpProtocolException(SftpStatus.AuthRejected, "Destination has no credential reference.");
        var cred = await credentials.ResolveAsync(config.SecretRef, role, ct).ConfigureAwait(false);
        var methods = new List<AuthenticationMethod>();
        try
        {
            if (cred.PrivateKey is { Length: > 0 } key)
            {
                using var ms = new MemoryStream(key, writable: false);
                using var pk = string.IsNullOrEmpty(cred.Passphrase) ? new PrivateKeyFile(ms) : new PrivateKeyFile(ms, cred.Passphrase);
                methods.Add(new PrivateKeyAuthenticationMethod(cred.UserName, pk));
            }

            if (!string.IsNullOrEmpty(cred.Password))
                methods.Add(new PasswordAuthenticationMethod(cred.UserName, cred.Password));
            if (methods.Count == 0)
                throw new SftpProtocolException(SftpStatus.AuthRejected, "No usable credential for destination.");
            var info = new ConnectionInfo(opt.Host, opt.Port, cred.UserName, [.. methods]) { Timeout = opt.ConnectTimeout };
            var client = new SftpClient(info);
            ServerIdentity? identity = null;
            var mismatch = false;
            client.HostKeyReceived += (_, e) =>
            {
                identity = new ServerIdentity("ssh-" + e.HostKeyName, HostKeyPin.Fingerprint(e.HostKey), e.HostKeyName);
                e.CanTrust = HostKeyPin.MatchesAny(opt.Pins, e.HostKey);
                mismatch = !e.CanTrust;
            };
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                client.Dispose();
                if (mismatch) throw new SftpProtocolException(SftpStatus.HostKeyMismatch, "SSH host key does not match the pinned key.", ex) { PresentedIdentity = identity };
                throw SshNetErrorTranslator.Translate(ex);
            }

            return new SshNetSftpTransport(client, identity);
        }
        finally
        {
            if (cred.PrivateKey is { } k) CryptographicOperations.ZeroMemory(k);
        }
    }

    /// <summary>
    /// Poznanie klucza hosta bez przypięcia (ProbeDestination): klucz zawsze odrzucany, uwierzytelnienie „none” bez poświadczeń nigdy nie jest wysyłane,
    /// bo połączenie kończy się na wymianie kluczy. Wynik = wyjątek HostKeyMismatch z odciskiem (albo błąd połączenia). Niezweryfikowane na prawdziwym serwerze — W-20.
    /// </summary>
    private static async ValueTask<ISftpTransport> ProbeIdentityAsync(SshNetOptions opt, CancellationToken ct)
    {
        var info = new ConnectionInfo(opt.Host, opt.Port, "kobian-probe", new NoneAuthenticationMethod("kobian-probe")) { Timeout = opt.ConnectTimeout };
        using var client = new SftpClient(info);
        ServerIdentity? identity = null;
        client.HostKeyReceived += (_, e) =>
        {
            identity = new ServerIdentity("ssh-" + e.HostKeyName, HostKeyPin.Fingerprint(e.HostKey), e.HostKeyName);
            e.CanTrust = false;
        };
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (identity is not null) throw new SftpProtocolException(SftpStatus.HostKeyMismatch, "SSH host key is not pinned (identity probe).", ex) { PresentedIdentity = identity };
            throw SshNetErrorTranslator.Translate(ex);
        }
        // Nie powinno się zdarzyć (klucz odrzucony); fail-closed.
        throw new SftpProtocolException(SftpStatus.HostKeyMismatch, "SSH identity probe did not stop at key exchange.");
    }
}

internal sealed class SshNetSftpTransport(SftpClient client, ServerIdentity? identity) : ISftpTransport
{
    public ServerIdentity? Identity { get; } = identity;

    // PROWIZORYCZNIE: bez rozszerzeń OpenSSH (fsync/statvfs/limits) do czasu testu na prawdziwym serwerze (W-13).
    public SftpFeatures Features => SftpFeatures.None;

    private static Task<T> Run<T>(Func<T> f, CancellationToken ct) => Task.Run(
        () =>
        {
            try
            {
                return f();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw SshNetErrorTranslator.Translate(ex);
            }
        },
        ct);

    private static SftpEntry ToEntry(ISftpFile f) => new(
        f.Name,
        f.IsSymbolicLink ? SftpEntryKind.Symlink : f.IsDirectory ? SftpEntryKind.Directory : f.IsRegularFile ? SftpEntryKind.File : SftpEntryKind.Other,
        f.Length,
        new DateTimeOffset(DateTime.SpecifyKind(f.LastWriteTimeUtc, DateTimeKind.Utc)));

    public async ValueTask<SftpEntry?> LstatAsync(string path, CancellationToken ct)
    {
        try
        {
            // SftpClient.Get używa SSH_FXP_LSTAT — nie podąża za dowiązaniami (do potwierdzenia testem W-13).
            return await Run(() => ToEntry(client.Get(path)), ct).ConfigureAwait(false);
        }
        catch (SftpProtocolException p) when (p.Status == SftpStatus.NoSuchFile)
        {
            return null;
        }
    }

    public async ValueTask<IReadOnlyList<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct)
        => await Run<IReadOnlyList<SftpEntry>>(
            () => client.ListDirectory(path).Where(f => f.Name is not ("." or "..")).Select(ToEntry).ToList(), ct).ConfigureAwait(false);

    public async ValueTask<Stream> OpenReadAsync(string path, long offset, CancellationToken ct)
        => await Run<Stream>(
            () =>
            {
                var s = client.Open(path, FileMode.Open, FileAccess.Read);
                if (offset > 0) s.Seek(offset, SeekOrigin.Begin);
                return s;
            },
            ct).ConfigureAwait(false);

    public async ValueTask<ISftpWriteHandle> OpenWriteAsync(string path, SftpOpenMode mode, long offset, CancellationToken ct)
        => await Run<ISftpWriteHandle>(
            () =>
            {
                if (mode == SftpOpenMode.CreateNew)
                    return new Handle(client.Open(path, FileMode.CreateNew, FileAccess.Write));
                var s = client.Open(path, FileMode.Open, FileAccess.Write);
                s.Seek(offset, SeekOrigin.Begin);
                return new Handle(s);
            },
            ct).ConfigureAwait(false);

    public async ValueTask RenameNoReplaceAsync(string source, string destination, CancellationToken ct)
        => await Run(
            () =>
            {
                if (client.Exists(destination))
                    throw new SftpProtocolException(SftpStatus.FileAlreadyExists, "Rename target already exists.");
                client.RenameFile(source, destination, isPosix: false);
                return 0;
            },
            ct).ConfigureAwait(false);

    public async ValueTask CreateDirectoryAsync(string path, CancellationToken ct)
        => await Run(() => { client.CreateDirectory(path); return 0; }, ct).ConfigureAwait(false);

    public async ValueTask DeleteFileAsync(string path, CancellationToken ct)
        => await Run(() => { client.DeleteFile(path); return 0; }, ct).ConfigureAwait(false);

    public ValueTask<long?> FreeSpaceAsync(string path, CancellationToken ct) => ValueTask.FromResult<long?>(null);

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Handle(Stream s) : ISftpWriteHandle
    {
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            try
            {
                await s.WriteAsync(data, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw SshNetErrorTranslator.Translate(ex);
            }
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            try
            {
                await s.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw SshNetErrorTranslator.Translate(ex);
            }
        }

        public ValueTask DisposeAsync() => s.DisposeAsync();
    }
}
