using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Repo;

namespace Kobian.Agent.Jobs;

/// <summary>Żądanie inicjalizacji repozytorium i zadania „files”. <c>PasswordFile</c> wskazuje plik z hasłem opakowującym klucz główny (≤ 1 KiB, pierwsza linia).</summary>
public sealed record InitJobRequest(string JobName, string Source, string DestinationScheme, string DestinationRoot, string PasswordFile, long? PackTargetSize = null,
    RemoteDestination? Remote = null, IReadOnlyList<JobSource>? Sources = null)
{
    /// <summary>Wszystkie źródła (domyślnie jedno lokalne <see cref="Source"/>).</summary>
    public IReadOnlyList<JobSource> AllSources => Sources ?? [JobSource.Local(Source)];

    /// <summary>Cel jako żądanie (poświadczenie i parametry SFTP z <see cref="Remote"/>).</summary>
    public DestinationRequest Destination => new(DestinationScheme, DestinationRoot, Remote?.Host, Remote?.Port, Remote?.SecretRef, Remote?.HostKey);
}

/// <summary>Żądanie z kreatora: hasło w pamięci (nigdy w wyniku/logach), opcjonalny klucz odzyskiwania i harmonogram (strefa = strefa zadania).</summary>
public sealed record WizardInitRequest(string JobName, string Source, string DestinationScheme, string DestinationRoot, string Password, bool CreateRecoveryKey, string TimeZone, string? Cron,
    Kobian.Application.Jobs.RetentionPolicy? Retention = null, DatabaseInit? Database = null, RemoteDestination? Remote = null, IReadOnlyList<JobSource>? Sources = null)
{
    /// <summary>Wszystkie źródła (domyślnie jedno lokalne <see cref="Source"/>).</summary>
    public IReadOnlyList<JobSource> AllSources => Sources ?? [JobSource.Local(Source)];

    /// <summary>Cel jako żądanie (poświadczenie i parametry SFTP z <see cref="Remote"/>).</summary>
    public DestinationRequest Destination => new(DestinationScheme, DestinationRoot, Remote?.Host, Remote?.Port, Remote?.SecretRef, Remote?.HostKey);
}

/// <summary>Źródło zadania „database” z kreatora (po walidacji <see cref="JobDraftValidator.ResolveDatabase"/>): opcje połączenia bez sekretów + odwołanie do loginu + nazwa bazy.</summary>
public sealed record DatabaseInit(Kobian.Providers.Sources.MsSql.SqlClient.MsSqlConnectionOptions Connection, string? SecretRef, string Name)
{
    /// <summary>Bez nazw serwera i bazy (NFR-11).</summary>
    public override string ToString() => "DatabaseInit(" + Connection.Authentication + ")";
}

/// <summary>Wynik kreatora: <c>RecoveryKey</c> jest SEKRETEM do jednorazowego pokazania użytkownikowi (AC-28.1) — Agent go nie przechowuje.</summary>
public sealed record WizardInitResult(string JobId, string RepoIdHex, string? RecoveryKey);

/// <summary>Wynik: identyfikatory (bez materiału tajnego) i ścieżki lokalnych kopii nagłówka i klucza w stanie Agenta.</summary>
public sealed record InitJobResult(string JobId, string RepoIdHex, string KeyIdHex, string LocalKeyFile, string LocalRepoHeader);

/// <summary>
/// Inicjalizacja repozytorium w Agencie (BL-24, D3.11, ADR-0003 zastąpiony): <see cref="RepoProvisioner.Create"/> → <c>kobian.repo</c> i <c>keys/&lt;keyId&gt;.key</c> na celu
/// (<see cref="VerifiedUploader"/>, rola Backup, bez nadpisywania) → lokalne kopie w katalogu stanu Agenta (<c>keys/&lt;repoId&gt;/</c>) → wiersz job_jobs (definicja v1, ADR-0005) przez <see cref="CatalogWriter"/>.
/// Kolejność: repozytorium na celu najpierw, wpis w katalogu na końcu — przerwanie w środku nie zostawia zadania wskazującego na nieistniejące repozytorium.
/// Istniejące <c>kobian.repo</c> na celu = KB-1003 (nie wolno nadpisać cudzego repozytorium). Hasło nie trafia do wyniku ani do komunikatów błędów (NFR-11).
/// </summary>
public sealed class RepoInitService
{
    private readonly string _catalogPath;
    private readonly CatalogWriter _writer;
    private readonly IReadOnlyDictionary<string, IStorageProviderFactory> _providers;
    private readonly string _stateDir;
    private readonly TimeProvider _time;

