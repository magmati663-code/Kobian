using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Kobian.Notifications.Webhooks;

/// <summary>Podpis webhooka D9.10 (koryguje K19.1): <c>v1=HMAC-SHA256(secret, timestamp + "." + body)</c>, okno 300 s.</summary>
public static class WebhookSignature
{
    public const int ToleranceSeconds = 300;

    public static string Sign(ReadOnlySpan<byte> secret, long unixTimestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes(unixTimestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var msg = new byte[prefix.Length + body.Length];
        prefix.CopyTo(msg, 0);
        body.CopyTo(msg.AsSpan(prefix.Length));
        return "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, msg));
    }

    /// <summary>Weryfikacja po stronie odbiorcy (przykład referencyjny do dokumentacji i testów): dowolny z podpisów v1, znacznik w oknie.</summary>
    public static bool Verify(ReadOnlySpan<byte> secret, string timestampHeader, string signatureHeader, ReadOnlySpan<byte> body, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(signatureHeader);
        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var ts)) return false;
        if (Math.Abs(nowUnix - ts) > ToleranceSeconds) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(secret, ts, body));
        var ok = false;
        foreach (var part in signatureHeader.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // brak wczesnego wyjścia — stały koszt względem liczby podpisów
            ok |= CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(part), expected);
        }
        return ok;
    }
}

/// <summary>
/// Statyczna walidacja URL webhooka (SEC-9.3 — SSRF, KB-6302): tylko https, bez danych logowania w URL, bez portów
/// innych niż 443 (domyślnie), bez literałów IP z zakresów prywatnych/lokalnych. Kontrola adresu po rozwiązaniu DNS
/// (ponownie przy każdym połączeniu) — w nadawcy (ConnectCallback), zadanie BL-45.
/// </summary>
public static class WebhookUrlPolicy
{
    public static bool IsAllowed(string url, bool allowNonStandardPort = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;
        if (!allowNonStandardPort && !u.IsDefaultPort) return false;
        if (u.IsLoopback) return false;
        if (u.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IsPublic(ip);
        string host;
        try
        {
            host = u.IdnHost.TrimEnd('.');
        }
        catch (UriFormatException)
        {
            return false; // it. 120 (przegląd rundy 8, Niskie): host niepoprawny wg IDNA — wcześniej wyjątek z walidacji konfiguracji zamiast odmowy (KB-6302)
        }
        return !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                 || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                 || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                 || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
                 || !host.Contains('.', StringComparison.Ordinal));
    }

    /// <summary>Adres publiczny: nie loopback, link-local, prywatny (RFC 1918/4193), CGNAT, multicast, nieokreślony, mapowany IPv4 prywatny.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224);
        }
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal) return false;
        // it. 120 (przegląd rundy 8, Niskie): IPv6 z osadzonym IPv4 — bramka NAT64/6to4 w sieci klienta przekazałaby połączenie do adresu wewnętrznego.
        Span<byte> v6 = stackalloc byte[16];
        if (!ip.TryWriteBytes(v6, out _)) return false;
        if (v6[..12].IndexOfAnyExcept((byte)0) < 0) return false; // ::/96 — nieokreślony, loopback, „IPv4-compatible” (przestarzałe)
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b) // NAT64: 64:ff9b::/96 (RFC 6052), 64:ff9b:1::/48 lokalny (RFC 8215)
            return !(v6[4] == 0x00 && v6[5] == 0x01) && IsPublic(new IPAddress(v6[12..]));
        if (v6[0] == 0x20 && v6[1] == 0x02) return IsPublic(new IPAddress(v6[2..6])); // 6to4 2002::/16
        return !(v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x00 && v6[3] == 0x00); // Teredo 2001::/32 — adres docelowy zaciemniony, nieprzewidywalny
    }
}
