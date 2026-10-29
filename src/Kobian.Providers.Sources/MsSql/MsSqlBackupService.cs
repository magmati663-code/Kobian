using System.Globalization;
using Kobian.Domain;
using Kobian.Providers.Sources.Erp;

namespace Kobian.Providers.Sources.MsSql;

/// <summary>Wariant weryfikacji kopii bazy (D5.7, K15.1).</summary>
public enum MsSqlVerifyMode
{
    /// <summary>Wariant minimalny uprawnień: <c>BACKUP … WITH CHECKSUM</c> bez błędu + skrót strumienia (V1 SQL: CHECKSUM).</summary>
    Checksum = 1,
    /// <summary>Wariant „z weryfikacją”: dodatkowo <c>RESTORE VERIFYONLY … WITH CHECKSUM</c>.</summary>
    VerifyOnly = 2,
}

/// <summary>Parametry przygotowania kopii bazy MSSQL (R2.5.1). Bez poświadczeń — połączenie jest już w <see cref="IMsSqlSession"/>.</summary>
/// <param name="Database">Nazwa bazy (sysname) z discovery.</param>
/// <param name="StagingRoot">Katalog stagingu widziany przez Agenta (lokalny NTFS — D5.10).</param>
/// <param name="RunId">Identyfikator uruchomienia (wzorzec <see cref="Staging.IsValidRunId"/>).</param>
/// <param name="SqlStagingRoot">Ten sam katalog widziany przez SQL Server (absolutna ścieżka Windows albo Linux); <see langword="null"/> = <paramref name="StagingRoot"/>.</param>
public sealed record MsSqlBackupRequest(
    string Database,
    string StagingRoot,
    string RunId,
    string? SqlStagingRoot = null,
    BackupImpactProfile Profile = BackupImpactProfile.Default,
    string? ErpProfile = null)
{
    /// <summary>AC-02.2: maksymalny czas oczekiwania na zakończenie kolidującej operacji (domyślnie 30 min, konfigurowalne).</summary>
    public TimeSpan ConflictWait { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Odstęp ponownego sprawdzenia P-01.</summary>
    public TimeSpan ConflictPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>SID konta usługi SQL (D5.10) dla ACL katalogu uruchomienia; <see langword="null"/> = bez ACL (poza Windows).</summary>
    public string? SqlServiceSid { get; init; }

    /// <summary>D5.9/D5.11: gdy <see cref="ErpProfile"/> nie jest podany — wykryj profil ERP po schemacie bazy i wykonaj jego zapytania kontrolne (PROWIZORYCZNIE Qb103: włącza Agent).</summary>
    public bool DetectErp { get; init; }
}

/// <summary>Ostrzeżenie przygotowania (kod KB-3xxx klasy „ostrzeżenie”) — kopia jest wykonywana dalej.</summary>
public sealed record MsSqlWarning(string Code);

/// <summary>
/// Plik .bak w stagingu gotowy do przesłania strumieniem przez potok (StagedFileSource) + <c>dbMeta</c> (klucz 14).
/// <see cref="DisposeAsync"/> usuwa plik i katalog uruchomienia z weryfikacją braku (SEC-5.2, K16.1, AC-02.1).
/// </summary>
public sealed class MsSqlStagedBackup : IAsyncDisposable
{
    private int _disposed;

    internal MsSqlStagedBackup(string stagedFile, MsSqlBackupInfo info, MsSqlVerifyMode verify, IReadOnlyList<MsSqlWarning> warnings, string? controls = null)
    {
        StagedFile = stagedFile;
        Info = info;
        Verify = verify;
        Warnings = warnings;
        var meta = new Dictionary<string, string>(DbSnapshotMeta.Build(info), StringComparer.Ordinal)
        {
            ["verify"] = verify == MsSqlVerifyMode.VerifyOnly ? "verifyonly" : "checksum",
        };
        if (controls is not null) meta[ControlSet.MetaKey] = controls; // D5.11: zapytania kontrolne przed/po BACKUP (liczby i daty)
        DbMeta = meta;
    }

    /// <summary>Ścieżka lokalna pliku .bak (nie trafia do snapshotu ani logów — nazwa w snapshotcie to <see cref="DbSnapshotMeta.EntryName"/>).</summary>
    public string StagedFile { get; }

    public MsSqlBackupInfo Info { get; }

    public MsSqlVerifyMode Verify { get; }

    public IReadOnlyList<MsSqlWarning> Warnings { get; }

    /// <summary>Klucz 14 snapshotu.</summary>
    public IReadOnlyDictionary<string, string> DbMeta { get; }

    /// <summary>Po <see cref="DisposeAsync"/>: czy pliku na pewno nie ma (AC-02.1 „brak *.bak w stagingu”).</summary>
    public bool Removed { get; private set; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Removed = MsSqlBackupService.RemoveStaged(StagedFile);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// BL-40 cz. 1 — przygotowanie kopii bazy MSSQL wg R2.5.1: D-01 wersja (D5.8) → D-02 baza ONLINE + rozmiar → P-02 miejsce
/// (1,1 × dane) → D-03 uprawnienia (AC-02.3) → D-04 TDE (KB-3301) → P-01 kolizje z oczekiwaniem (AC-02.2) → P-03b baza różnicowa
/// → katalog uruchomienia (+ ACL) → <c>BACKUP … COPY_ONLY, CHECKSUM</c> → zestaw w msdb po urządzeniu (COPY_ONLY ∧ CHECKSUM)
/// → opcjonalnie VERIFYONLY (D5.7) → kontrola, że baza różnicowa się nie zmieniła (AC-02.1). Każda porażka sprząta staging.
/// Wszystkie polecenia jako szablony z parametrami (SEC-5.1); wyjątki i ostrzeżenia bez nazw baz i ścieżek (NFR-11).
/// </summary>
public sealed class MsSqlBackupService
{
    /// <summary>P-02: wymagane wolne miejsce = 1,1 × rozmiar plików danych (D-02).</summary>
    public const double StagingSpaceFactor = 1.1;

    private readonly IMsSqlSession _sql;
    private readonly IStagingVolume _volume;
    private readonly IStagingAcl? _acl;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<ErpProfile>? _erpProfiles;

    private static readonly Lazy<IReadOnlyList<ErpProfile>> BuiltInProfiles = new(ErpProfileCatalog.LoadBuiltIn);

    public MsSqlBackupService(IMsSqlSession sql, IStagingVolume? volume = null, IStagingAcl? acl = null, TimeProvider? time = null, IReadOnlyList<ErpProfile>? erpProfiles = null)
    {
        _erpProfiles = erpProfiles;
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _volume = volume ?? new DriveInfoStagingVolume();
        _acl = acl;
        _time = time ?? TimeProvider.System;
    }

    public async Task<MsSqlStagedBackup> PrepareAsync(MsSqlBackupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        TSql.RequireSysname(request.Database, nameof(request));
        if (!Staging.IsValidRunId(request.RunId)) throw new ArgumentException("Invalid run id.", nameof(request));
        var sqlRoot = request.SqlStagingRoot ?? request.StagingRoot;
        if (!SqlServerPath.IsAbsolute(sqlRoot)) throw new ArgumentException("SQL staging root must be an absolute path on the SQL Server host.", nameof(request));
        var db = new Dictionary<string, object>(StringComparer.Ordinal) { ["@db"] = request.Database };
        var warnings = new List<MsSqlWarning>();

        // D-01: wersja serwera (D5.8).
        var server = Single(await Query(MsSqlQueries.D01Server, [], cancellationToken).ConfigureAwait(false));
        switch (SqlVersionPolicy.Classify(ToInt(server, "major")))
        {
            case SqlSupport.Unsupported:
                throw new KobianException(KbCode.SqlVersionUnsupported, ErrorClass.Permanent, "SQL Server version is not supported (D5.8).");
            case SqlSupport.Conditional:
                warnings.Add(new MsSqlWarning(KbCode.SqlVersionUnsupported));
                break;
        }

        // D-02: baza użytkownika ONLINE (dokładna nazwa z discovery) + rozmiar danych.
        var dbRow = (await Query(MsSqlQueries.D02Databases, [], cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(r => string.Equals(Get(r, "name") as string, request.Database, StringComparison.Ordinal))
            ?? throw new KobianException(KbCode.SqlDatabaseUnavailable, ErrorClass.Permanent, "Database is missing or not ONLINE.");
        var dataBytes = ToLong(dbRow, "data_bytes");

        // P-02: miejsce na staging (nieznane = nie blokuje; ostateczną ochroną jest błąd BACKUP).
        var free = _volume.FreeBytes(request.StagingRoot);
        if (free is { } f && dataBytes > 0 && f < (long)Math.Ceiling(dataBytes * StagingSpaceFactor))
            throw new KobianException(KbCode.SqlStagingSpace, ErrorClass.Permanent, "Not enough free space for the staging file.");

        // D-03: uprawnienia (AC-02.3) — brak BACKUP DATABASE = trwały błąd z akcją „skrypt uprawnień”.
        var perms = Single(await Query(MsSqlQueries.D03Permissions, db, cancellationToken).ConfigureAwait(false));
        if (ToInt(perms, "can_backup_db") != 1)
            throw new KobianException(KbCode.SqlBackupPermissionMissing, ErrorClass.Permanent, "Login lacks BACKUP DATABASE permission.");
        var verify = ToInt(perms, "can_verifyonly") == 1 ? MsSqlVerifyMode.VerifyOnly : MsSqlVerifyMode.Checksum;

        // D-04: TDE — kopia odtwarzalna tylko z certyfikatem (KB-3301 ostrzeżenie; dołączanie certyfikatu — później).
        if (ToInt(dbRow, "is_encrypted") == 1) warnings.Add(new MsSqlWarning(KbCode.SqlTdeCertificateMissing));

        // P-01: kolizje (AC-02.2): czekaj maks. ConflictWait, potem pominięcie KB-3101.
        await WaitForConflictsAsync(request, db, cancellationToken).ConfigureAwait(false);

        // P-03b przed: baza różnicowa (AC-02.1 — COPY_ONLY nie może jej zmienić).
        var baseBefore = await DifferentialBaseAsync(db, cancellationToken).ConfigureAwait(false);

        // D5.9/D5.11: profil ERP i zapytania kontrolne PRZED BACKUP (READ COMMITTED, LOCK_TIMEOUT 5 s, 30 s/zapytanie; porażka = ostrzeżenie KB-3010).
        var erp = await ResolveErpAsync(request, cancellationToken).ConfigureAwait(false);
        var controlSkipped = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<ControlValue> controlsBefore = [];
        if (erp is { ControlQueries.Count: > 0 })
        {
            var run = await new ControlQueryRunner(_sql).RunAsync(request.Database, erp.ControlQueries, cancellationToken).ConfigureAwait(false);
            controlsBefore = run.Values;
            controlSkipped.UnionWith(run.SkippedQueryIds);
        }

        var fileId = Guid.NewGuid();
        var localFile = Staging.FilePath(request.StagingRoot, request.RunId, fileId);
        var sqlFile = SqlServerPath.Join(sqlRoot, request.RunId, fileId.ToString("N") + ".bak");
        var runDir = Path.GetDirectoryName(localFile)!;
        if (Directory.Exists(runDir)) throw new KobianException(KbCode.Internal, ErrorClass.Permanent, "Staging run directory already exists.");
        Directory.CreateDirectory(runDir);
        try
        {
            Staging.ShareWithSqlServer(request.StagingRoot, runDir, directory: true); // Linux/macOS: SQL Server zapisuje .bak na własnym koncie
            if (_acl is not null && request.SqlServiceSid is { Length: > 0 } sid) _acl.Apply(runDir, sid);

            await Execute(MsSqlBackupCommand.FullCopyOnly(request.Database, sqlFile, request.Profile), cancellationToken).ConfigureAwait(false);

            var set = (await Query(MsSqlQueries.BackupSetByDevice,
                    new Dictionary<string, object>(StringComparer.Ordinal) { ["@db"] = request.Database, ["@path"] = sqlFile }, cancellationToken)
                .ConfigureAwait(false)) is { Count: > 0 } sets ? sets[0]
                : throw new KobianException(KbCode.SqlBackupSetMismatch, ErrorClass.Integrity, "Backup set not found in msdb.");
            if (ToInt(set, "is_copy_only") != 1 || ToInt(set, "has_backup_checksums") != 1 || !string.Equals(Get(set, "type") as string, "D", StringComparison.Ordinal))
                throw new KobianException(KbCode.SqlBackupSetMismatch, ErrorClass.Integrity, "Backup set is not a full COPY_ONLY backup WITH CHECKSUM.");

            if (verify == MsSqlVerifyMode.VerifyOnly)
            {
                try
                {
                    await _sql.ExecuteAsync(MsSqlBackupCommand.VerifyOnly(sqlFile), cancellationToken).ConfigureAwait(false);
                }
                catch (MsSqlServerException e)
                {
                    var (code, cls) = SqlErrorMap.MapMany(e.ErrorNumbers);
                    // Każda porażka VERIFYONLY to błąd integralności kopii, chyba że rozpoznano utratę połączenia (ponowienie).
                    if (cls == ErrorClass.Transient) throw new KobianException(code, cls, "SQL Server connection lost during VERIFYONLY.", e);
                    throw new KobianException(KbCode.SqlVerifyFailed, ErrorClass.Integrity, "RESTORE VERIFYONLY failed.", e);
                }
            }

            var baseAfter = await DifferentialBaseAsync(db, cancellationToken).ConfigureAwait(false);
            if (baseAfter != baseBefore) warnings.Add(new MsSqlWarning(KbCode.SqlForeignFullBackup)); // obca pełna kopia w trakcie

            // D5.11: te same zapytania PO kopii — przyrost obserwowany w trakcie backupu wyznacza górną granicę tolerancji „range”.
            string? controlsMeta = null;
            if (erp is { } e2 && controlsBefore.Count > 0)
            {
                var answered = e2.ControlQueries.Where(q => controlsBefore.Any(v => v.QueryId == q.Id)).ToList();
                var runAfter = await new ControlQueryRunner(_sql).RunAsync(request.Database, answered, cancellationToken).ConfigureAwait(false);
                controlsMeta = ControlSet.Serialize(ControlSet.Merge(controlsBefore, runAfter.Values));
                if (controlsMeta is null) controlSkipped.Add("size"); // zbyt duży zestaw — nie zapisujemy (SnapshotCodec.MaxStringBytes)
            }
            if (controlSkipped.Count > 0) warnings.Add(new MsSqlWarning(KbCode.SqlControlQuerySkipped));

            var fi = new FileInfo(localFile);
            if (!fi.Exists || fi.Length == 0 || fi.LinkTarget is not null)
                throw new KobianException(KbCode.SqlBackupSetMismatch, ErrorClass.Integrity, "Staging file is missing after BACKUP.");

            var info = new MsSqlBackupInfo(
                ServerName: Get(set, "server_name") as string ?? Get(server, "instance") as string ?? "",
                DatabaseName: request.Database,
                Kind: MsSqlBackupKind.Full,
                CopyOnly: true,
                HasChecksum: true,
                BackupSetUuid: ToGuid(set, "backup_set_uuid") ?? throw new KobianException(KbCode.SqlBackupSetMismatch, ErrorClass.Integrity, "Backup set has no uuid."),
                DifferentialBaseGuid: baseAfter,
                FirstLsn: ToDecimal(set, "first_lsn"),
                LastLsn: ToDecimal(set, "last_lsn"),
                DatabaseBackupLsn: ToDecimal(set, "database_backup_lsn"),
                FinishedUtc: ToUtc(Get(set, "backup_finish_date")) ?? _time.GetUtcNow(),
                ErpProfile: erp?.Id ?? request.ErpProfile);
            return new MsSqlStagedBackup(localFile, info, verify, warnings, controlsMeta);
        }
        catch
        {
            RemoveStaged(localFile);
            throw;
        }
    }

    /// <summary>Profil ERP: jawny identyfikator z żądania albo (DetectErp) wykrycie po schemacie bazy; brak uprawnień do metadanych = brak profilu (bez błędu).</summary>
    private async Task<ErpProfile?> ResolveErpAsync(MsSqlBackupRequest request, CancellationToken ct)
    {
        var profiles = _erpProfiles ?? BuiltInProfiles.Value;
        if (request.ErpProfile is { Length: > 0 } id) return profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (!request.DetectErp) return null;
        var detector = new ErpDetector(profiles);
        var parameters = new Dictionary<string, object>(detector.BuildParameters(), StringComparer.Ordinal) { ["@db"] = request.Database };
        try
        {
            var rows = await _sql.QueryAsync(new SqlCommandSpec(detector.BuildDatabaseQuery(), parameters), ct).ConfigureAwait(false);
            var present = rows.Select(r => (Get(r, "schema_name") as string ?? "", Get(r, "table_name") as string ?? ""));
            return detector.Match(present).FirstOrDefault(m => m.Confirmed)?.Profile;
        }
        catch (MsSqlServerException)
        {
            return null;
        }
    }

    /// <summary>Usuwa plik i (pusty) katalog uruchomienia; zwraca <see langword="true"/>, gdy pliku nie ma (K16.1).</summary>
    internal static bool RemoveStaged(string localFile)
    {
        try
        {
            if (File.Exists(localFile)) return Staging.DeleteAndVerify(localFile);
            var dir = Path.GetDirectoryName(localFile)!;
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return !File.Exists(localFile); // RecoveryService posprząta przeterminowany katalog (Staging.CleanupStale)
        }
    }

    private async Task WaitForConflictsAsync(MsSqlBackupRequest request, Dictionary<string, object> db, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + request.ConflictWait;
        while (true)
        {
            var running = await Query(MsSqlQueries.P01ConflictingOperations, db, ct).ConfigureAwait(false);
            if (running.Count == 0) return;
            var now = _time.GetUtcNow();
            if (now >= deadline)
                throw new KobianException(KbCode.SqlConcurrentOperation, ErrorClass.Permanent, "Another BACKUP/RESTORE/DBCC is running on the database.");
            var wait = request.ConflictPollInterval < deadline - now ? request.ConflictPollInterval : deadline - now;
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    private async Task<Guid?> DifferentialBaseAsync(Dictionary<string, object> db, CancellationToken ct)
    {
        var rows = await Query(MsSqlQueries.P03DifferentialBase, db, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : ToGuid(rows[0], "differential_base_guid");
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Query(string text, Dictionary<string, object> parameters, CancellationToken ct)
    {
        try
        {
            return await _sql.QueryAsync(new SqlCommandSpec(text, parameters), ct).ConfigureAwait(false);
        }
        catch (MsSqlServerException e)
        {
            throw Map(e);
        }
    }

    private async Task Execute(SqlCommandSpec spec, CancellationToken ct)
    {
        try
        {
            await _sql.ExecuteAsync(spec, ct).ConfigureAwait(false);
        }
        catch (MsSqlServerException e)
        {
            throw Map(e);
        }
    }

    private static KobianException Map(MsSqlServerException e)
    {
        var (code, cls) = SqlErrorMap.MapMany(e.ErrorNumbers);
        return new KobianException(code, cls, "SQL Server reported an error (" + code + ").", e);
    }

    private static IReadOnlyDictionary<string, object?> Single(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        rows.Count > 0 ? rows[0] : throw new KobianException(KbCode.Internal, ErrorClass.Permanent, "SQL query returned no rows.");

    private static object? Get(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (row.TryGetValue(column, out var v)) return v;
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, column, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        }
        return null;
    }

    private static int ToInt(IReadOnlyDictionary<string, object?> row, string column) => (int)ToLong(row, column);

    private static long ToLong(IReadOnlyDictionary<string, object?> row, string column) => Get(row, column) switch
    {
        null => 0,
        bool b => b ? 1 : 0,
        IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
        _ => 0,
    };

    private static decimal ToDecimal(IReadOnlyDictionary<string, object?> row, string column) => Get(row, column) switch
    {
        null => 0m,
        IConvertible c => c.ToDecimal(CultureInfo.InvariantCulture),
        _ => 0m,
    };

    private static Guid? ToGuid(IReadOnlyDictionary<string, object?> row, string column) => Get(row, column) switch
    {
        Guid g => g,
        string s when Guid.TryParse(s, out var g) => g,
        _ => null,
    };

    private static DateTimeOffset? ToUtc(object? value) => value switch
    {
        DateTimeOffset d => d.ToUniversalTime(),
        // msdb: czas lokalny serwera bez strefy; MVP — Agent na tym samym hoście co SQL Server (D5.10), więc strefa Agenta.
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Local : d.Kind)).ToUniversalTime(),
        _ => null,
    };
}
