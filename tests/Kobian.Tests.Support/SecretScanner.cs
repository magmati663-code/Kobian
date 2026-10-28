using System.Text.RegularExpressions;

namespace Kobian.Tests.Support;

public sealed record SecretFinding(string File, int Line, string Rule);

/// <summary>
/// Skaner sekretów (NFR-11, BL-7): wzorce kluczy prywatnych, tokenów chmurowych, haseł w konfiguracji oraz
/// znane wartości testowe przekazane przez test (sekret losowy → musi nie wystąpić w artefaktach).
/// Wynik nie zawiera dopasowanej wartości (tylko plik, linia, reguła).
/// </summary>
public static partial class SecretScanner
{
    private static readonly (string Rule, Regex Pattern)[] Patterns =
    [
        ("private-key", new Regex(@"-----BEGIN (RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----")),
        ("aws-access-key", new Regex(@"\bAKIA[0-9A-Z]{16}\b")),
        ("github-token", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b")),
        ("azure-sas", new Regex(@"[?&]sig=[A-Za-z0-9%/+]{40,}")),
        ("slack-token", new Regex(@"\bxox[abpors]-[A-Za-z0-9-]{10,}")),
        ("password-assignment", new Regex(@"(?i)\b(password|passwd|pwd|haslo|hasło)\b\s*[:=]\s*[""'][^""'\s]{6,}[""']")),
        ("connection-string-password", new Regex(@"(?i)\b(password|pwd)\s*=\s*[^;""'\s]{6,};")),
    ];

    public static IReadOnlyList<SecretFinding> Scan(IEnumerable<string> files, IReadOnlyCollection<string>? knownSecrets = null, Func<string, int, string, bool>? allow = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var findings = new List<SecretFinding>();
        foreach (var f in files)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(f);
            }
            catch (IOException)
            {
                continue;
            }
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (rule, p) in Patterns)
                    if (p.IsMatch(lines[i]) && !(allow?.Invoke(f, i + 1, rule) ?? false))
                        findings.Add(new SecretFinding(f, i + 1, rule));
                if (knownSecrets is not null)
                    foreach (var s in knownSecrets)
                        if (s.Length >= 8 && lines[i].Contains(s, StringComparison.Ordinal))
                            findings.Add(new SecretFinding(f, i + 1, "known-test-secret"));
            }
        }
        return findings;
    }
}
