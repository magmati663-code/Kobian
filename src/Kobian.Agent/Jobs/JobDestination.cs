using System.Globalization;
using System.Text.Json;
using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Agent.Jobs;

/// <summary>Część celu poza schematem i korzeniem: odwołanie do poświadczenia (SMB/SFTP) i parametry serwera SFTP. Bez sekretów (AC-01.3).</summary>
public sealed record RemoteDestination(string? SecretRef, string? Host = null, int? Port = null, string? HostKey = null);

/// <summary>
/// Reguły celu w definicji zadania v1 (BL-73 cz. 2, ADR-0005 rozszerzony — ADR-0026): jedno miejsce dla parsera definicji, inicjalizacji repozytorium,
/// walidacji szkicu kreatora i zapisu JSON. Kształt sekcji:
/// <c>"destination":{"scheme":"local","root":"C:\\Kopie"}</c>,
/// <c>{"scheme":"smb","root":"\\\\nas\\kopie\\biuro","secretRef":"file:nas"}</c>,
/// <c>{"scheme":"sftp","root":"/srv/kopie","host":"backup.example","port":22,"hostKey":"SHA256:…","secretRef":"file:sftp1"}</c>,
/// <c>{"scheme":"ftps","root":"/kopie","host":"nas.example","port":21,"hostKey":"SHA256:&lt;SPKI&gt;","secretRef":"file:ftp1"}</c> (BL-34; „hostKey” = pin SPKI,
/// opcjonalny — certyfikat z zaufanego urzędu go nie wymaga, D4.11). Jawny „ftp” nie jest przyjmowany w definicji zadania (D4.3: wymaga flagi i potwierdzenia —
/// brak pola w definicji v1, KB-2115; następny krok w notatkach BL-34),
/// <c>{"scheme":"webdav","root":"/remote.php/dav/files/kopie/kobian","host":"cloud.example","port":443,"hostKey":"SHA256:&lt;SPKI&gt;","secretRef":"file:dav1"}</c>
/// (HTTPS; „hostKey” = pin SPKI jak FTPS, opcjonalny przy certyfikacie z zaufanego urzędu).
/// Korzeń jest walidowany wg schematu (UNC nie jest „fully qualified” poza Windows, ścieżka SFTP jest uniksowa niezależnie od systemu Agenta).
/// W zadaniu (inaczej niż w ProbeDestination) cel zdalny MUSI mieć poświadczenie, a SFTP — przypięty klucz hosta (D4.11, bez TOFU).
/// </summary>
public static class JobDestination
{
    public const int MaxSecretRefLength = 70;
    public const int MaxHostKeyLength = 128;

