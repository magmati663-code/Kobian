using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql.SqlClient;

namespace Kobian.Providers.Sources.MsSql.Tls;

/// <summary>Adres serwera SQL do pobrania certyfikatu: host + port TCP albo instancja nazwana (port z SQL Browser).</summary>
public sealed record SqlEndpoint(string Host, int? Port, string? Instance)
{
    public const int DefaultPort = 1433;

    /// <summary>Ta sama walidacja co w łańcuchu połączenia (<see cref="MsSqlConnectionFactory"/>): bez prefiksów protokołu, DAC i potoków.</summary>
    public static SqlEndpoint Parse(string? server)
    {
        var s = MsSqlConnectionFactory.ValidateServer(server);
        var parts = s.Split(',');
        int? port = parts.Length == 2 ? int.Parse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture) : null;
        var hostPart = parts[0];
        string? instance = null;
        var slash = hostPart.IndexOf('\\', StringComparison.Ordinal);
        if (slash >= 0)
        {
            instance = hostPart[(slash + 1)..];
            hostPart = hostPart[..slash];
            if (string.Equals(instance, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase)) instance = null; // instancja domyślna
        }
        if (hostPart is "." || string.Equals(hostPart, "(local)", StringComparison.OrdinalIgnoreCase)) hostPart = "localhost";
        if (hostPart.StartsWith('[') && hostPart.EndsWith(']')) hostPart = hostPart[1..^1];
        if (hostPart.Length == 0) throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Invalid MSSQL connection option: server.");
        return new SqlEndpoint(hostPart, port, port is null ? instance : null);
    }
}

/// <summary>Certyfikat przedstawiony przez serwer SQL (tylko liść) — dane do zatwierdzenia przez Operatora (bez danych klienta poza nazwą serwera).</summary>
public sealed record SqlServerCertificateInfo(
    byte[] RawData,
    string Sha256Hex,
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool SelfIssued,
    bool SystemTrusted)
{
    public static SqlServerCertificateInfo From(X509Certificate2 cert, string host)
    {
        ArgumentNullException.ThrowIfNull(cert);
        var raw = cert.RawData;
        bool trusted;
        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // bez ruchu sieciowego do CRL/OCSP w trakcie kreatora
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // serverAuth
            trusted = chain.Build(cert) && cert.MatchesHostname(host, allowWildcards: true, allowCommonName: true);
            foreach (var e in chain.ChainElements) e.Certificate.Dispose();
        }
        return new SqlServerCertificateInfo(raw, Convert.ToHexString(SHA256.HashData(raw)), cert.Subject, cert.Issuer,
            cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(),
            string.Equals(cert.Subject, cert.Issuer, StringComparison.Ordinal), trusted);
    }
}

/// <summary>Port: pobranie certyfikatu TLS serwera SQL bez logowania (BL-40 cz. 5, Qb32).</summary>
public interface ISqlServerCertificateFetcher
{
    Task<SqlServerCertificateInfo> FetchAsync(string server, bool strict, CancellationToken ct);
}

/// <summary>
/// Pobranie certyfikatu z handshake TLS (BL-40 cz. 5, warunek pinu z Qb30): TDS 7.x — PRELOGIN z ENCRYPT_ON, potem handshake TLS
/// w pakietach PRELOGIN (<see cref="TdsPreloginStream"/>); TDS 8 (<c>strict</c>) — TLS od pierwszego bajtu z ALPN <c>tds/8.0</c>.
/// Callback walidacji zapamiętuje certyfikat i ZAWSZE odrzuca połączenie: żadne dane logowania nie są wysyłane (brak LOGIN7),
/// więc nie da się „przypadkiem” zalogować do niezatwierdzonego serwera. Błędy: KB-3001 (brak połączenia/limit czasu), KB-3006 (brak TLS).
/// </summary>
public sealed class TdsCertificateFetcher : ISqlServerCertificateFetcher
{
    /// <summary>Maksymalna liczba pakietów odpowiedzi PRELOGIN (serwer SQL wysyła jeden).</summary>
    internal const int MaxPreloginPackets = 16;

