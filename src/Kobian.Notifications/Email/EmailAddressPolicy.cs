using MimeKit;

namespace Kobian.Notifications.Email;

/// <summary>
/// Walidacja adresów e-mail (SEC-9.1, CVE-2026-30227; BL-45): pojedynczy adres w postaci <c>lokalna@domena</c>, wyłącznie drukowalne ASCII
/// (odrzuca CR/LF, NUL i inne znaki sterujące przed parserem), bez nazwy wyświetlanej, grup, komentarzy i list; limity RFC 5321
/// (lokalna ≤ 64, domena ≤ 253, całość ≤ 254); składnia sprawdzana przez MimeKit w trybie ścisłym i wynik musi być identyczny z wejściem.
/// PROWIZORYCZNIE Qb65: adresy IDN/UTF-8 odrzucane — administrator podaje domenę w punycode.
/// </summary>
public static class EmailAddressPolicy
{
    public const int MaxLength = 254;
    public const int MaxLocalPart = 64;
    public const int MaxDomain = 253;

    private static readonly ParserOptions Strict = new()
    {
        AddressParserComplianceMode = RfcComplianceMode.Strict,
        AllowAddressesWithoutDomain = false,
        AllowUnquotedCommasInAddresses = false,
    };

    public static bool TryParse(string? text, out MailboxAddress? address)
    {
        address = null;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return false;
        foreach (var ch in text)
            if (ch is < (char)0x21 or > (char)0x7E) return false; // CR/LF, spacje, sterujące, spoza ASCII
        if (text.IndexOfAny(['<', '>', '"', '(', ')', ',', ';', ':', '\\', '[', ']']) >= 0) return false; // tylko addr-spec, bez cytowania i literałów
        var at = text.LastIndexOf('@');
        if (at <= 0 || at != text.IndexOf('@', StringComparison.Ordinal) || at > MaxLocalPart || text.Length - at - 1 > MaxDomain) return false;
        var domain = text[(at + 1)..];
        if (Uri.CheckHostName(domain) != UriHostNameType.Dns) return false;
        if (!MailboxAddress.TryParse(Strict, text, out var parsed) || parsed is null) return false;
        if (!string.Equals(parsed.Address, text, StringComparison.Ordinal) || !string.IsNullOrEmpty(parsed.Name) || parsed.Route.Count > 0) return false;
        address = parsed;
        return true;
    }

    public static bool IsValid(string? text) => TryParse(text, out _);
}