    /// <summary>Kod KB, gdy cel nie nadaje się do zadania; null = poprawny. Nie sprawdza dostępności dostawcy (to wie rejestr dostawców).</summary>
    public static string? Validate(DestinationRequest d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (string.IsNullOrWhiteSpace(d.Root) || d.Root.Length > DestinationProbeService.MaxRootLength || d.Root.Any(char.IsControl)) return KbCode.DestinationNotFound;
        switch (d.Scheme)
        {
            case "local":
                if (!Path.IsPathFullyQualified(d.Root)) return KbCode.DestinationNotFound;
                return d.SecretRef is null && d.Host is null && d.Port is null && d.HostKey is null ? null : KbCode.JobDefinitionInvalid;
            case "smb":
                try
                {
                    _ = Kobian.Providers.Storage.Smb.UncPath.Parse(d.Root);
                }
                catch (StorageException ex)
                {
                    return ex.Code;
                }
                if (d.Host is not null || d.Port is not null || d.HostKey is not null) return KbCode.JobDefinitionInvalid;
                return ValidSecretRef(d.SecretRef) ? null : KbCode.AuthRejected;
            case "sftp":
                if (!d.Root.StartsWith('/') || d.Root.Contains('\\', StringComparison.Ordinal) || d.Root.Split('/').Any(s => s == "..")) return KbCode.DestinationNotFound;
                if (!ValidHost(d.Host)) return KbCode.DestinationNotFound;
                if (d.Port is { } p && p is < 1 or > 65535) return KbCode.DestinationNotFound;
                if (!ValidHostKey(d.HostKey)) return KbCode.HostKeyMismatch; // PROWIZORYCZNIE Qb15: zadanie bez przypiętego klucza = odmowa
                return ValidSecretRef(d.SecretRef) ? null : KbCode.AuthRejected;
            case "ftps":
                if (!d.Root.StartsWith('/') || d.Root.Contains('\\', StringComparison.Ordinal) || d.Root.Split('/').Any(s => s == "..")) return KbCode.DestinationNotFound;
                if (!ValidHost(d.Host)) return KbCode.DestinationNotFound;
                if (d.Port is { } fp && fp is < 1 or > 65535) return KbCode.DestinationNotFound;
                // D4.11: pin SPKI opcjonalny (ważny łańcuch CA wystarcza); jeśli podany — musi mieć poprawny kształt.
                if (!string.IsNullOrEmpty(d.HostKey) && !ValidHostKey(d.HostKey)) return KbCode.TlsIdentityMismatch;
                return ValidSecretRef(d.SecretRef) ? null : KbCode.AuthRejected;
            case "webdav":
                // HTTPS zawsze (jawny HTTP poza definicją zadania, jak „ftp”); korzeń = ścieżka URL na serwerze (np. /remote.php/dav/files/kopie/kobian).
                if (!d.Root.StartsWith('/') || d.Root.Contains('\\', StringComparison.Ordinal) || d.Root.Split('/').Any(s => s is ".." or ".")) return KbCode.DestinationNotFound;
                if (!ValidHost(d.Host)) return KbCode.DestinationNotFound;
                if (d.Port is { } wp && wp is < 1 or > 65535) return KbCode.DestinationNotFound;
                if (!string.IsNullOrEmpty(d.HostKey) && !ValidHostKey(d.HostKey)) return KbCode.TlsIdentityMismatch;
                return ValidSecretRef(d.SecretRef) ? null : KbCode.AuthRejected;
            case "ftp":
                return KbCode.InsecureTransportNotConfirmed;
            default:
                return KbCode.JobDefinitionInvalid;
        }
    }

