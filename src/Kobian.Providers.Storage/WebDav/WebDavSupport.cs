using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Xml;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Ftp;

namespace Kobian.Providers.Storage.WebDav;

// Czyste (testowalne bez serwera) elementy adaptera WebDAV: opcje celu, kodowanie ścieżek, parser odpowiedzi 207 Multi-Status
// i klasyfikacja błędów HTTP wg D4.8.

/// <summary>
/// Opcje celu „webdav” z <see cref="DestinationConfig.Options"/>: host, port, spki (lista pinów „SHA256:&lt;base64&gt;”, jak FTPS), require_pin,
/// connect_timeout_s, insecure (= „1”: jawny HTTP — tylko poza definicją zadania; hasło leci otwartym tekstem, D4.3).
/// </summary>
internal sealed record WebDavOptions(string Host, int Port, bool Tls, IReadOnlyList<string> SpkiPins, bool RequirePin, TimeSpan ConnectTimeout)
{
    public const string InsecureOption = "insecure";
    public const int MaxPins = 4;

    public Uri BaseUri => new((Tls ? "https://" : "http://") + (Host.Contains(':', StringComparison.Ordinal) ? "[" + Host + "]" : Host) + ":"
                              + Port.ToString(CultureInfo.InvariantCulture) + "/");

    public static WebDavOptions Parse(DestinationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var o = config.Options;
        string Get(string k) => o is not null && o.TryGetValue(k, out var v) ? v : "";
        var host = Get("host");
        if (host.Length is 0 or > 253 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '_'))
            throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Invalid WebDAV host option.");
        var tls = Get(InsecureOption) switch
        {
            "" or "0" => true,
            "1" => false,
            _ => throw new StorageException(ErrorClass.Security, KbCode.InsecureTransportNotConfirmed, "Invalid WebDAV insecure option."),
        };
        var port = tls ? 443 : 80;
        if (Get("port") is { Length: > 0 } ps
            && (!int.TryParse(ps, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            throw new StorageException(ErrorClass.Permanent, KbCode.DestinationNotFound, "Invalid WebDAV port option.");
        var pins = new List<string>();
        if (Get("spki") is { Length: > 0 } spki)
        {
            foreach (var raw in spki.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!FtpOptions.IsPinShape(raw) || pins.Count >= MaxPins)
                    throw new StorageException(ErrorClass.Security, KbCode.TlsIdentityMismatch, "Invalid SPKI pin option.");
                pins.Add(raw.TrimEnd('='));
            }
        }
        var requirePin = Get("require_pin") switch
        {
            "" or "0" => false,
            "1" => true,
            _ => throw new StorageException(ErrorClass.Security, KbCode.TlsIdentityMismatch, "Invalid require_pin option."),
        };
        var timeout = TimeSpan.FromSeconds(30);
        if (Get("connect_timeout_s") is { Length: > 0 } ts
            && int.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out var sec) && sec is >= 1 and <= 300)
            timeout = TimeSpan.FromSeconds(sec);
        return new WebDavOptions(host, port, tls, pins, requirePin, timeout);
    }
}

/// <summary>Kodowanie ścieżek URL: każdy segment osobno (spacje, polskie znaki, „#”, „?” w korzeniu); „/” zostaje separatorem.</summary>
internal static class WebDavPath
{
    public static string Encode(string path, bool collection = false)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var encoded = "/" + string.Join('/', segs.Select(Uri.EscapeDataString));
        return collection && segs.Length > 0 ? encoded + "/" : encoded;
    }

    /// <summary>Ścieżka z „href” odpowiedzi (bezwzględny URL albo ścieżka), zdekodowana, bez końcowego „/” (korzeń = „/”).</summary>
    public static string? FromHref(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        string raw;
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)) raw = abs.AbsolutePath;
        else if (href.StartsWith('/')) raw = href.Split('?', '#')[0];
        else return null;
        var decoded = Uri.UnescapeDataString(raw);
        if (decoded.Any(char.IsControl)) return null;
        var trimmed = decoded.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    public static string Normalize(string path)
    {
        var t = path.TrimEnd('/');
        return t.Length == 0 ? "/" : t;
    }

    public static string NameOf(string path) => path == "/" ? "/" : path[(path.LastIndexOf('/') + 1)..];

    public static string ParentOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }
}

/// <summary>Jeden element &lt;response&gt; z odpowiedzi 207 (właściwości z propstat o statusie 200).</summary>
internal sealed record MultiStatusEntry(string Path, bool IsCollection, long? Length, DateTimeOffset? Modified, long? QuotaAvailable);

/// <summary>
/// Parser 207 Multi-Status (RFC 4918 §14.16) odporny na wrogi serwer: bez DTD i encji zewnętrznych, limit rozmiaru dokumentu,
/// tylko właściwości z propstat „200 OK”; wpisy z nieczytelnym href są pomijane.
/// </summary>
internal static class MultiStatusParser
{
    public const string Dav = "DAV:";

    /// <summary>Górna granica dokumentu (znaki) — katalog z dziesiątkami tysięcy packów mieści się z zapasem.</summary>
    public const long MaxCharacters = 256L * 1024 * 1024;

