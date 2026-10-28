using System.Text.RegularExpressions;
using Xunit;

namespace Kobian.Tests.Architecture;

/// <summary>
/// Lista zakazanych API (lista kontrolna roli 11, pkt 5): statyczny skan źródeł produktu (src/**/*.cs).
/// Wyjątek wymaga wpisu w <see cref="Allow"/> z uzasadnieniem i ID decyzji.
/// </summary>
public sealed partial class BannedApiTests
{
    private static readonly (string Name, Regex Pattern, string Why)[] Rules =
    [
        ("BinaryFormatter", new Regex(@"\bBinaryFormatter\b|\bSoapFormatter\b|\bNetDataContractSerializer\b|\bLosFormatter\b"), "deserializacja niebezpieczna"),
        ("ScpClient", new Regex(@"\bScpClient\b"), "D4.4 — CVE-2026-48798/85756"),
        ("CertValidationBypass", new Regex(@"ServerCertificate(Custom)?ValidationCallback\s*=\s*.*=>\s*true|RemoteCertificateValidationCallback\s*\(.*\)\s*=>\s*true|DangerousAcceptAnyServerCertificateValidator"), "wyłączanie walidacji certyfikatów"),
        ("ShellExecute", new Regex(@"UseShellExecute\s*=\s*true"), "Process.Start z powłoką"),
        ("CmdConcat", new Regex(@"Process\.Start\(\s*""(cmd|powershell|pwsh)(\.exe)?""", RegexOptions.IgnoreCase), "składanie poleceń powłoki"),
        ("WeakHash", new Regex(@"\b(MD5|SHA1)(\.Create|\.HashData|CryptoServiceProvider|Managed)\b|\bHMACMD5\b|\bHMACSHA1\b"), "MD5/SHA-1 do celów bezpieczeństwa"),
        ("WeakCipher", new Regex(@"\b(DES|TripleDES|RC2)\.Create\b|CipherMode\.ECB|EncryptEcb\("), "słabe szyfry / ECB"),
        ("RandomInCrypto", new Regex(@"new Random\(|Random\.Shared"), "Random zamiast RandomNumberGenerator (dozwolone tylko poza krypto — patrz Allow)"),
        ("SqlConcat", new Regex(@"(SELECT|INSERT|UPDATE|DELETE|EXEC)[^""]*""\s*\+\s*[a-zA-Z_]", RegexOptions.IgnoreCase), "składanie SQL z danych (SEC-5.1)"),
        ("TrustServerCertificate", new Regex(@"TrustServerCertificate\s*=\s*true|Trust\s*Server\s*Certificate\s*=\s*true", RegexOptions.IgnoreCase), "SEC-5.6 — tylko localhost przez MsSqlConnectionFactory"),
        ("Unsafe", new Regex(@"\bunsafe\s*\{|\bAllowUnsafeBlocks\b"), "kod unsafe bez ADR"),
    ];

    // plik (względny, '/') → reguły dozwolone z uzasadnieniem
    private static readonly Dictionary<string, string[]> Allow = new(StringComparer.Ordinal)
    {
        // Jitter misfire (D7.8) — bez znaczenia dla bezpieczeństwa; plik poza Kobian.Crypto.
        ["src/Kobian.Scheduler/Timing/MisfirePlanner.cs"] = ["RandomInCrypto"],
        // Jitter dławienia otwarć sesji (D4.16 pkt 3, it. 97) — rozproszenie prób w czasie, bez znaczenia dla bezpieczeństwa.
        ["src/Kobian.Providers.Storage/Throttling/SessionOpenGovernor.cs"] = ["RandomInCrypto"],
    };

    [Fact]
    public void Product_sources_do_not_use_banned_apis()
    {
        var root = RepoRoot.Find();
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal) || line.TrimStart().StartsWith("///", StringComparison.Ordinal)) continue;
                foreach (var (name, pattern, why) in Rules)
                {
                    if (!pattern.IsMatch(line)) continue;
                    if (Allow.TryGetValue(rel, out var allowed) && allowed.Contains(name)) continue;
                    violations.Add($"{rel}:{i + 1} [{name}] {why}");
                }
            }
        }
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("var f = new BinaryFormatter();", "BinaryFormatter")]
    [InlineData("handler.ServerCertificateCustomValidationCallback = (a, b, c, d) => true;", "CertValidationBypass")]
    [InlineData("smtp.ServerCertificateValidationCallback = (s, c, h, e) => true;", "CertValidationBypass")] // MailKit (BL-45)
    [InlineData("psi.UseShellExecute = true;", "ShellExecute")]
    [InlineData("var h = MD5.HashData(x);", "WeakHash")]
    [InlineData("var r = new Random(42);", "RandomInCrypto")]
    [InlineData("cmd.CommandText = \"SELECT * FROM t WHERE n = '\" + name;", "SqlConcat")]
    [InlineData("aes.EncryptEcb(a, b, PaddingMode.None);", "WeakCipher")]
    [InlineData("b.TrustServerCertificate = true;", "TrustServerCertificate")]
    [InlineData("\"Server=x;Trust Server Certificate=True\"", "TrustServerCertificate")]
    public void Rules_detect_known_bad_snippets(string snippet, string rule) =>
        Assert.Contains(Rules, r => r.Name == rule && r.Pattern.IsMatch(snippet));
}
