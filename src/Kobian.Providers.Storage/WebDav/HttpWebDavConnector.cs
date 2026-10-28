using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Ftp;

namespace Kobian.Providers.Storage.WebDav;

/// <summary>
/// Adapter WebDAV na <see cref="HttpClient"/> (bez zewnętrznych bibliotek). Domyślnie HTTPS (TLS 1.2/1.3); certyfikat: łańcuch systemowy + nazwa ALBO przypięty
/// SPKI SHA-256 — ta sama polityka co FTPS (<see cref="FtpsCertificatePolicy"/>, D4.11), odmowa = KB-2109 z odciskiem do zatwierdzenia.
/// Uwierzytelnienie Basic wysyłane od pierwszego żądania, ale dopiero po zestawieniu i zweryfikowaniu TLS — odrzucony certyfikat nie ujawnia hasła.
/// Bez przekierowań (cel nie może przenieść poświadczeń na inny host), bez ciasteczek i bez dekompresji. Jawny HTTP wyłącznie z opcją „insecure”.
/// PROWIZORYCZNIE: tylko Basic (Nextcloud, ownCloud, Synology, QNAP, Apache mod_dav, nginx) — Digest/NTLM poza zakresem v1.
/// Niezweryfikowane na prawdziwym serwerze (testy: serwer pętli zwrotnej w procesie).
/// </summary>
public sealed class HttpWebDavConnector(IFtpCredentialProvider credentials) : IWebDavConnector
{
    public async ValueTask<IWebDavTransport> ConnectAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var opt = WebDavOptions.Parse(config);
        if (string.IsNullOrEmpty(config.SecretRef))
            throw new StorageException(ErrorClass.Permanent, KbCode.AuthRejected, "Destination has no credential reference.");
        var cred = await credentials.ResolveAsync(config.SecretRef, role, ct).ConfigureAwait(false);
        if (cred.UserName.Contains(':', StringComparison.Ordinal) || cred.UserName.Any(char.IsControl))
            throw new StorageException(ErrorClass.Permanent, KbCode.AuthRejected, "WebDAV user name must not contain ':' (Basic authentication).");
        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(cred.UserName + ":" + cred.Password)));
        var tls = new TlsState();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = opt.ConnectTimeout,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 2,
            PreAuthenticate = false,
            Credentials = null,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.Online, // jak FTPS (Qb72): CRL offline ⇒ potrzebny pin SPKI
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    var v = FtpsCertificatePolicy.Evaluate(cert, errors, opt.SpkiPins, opt.RequirePin);
                    lock (tls)
                    {
                        // Pierwszy werdykt wyznacza tożsamość sesji; odrzucenie na którymkolwiek połączeniu jest trwałe.
                        if (tls.Verdict is null || !v.Accepted) tls.Verdict = v;
                    }
                    return v.Accepted;
                },
            },
        };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Kobian-Backup");
        return new HttpWebDavTransport(http, opt.BaseUri, auth, opt.Tls ? tls : null);
    }
}

internal sealed class TlsState
{
    public TlsVerdict? Verdict;
}

internal sealed class HttpWebDavTransport(HttpClient http, Uri baseUri, AuthenticationHeaderValue auth, TlsState? tls) : IWebDavTransport
{
    /// <summary>Limit pojedynczej operacji sterującej (PROPFIND, MKCOL, MOVE, DELETE) — jak OperationTimeout SFTP.</summary>
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Bezczynność odczytu strumienia GET (D4.13: kanał danych 60 s).</summary>
    internal static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Minimalna zakładana przepustowość PUT do wyliczenia limitu czasu (32 KiB/s — wolne łącze, a nie zawieszony serwer).</summary>
    internal const long MinUploadBytesPerSecond = 32 * 1024;

    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Mkcol = new("MKCOL");
    private static readonly HttpMethod Move = new("MOVE");

    private const string StatBody = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>""";
    private const string QuotaBody = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:quota-available-bytes/></d:prop></d:propfind>""";

    public ServerIdentity? Identity
    {
        get
        {
            if (tls is null) return null;
            lock (tls) return tls.Verdict is { Accepted: true } v ? v.Identity : null;
        }
    }

