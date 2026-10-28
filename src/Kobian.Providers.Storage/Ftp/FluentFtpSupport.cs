using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Exceptions;
using Kobian.Application.Storage;

namespace Kobian.Providers.Storage.Ftp;

// Czyste (testowalne bez serwera) elementy adaptera FluentFTP: opcje celu, konfiguracja klienta (D4.3, SEC-4.6), pinning SPKI (D4.11),
// parsowanie odpowiedzi MLST/SIZE/AVBL i tłumaczenie wyjątków biblioteki na FtpProtocolException (D4.8).

/// <summary>Tryb szyfrowania wg D4.3 (kolejność preferencji: Explicit → Implicit → jawny tylko z flagą).</summary>
public enum FtpTlsMode
{
    Explicit = 1,
    Implicit = 2,
    None = 3,
}

/// <summary>Tryb kanału danych (zawsze pasywny, D4.3; aktywny PORT/EPRT celowo nieobsługiwany w 1.0).</summary>
public enum FtpPassiveMode
{
    /// <summary>PASV z adresem z odpowiedzi IGNOROWANYM — połączenie danych na IP kanału sterowania (ochrona przed FTP bounce/NAT, D4.3).</summary>
    PasvControlIp = 1,

    /// <summary>EPSV (RFC 2428) — tylko port, adres = sterowanie; wymagany dla IPv6.</summary>
    Epsv = 2,
}

/// <summary>
/// Opcje celu FTP/FTPS z <see cref="DestinationConfig.Options"/>: host, port, tls (explicit|implicit), spki (lista pinów „SHA256:&lt;base64&gt;”
/// rozdzielona przecinkami — rotacja, AC-12.2), passive (pasv|epsv), connect_timeout_s, insecure (tylko schemat „ftp”).
/// </summary>
internal sealed record FtpOptions(
    string Host,
    int Port,
    FtpTlsMode Tls,
    IReadOnlyList<string> SpkiPins,
    FtpPassiveMode Passive,
    TimeSpan ConnectTimeout,
    TimeSpan IoTimeout,
    bool RequirePin = false)
{
    public const string PinPrefix = "SHA256:";
    public const int MaxPins = 4;

    public static FtpOptions Parse(DestinationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var o = config.Options;
        string Get(string k) => o is not null && o.TryGetValue(k, out var v) ? v : "";
        var host = Get("host");
        if (host.Length is 0 or > 253 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '_'))
            throw new FtpProtocolException(FtpFailure.Transient, "Invalid FTP host option.");
        FtpTlsMode tls;
        if (config.Scheme == "ftp")
        {
            // D4.3: jawny FTP tylko z flagą „insecure” (poświadczenia lecą otwartym tekstem; szyfrowanie repo i tak obowiązkowe).
            if (Get(FtpStorageProviderFactory.InsecureOption) != "1")
                throw new FtpProtocolException(FtpFailure.InsecureNotConfirmed, "Unencrypted FTP requires the 'insecure' option.");
            tls = FtpTlsMode.None;
        }
        else if (config.Scheme == "ftps")
        {
            tls = Get("tls") switch
            {
                "" or "explicit" => FtpTlsMode.Explicit,
                "implicit" => FtpTlsMode.Implicit,
                _ => throw new FtpProtocolException(FtpFailure.TlsNotSupported, "Invalid FTPS 'tls' option."),
            };
        }
        else
        {
            throw new FtpProtocolException(FtpFailure.Transient, "Unsupported FTP scheme.");
        }
        var port = tls == FtpTlsMode.Implicit ? 990 : 21;
        if (Get("port") is { Length: > 0 } ps
            && (!int.TryParse(ps, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            throw new FtpProtocolException(FtpFailure.Transient, "Invalid FTP port option.");
        var pins = new List<string>();
        if (Get("spki") is { Length: > 0 } spki)
        {
            foreach (var raw in spki.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IsPinShape(raw) || pins.Count >= MaxPins)
                    throw new FtpProtocolException(FtpFailure.TlsIdentityRejected, "Invalid SPKI pin option.");
                pins.Add(raw.TrimEnd('='));
            }
        }
        // PROWIZORYCZNIE Qb70: domyślnie PASV z ignorowaniem adresu (IPv4, stare NAS), EPSV opcją; plan: „EPSV → PASV”.
        var passive = Get("passive") switch
        {
            "" or "pasv" => FtpPassiveMode.PasvControlIp,
            "epsv" => FtpPassiveMode.Epsv,
            _ => throw new FtpProtocolException(FtpFailure.Transient, "Invalid FTP passive option."),
        };
        var timeout = TimeSpan.FromSeconds(30);
        if (Get("connect_timeout_s") is { Length: > 0 } ts
            && int.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out var sec) && sec is >= 1 and <= 300)
            timeout = TimeSpan.FromSeconds(sec);
        // D4.13: bezczynność kanału danych 60 s (watchdog postępu wyżej); sterowanie 30 s.
        // SEC-14.5 (BL-35): cel „wysokiego ryzyka” — pin SPKI wymagany także przy ważnym łańcuchu CA (opcja Administratora w katalogu tożsamości Agenta).
        var requirePin = Get("require_pin") switch
        {
            "" or "0" => false,
            "1" => true,
            _ => throw new FtpProtocolException(FtpFailure.TlsIdentityRejected, "Invalid require_pin option."),
        };
        return new FtpOptions(host, port, tls, pins, passive, timeout, TimeSpan.FromSeconds(60), requirePin);
    }

    internal static bool IsPinShape(string pin) =>
        pin.Length is > 7 and <= 64 && pin.StartsWith(PinPrefix, StringComparison.Ordinal)
        && pin.AsSpan(PinPrefix.Length).IndexOfAnyExcept(Base64Chars) < 0;

    private static readonly System.Buffers.SearchValues<char> Base64Chars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=");
}

