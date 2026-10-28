using FluentFTP;
using FluentFTP.Exceptions;
using Kobian.Application.Storage;

namespace Kobian.Providers.Storage.Ftp;

/// <summary>Poświadczenia FTP rozwiązane z SecretRef (AC-01.3). Sekrety nie trafiają do logów ani wyjątków.</summary>
public sealed class FtpCredential
{
    public FtpCredential(string userName, string pw)
    {
        UserName = userName;
        Password = pw;
    }

    public string UserName { get; }

    public string Password { get; }
}

public interface IFtpCredentialProvider
{
    /// <summary>Rola Backup i Maintenance mają osobne poświadczenia (D4.1).</summary>
    ValueTask<FtpCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct);
}

/// <summary>
/// Adapter FluentFTP (D4.3): <see cref="AsyncFtpClient"/>, Explicit FTPS (AUTH TLS) domyślnie, Implicit (990) opcją, jawny FTP tylko z flagą „insecure”;
/// nigdy cichego zejścia do jawnego FTP. Certyfikat: łańcuch systemowy + nazwa ALBO przypięty SPKI SHA-256 (D4.11) — inaczej KB-2109 z odciskiem.
/// Uwierzytelnienie (USER/PASS) następuje dopiero po zestawieniu TLS, więc odrzucony certyfikat nie ujawnia poświadczeń.
/// Niezweryfikowane na prawdziwym serwerze — kolejka SP-04 (vsftpd require_ssl_reuse, FileZilla Server, IIS).
/// </summary>
public sealed class FluentFtpConnector(IFtpCredentialProvider credentials) : IFtpConnector
{
    public async ValueTask<IFtpTransport> ConnectAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
    {
        var opt = FtpOptions.Parse(config);
        if (string.IsNullOrEmpty(config.SecretRef))
            throw new FtpProtocolException(FtpFailure.AuthRejected, "Destination has no credential reference.");
        var cred = await credentials.ResolveAsync(config.SecretRef, role, ct).ConfigureAwait(false);
        var client = new AsyncFtpClient(opt.Host, cred.UserName, cred.Password, opt.Port, FluentFtpConfig.Build(opt), logger: null);
        TlsVerdict? verdict = null;
        client.ValidateCertificate += (_, e) =>
        {
            var v = FtpsCertificatePolicy.Evaluate(e.Certificate, e.PolicyErrors, opt.SpkiPins, opt.RequirePin);
            // Pierwszy werdykt (kanał sterowania) wyznacza tożsamość sesji; odrzucenie na którymkolwiek etapie jest trwałe.
            if (verdict is null || !v.Accepted) verdict = v;
            e.Accept = v.Accepted;
        };
        try
        {
            await client.Connect(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            if (verdict is { Accepted: false } rejected)
                throw new FtpProtocolException(FtpFailure.TlsIdentityRejected, "FTPS certificate is not trusted and its key is not pinned.", ex) { PresentedIdentity = rejected.Identity };
            throw FluentFtpErrorTranslator.Translate(ex);
        }
        if (opt.Tls != FtpTlsMode.None && (!client.IsEncrypted || verdict is not { Accepted: true }))
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new FtpProtocolException(FtpFailure.TlsNotSupported, "FTPS session is not encrypted.");
        }
        return new FluentFtpTransport(client, opt.Tls == FtpTlsMode.None ? null : verdict!.Identity);
    }
}

internal sealed class FluentFtpTransport : IFtpTransport
{
    private readonly AsyncFtpClient _c;
    private bool _transferOpen;

    public FluentFtpTransport(AsyncFtpClient client, ServerIdentity? identity)
    {
        _c = client;
        Identity = identity;
        var f = FtpFeatures.None;
        if (client.HasFeature(FtpCapability.MLST)) f |= FtpFeatures.Mlsd;
        if (client.HasFeature(FtpCapability.SIZE)) f |= FtpFeatures.Size;
        if (client.HasFeature(FtpCapability.REST)) f |= FtpFeatures.Rest;
        if (client.HasFeature(FtpCapability.HASH) && (client.HashAlgorithms & FtpHashAlgorithm.SHA256) != 0) f |= FtpFeatures.HashSha256;
        if (client.HasFeature(FtpCapability.AVBL)) f |= FtpFeatures.Avbl;
        Features = f;
    }

    public ServerIdentity? Identity { get; }

    public FtpFeatures Features { get; }