    private const byte EncryptOff = 0x00, EncryptOn = 0x01, EncryptNotSupported = 0x02, EncryptRequired = 0x03;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Port UDP usługi SQL Browser (SSRP) — zmieniany tylko w testach.</summary>
    public int BrowserPort { get; init; } = SsrpClient.DefaultPort;

    public async Task<SqlServerCertificateInfo> FetchAsync(string server, bool strict, CancellationToken ct)
    {
        var ep = SqlEndpoint.Parse(server);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var port = ep.Port ?? (ep.Instance is { } inst
                ? await SsrpClient.ResolveTcpPortAsync(ep.Host, inst, BrowserPort, cts.Token).ConfigureAwait(false)
                : SqlEndpoint.DefaultPort);
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(ep.Host, port, cts.Token).ConfigureAwait(false);
            var net = tcp.GetStream();
            Stream transport = net;
            if (!strict)
            {
                await SendPreloginAsync(net, cts.Token).ConfigureAwait(false);
                var encryption = await ReadPreloginEncryptionAsync(net, cts.Token).ConfigureAwait(false);
                if (encryption is not (EncryptOff or EncryptOn or EncryptRequired)) throw TlsUnavailable();
                transport = new TdsPreloginStream(net);
            }

            X509Certificate2? captured = null;
            await using var ssl = new SslStream(transport, leaveInnerStreamOpen: true);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = ep.Host,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is not null && captured is null) captured = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
                    return false; // zawsze odrzuć: tylko odczyt certyfikatu, bez logowania
                },
            };
            if (strict) options.ApplicationProtocols = [new SslApplicationProtocol("tds/8.0")];
            try
            {
                await ssl.AuthenticateAsClientAsync(options, cts.Token).ConfigureAwait(false);
            }
            catch (AuthenticationException)
            {
                // oczekiwane: callback odrzucił certyfikat
            }
            catch (IOException) when (captured is not null)
            {
                // serwer zamknął połączenie po odrzuceniu — certyfikat już mamy
            }

            if (captured is null) throw TlsUnavailable();
            using (captured) return SqlServerCertificateInfo.From(captured, ep.Host);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Unreachable("timeout");
        }
        catch (SocketException)
        {
            throw Unreachable("socket");
        }
        catch (IOException)
        {
            throw TlsUnavailable();
        }
        catch (InvalidDataException)
        {
            throw TlsUnavailable();
        }
    }

    /// <summary>PRELOGIN (MS-TDS 2.2.6.5): VERSION (0) + ENCRYPTION = ENCRYPT_ON + terminator. Bez INSTOPT/THREADID/MARS.</summary>
    internal static byte[] PreloginPayload()
    {
        // 2 opcje × 5 B + terminator = 11 B nagłówka opcji; VERSION 6 B od 11; ENCRYPTION 1 B od 17
        return [0x00, 0, 11, 0, 6, 0x01, 0, 17, 0, 1, 0xFF, 0, 0, 0, 0, 0, 0, EncryptOn];
    }

    private static async Task SendPreloginAsync(Stream s, CancellationToken ct)
    {
        var payload = PreloginPayload();
        await s.WriteAsync(TdsPreloginStream.Header(TdsPreloginStream.PreloginType, payload.Length, 1), ct).ConfigureAwait(false);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Odczyt odpowiedzi PRELOGIN (pakiety do EOM, łącznie ≤ 4 KiB) i wartości opcji ENCRYPTION; brak opcji = 0x02 (brak TLS).</summary>
    internal static async Task<byte> ReadPreloginEncryptionAsync(Stream s, CancellationToken ct)
    {
        var payload = new List<byte>();
        for (var packets = 1; ; packets++)
        {
            // pakiety bez EOM (także puste) ograniczone liczbą, nie tylko sumą ładunku — inaczej pętla do limitu czasu (SEC-3.3, it. 75)
            if (packets > MaxPreloginPackets) throw new InvalidDataException("Too many PRELOGIN response packets.");
            var header = new byte[TdsPreloginStream.HeaderLength];
            if (!await TdsPreloginStream.ReadExactAsync(s, header, ct).ConfigureAwait(false)) throw new InvalidDataException("No PRELOGIN response.");
            var length = (header[2] << 8) | header[3];
            if (header[0] != 0x04 || length < TdsPreloginStream.HeaderLength || payload.Count + length > 4096) throw new InvalidDataException("Bad PRELOGIN response.");
            var body = new byte[length - TdsPreloginStream.HeaderLength];
            if (!await TdsPreloginStream.ReadExactAsync(s, body, ct).ConfigureAwait(false)) throw new InvalidDataException("Bad PRELOGIN response.");
            payload.AddRange(body);
            if ((header[1] & 0x01) != 0) break;
        }
        return ParseEncryption([.. payload]);
    }

    internal static byte ParseEncryption(ReadOnlySpan<byte> payload)
    {
        for (var i = 0; i + 4 < payload.Length && payload[i] != 0xFF; i += 5)
        {
            if (payload[i] != 0x01) continue;
            var offset = (payload[i + 1] << 8) | payload[i + 2];
            var length = (payload[i + 3] << 8) | payload[i + 4];
            if (length < 1 || offset >= payload.Length) throw new InvalidDataException("Bad ENCRYPTION option.");
            return payload[offset];
        }
        return EncryptNotSupported;
    }

    private static KobianException Unreachable(string why) =>
        new(KbCode.SqlUnreachable, ErrorClass.Transient, "Cannot reach the SQL Server to read its certificate (" + why + ").");

    private static KobianException TlsUnavailable() =>
        new(KbCode.SqlTlsUnavailable, ErrorClass.Security, "The SQL Server did not present a TLS certificate.");
}

