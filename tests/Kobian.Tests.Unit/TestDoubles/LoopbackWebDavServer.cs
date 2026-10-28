using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Kobian.Tests.Unit.TestDoubles;

/// <summary>
/// Minimalny serwer WebDAV (RFC 4918, podzbiór: PROPFIND 0/1, GET z Range, PUT z If-None-Match, MKCOL, MOVE z Overwrite, DELETE, quota RFC 4331)
/// na 127.0.0.1 — wyłącznie do testów adaptera <c>HttpWebDavConnector</c> bez prawdziwego serwera. Dane w pamięci. Opcjonalnie TLS (certyfikat testowy)
/// i przełączniki zachowań spotykanych na prawdziwych serwerach (ignorowanie Range i If-None-Match, przekierowanie kolekcji bez „/”, href jako pełny URL).
/// </summary>
public sealed class LoopbackWebDavServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly X509Certificate2? _cert;
    private readonly Task _loop;
    private readonly object _gate = new();

    /// <summary>Zasoby: ścieżka zdekodowana bez końcowego „/” → treść (null = kolekcja).</summary>
    private readonly SortedDictionary<string, byte[]?> _items = new(StringComparer.Ordinal) { ["/"] = null };

    public LoopbackWebDavServer(X509Certificate2? cert = null, string user = "kopie", string secret = "tajne-haslo-123")
    {
        _cert = cert;
        User = user;
        Secret = secret;
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string User { get; }

    public string Secret { get; }

    /// <summary>Czy jakiekolwiek żądanie niosło nagłówek Authorization (test: brak hasła przed weryfikacją TLS).</summary>
    public bool AuthorizationSeen { get; private set; }

    public bool IgnoreRange { get; set; }

    public bool IgnoreIfNoneMatch { get; set; }

    public bool RedirectCollectionsWithoutSlash { get; set; }

    public bool AbsoluteHrefs { get; set; }

    public long? QuotaAvailable { get; set; }

    /// <summary>Następny PUT kończy się tym statusem (np. 507, 413) — wstrzykiwanie błędów.</summary>
    public int? FailNextPut { get; set; }

    /// <summary>Wywoływane tuż przed obsłużeniem MOVE (test wyścigu: ktoś zapisał cel między sprawdzeniem a rename).</summary>
    public Action? BeforeMove { get; set; }

    public List<string> Requests { get; } = [];

    public void AddCollection(string path)
    {
        lock (_gate) _items[Norm(path)] = null;
    }

    public void AddFile(string path, byte[] data)
    {
        lock (_gate) _items[Norm(path)] = data;
    }

    public byte[]? File(string path)
    {
        lock (_gate) return _items.TryGetValue(Norm(path), out var d) ? d : null;
    }

    public IReadOnlyList<string> Paths()
    {
        lock (_gate) return [.. _items.Keys];
    }

    private static string Norm(string p)
    {
        var t = p.TrimEnd('/');
        return t.Length == 0 ? "/" : t;
    }

    private static string Parent(string p)
    {
        var i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try
            {
                c = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(c));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        Stream s = client.GetStream();
        try
        {
            if (_cert is not null)
            {
                var ssl = new SslStream(s);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, _cts.Token);
                s = ssl;
            }
            while (!_cts.IsCancellationRequested)
            {
                var head = await ReadHeadAsync(s);
                if (head is null) return;
                var lines = head.Split("\r\n");
                var parts = lines[0].Split(' ');
                if (parts.Length < 3) return;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in lines.Skip(1))
                {
                    var i = l.IndexOf(':', StringComparison.Ordinal);
                    if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim();
                }
                var body = Array.Empty<byte>();
                if (headers.TryGetValue("Content-Length", out var cl) && long.Parse(cl, CultureInfo.InvariantCulture) is > 0 and var len)
                {
                    body = new byte[len];
                    await s.ReadExactlyAsync(body, _cts.Token);
                }
                var (status, respHeaders, respBody) = Handle(parts[0], parts[1], headers, body);
                var sb = new StringBuilder();
                sb.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} X\r\n");
                foreach (var (k, v) in respHeaders) sb.Append(CultureInfo.InvariantCulture, $"{k}: {v}\r\n");
                sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {respBody.Length}\r\n\r\n");
                await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), _cts.Token);
                await s.WriteAsync(respBody, _cts.Token);
                await s.FlushAsync(_cts.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException or SocketException or EndOfStreamException)
        {
            // klient zamknął połączenie albo odrzucił certyfikat
        }
    }

    private async Task<string?> ReadHeadAsync(Stream s)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one, _cts.Token);
            if (n == 0) return null;
            buf.Add(one[0]);
            if (buf.Count == 1 && one[0] == 0x16 && _cert is null)
            {
                // ClientHello na porcie bez TLS — jak nginx/Apache: odpowiedź 400 otwartym tekstem i zamknięcie połączenia.
                await s.WriteAsync("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _cts.Token);
                return null;
            }
            if (buf.Count >= 4 && buf[^4] == '\r' && buf[^3] == '\n' && buf[^2] == '\r' && buf[^1] == '\n')
                return Encoding.ASCII.GetString(buf.ToArray(), 0, buf.Count - 4);
            if (buf.Count > 64 * 1024) return null;
        }
    }

    private (int Status, List<(string, string)> Headers, byte[] Body) Handle(string method, string target, Dictionary<string, string> h, byte[] body)
    {
        var none = new List<(string, string)>();
        lock (_gate) Requests.Add(method + " " + target);
        if (h.ContainsKey("Authorization")) AuthorizationSeen = true;
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Secret));
        if (!h.TryGetValue("Authorization", out var auth) || auth != expected)
            return (401, [("WWW-Authenticate", "Basic realm=\"kobian\"")], []);
        var rawPath = target.Split('?')[0];
        var trailingSlash = rawPath.Length > 1 && rawPath.EndsWith('/');
        var path = Norm(Uri.UnescapeDataString(rawPath));
        lock (_gate)
        {
            switch (method)
            {
                case "PROPFIND":
                {
                    if (!_items.TryGetValue(path, out var item)) return (404, none, []);
                    if (item is null && !trailingSlash && path != "/" && RedirectCollectionsWithoutSlash)
                        return (301, [("Location", rawPath + "/")], []);
                    var depth = h.TryGetValue("Depth", out var d) ? d : "infinity";
                    var list = new List<string> { path };
                    if (depth == "1" && item is null) list.AddRange(_items.Keys.Where(k => k != path && Parent(k) == path));
                    var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\">");
                    foreach (var p in list)
                    {
                        var data = _items[p];
                        var href = (p == "/" ? "" : string.Join('/', p.Split('/').Select(Uri.EscapeDataString))) + (data is null ? "/" : "");
                        if (AbsoluteHrefs) href = "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + href;
                        xml.Append("<d:response><d:href>").Append(href).Append("</d:href><d:propstat><d:prop>");
                        xml.Append(data is null ? "<d:resourcetype><d:collection/></d:resourcetype>" : "<d:resourcetype/>");
                        if (data is not null) xml.Append(CultureInfo.InvariantCulture, $"<d:getcontentlength>{data.Length}</d:getcontentlength>");
                        xml.Append("<d:getlastmodified>Tue, 27 Oct 2026 10:00:00 GMT</d:getlastmodified>");
                        if (QuotaAvailable is { } q && data is null) xml.Append(CultureInfo.InvariantCulture, $"<d:quota-available-bytes>{q}</d:quota-available-bytes>");
                        xml.Append("</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>");
                        xml.Append("<d:propstat><d:prop><d:getetag/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>");
                    }
                    xml.Append("</d:multistatus>");
                    return (207, [("Content-Type", "application/xml; charset=utf-8")], Encoding.UTF8.GetBytes(xml.ToString()));
                }
                case "GET":
                {
                    if (!_items.TryGetValue(path, out var data) || data is null) return (404, none, []);
                    if (!IgnoreRange && h.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.Ordinal))
                    {
                        var from = long.Parse(range[6..].Split('-')[0], CultureInfo.InvariantCulture);
                        if (from >= data.Length) return (416, none, []);
                        return (206, [("Content-Range", string.Create(CultureInfo.InvariantCulture, $"bytes {from}-{data.Length - 1}/{data.Length}"))], data[(int)from..]);
                    }
                    return (200, none, data);
                }
                case "PUT":
                {
                    if (FailNextPut is { } fail)
                    {
                        FailNextPut = null;
                        return (fail, none, []);
                    }
                    if (!_items.TryGetValue(Parent(path), out var parent) || parent is not null) return (409, none, []);
                    if (_items.TryGetValue(path, out var existing))
                    {
                        if (existing is null) return (405, none, []);
                        if (!IgnoreIfNoneMatch && h.TryGetValue("If-None-Match", out var inm) && inm == "*") return (412, none, []);
                    }
                    _items[path] = body;
                    return (201, none, []);
                }
                case "MKCOL":
                    if (_items.ContainsKey(path)) return (405, none, []);
                    if (!_items.TryGetValue(Parent(path), out var p2) || p2 is not null) return (409, none, []);
                    _items[path] = null;
                    return (201, none, []);
                case "MOVE":
                {
                    BeforeMove?.Invoke();
                    if (!_items.TryGetValue(path, out var data) || data is null) return (404, none, []);
                    if (!h.TryGetValue("Destination", out var dest)) return (400, none, []);
                    var to = Norm(Uri.UnescapeDataString(new Uri(dest).AbsolutePath));
                    var overwrite = !h.TryGetValue("Overwrite", out var ow) || ow != "F";
                    if (_items.ContainsKey(to) && !overwrite) return (412, none, []);
                    if (!_items.TryGetValue(Parent(to), out var pp) || pp is not null) return (409, none, []);
                    var existed = _items.ContainsKey(to);
                    _items.Remove(path);
                    _items[to] = data;
                    return (existed ? 204 : 201, none, []);
                }
                case "DELETE":
                {
                    if (!_items.ContainsKey(path)) return (404, none, []);
                    foreach (var k in _items.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList()) _items.Remove(k);
                    return (204, none, []);
                }
                default:
                    return (405, none, []);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}