    /// <summary>
    /// Katalog stagingu kopii baz widziany przez Agenta (sekcja „staging” definicji). PROWIZORYCZNIE Qb31: domyślnie <c>&lt;stan&gt;/staging</c>
    /// (Agent na hoście SQL — D5.10; na Windows %ProgramData%\Kobian\staging z ACL W-24). Koszt zmiany: mały (pole definicji).
    /// </summary>
    public string? DatabaseStagingRoot { get; init; }

    /// <summary>Ten sam katalog widziany przez SQL Server, gdy różni się od ścieżki Agenta (np. Agent poza Windows w testach); null = jak <see cref="DatabaseStagingRoot"/>.</summary>
    public string? DatabaseSqlStagingRoot { get; init; }

    public RepoInitService(string catalogPath, CatalogWriter writer, IReadOnlyDictionary<string, IStorageProviderFactory> providers, string stateDir, TimeProvider time)
    {
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _stateDir = Path.GetFullPath(stateDir ?? throw new ArgumentNullException(nameof(stateDir)));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<InitJobResult> InitAsync(InitJobRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        Check(req.JobName, req.Destination, req.PasswordFile);
        CheckSources(req.AllSources);
        var pwText = KeyFileLoader.ReadPasswordFile(req.PasswordFile);
        if (pwText.Length == 0) throw new KobianException(KbCode.KeyUnwrapFailed, ErrorClass.Security, "Password must not be empty.");
        var prov = RepoProvisioner.Create(pwText, _time.GetUtcNow());
        var (jobId, localKey, localHeader) = await ProvisionAsync(req.JobName, req.AllSources, req.Destination, prov, null,
            _ => req.PasswordFile, enabled: true, timeZone: "UTC", req.PackTargetSize, cron: null, retention: null, ct).ConfigureAwait(false);
        return new InitJobResult(jobId, prov.RepoIdHex, prov.KeyIdHex, localKey, localHeader);
    }

    /// <summary>
    /// Inicjalizacja z kreatora SCR-03 (BL-60/BL-61, Qb20/Qb24): hasło przychodzi w wiadomości po uwierzytelnionym pipe i jest zapisywane wyłącznie w katalogu
    /// stanu Agenta (<c>secrets/repo-&lt;repoId&gt;.pw</c>, tylko właściciel — 0600; na Windows ACL usługi/DPAPI: W-21), skąd czytają je kolejne uruchomienia.
    /// Opcjonalny klucz odzyskiwania (drugi klucz na liście nagłówka) wraca jednorazowo w wyniku. Zadanie powstaje WYŁĄCZONE — włącza je zapis kreatora (UpsertJob),
    /// więc porzucony kreator nie uruchamia kopii według harmonogramu.
    /// </summary>
    public async Task<WizardInitResult> InitFromWizardAsync(WizardInitRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        string? databaseSection = null;
        if (req.Database is { } db)
        {
            Check(req.JobName, req.Destination);
            databaseSection = DatabaseSection(db); // walidacja definicji PRZED utworzeniem repozytorium na celu
        }
        else
        {
            Check(req.JobName, req.Destination);
            CheckSources(req.AllSources);
        }
        if (string.IsNullOrEmpty(req.Password)) throw new KobianException(KbCode.PasswordWeak, ErrorClass.Permanent, "Password must not be empty.");
        ProvisionedRepo prov;
        ProvisionedRepoWithRecovery? withRecovery = null;
        if (req.CreateRecoveryKey)
        {
            withRecovery = RepoProvisioner.CreateWithRecovery(req.Password, _time.GetUtcNow());
            prov = withRecovery.Repo;
        }
        else
        {
            prov = RepoProvisioner.Create(req.Password, _time.GetUtcNow());
        }
        var (jobId, _, _) = await ProvisionAsync(req.JobName, req.AllSources, req.Destination, prov, withRecovery,
            p => WritePasswordFile(p.RepoIdHex, req.Password), enabled: false, req.TimeZone, null, req.Cron, req.Retention, ct, databaseSection).ConfigureAwait(false);
        return new WizardInitResult(jobId, prov.RepoIdHex, withRecovery?.RecoveryKey);
    }

    /// <summary>
    /// Nazwa, dostępność dostawcy, cel wg schematu (<see cref="JobDestination.Validate"/> — UNC i ścieżka SFTP nie są „fully qualified” w sensie lokalnym)
    /// i ścieżki lokalne (źródło, plik hasła) jako bezwzględne.
    /// </summary>
    /// <summary>Źródła: 1–32; lokalne — ścieżki bezwzględne; serwery — reguły jak dla celu i dostępny dostawca.</summary>
    private void CheckSources(IReadOnlyList<JobSource> sources)
    {
        if (sources.Count is 0 or > JobSource.MaxSources) throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Between 1 and 32 sources are supported.");
        foreach (var s in sources)
        {
            if (s.Path is { } p)
            {
                if (string.IsNullOrWhiteSpace(p) || p.Contains('\0', StringComparison.Ordinal) || !Path.IsPathFullyQualified(p))
                    throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Paths must be absolute.");
            }
            else
            {
                var bad = JobSource.ValidateRemote(s.Remote!) ?? (_providers.ContainsKey(s.Remote!.Scheme) ? null : KbCode.JobDefinitionInvalid);
                if (bad is not null) throw new KobianException(bad, ErrorClass.Permanent, "The source server is not valid.");
            }
        }
    }

    private void Check(string jobName, DestinationRequest destination, params string[] paths)
    {
        if (string.IsNullOrWhiteSpace(jobName) || jobName.Length > 128 || jobName.Any(char.IsControl))
            throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Job name is invalid.");
        if (!_providers.ContainsKey(destination.Scheme))
            throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Destination provider is not available.");
        if (JobDestination.Validate(destination) is { } bad)
            throw new KobianException(bad, ErrorClass.Permanent, "The destination is not valid for this scheme.");
        foreach (var p in paths)
            if (string.IsNullOrWhiteSpace(p) || p.Contains('\0', StringComparison.Ordinal) || !Path.IsPathFullyQualified(p))
                throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Paths must be absolute.");
        if (JobExists(jobName)) throw new KobianException(KbCode.JobNameTaken, ErrorClass.Permanent, "A job with this name already exists.");
    }

    /// <summary>Plik hasła w katalogu stanu Agenta, tworzony jako nowy (bez nadpisania), na Unix z prawami 0600 od chwili utworzenia.</summary>
    private string WritePasswordFile(string repoIdHex, string password)
    {
        var dir = Path.Combine(_stateDir, "secrets");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "repo-" + repoIdHex + ".pw");
        var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var bytes = new UTF8Encoding(false).GetBytes(password);
        try
        {
            using var fs = new FileStream(path, opts);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        return path;
    }

    private async Task<(string JobId, string LocalKey, string LocalHeader)> ProvisionAsync(string jobName, IReadOnlyList<JobSource> sources, DestinationRequest destination,
        ProvisionedRepo prov, ProvisionedRepoWithRecovery? recovery, Func<ProvisionedRepo, string> passwordFile, bool enabled, string timeZone, long? packTarget,
        string? cron, Kobian.Application.Jobs.RetentionPolicy? retention, CancellationToken ct, string? databaseSection = null)
    {
        var factory = _providers[destination.Scheme];
        await using (var session = await factory.OpenAsync(JobDestination.ToConfig("init-" + prov.RepoIdHex[..8], destination), CredentialRole.Backup, ct).ConfigureAwait(false))
        {
            if (await session.StatAsync("kobian.repo", ct).ConfigureAwait(false) is not null)
                throw new KobianException(KbCode.ObjectAlreadyExists, ErrorClass.Permanent, "The destination already contains a repository.");
            var up = new VerifiedUploader(session);
            // klucze przed nagłówkiem: nagłówek = „repozytorium gotowe”
            await up.UploadAsync(prov.KeyFileName, Encoding.UTF8.GetBytes(prov.KeyFileJson), ct).ConfigureAwait(false);
            if (recovery is not null) await up.UploadAsync(recovery.RecoveryKeyFileName, Encoding.UTF8.GetBytes(recovery.RecoveryKeyFileJson), ct).ConfigureAwait(false);
            await up.UploadAsync("kobian.repo", Encoding.UTF8.GetBytes(prov.HeaderJson), ct).ConfigureAwait(false);
        }

        var dir = Path.Combine(_stateDir, "keys", prov.RepoIdHex);
        Directory.CreateDirectory(dir);
        var localKey = Path.Combine(dir, prov.KeyIdHex + ".key");
        var localHeader = Path.Combine(dir, "kobian.repo");
        await File.WriteAllTextAsync(localKey, prov.KeyFileJson, new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(localHeader, prov.HeaderJson, new UTF8Encoding(false), ct).ConfigureAwait(false);

        var def = Definition(databaseSection is null ? sources : null, destination, localKey, passwordFile(prov), localHeader, packTarget, retention, databaseSection);
        var jobId = "job-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var now = Iso(_time.GetUtcNow());
        var defBytes = Encoding.UTF8.GetBytes(def);
        await _writer.ExecuteControlAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ($i,$n,$y,$d,$h,$e,$z,'skip',$t,$t);";
            cmd.Parameters.AddWithValue("$y", databaseSection is null ? "files" : "database");
            cmd.Parameters.AddWithValue("$i", jobId);
            cmd.Parameters.AddWithValue("$n", jobName);
            cmd.Parameters.AddWithValue("$d", defBytes);
            cmd.Parameters.AddWithValue("$h", SHA256.HashData(defBytes));
            cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$z", timeZone);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
            return 0;
        }, ct).ConfigureAwait(false);
        if (cron is not null)
            await CatalogSchedulerStore.UpsertCronTriggerAsync(_writer, ScheduleService.TriggerIdFor(jobId), jobId, cron, timeZone, ct).ConfigureAwait(false);
        return (jobId, localKey, localHeader);
    }