/// <summary>Pin SHA-256 z SubjectPublicKeyInfo certyfikatu (D4.11: przetrwa odnowienie certyfikatu z tym samym kluczem).</summary>
internal static class TlsSpkiPin
{
    public const string Kind = "tls-spki";

    /// <summary>„SHA256:” + base64 bez dopełnienia (ten sam zapis co odcisk klucza SSH — jeden format w UI i CLI).</summary>
    public static string Fingerprint(X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(cert);
        return FtpOptions.PinPrefix + Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())).TrimEnd('=');
    }

    /// <summary>Porównanie stałoczasowe z każdym pinem; pusta lista nigdy nie jest zgodnością.</summary>
    public static bool Matches(IReadOnlyList<string> pins, string fingerprint)
    {
        var actual = System.Text.Encoding.ASCII.GetBytes(fingerprint);
        var ok = false;
        foreach (var p in pins)
        {
            var expected = System.Text.Encoding.ASCII.GetBytes(p.TrimEnd('='));
            ok |= actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        return ok;
    }

    public static ServerIdentity Identity(X509Certificate2 cert) =>
        new(Kind, Fingerprint(cert), "SPKI " + cert.PublicKey.Oid.FriendlyName + ", subject " + Trim(cert.Subject));

    private static string Trim(string s) => s.Length <= 120 ? s : s[..120];
}

/// <summary>Wynik walidacji certyfikatu serwera FTPS (kanał sterowania).</summary>
internal sealed record TlsVerdict(bool Accepted, ServerIdentity Identity, bool ViaPin);