    /// <summary>Opcje dostawcy (SFTP: host/port/hostkey; nigdy „identity_probe” — to wyłącznie tryb ProbeDestination; FTPS: host/port/spki/tls).</summary>
    public static IReadOnlyDictionary<string, string>? Options(DestinationRequest d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (d.Scheme == "ftps") return FtpsOptions(d);
        if (d.Scheme == "webdav") return WebDavOptions(d);
        if (d.Scheme != "sftp") return null;
        var o = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = d.Host ?? "", ["hostkey"] = d.HostKey ?? "" };
        if (d.Port is { } p) o["port"] = p.ToString(CultureInfo.InvariantCulture);
        return o;
    }

    /// <summary>
    /// Opcje FTPS (wspólne dla zadania i ProbeDestination). PROWIZORYCZNIE Qb68: port 990 = Implicit FTPS, każdy inny = Explicit (AUTH TLS) —
    /// definicja v1 nie ma pola trybu TLS. Alternatywa: pole „tls” w definicji (zmiana schematu ADR-0026). Koszt zmiany: mały (parser + zapis + UI).
    /// </summary>
    internal static Dictionary<string, string> FtpsOptions(DestinationRequest d)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = d.Host ?? "" };
        if (d.Port is { } p)
        {
            o["port"] = p.ToString(CultureInfo.InvariantCulture);
            if (p == 990) o["tls"] = "implicit";
        }
        if (!string.IsNullOrEmpty(d.HostKey)) o["spki"] = d.HostKey;
        return o;
    }

    /// <summary>Opcje WebDAV (wspólne dla zadania i ProbeDestination): host, port (domyślnie 443), pin SPKI jak FTPS. Nigdy „insecure”.</summary>
    internal static Dictionary<string, string> WebDavOptions(DestinationRequest d)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = d.Host ?? "" };
        if (d.Port is { } p) o["port"] = p.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(d.HostKey)) o["spki"] = d.HostKey;
        return o;
    }

    /// <summary>Konfiguracja sesji dostawcy dla poprawnego (<see cref="Validate"/>) celu.</summary>
    public static DestinationConfig ToConfig(string id, DestinationRequest d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new DestinationConfig(id, d.Scheme, d.Root, d.SecretRef, Options(d));
    }

    /// <summary>
    /// Stabilny identyfikator celu (klucz zasobu i wiersza repo_repositories): skrót „schemat:korzeń” — dla local/smb bez zmian względem it. 47–92,
    /// dla SFTP dodatkowo host i port (ten sam katalog na dwóch serwerach to dwa cele).
    /// </summary>
    public static string Id(string scheme, string root, IReadOnlyDictionary<string, string>? options)
    {
        var key = scheme + ":" + root;
        if (scheme is "sftp" or "ftps" or "webdav" && options is not null)
            key = scheme + "://" + (options.TryGetValue("host", out var h) ? h : "") + ":" + (options.TryGetValue("port", out var p) ? p : DefaultPort(scheme)) + root;
        return "dest-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
    }

    internal static string DefaultPort(string scheme) => scheme switch { "sftp" => "22", "webdav" => "443", _ => "21" };

    /// <summary>Sekcja „destination” z definicji v1 → żądanie celu; niepoprawny kształt = KB-0103 (JobDefinitionInvalid).</summary>
    internal static DestinationRequest Parse(JsonElement dest)
    {
        foreach (var p in dest.EnumerateObject())
            if (p.Name is not ("scheme" or "root" or "secretRef" or "host" or "port" or "hostKey")) throw JobDefinition.Invalid("Unknown destination field.");
        var scheme = JobDefinition.Str(dest, "scheme");
        var root = JobDefinition.Str(dest, "root");
        string? Opt(string name) => dest.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : throw JobDefinition.Invalid($"Field '{name}' must be a string."))
            : null;
        int? port = null;
        if (dest.TryGetProperty("port", out var pv) && pv.ValueKind != JsonValueKind.Null)
            port = pv.ValueKind == JsonValueKind.Number && pv.TryGetInt32(out var n) ? n : throw JobDefinition.Invalid("Field 'port' must be an integer.");
        var d = new DestinationRequest(scheme, root, Opt("host"), port, Opt("secretRef"), Opt("hostKey"));
        if (Validate(d) is not null)
            throw JobDefinition.Invalid(scheme is "local" or "smb" or "sftp" or "ftps" or "webdav" ? "The destination is not valid for this scheme." : "Unsupported destination scheme.");
        return d;
    }

    /// <summary>Zapis sekcji „destination” (te same nazwy pól co <see cref="Parse"/>; pola puste pomijane).</summary>
    internal static void Write(Utf8JsonWriter w, DestinationRequest d)
    {
        w.WriteStartObject("destination");
        w.WriteString("scheme", d.Scheme);
        w.WriteString("root", d.Root);
        if (d.Host is not null) w.WriteString("host", d.Host);
        if (d.Port is { } p) w.WriteNumber("port", p);
        if (d.HostKey is not null) w.WriteString("hostKey", d.HostKey);
        if (d.SecretRef is not null) w.WriteString("secretRef", d.SecretRef);
        w.WriteEndObject();
    }

    /// <summary>Format magazynu sekretów Agenta: <c>file:&lt;nazwa&gt;</c> (ten sam test nazwy co FileSftpCredentialProvider/FileSmbCredentialProvider).</summary>
    internal static bool ValidSecretRef(string? secretRef) =>
        secretRef is { Length: > 5 and <= MaxSecretRefLength } && secretRef.StartsWith("file:", StringComparison.Ordinal)
        && Kobian.Providers.Storage.Secrets.FileSftpCredentialProvider.IsSafeName(secretRef[5..]);

    private static bool ValidHost(string? host) =>
        host is { Length: > 0 and <= DestinationProbeService.MaxHostLength } && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '_');

    private static bool ValidHostKey(string? pin) =>
        pin is { Length: > 7 and <= MaxHostKeyLength } && pin.StartsWith("SHA256:", StringComparison.Ordinal)
        && pin.AsSpan(7).IndexOfAnyExcept(Base64Chars) < 0;

    private static readonly System.Buffers.SearchValues<char> Base64Chars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=");
}
