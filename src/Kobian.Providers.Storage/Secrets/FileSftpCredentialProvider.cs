using System.Text.Json;
using System.Text.RegularExpressions;
using Kobian.Application.Storage;
using Kobian.Providers.Storage.Sftp;

namespace Kobian.Providers.Storage.Secrets;

/// <summary>
/// Rozwiązuje <c>SecretRef</c> typu <c>file:&lt;nazwa&gt;</c> do poświadczeń SFTP z katalogu sekretów Agenta (AC-01.3, D4.1).
/// PROWIZORYCZNIE Qb16 (ADR-0007): plik JSON w katalogu sekretów (<c>KOBIAN_SECRETS_DIR</c>, domyślnie <c>&lt;stan&gt;/secrets</c>) z uprawnieniami
/// tylko dla właściciela (poza Windows wymuszane; na Windows ACL katalogu = BL-17), zamiast magazynu DPAPI (Windows, BL-24).
/// Koszt zmiany zdania: mały — to jedyna implementacja <see cref="ISftpCredentialProvider"/> w Agencie, zamiana = nowa klasa + rejestracja.
/// Rola Backup i Maintenance mają osobne wpisy; brak wpisu = odmowa (bez cichego przejścia na drugą rolę).
/// Komunikaty błędów nie zawierają wartości sekretów ani ścieżek plików.
/// </summary>
public sealed partial class FileSftpCredentialProvider(string secretsDir) : ISftpCredentialProvider
{
    private const string Prefix = "file:";
    private const long MaxFileBytes = 64 * 1024;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRule();

    /// <summary>Nazwy urządzeń Windows (CON, NUL, COM1…) — otwarcie „CON.json” nie czyta pliku (D8.9; przegląd it. 60).</summary>
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\\..*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WindowsDeviceName();

    /// <summary>Wspólna reguła nazwy wpisu i pliku klucza: znaki bezpieczne, bez „..”, bez kropki na końcu (Windows ją obcina), bez nazw urządzeń.</summary>
    public static bool IsSafeName(string name) =>
        NameRule().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal) && !name.EndsWith('.') && !WindowsDeviceName().IsMatch(name);

    public ValueTask<SftpCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var name = ParseName(secretRef);
        var file = Path.Combine(secretsDir, name + ".json");
        var json = ReadOwnerOnly(file, "credential entry");
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var key = role == CredentialRole.Maintenance ? "maintenance" : "backup";
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object)
                throw Refuse("No credential configured for this role.");
            var user = Str(entry, "user");
            var secretText = Str(entry, "password");
            var keyFileName = Str(entry, "privateKeyFile");
            var phrase = Str(entry, "passphrase");
            if (string.IsNullOrEmpty(user) || (string.IsNullOrEmpty(secretText) && string.IsNullOrEmpty(keyFileName)))
                throw Refuse("Credential entry is incomplete.");
            byte[]? keyBytes = null;
            if (!string.IsNullOrEmpty(keyFileName))
            {
                if (!IsSafeName(keyFileName))
                    throw Refuse("Invalid key file name.");
                keyBytes = System.Text.Encoding.UTF8.GetBytes(ReadOwnerOnly(Path.Combine(secretsDir, keyFileName), "key file"));
            }

            return ValueTask.FromResult(new SftpCredential(user, string.IsNullOrEmpty(secretText) ? null : secretText, keyBytes, string.IsNullOrEmpty(phrase) ? null : phrase));
        }
        catch (JsonException)
        {
            throw Refuse("Credential entry is malformed.");
        }
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static SftpProtocolException Refuse(string message) => new(SftpStatus.AuthRejected, message);

    private static string ParseName(string secretRef)
    {
        if (secretRef is null || !secretRef.StartsWith(Prefix, StringComparison.Ordinal))
            throw Refuse("Unsupported credential reference.");
        var name = secretRef[Prefix.Length..];
        if (!IsSafeName(name))
            throw Refuse("Invalid credential reference.");
        return name;
    }

    private string ReadOwnerOnly(string path, string what)
    {
        if (!Directory.Exists(secretsDir)) throw Refuse("Secrets directory is missing.");
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(secretsDir) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw Refuse("Secrets directory is writable by others.");
            if (File.Exists(path) && (File.GetUnixFileMode(path) & others) != 0) throw Refuse($"The {what} is accessible to others.");
        }

        var fi = new FileInfo(path);
        if (!fi.Exists) throw Refuse($"Missing {what}.");
        // Dowiązanie/punkt ponownej analizy odrzucane na KAŻDEJ platformie (D8.9) — wcześniej tylko poza Windows (przegląd it. 60).
        if (fi.LinkTarget is not null || (fi.Attributes & FileAttributes.ReparsePoint) != 0) throw Refuse($"The {what} must not be a link.");
        if (fi.Length > MaxFileBytes) throw Refuse($"The {what} is too large.");
        return File.ReadAllText(path);
    }
}