/// <summary>
/// Walidacja D4.11 (bez opcji „akceptuj wszystko”): (0) cel „wysokiego ryzyka” (<c>requirePin</c>, SEC-14.5) — wyłącznie zgodny pin, łańcuch CA nie wystarcza;
/// (1) poprawny łańcuch wobec systemowego magazynu zaufania i zgodna nazwa hosta → akceptacja
/// (SPKI zapisany informacyjnie; zmiana SPKI przy ważnym łańcuchu nie jest błędem); (2) w przeciwnym razie (self-signed, nieważny łańcuch, nazwa)
/// wymagany przypięty SPKI SHA-256 — brak lub niezgodność = odmowa KB-2109 z odciskiem do zatwierdzenia poza pasmem.
/// </summary>
internal static class FtpsCertificatePolicy
{
    public static TlsVerdict Evaluate(X509Certificate? certificate, SslPolicyErrors errors, IReadOnlyList<string> pins, bool requirePin = false)
    {
        if (certificate is null)
            return new TlsVerdict(false, new ServerIdentity(TlsSpkiPin.Kind, "", "no certificate"), false);
        using var cert = new X509Certificate2(certificate);
        var id = TlsSpkiPin.Identity(cert);
        if (errors == SslPolicyErrors.None && !requirePin) return new TlsVerdict(true, id, false);
        // Brak certyfikatu zdalnego nigdy nie jest akceptowany; pozostałe błędy (łańcuch, nazwa) — tylko z przypiętym SPKI.
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return new TlsVerdict(false, id, false);
        return TlsSpkiPin.Matches(pins, id.Fingerprint) ? new TlsVerdict(true, id, true) : new TlsVerdict(false, id, false);
    }
}

/// <summary>Konfiguracja <see cref="AsyncFtpClient"/> wg D4.3/D4.14/SEC-4.6 — jedno miejsce, testowane bez serwera.</summary>
internal static class FluentFtpConfig
{
    public static FtpConfig Build(FtpOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var connectMs = (int)o.ConnectTimeout.TotalMilliseconds;
        var ioMs = (int)o.IoTimeout.TotalMilliseconds;
        return new FtpConfig
        {
            EncryptionMode = o.Tls switch
            {
                FtpTlsMode.Explicit => FtpEncryptionMode.Explicit,
                FtpTlsMode.Implicit => FtpEncryptionMode.Implicit,
                _ => FtpEncryptionMode.None,
            },
            // TLS także na kanale danych (PROT P); brak CCC (szyfrowanie nie jest wyłączane po logowaniu).
            DataConnectionEncryption = o.Tls != FtpTlsMode.None,
            EncryptAuthenticationOnly = false,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            // Nigdy „akceptuj wszystko” (STRIDE S, D4.3): decyzję podejmuje FtpsCertificatePolicy w zdarzeniu ValidateCertificate.
            ValidateAnyCertificate = false,
            ValidateCertificateRevocation = true, // PROWIZORYCZNIE Qb72: offline CRL ⇒ potrzebny pin SPKI
            // SEC-4.6: bez logowania surowych komend i danych logowania (logger biblioteki i tak nie jest podłączony).
            LogToConsole = false,
            LogPassword = false,
            LogUserName = false,
            LogHost = false,
            DataConnectionType = o.Passive == FtpPassiveMode.Epsv ? FtpDataConnectionType.EPSV : FtpDataConnectionType.PassiveExtended,
            ConnectTimeout = connectMs,
            ReadTimeout = (int)TimeSpan.FromSeconds(30).TotalMilliseconds,
            DataConnectionConnectTimeout = connectMs,
            DataConnectionReadTimeout = ioMs,
            DataConnectionWriteTimeout = ioMs,
            SocketKeepAlive = true,
            // D4.14: TYPE I dla transferów i listingów.
            UploadDataType = FtpDataType.Binary,
            DownloadDataType = FtpDataType.Binary,
            ListingDataType = FtpDataType.Binary,
            CheckCapabilities = true,
            // Bez ukrytych ponownych połączeń i reautoryzacji: zerwanie = błąd przejściowy dla warstwy wyżej (D4.7 wznowienie, D4.16 dławienie otwarć).
            SelfConnectMode = FtpSelfConnectMode.Never,
            SslSessionLength = 0,
            Noop = false,
            RetryAttempts = 1,
        };
    }
}