/// <summary>
/// SQL Server Resolution Protocol (MS-SQLR): CLNT_UCAST_INST (0x04 + nazwa instancji) na UDP 1434 → SVR_RESP z parą <c>tcp;port</c>.
/// Odpowiedź ≤ 1 KiB; brak TCP w odpowiedzi albo brak odpowiedzi w limicie = KB-3001.
/// </summary>
public static class SsrpClient
{
    public const int DefaultPort = 1434;

    public static async Task<int> ResolveTcpPortAsync(string host, string instance, int browserPort, CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.Connect(host, browserPort);
        var request = new byte[instance.Length + 2];
        request[0] = 0x04;
        Encoding.ASCII.GetBytes(instance, 0, instance.Length, request, 1); // nazwa zwalidowana: [A-Za-z0-9._-]
        await udp.SendAsync(request, ct).ConfigureAwait(false);
        var reply = await udp.ReceiveAsync(ct).ConfigureAwait(false);
        return ParseTcpPort(reply.Buffer, instance)
            ?? throw new KobianException(KbCode.SqlUnreachable, ErrorClass.Transient, "SQL Browser did not report a TCP port for the instance.");
    }

    internal static int? ParseTcpPort(ReadOnlySpan<byte> reply, string instance)
    {
        if (reply.Length < 3 || reply.Length > 1024 || reply[0] != 0x05) return null;
        var size = reply[1] | (reply[2] << 8);
        if (size > reply.Length - 3) return null;
        var fields = Encoding.ASCII.GetString(reply.Slice(3, size)).Split(';');
        var instanceOk = false;
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            if (string.Equals(fields[i], "InstanceName", StringComparison.OrdinalIgnoreCase))
                instanceOk = string.Equals(fields[i + 1], instance, StringComparison.OrdinalIgnoreCase);
            else if (string.Equals(fields[i], "tcp", StringComparison.OrdinalIgnoreCase) && instanceOk
                && int.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
                return port;
        }
        return null;
    }
}