    private void NoTransfer()
    {
        if (_transferOpen) throw new InvalidOperationException("FTP data transfer in progress; close it before the next command.");
    }

    private async ValueTask<T> Run<T>(Func<Task<T>> f, bool writeContext = false)
    {
        NoTransfer();
        try
        {
            return await f().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            throw FluentFtpErrorTranslator.Translate(ex, writeContext);
        }
    }

    /// <summary>Komenda z oczekiwaną odpowiedzią pozytywną; negatywna → wyjątek z kodem (bez treści komendy).</summary>
    private async ValueTask<FtpReply> Exec(string command, bool writeContext, CancellationToken ct)
    {
        var r = await Run(() => _c.Execute(command, ct), writeContext).ConfigureAwait(false);
        if (!r.Success) throw FluentFtpErrorTranslator.FromReply(FtpReplyParser.Code(r.Code), r.Message, writeContext);
        return r;
    }

    public async ValueTask<FtpEntry?> StatAsync(string path, CancellationToken ct)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if ((Features & FtpFeatures.Mlsd) != 0)
        {
            var r = await Run(() => _c.Execute("MLST " + path, ct)).ConfigureAwait(false);
            if (r.Code == "550") return null;
            if (!r.Success) throw FluentFtpErrorTranslator.FromReply(FtpReplyParser.Code(r.Code), r.Message, false);
            foreach (var line in (r.InfoMessages ?? "").Split('\n'))
                if (FtpReplyParser.ParseMlstLine(line.TrimEnd('\r'), name) is { } e) return e;
            throw new FtpProtocolException(FtpFailure.Transient, "Unparseable MLST reply.");
        }
        // Bez MLST: wpis z listingu katalogu nadrzędnego (LIST, parser FluentFTP; „l” = dowiązanie).
        var parent = path.LastIndexOf('/') <= 0 ? "/" : path[..path.LastIndexOf('/')];
        if (path == "/") return new FtpEntry("/", FtpEntryKind.Directory, 0, null);
        var items = await ListDirectoryAsync(parent, ct).ConfigureAwait(false);
        return items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
    }

    public async ValueTask<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string path, CancellationToken ct)
    {
        var items = await Run(() => _c.GetListing(path, FtpListOption.AllFiles | FtpListOption.NoPath, ct)).ConfigureAwait(false);
        return items
            .Where(i => i.Name is not ("." or ".."))
            .Select(i => new FtpEntry(
                i.Name,
                i.Type switch { FtpObjectType.File => FtpEntryKind.File, FtpObjectType.Directory => FtpEntryKind.Directory, FtpObjectType.Link => FtpEntryKind.Link, _ => FtpEntryKind.Other },
                i.Size < 0 ? 0 : i.Size,
                i.Modified == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(i.Modified, DateTimeKind.Utc))))
            .ToList();
    }

    public async ValueTask<long?> SizeAsync(string path, CancellationToken ct)
    {
        // D4.14 / CE-22: SIZE wyłącznie w trybie binarnym — TYPE I wysyłane jawnie przed każdym SIZE (GetFileSize biblioteki tego nie gwarantuje).
        await Exec("TYPE I", false, ct).ConfigureAwait(false);
        var r = await Run(() => _c.Execute("SIZE " + path, ct)).ConfigureAwait(false);
        if (r.Code == "550") return null;
        if (!r.Success) throw FluentFtpErrorTranslator.FromReply(FtpReplyParser.Code(r.Code), r.Message, false);
        return FtpReplyParser.ParseNumber213(r.Code, r.Message) ?? throw new FtpProtocolException(FtpFailure.Transient, "Unparseable SIZE reply.");
    }

    public async ValueTask<Stream> OpenReadAsync(string path, long offset, CancellationToken ct)
    {
        var s = await Run(() => _c.OpenRead(path, FtpDataType.Binary, offset, -1, ct)).ConfigureAwait(false);
        _transferOpen = true;
        return new ReadStream(this, s);
    }

    public async ValueTask<IFtpUpload> OpenUploadAsync(string path, long resumeFrom, CancellationToken ct)
    {
        var s = resumeFrom == 0
            ? await Run(() => _c.OpenWrite(path, FtpDataType.Binary, -1, ct), writeContext: true).ConfigureAwait(false)
            : await Run(() => _c.OpenAppend(path, FtpDataType.Binary, resumeFrom, ct), writeContext: true).ConfigureAwait(false);
        _transferOpen = true;
        return new Upload(this, s);
    }

    public async ValueTask RenameNoReplaceAsync(string source, string destination, CancellationToken ct)
    {
        // FTP nie ma RNTO „bez nadpisania”; sprawdzenie tuż przed RNTO (okno wyścigu: docs/admin/cel-ftps.md). Błąd sprawdzenia ≠ „nie istnieje”.
        if (await StatAsync(destination, ct).ConfigureAwait(false) is not null)
            throw new FtpProtocolException(FtpFailure.AlreadyExists, "Rename target already exists.");
        await Exec("RNFR " + source, true, ct).ConfigureAwait(false);
        await Exec("RNTO " + destination, true, ct).ConfigureAwait(false);
    }

    public async ValueTask CreateDirectoryAsync(string path, CancellationToken ct) => await Exec("MKD " + path, true, ct).ConfigureAwait(false);

    public async ValueTask DeleteFileAsync(string path, CancellationToken ct) => await Exec("DELE " + path, true, ct).ConfigureAwait(false);

    public async ValueTask<long?> FreeSpaceAsync(string path, CancellationToken ct)
    {
        if ((Features & FtpFeatures.Avbl) == 0) return null;
        var r = await Run(() => _c.Execute("AVBL " + path, ct)).ConfigureAwait(false);
        return r.Success ? FtpReplyParser.ParseNumber213(r.Code, r.Message) : null;
    }

    public async ValueTask<byte[]?> HashSha256Async(string path, CancellationToken ct)
    {
        if ((Features & FtpFeatures.HashSha256) == 0) return null;
        try
        {
            var h = await Run(() => _c.GetChecksum(path, FtpHashAlgorithm.SHA256, ct)).ConfigureAwait(false);
            return h is { IsValid: true, Algorithm: FtpHashAlgorithm.SHA256 } ? FtpReplyParser.ParseSha256Hex(h.Value) : null;
        }
        catch (FtpProtocolException p) when (p.InnerException is FtpHashUnsupportedException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_c.IsConnected) await _c.Disconnect(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or FtpException or System.Net.Sockets.SocketException)
        {
            // zerwane połączenie przy zamykaniu — bez znaczenia
        }
        await _c.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Zamyka kanał danych i czyta odpowiedź końcową z kanału sterowania (wzór: UploadFileInternal biblioteki — DisposeAsync, potem GetReply).
    /// <paramref name="expectSuccess"/> = false (przerwany odczyt/zapis): 426/451 jest oczekiwane, odpowiedź tylko konsumowana.
    /// </summary>
    private async ValueTask FinishTransferAsync(Stream data, bool expectSuccess, bool writeContext, CancellationToken ct)
    {
        try
        {
            await data.DisposeAsync().ConfigureAwait(false);
            var r = await _c.GetReply(ct).ConfigureAwait(false);
            if (expectSuccess && !r.Success) throw FluentFtpErrorTranslator.FromReply(FtpReplyParser.Code(r.Code), r.Message, writeContext);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FtpProtocolException)
        {
            if (expectSuccess) throw FluentFtpErrorTranslator.Translate(ex, writeContext);
        }
        finally
        {
            _transferOpen = false;
        }
    }

    private sealed class Upload(FluentFtpTransport t, Stream s) : IFtpUpload
    {
        private bool _done;

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            try
            {
                await s.WriteAsync(data, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw FluentFtpErrorTranslator.Translate(ex, writeContext: true);
            }
        }

        public async ValueTask CompleteAsync(CancellationToken ct)
        {
            if (_done) return;
            _done = true;
            try
            {
                await s.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                t._transferOpen = false;
                throw FluentFtpErrorTranslator.Translate(ex, writeContext: true);
            }
            await t.FinishTransferAsync(s, expectSuccess: true, writeContext: true, ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_done) return;
            _done = true;
            await t.FinishTransferAsync(s, expectSuccess: false, writeContext: true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Strumień RETR: zamknięcie kończy transfer na kanale sterowania (odpowiedź 226 lub 426 przy przerwaniu).</summary>
    private sealed class ReadStream(FluentFtpTransport t, Stream inner) : Stream
    {
        private bool _closed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                _closed = true;
                await t.FinishTransferAsync(inner, expectSuccess: false, writeContext: false, CancellationToken.None).ConfigureAwait(false);
            }
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                inner.Dispose();
                t._transferOpen = false;
            }
            base.Dispose(disposing);
        }
    }
}