/// <summary>Parsowanie odpowiedzi serwera (MLST/MLSD fakty RFC 3659, SIZE/AVBL „213 n”, HASH).</summary>
internal static class FtpReplyParser
{
    /// <summary>Linia faktów MLST („type=file;size=12;modify=20260101120000; /ścieżka”) → wpis; null, gdy nie wygląda jak fakty.</summary>
    public static FtpEntry? ParseMlstLine(string line, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(line);
        var t = line.TrimStart();
        var sp = t.IndexOf(' ', StringComparison.Ordinal);
        if (sp <= 0 || !t.Contains("type=", StringComparison.OrdinalIgnoreCase)) return null;
        var facts = t[..sp];
        var name = t[(sp + 1)..].TrimEnd();
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.Length == 0) name = fallbackName;
        var kind = FtpEntryKind.Other;
        long size = 0;
        DateTimeOffset? modified = null;
        foreach (var f in facts.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = f.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = f[..eq].ToLowerInvariant();
            var val = f[(eq + 1)..];
            switch (key)
            {
                case "type":
                    var v = val.ToLowerInvariant();
                    kind = v switch
                    {
                        "file" => FtpEntryKind.File,
                        "dir" or "cdir" or "pdir" => FtpEntryKind.Directory,
                        _ when v.StartsWith("os.unix=slink", StringComparison.Ordinal) || v.StartsWith("os.unix=symlink", StringComparison.Ordinal) => FtpEntryKind.Link,
                        _ => FtpEntryKind.Other,
                    };
                    break;
                case "size":
                    if (!long.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out size)) size = 0;
                    break;
                case "modify":
                    if (DateTime.TryParseExact(val.Split('.')[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                        modified = new DateTimeOffset(dt, TimeSpan.Zero);
                    break;
                case "unix.mode" or "perm":
                    break;
            }
        }
        // Niektóre serwery (ProFTPD) podają dowiązanie jako type=OS.unix=symlink — obsłużone wyżej; inne jako fakt „unix.type”/„x.type”.
        return new FtpEntry(name, kind, size, modified);
    }

    /// <summary>„213 12345” → 12345 (SIZE, AVBL). Wymaga kodu 213 i liczby bez znaku.</summary>
    public static long? ParseNumber213(string code, string message)
    {
        if (code != "213") return null;
        var m = message.Trim();
        return long.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    public static int Code(string? code) => int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 0;

    /// <summary>Wartość hex SHA-256 z odpowiedzi HASH → 32 bajty albo null.</summary>
    public static byte[]? ParseSha256Hex(string? hex)
    {
        if (hex is null || hex.Length != 64) return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Wyjątki FluentFTP / sieci / TLS → <see cref="FtpProtocolException"/> (D4.8). Komunikaty bez treści komend (SEC-4.6).</summary>
internal static class FluentFtpErrorTranslator
{
    public static FtpProtocolException Translate(Exception ex, bool writeContext = false) => ex switch
    {
        FtpProtocolException p => p,
        FtpAuthenticationException => new(FtpFailure.AuthRejected, "FTP authentication rejected.", ex, 530),
        FtpSecurityNotAvailableException => new(FtpFailure.TlsNotSupported, "FTP server refused AUTH TLS.", ex),
        FtpInvalidCertificateException or AuthenticationException => new(FtpFailure.TlsIdentityRejected, "FTPS certificate validation failed.", ex),
        FtpCommandException c => FromReply(FtpReplyParser.Code(c.CompletionCode), c.Message, writeContext, ex),
        TimeoutException => new(FtpFailure.Transient, "FTP operation timed out.", ex),
        System.Net.Sockets.SocketException => new(FtpFailure.Transient, "FTP connection failed.", ex),
        IOException => new(FtpFailure.Transient, "FTP connection lost.", ex),
        _ => new(FtpFailure.Transient, "FTP error.", ex),
    };

    /// <summary>Negatywna odpowiedź serwera → wyjątek z kodem (do logu „ftp=NNN”), bez treści komendy.</summary>
    public static FtpProtocolException FromReply(int code, string? message, bool writeContext, Exception? inner = null) =>
        new(FtpErrorMap.Classify(code, message, writeContext), "FTP server replied " + code.ToString(CultureInfo.InvariantCulture) + ".", inner, code);
}