    /// <summary>
    /// Sekcja „database” (ADR-0018) z kreatora; parsowana tu tym samym kodem co przy uruchomieniu (<see cref="MsSqlSourceDefinition.Parse"/>),
    /// więc niepoprawny staging/serwer/pin kończy się KB-0103 zanim powstanie repozytorium.
    /// </summary>
    private string DatabaseSection(DatabaseInit db)
    {
        var staging = DatabaseStagingRoot ?? Path.Combine(_stateDir, "staging");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("engine", "mssql");
            w.WriteString("server", db.Connection.Server);
            w.WriteString("name", db.Name);
            var sql = db.Connection.Authentication == Kobian.Providers.Sources.MsSql.SqlClient.MsSqlAuthentication.SqlLogin;
            w.WriteString("auth", sql ? "sql" : "integrated");
            if (sql) w.WriteString("secretRef", db.SecretRef);
            if (db.Connection.ServerCertificatePath is { } cer) w.WriteString("certificate", cer);
            w.WriteBoolean("strict", db.Connection.Strict);
            w.WriteBoolean("allowLocalhostUntrusted", db.Connection.AllowLocalhostUntrusted);
            w.WriteString("staging", staging);
            if (DatabaseSqlStagingRoot is { } s) w.WriteString("sqlStaging", s);
            w.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(json);
        MsSqlSourceDefinition.Parse(doc.RootElement); // KB-0103 (JobDefinition.Invalid)
        return json;
    }

    private static string Definition(IReadOnlyList<JobSource>? sources, DestinationRequest destination, string keyFile, string passwordFile, string header, long? packTarget,
        Kobian.Application.Jobs.RetentionPolicy? retention, string? databaseSection = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            if (databaseSection is not null)
            {
                w.WritePropertyName("database");
                w.WriteRawValue(databaseSection);
            }
            else
            {
                w.WriteStartArray("sources");
                foreach (var s in sources ?? []) s.Write(w);
                w.WriteEndArray();
            }
            JobDestination.Write(w, destination);
            w.WriteStartObject("key");
            w.WriteString("file", keyFile);
            w.WriteString("passwordFile", passwordFile);
            w.WriteString("repoHeader", header);
            w.WriteEndObject();
            if (packTarget is { } t) w.WriteNumber("packTargetSize", t);
            if (retention is not null) JobDefinition.WriteRetention(w, retention);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private bool JobExists(string name)
    {
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM job_jobs WHERE name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
