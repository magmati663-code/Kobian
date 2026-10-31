using System.Net;
using System.Text.RegularExpressions;
using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Smb;

/// <summary>
/// Walidacja korzenia celu SMB <c>\\serwer\udział[\ścieżka]</c> (BL-73, D4.2, D8.9). Odrzuca: ścieżki urządzeń i „long path” (<c>\\?\</c>, <c>\\.\</c>),
/// strumienie ADS i litery dysku (<c>:</c>), segmenty „.”/„..”, nazwy urządzeń Windows, kropkę/spację na końcu segmentu (Windows je obcina — dwie nazwy,
/// jeden plik), udziały administracyjne (<c>IPC$</c>, <c>ADMIN$</c>, <c>C$</c>) oraz adresy IPv6 (Windows wymaga zapisu <c>ipv6-literal.net</c> — poza 1.0).
/// Separator „/” jest zamieniany na „\”. Komunikaty błędów nie zawierają ścieżki (NFR-11).
/// </summary>
public static partial class UncPath
{
    public const int MaxLength = 1024;
    public const int MaxShareLength = 80;

    [GeneratedRegex("^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$", RegexOptions.CultureInvariant)]
    private static partial Regex HostRule();

    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9]|CONIN\\$|CONOUT\\$)(\\..*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DeviceName();

    [GeneratedRegex("^([A-Za-z]\\$|IPC\\$|ADMIN\\$|PRINT\\$)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AdminShare();

    private static readonly System.Buffers.SearchValues<char> Forbidden = System.Buffers.SearchValues.Create("<>:\"|?*");

    /// <summary>Parsuje korzeń; błąd → <see cref="StorageException"/> KB-2106 (kształt) albo KB-2302 (niedozwolona nazwa — bezpieczeństwo).</summary>
    public static SmbShare Parse(string? root)
    {
        if (string.IsNullOrEmpty(root) || root.Length > MaxLength || root.Any(char.IsControl))
            throw NotFound("SMB destination root must be a UNC path \\\\server\\share[\\path].");
        var s = root.Replace('/', '\\');
        if (!s.StartsWith(@"\\", StringComparison.Ordinal) || s.StartsWith(@"\\?\", StringComparison.Ordinal) || s.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw NotFound("SMB destination root must be a UNC path \\\\server\\share[\\path].");
        var parts = s[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw NotFound("SMB destination root must name a server and a share.");
        var server = parts[0];
        if (!IsValidServer(server)) throw NotFound("SMB server name is not valid.");
        var share = parts[1];
        if (share.Length > MaxShareLength || !IsSafeSegment(share)) throw Refused("SMB share name is not allowed.");
        if (AdminShare().IsMatch(share)) throw Refused("Administrative shares are not allowed as a destination.");
        foreach (var seg in parts.AsSpan(2))
            if (!IsSafeSegment(seg)) throw Refused("SMB destination path contains a segment that is not allowed.");
        return new SmbShare(server, share, string.Join('\\', parts.Skip(2)));
    }

    /// <summary>Serwer podany jako adres IPv4 (Kerberos wymaga nazwy — kreator ostrzega; D4.15, KB-2113 po SP-22).</summary>
    public static bool ServerIsIpAddress(SmbShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        return IPAddress.TryParse(share.Server, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    private static bool IsValidServer(string server)
    {
        if (server.Contains(':', StringComparison.Ordinal)) return false; // IPv6 / port — poza 1.0
        if (IPAddress.TryParse(server, out var ip))
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && server.Count(c => c == '.') == 3;
        // Same cyfry i kropki, a nie poprawny IPv4 (np. „300.1.1.1”, „10.1”) — nie traktujemy jak nazwy hosta (resolwer zinterpretowałby to różnie).
        if (server.All(c => char.IsAsciiDigit(c) || c == '.')) return false;
        return HostRule().IsMatch(server);
    }

    private static bool IsSafeSegment(string seg) =>
        seg.Length is > 0 and <= 255
        && seg is not ("." or "..")
        && seg.AsSpan().IndexOfAny(Forbidden) < 0
        && !seg.EndsWith('.') && !seg.EndsWith(' ') && !seg.StartsWith(' ')
        && !DeviceName().IsMatch(seg);

    private static StorageException NotFound(string message) => new(ErrorClass.Permanent, KbCode.DestinationNotFound, message);

    private static StorageException Refused(string message) => new(ErrorClass.Security, KbCode.InvalidObjectName, message);
}
