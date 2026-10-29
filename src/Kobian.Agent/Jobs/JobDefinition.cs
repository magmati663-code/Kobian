using System.Text.Json;
using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Definicja zadania „files” (kolumna job_jobs.definition, JSON UTF-8 ≤ 64 KiB). Wersja 1:
/// <c>{"version":1,"sources":["/abs", {"scheme":"sftp","root":"/srv/dane","host":"…","hostKey":"SHA256:…","secretRef":"file:…"}],"destination":{"scheme":"local","root":"/abs"},
/// "key":{"file":"/abs","passwordFile":"/abs","repoHeader":"/abs"},"packTargetSize":4194304}</c> — źródła: foldery lokalne i serwery (<see cref="JobSource"/>).
/// PROWIZORYCZNIE Qb13 (ADR-0005): klucz wskazywany ścieżkami plików (KeyFileLoader) do czasu inicjalizacji repozytorium w Agencie (it. 48, BL-24);
/// jedno źródło; cel „local”, „smb” lub „sftp” wg <see cref="JobDestination"/> (BL-73 cz. 2, ADR-0026). Komunikaty błędów nie zawierają ścieżek (NFR-11).
/// </summary>
public sealed record JobDefinition(string Source, string DestinationScheme, string DestinationRoot, string KeyFile, string? PasswordFile, string? RepoHeader, long? PackTargetSize,
    Kobian.Application.Jobs.RetentionPolicy? Retention = null, MsSqlSourceDefinition? MsSql = null, RemoteDestination? Remote = null, VerifyPolicy? Verify = null,
    IReadOnlyList<JobSource>? SourceList = null)
{
    /// <summary>Wszystkie źródła zadania „files” (foldery lokalne i serwery); <see cref="Source"/> = pierwsze (etykieta, zgodność wstecz).</summary>
    public IReadOnlyList<JobSource> Sources => SourceList ?? (MsSql is null ? [JobSource.Local(Source)] : []);

    /// <summary>Polityka V3 z harmonogramu (3.7); brak sekcji „verify” = <see cref="VerifyPolicy.Default"/>.</summary>
    public VerifyPolicy EffectiveVerify => Verify ?? VerifyPolicy.Default;

    /// <summary>Cel jako żądanie (schemat, korzeń, poświadczenie, parametry SFTP) — wejście <see cref="JobDestination"/>.</summary>
    public DestinationRequest Destination => new(DestinationScheme, DestinationRoot, Remote?.Host, Remote?.Port, Remote?.SecretRef, Remote?.HostKey);

    /// <summary>
    /// Konfiguracja sesji dostawcy z kompletem danych celu (BL-73 cz. 2): zastępuje ręczne <c>new DestinationConfig(id, scheme, root)</c>,
    /// które gubiło <c>SecretRef</c> i opcje SFTP.
    /// </summary>
    public DestinationConfig ToDestination(string id) => JobDestination.ToConfig(id, Destination);

    /// <summary>Stabilny identyfikator celu (<see cref="JobDestination.Id"/>).</summary>
    public string DestinationId => JobDestination.Id(DestinationScheme, DestinationRoot, JobDestination.Options(Destination));

    /// <summary>Wartość <see cref="Source"/> dla zadań „database” z silnikiem MSSQL (źródłem jest baza, nie katalog; nazwa wyświetlana w snapshotcie bez danych klienta — NFR-11).</summary>
    public const string MsSqlSourceMarker = "mssql";

    /// <summary>Górna granica liczników retencji (ochrona przed absurdalnymi wartościami; 10 000 dni ≈ 27 lat).</summary>
    public const int MaxRetentionCount = 10_000;

    public const int MaxBytes = 64 * 1024;
    public const long MinPackTarget = 1L << 20;
    public const long MaxPackTarget = 512L << 20;

    public static JobDefinition Parse(string jobType, ReadOnlySpan<byte> json)
    {
        if (jobType is not ("files" or "database")) throw Invalid("Only the 'files' and 'database' job types are supported.");
        if (json.Length is 0 or > MaxBytes) throw Invalid("Definition size out of range.");
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1)
                throw Invalid("Unsupported definition version.");
            string source;
            MsSqlSourceDefinition? mssql = null;
            JobSourceList? sources = null;
            if (jobType == "database")
            {
                if (r.TryGetProperty("sources", out _)) throw Invalid("A 'database' job has a 'database' section instead of 'sources'.");
                mssql = MsSqlSourceDefinition.Parse(Obj(r, "database"));
                source = MsSqlSourceMarker;
            }
            else
            {
                // Wiele źródeł (foldery lokalne i serwery SFTP/FTPS/WebDAV) — każde jako osobny folder w kopii; jedno źródło lokalne = układ kopii jak dotąd.
                if (!r.TryGetProperty("sources", out var src) || src.ValueKind != JsonValueKind.Array || src.GetArrayLength() is 0 or > JobSource.MaxSources)
                    throw Invalid("Between 1 and 32 sources are supported.");
                sources = new JobSourceList(src.EnumerateArray().Select(JobSource.Parse));
                source = sources[0].Label;
            }
            var dest = JobDestination.Parse(Obj(r, "destination"));
            var key = Obj(r, "key");
            var keyFile = AbsolutePath(Prop(key, "file"), "key file");
            var pw = OptionalPath(key, "passwordFile");
            var header = OptionalPath(key, "repoHeader");
            long? target = null;
            if (r.TryGetProperty("packTargetSize", out var t))
            {
                if (t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out var n) || n is < MinPackTarget or > MaxPackTarget) throw Invalid("packTargetSize out of range.");
                target = n;
            }
            Kobian.Application.Jobs.RetentionPolicy? retention = null;
            if (r.TryGetProperty("retention", out var ret) && ret.ValueKind != JsonValueKind.Null) retention = ParseRetention(ret);
            VerifyPolicy? verify = null;
            if (r.TryGetProperty("verify", out var ver) && ver.ValueKind != JsonValueKind.Null) verify = VerifyPolicy.Parse(ver); // BL-26: V3 z harmonogramu (opcjonalne)
            var remote = dest.Scheme == "local" ? null : new RemoteDestination(dest.SecretRef, dest.Host, dest.Port, dest.HostKey);
            return new JobDefinition(source, dest.Scheme, dest.Root, keyFile, pw, header, target, retention, mssql, remote, verify, sources);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Job definition is not valid JSON of the expected shape.", ex);
        }
    }

    /// <summary>
    /// <c>"retention":{"keepLast":7,"keepDaily":14,"keepWeekly":4,"keepMonthly":12,"keepYearly":0,"keepWithinDays":0,"lockDays":14}</c> (D3.12, BL-25).
    /// Pola opcjonalne (0 = reguła wyłączona); błąd → KB-0401. Brak sekcji = brak retencji (wszystkie kopie zostają).
    /// </summary>
    public static Kobian.Application.Jobs.RetentionPolicy ParseRetention(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw RetentionInvalid();
        int N(string name)
        {
            if (!e.TryGetProperty(name, out var v)) return 0;
            return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n is >= 0 and <= MaxRetentionCount ? n : throw RetentionInvalid();
        }
        foreach (var p in e.EnumerateObject())
            if (p.Name is not ("keepLast" or "keepDaily" or "keepWeekly" or "keepMonthly" or "keepYearly" or "keepWithinDays" or "lockDays")) throw RetentionInvalid();
        var policy = new Kobian.Application.Jobs.RetentionPolicy(N("keepLast"), N("keepDaily"), N("keepWeekly"), N("keepMonthly"), N("keepYearly"),
            TimeSpan.FromDays(N("keepWithinDays")), TimeSpan.FromDays(N("lockDays")));
        if (Kobian.Application.Jobs.JobValidator.ValidateRetention(policy).Count > 0) throw RetentionInvalid();
        return policy;
    }

    /// <summary>Zapis polityki w kształcie definicji v1 (te same nazwy pól co <see cref="ParseRetention"/>).</summary>
    public static void WriteRetention(Utf8JsonWriter w, Kobian.Application.Jobs.RetentionPolicy p)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(p);
        w.WriteStartObject("retention");
        w.WriteNumber("keepLast", p.KeepLast);
        w.WriteNumber("keepDaily", p.KeepDaily);
        w.WriteNumber("keepWeekly", p.KeepWeekly);
        w.WriteNumber("keepMonthly", p.KeepMonthly);
        w.WriteNumber("keepYearly", p.KeepYearly);
        w.WriteNumber("keepWithinDays", (int)p.KeepWithin.TotalDays);
        w.WriteNumber("lockDays", (int)p.RetentionLock.TotalDays);
        w.WriteEndObject();
    }

    /// <summary>Kopia definicji JSON z zastąpioną (lub dodaną) sekcją „retention”; pozostałe pola bez zmian i w tej samej kolejności.</summary>
    public static byte[] WithRetention(ReadOnlySpan<byte> json, Kobian.Application.Jobs.RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (json.Length is 0 or > MaxBytes) throw Invalid("Definition size out of range.");
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("Definition must be an object.");
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name != "retention") p.WriteTo(w);
                WriteRetention(w, policy);
                w.WriteEndObject();
            }
            return ms.Length > MaxBytes ? throw Invalid("Definition size out of range.") : ms.ToArray();
        }
        catch (JsonException ex)
        {
            throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Job definition is not valid JSON.", ex);
        }
    }

    private static KobianException RetentionInvalid() => new(KbCode.RetentionInvalid, ErrorClass.Permanent, "Retention policy is invalid.");

    internal static KobianException Invalid(string message) => new(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, message);

    internal static JsonElement Prop(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) ? p : throw Invalid($"Missing field '{name}'.");

    internal static JsonElement Obj(JsonElement o, string name)
    {
        var p = Prop(o, name);
        return p.ValueKind == JsonValueKind.Object ? p : throw Invalid($"Field '{name}' must be an object.");
    }

    internal static string Str(JsonElement o, string name)
    {
        var p = Prop(o, name);
        return p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString()! : throw Invalid($"Field '{name}' must be a non-empty string.");
    }

    internal static string AbsolutePath(JsonElement e, string what)
    {
        var s = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        if (string.IsNullOrWhiteSpace(s) || s.Contains('\0', StringComparison.Ordinal) || !Path.IsPathFullyQualified(s)) throw Invalid($"The {what} must be an absolute path.");
        return s;
    }

    internal static string? OptionalPath(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? AbsolutePath(p, name) : null;
}