    private ServerIdentity? Rejected()
    {
        if (tls is null) return null;
        lock (tls) return tls.Verdict is { Accepted: false } v ? v.Identity : null;
    }

    private Uri Url(string path, bool collection = false) => new(baseUri, WebDavPath.Encode(path, collection));

    private HttpRequestMessage Request(HttpMethod method, string path, bool collection = false)
    {
        var req = new HttpRequestMessage(method, Url(path, collection)) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        req.Headers.Authorization = auth;
        return req;
    }

    /// <summary>Wysłanie z limitem czasu; anulowanie przez wywołującego przechodzi bez zmian, przekroczenie limitu = błąd przejściowy.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, TimeSpan timeout, HttpCompletionOption completion, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await http.SendAsync(req, completion, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw WebDavErrorMap.FromException(ex is OperationCanceledException ? new TimeoutException("WebDAV request timed out.", ex) : ex, Rejected());
        }
    }

    private async Task<List<MultiStatusEntry>?> PropfindAsync(string path, bool collection, int depth, string body, CancellationToken ct)
    {
        using var req = Request(Propfind, path, collection);
        req.Headers.Add("Depth", depth == 0 ? "0" : "1");
        req.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        using var resp = await SendAsync(req, OperationTimeout, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if ((int)resp.StatusCode is >= 300 and < 400 && !collection && depth == 0)
            return await PropfindAsync(path, collection: true, depth, body, ct).ConfigureAwait(false); // kolekcja bez „/” → przekierowanie na „/” (Apache, IIS)
        if ((int)resp.StatusCode != 207) throw WebDavErrorMap.FromStatus(resp.StatusCode);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(OperationTimeout);
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            // Treść czytana do bufora z limitem czasu, potem parsowana synchronicznie (XmlReader bez async).
            using var buf = new MemoryStream();
            await CopyLimitedAsync(s, buf, MultiStatusParser.MaxCharacters, cts.Token).ConfigureAwait(false);
            buf.Position = 0;
            return MultiStatusParser.Parse(buf);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw WebDavErrorMap.FromException(ex, Rejected());
        }
    }

    private static async Task CopyLimitedAsync(Stream from, Stream to, long max, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > max) throw new StorageException(ErrorClass.Transient, KbCode.TransientIo, "WebDAV multistatus reply too large.");
            await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
        }
    }

    private static WebDavEntry ToEntry(MultiStatusEntry e) =>
        new(WebDavPath.NameOf(e.Path), e.IsCollection ? WebDavEntryKind.Collection : WebDavEntryKind.File, e.IsCollection ? 0 : e.Length ?? 0, e.Modified);

    public async ValueTask<WebDavEntry?> StatAsync(string path, CancellationToken ct)
    {
        var list = await PropfindAsync(path, collection: false, 0, StatBody, ct).ConfigureAwait(false);
        if (list is null) return null;
        var want = WebDavPath.Normalize(path);
        // Depth 0 zwraca jeden wpis; serwer bywa niekonsekwentny w kodowaniu href, więc przy jednym wpisie przyjmujemy go jako ten zasób.
        var e = list.FirstOrDefault(x => string.Equals(x.Path, want, StringComparison.Ordinal)) ?? (list.Count == 1 ? list[0] : null);
        if (e is null) throw new StorageException(ErrorClass.Transient, KbCode.TransientIo, "WebDAV reply does not describe the requested resource.");
        return ToEntry(e with { Path = want });
    }

    public async ValueTask<IReadOnlyList<WebDavEntry>> ListAsync(string collectionPath, CancellationToken ct)
    {
        var list = await PropfindAsync(collectionPath, collection: true, 1, StatBody, ct).ConfigureAwait(false)
                   ?? throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination path not found.");
        var self = WebDavPath.Normalize(collectionPath);
        // Tylko bezpośrednie dzieci żądanej kolekcji (sama kolekcja i wpisy spoza niej — pomijane).
        return list.Where(e => e.Path != self && WebDavPath.ParentOf(e.Path) == self).Select(ToEntry).ToList();
    }

    public async ValueTask<Stream> GetAsync(string path, long offset, CancellationToken ct)
    {
        var req = Request(HttpMethod.Get, path);
        if (offset > 0) req.Headers.Range = new RangeHeaderValue(offset, null);
        HttpResponseMessage resp;
        try
        {
            resp = await SendAsync(req, OperationTimeout, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch
        {
            req.Dispose();
            throw;
        }
        try
        {
            if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)) throw WebDavErrorMap.FromStatus(resp.StatusCode);
            if (offset > 0 && resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.From != offset)
                throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "WebDAV server returned a different range than requested.");
            var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var stream = new IdleTimeoutReadStream(s, resp, req, ReadIdleTimeout);
            // Serwer zignorował Range (200 zamiast 206) — pomijamy początek po stronie klienta (wolniej, ale poprawnie).
            if (offset > 0 && resp.StatusCode == HttpStatusCode.OK) await stream.SkipAsync(offset, ct).ConfigureAwait(false);
            return stream;
        }
        catch (Exception ex)
        {
            resp.Dispose();
            req.Dispose();
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw WebDavErrorMap.FromException(ex, Rejected());
        }
    }

    public async ValueTask PutAsync(string path, ReadOnlyMemory<byte> body, bool ifNoneMatch, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Put, path);
        req.Content = new ReadOnlyMemoryContent(body);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentLength = body.Length;
        if (ifNoneMatch) req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        var timeout = OperationTimeout + TimeSpan.FromSeconds((double)body.Length / MinUploadBytesPerSecond);
        using var resp = await SendAsync(req, timeout, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (resp.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.NoContent or HttpStatusCode.OK)) throw WebDavErrorMap.FromStatus(resp.StatusCode);
    }

    public async ValueTask MoveNoReplaceAsync(string source, string destination, CancellationToken ct)
    {
        using var req = Request(Move, source);
        req.Headers.Add("Destination", Url(destination).AbsoluteUri);
        req.Headers.Add("Overwrite", "F");
        using var resp = await SendAsync(req, OperationTimeout, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (resp.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.NoContent)) throw WebDavErrorMap.FromStatus(resp.StatusCode);
    }

    public async ValueTask<bool> MakeCollectionAsync(string path, CancellationToken ct)
    {
        using var req = Request(Mkcol, path, collection: true);
        using var resp = await SendAsync(req, OperationTimeout, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.MethodNotAllowed) return false;
        if (resp.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.NoContent)) throw WebDavErrorMap.FromStatus(resp.StatusCode);
        return true;
    }

    public async ValueTask DeleteAsync(string path, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Delete, path);
        using var resp = await SendAsync(req, OperationTimeout, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (resp.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK)) throw WebDavErrorMap.FromStatus(resp.StatusCode);
    }

    public async ValueTask<long?> QuotaAvailableAsync(string path, CancellationToken ct)
    {
        var list = await PropfindAsync(path, collection: true, 0, QuotaBody, ct).ConfigureAwait(false);
        return list is { Count: > 0 } && list[0].QuotaAvailable is >= 0 and var q ? q : null;
    }

    public ValueTask DisposeAsync()
    {
        http.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Strumień GET z limitem bezczynności każdego odczytu; zamknięcie zwalnia odpowiedź HTTP (połączenie wraca do puli albo jest zamykane).</summary>
internal sealed class IdleTimeoutReadStream(Stream inner, HttpResponseMessage response, HttpRequestMessage request, TimeSpan idle) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(idle);
        try
        {
            return await inner.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            throw new StorageException(ErrorClass.Transient, KbCode.TransientIo, "WebDAV download stalled or was interrupted.", ex);
        }
    }

    internal async Task SkipAsync(long count, CancellationToken ct)
    {
        var buf = new byte[81920];
        while (count > 0)
        {
            var n = await ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count)), ct).ConfigureAwait(false);
            if (n == 0) throw new StorageException(ErrorClass.Integrity, KbCode.SizeMismatch, "Read offset beyond end of object.");
            count -= n;
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
            request.Dispose();
        }
        base.Dispose(disposing);
    }
}