    public static List<MultiStatusEntry> Parse(Stream body)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            Async = false,
        };
        var list = new List<MultiStatusEntry>();
        try
        {
            using var r = XmlReader.Create(body, settings);
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "response" && r.NamespaceURI == Dav && !r.IsEmptyElement)
                {
                    using var sub = r.ReadSubtree();
                    if (ParseResponse(sub) is { } e) list.Add(e);
                }
            }
        }
        catch (XmlException ex)
        {
            throw new StorageException(ErrorClass.Transient, KbCode.TransientIo, "Unparseable WebDAV multistatus reply.", ex);
        }
        return list;
    }

    private static MultiStatusEntry? ParseResponse(XmlReader r)
    {
        string? href = null;
        bool collection = false, ok = false;
        long? length = null, quota = null;
        DateTimeOffset? modified = null;
        r.Read(); // <response>
        r.Read();
        // ReadElementContentAsString przesuwa czytnik ZA element — wtedy bez kolejnego Read (inaczej pominęlibyśmy sąsiedni element).
        while (!r.EOF)
        {
            if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == Dav && r.LocalName == "href" && href is null)
            {
                href = r.ReadElementContentAsString();
                continue;
            }
            if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == Dav && r.LocalName == "propstat" && !r.IsEmptyElement)
            {
                using (var ps = r.ReadSubtree())
                {
                    var p = ParsePropstat(ps);
                    if (p.Ok)
                    {
                        ok = true;
                        collection |= p.Collection;
                        length ??= p.Length;
                        modified ??= p.Modified;
                        quota ??= p.Quota;
                    }
                }
            }
            r.Read();
        }
        var path = href is null ? null : WebDavPath.FromHref(href);
        return path is null || !ok ? null : new MultiStatusEntry(path, collection, length, modified, quota);
    }

    private static (bool Ok, bool Collection, long? Length, DateTimeOffset? Modified, long? Quota) ParsePropstat(XmlReader r)
    {
        bool collection = false;
        long? length = null, quota = null;
        DateTimeOffset? modified = null;
        string? status = null;
        r.Read(); // <propstat>
        r.Read();
        while (!r.EOF)
        {
            if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == Dav && !r.IsEmptyElement)
            {
                switch (r.LocalName)
                {
                    case "status":
                        status = r.ReadElementContentAsString();
                        continue;
                    case "getcontentlength":
                        if (long.TryParse(r.ReadElementContentAsString().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) length = n;
                        continue;
                    case "quota-available-bytes":
                        if (long.TryParse(r.ReadElementContentAsString().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var q)) quota = q;
                        continue;
                    case "getlastmodified":
                        if (DateTimeOffset.TryParseExact(r.ReadElementContentAsString().Trim(), "r", CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal, out var m)) modified = m;
                        continue;
                }
            }
            if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == Dav && r.LocalName == "collection") collection = true;
            r.Read();
        }
        // „HTTP/1.1 200 OK” — kod na drugiej pozycji.
        var parts = status?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ok = parts is { Length: >= 2 } && parts[1] == "200";
        return (ok, collection, length, modified, quota);
    }
}

/// <summary>Klasyfikacja błędów WebDAV/HTTP wg D4.8 (odpowiednik wierszy SFTP/FTP).</summary>
internal static class WebDavErrorMap
{
    public static StorageException FromStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return code switch
        {
            401 => new(ErrorClass.Permanent, KbCode.AuthRejected, "Credentials rejected by destination."),
            403 => new(ErrorClass.Permanent, KbCode.AccessDenied, "Access denied on destination."),
            404 or 409 or 410 => new(ErrorClass.Permanent, KbCode.DestinationNotFound, "Destination path not found."),
            412 => new(ErrorClass.Permanent, KbCode.ObjectExists, "Object already exists on destination; never overwritten."),
            413 => new(ErrorClass.Permanent, KbCode.DestinationObjectTooLarge, "Destination rejected the object as too large."),
            507 => new(ErrorClass.Permanent, KbCode.DestinationFull, "No space left on destination."),
            423 or 408 or 425 => new(ErrorClass.Transient, KbCode.TransientIo, "Destination resource temporarily unavailable."),
            429 or 503 => new(ErrorClass.Transient, KbCode.ServerSessionLimit, "Destination refused the request (server limit)."),
            >= 300 and < 400 => new(ErrorClass.Permanent, KbCode.WebDavRequestRefused, "Destination answered with a redirect; redirects are not followed."),
            >= 500 => new(ErrorClass.Transient, KbCode.TransientIo, "Transient WebDAV error on destination."),
            _ => new(ErrorClass.Permanent, KbCode.WebDavRequestRefused, "WebDAV request refused by destination."),
        };
    }

    /// <summary>Wyjątki transportu (bez anulowania przez wywołującego). <paramref name="rejected"/> = odrzucony certyfikat z odciskiem.</summary>
    public static StorageException FromException(Exception ex, ServerIdentity? rejected)
    {
        if (ex is StorageException se) return se;
        if (rejected is not null)
            return new(ErrorClass.Security, KbCode.TlsIdentityMismatch, "Server certificate is not trusted and its key is not pinned.", ex) { PresentedIdentity = rejected };
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return new(ErrorClass.Permanent, KbCode.FtpsNotSupported, "TLS handshake with the WebDAV server failed.", ex);
        }
        return ex switch
        {
            HttpRequestException or IOException or TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException
                => new(ErrorClass.Transient, KbCode.TransientIo, "Connection error on destination.", ex),
            UnauthorizedAccessException => new(ErrorClass.Permanent, KbCode.AccessDenied, "Access denied on destination.", ex),
            _ => new(ErrorClass.Transient, KbCode.TransientIo, "Transient WebDAV error on destination.", ex),
        };
    }
}
