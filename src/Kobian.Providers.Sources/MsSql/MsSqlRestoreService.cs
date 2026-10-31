using System.Globalization;
using Kobian.Domain;
using Kobian.Providers.Sources.Erp;

namespace Kobian.Providers.Sources.MsSql;

/// <summary>Żądanie odtworzenia bazy z pliku .bak już obecnego na hoście SQL (staging): nazwa docelowa (null = „&lt;źródło&gt;_restore_RRRRMMDD”), katalogi plików (null = domyślne serwera).</summary>
public sealed record MsSqlRestoreRequest(string BackupPath, string SourceDatabase, string? TargetDatabase = null, string? DataDirectory = null, string? LogDirectory = null)
{
    /// <summary>D5.11: profil ERP i zapytania kontrolne zapisane przy kopii (<c>dbMeta.controls</c>) — po odtworzeniu te same zapytania na nowej bazie (V4).</summary>
    public ErpProfile? Profile { get; init; }

    public IReadOnlyList<ControlRecord>? Controls { get; init; }
}

/// <summary>Plan MOVE: plik logiczny → nowa ścieżka fizyczna na serwerze.</summary>
public sealed record MsSqlRestoreMove(string LogicalName, char Type, string PhysicalPath);

/// <param name="ControlCheck">Wynik kontroli D5.11 (<see langword="null"/> = brak zapisanych zapytań kontrolnych w kopii).</param>
public sealed record MsSqlRestoreResult(string TargetDatabase, IReadOnlyList<MsSqlRestoreMove> Moves, ControlCheckResult? ControlCheck = null);

/// <summary>
/// Odtworzenie bazy MSSQL do NOWEJ bazy (SCR-08, 8.4, AC-32.1; it. 83) na porcie <see cref="IMsSqlSession"/>:
/// (1) nazwa docelowa sysname, domyślnie „&lt;źródło&gt;_restore_RRRRMMDD”; istniejąca baza → KB-3501 (bez REPLACE — nadpisanie produkcji
/// wymaga step-up Administratora, AC-32.2 — nieobsługiwane, PROWIZORYCZNIE Qb39); (2) CREATE ANY DATABASE → inaczej KB-3502;
/// (3) RESTORE VERIFYONLY … WITH CHECKSUM; (4) RESTORE FILELISTONLY → plan MOVE do katalogów danych/dziennika (FILESTREAM/full-text → KB-3503;
/// brak katalogów → KB-3504); (5) RESTORE DATABASE @db FROM DISK = @path WITH MOVE @l0 TO @p0 …, CHECKSUM, RECOVERY — nazwy wyłącznie jako
/// parametry (zmienne T-SQL), tekst polecenia zależy tylko od LICZBY plików; (6) TRUSTWORTHY OFF (8.4). Błędy serwera → kody przez <see cref="SqlErrorMap"/>.
/// </summary>
public sealed class MsSqlRestoreService(IMsSqlSession session, TimeProvider time)
{
    public const int MaxFiles = 256;

    public const string ExistsText = "SELECT CAST(CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END AS int) AS exists_flag;";
    public const string CanCreateText = "SELECT CAST(HAS_PERMS_BY_NAME(NULL, NULL, N'CREATE ANY DATABASE') AS int) AS can_create;";
    public const string DefaultPathsText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000)) AS data_path, CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000)) AS log_path;";
    public const string FileListText = "RESTORE FILELISTONLY FROM DISK = @path;";
    /// <summary>
    /// RESTORE zawsze ustawia TRUSTWORTHY = OFF, a ALTER DATABASE … SET TRUSTWORTHY wymaga roli sysadmin — wykonane bezwarunkowo kończyło odtwarzanie loginem
    /// z minimalnymi uprawnieniami (dbcreator) błędem 15247, mimo że baza już powstała (znalezione na SQL Server 2022). Teraz tylko sprawdzenie i — gdyby flaga
    /// była jednak włączona — próba wyłączenia (błąd uprawnień = KB-3502, baza nie zostaje uznana za bezpieczną po cichu).
    /// </summary>
    public const string TrustworthyOffText = """
        IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @name AND is_trustworthy_on = 1)
        BEGIN
            DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET TRUSTWORTHY OFF';
            EXEC sp_executesql @sql;
        END
        """;

    /// <summary>Brak uprawnień w trakcie odtwarzania: 262 (CREATE DATABASE), 229/300 (odmowa uprawnienia), 15247 (operacja wymaga wyższej roli).</summary>
    private static (string Code, ErrorClass Class) MapRestoreError(IReadOnlyCollection<int> numbers) =>
        numbers.Any(n => n is 262 or 229 or 300 or 15247)
            ? (KbCode.SqlRestorePermissionMissing, ErrorClass.Permanent)
            : SqlErrorMap.MapMany(numbers);

    public static string DefaultTargetName(string source, DateTimeOffset nowUtc) =>
        source + "_restore_" + nowUtc.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public async Task<MsSqlRestoreResult> RestoreAsync(MsSqlRestoreRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        TSql.RequireSysname(request.SourceDatabase, nameof(request.SourceDatabase));
        _ = MsSqlBackupCommand.VerifyOnly(request.BackupPath); // walidacja ścieżki (Windows albo Linux, bez NUL, ≤ 4000)
        foreach (var dir in new[] { request.DataDirectory, request.LogDirectory })
            if (dir is not null && (!SqlServerPath.IsAbsolute(dir) || dir.Contains('\0', StringComparison.Ordinal) || dir.Length > 3000))
                throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Invalid target directory.");

        try
        {
            var target = await ResolveTargetAsync(request.SourceDatabase, request.TargetDatabase, ct).ConfigureAwait(false);

            await session.ExecuteAsync(MsSqlBackupCommand.VerifyOnly(request.BackupPath), ct).ConfigureAwait(false);

            var files = await session.QueryAsync(new SqlCommandSpec(FileListText, P(("@path", request.BackupPath))), ct).ConfigureAwait(false);
            if (files.Count is 0 or > MaxFiles) throw new KobianException(KbCode.SqlVerifyFailed, ErrorClass.Integrity, "Unexpected backup file list.");
            string? dataDir = request.DataDirectory, logDir = request.LogDirectory;
            if (dataDir is null || logDir is null)
            {
                var paths = await One(DefaultPathsText, null, ct).ConfigureAwait(false);
                dataDir ??= paths.GetValueOrDefault("data_path") as string;
                logDir ??= paths.GetValueOrDefault("log_path") as string;
            }
            if (string.IsNullOrWhiteSpace(dataDir) || string.IsNullOrWhiteSpace(logDir) || !SqlServerPath.IsAbsolute(dataDir) || !SqlServerPath.IsAbsolute(logDir))
                throw new KobianException(KbCode.SqlDefaultPathUnknown, ErrorClass.Permanent, "Default data/log path unknown.");

            var moves = PlanMoves(files, target, dataDir, logDir);
            await session.ExecuteAsync(RestoreCommand(target, request.BackupPath, moves), ct).ConfigureAwait(false);
            await session.ExecuteAsync(new SqlCommandSpec(TrustworthyOffText, P(("@name", target))), ct).ConfigureAwait(false);

            // D5.11 / D8.4 (V4): „kopia poprawna technicznie, logicznie pusta/zła” — te same zapytania co przy backupie, już na odtworzonej bazie.
            ControlCheckResult? check = null;
            if (request.Profile is { ControlQueries.Count: > 0 } profile && request.Controls is { Count: > 0 } records)
            {
                var run = await new ControlQueryRunner(session).RunAsync(target, profile.ControlQueries, ct).ConfigureAwait(false);
                check = ControlVerifier.Compare(profile, records, run.Values, run.SkippedQueryIds);
            }
            return new MsSqlRestoreResult(target, moves, check);
        }
        catch (MsSqlServerException e)
        {
            var (code, cls) = MapRestoreError(e.ErrorNumbers);
            throw new KobianException(code, cls, "SQL Server rejected the restore.");
        }
    }

    /// <summary>
    /// Kontrola WSTĘPNA przed pobraniem pliku .bak z repozytorium (CatalogRestoreService): nazwa docelowa poprawna i wolna, login ma CREATE ANY DATABASE.
    /// Kopia bazy to dziesiątki GB — błąd nazwy czy uprawnień ma wyjść od razu, a nie po godzinie pobierania. Zwraca ostateczną nazwę docelową;
    /// <see cref="RestoreAsync"/> sprawdza to samo ponownie tuż przed RESTORE (baza mogła powstać w międzyczasie).
    /// </summary>
    public async Task<string> PreflightAsync(string sourceDatabase, string? targetDatabase, CancellationToken ct)
    {
        TSql.RequireSysname(sourceDatabase, nameof(sourceDatabase));
        try
        {
            return await ResolveTargetAsync(sourceDatabase, targetDatabase, ct).ConfigureAwait(false);
        }
        catch (MsSqlServerException e)
        {
            var (code, cls) = MapRestoreError(e.ErrorNumbers);
            throw new KobianException(code, cls, "SQL Server rejected the restore preflight.");
        }
    }

    /// <summary>
    /// Nazwa docelowa: podana jawnie (istniejąca baza → KB-3501, nigdy REPLACE) albo domyślna „&lt;źródło&gt;_restore_RRRRMMDD”; gdy domyślna jest zajęta (drugie odtworzenie tego samego dnia),
    /// wybieramy pierwszą wolną z przyrostkiem „_2”…„_9” zamiast odmawiać. Potem CREATE ANY DATABASE (KB-3502).
    /// </summary>
    private async Task<string> ResolveTargetAsync(string sourceDatabase, string? requested, CancellationToken ct)
    {
        var baseName = requested ?? DefaultTargetName(sourceDatabase, time.GetUtcNow());
        var target = baseName;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                TSql.RequireSysname(target, nameof(requested));
            }
            catch (ArgumentException)
            {
                throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Invalid target database name.");
            }
            if (Int(await One(ExistsText, ("@name", target), ct).ConfigureAwait(false), "exists_flag") == 0) break;
            if (requested is not null || attempt >= MaxDefaultNameAttempts)
                throw new KobianException(KbCode.SqlRestoreTargetExists, ErrorClass.Permanent, "Target database exists.");
            target = baseName + "_" + (attempt + 1).ToString(CultureInfo.InvariantCulture);
        }
        if (Int(await One(CanCreateText, null, ct).ConfigureAwait(false), "can_create") != 1)
            throw new KobianException(KbCode.SqlRestorePermissionMissing, ErrorClass.Permanent, "CREATE ANY DATABASE is missing.");
        return target;
    }

    /// <summary>Ile nazw (domyślna + przyrostki _2…) próbujemy, zanim zgłosimy KB-3501.</summary>
    public const int MaxDefaultNameAttempts = 9;

    /// <summary>Nowe nazwy plików: &lt;baza&gt;.mdf, &lt;baza&gt;_N.ndf (kolejne pliki danych), &lt;baza&gt;_log.ldf / _log_N.ldf — wyłącznie z nazwy sysname (bez ścieżek z kopii).</summary>
    internal static IReadOnlyList<MsSqlRestoreMove> PlanMoves(IReadOnlyList<IReadOnlyDictionary<string, object?>> files, string target, string dataDir, string logDir)
    {
        var safe = string.Concat(target.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        var moves = new List<MsSqlRestoreMove>();
        int data = 0, log = 0;
        foreach (var f in files)
        {
            var logical = f.GetValueOrDefault("LogicalName") as string;
            var type = (f.GetValueOrDefault("Type") as string)?.Trim();
            if (string.IsNullOrEmpty(logical) || logical.Length > 128 || logical.Any(char.IsControl) || type is not ("D" or "L" or "S" or "F"))
                throw new KobianException(KbCode.SqlVerifyFailed, ErrorClass.Integrity, "Unexpected backup file entry.");
            if (type is "S" or "F") throw new KobianException(KbCode.SqlRestoreUnsupportedFile, ErrorClass.Permanent, "FILESTREAM/full-text file.");
            string path;
            if (type == "D")
            {
                path = Join(dataDir, data == 0 ? safe + ".mdf" : safe + "_" + data.ToString(CultureInfo.InvariantCulture) + ".ndf");
                data++;
            }
            else
            {
                path = Join(logDir, log == 0 ? safe + "_log.ldf" : safe + "_log_" + log.ToString(CultureInfo.InvariantCulture) + ".ldf");
                log++;
            }
            moves.Add(new MsSqlRestoreMove(logical, type[0], path));
        }
        if (data == 0 || log == 0) throw new KobianException(KbCode.SqlVerifyFailed, ErrorClass.Integrity, "Backup lacks data or log file.");
        return moves;
    }

    /// <summary>RESTORE z nazwami jako zmienne: tekst zależy tylko od liczby plików (SEC-5.1 — zero sklejania danych).</summary>
    internal static SqlCommandSpec RestoreCommand(string target, string backupPath, IReadOnlyList<MsSqlRestoreMove> moves)
    {
        var p = new Dictionary<string, object>(StringComparer.Ordinal) { ["@db"] = target, ["@path"] = backupPath };
        var parts = new List<string>();
        for (var i = 0; i < moves.Count; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            parts.Add($"MOVE @l{n} TO @p{n}");
            p["@l" + n] = moves[i].LogicalName;
            p["@p" + n] = moves[i].PhysicalPath;
        }
        return new SqlCommandSpec("RESTORE DATABASE @db FROM DISK = @path WITH " + string.Join(", ", parts) + ", CHECKSUM, RECOVERY, STATS = 5;", p);
    }

    private static string Join(string dir, string file) => SqlServerPath.Join(dir, file);

    private async Task<IReadOnlyDictionary<string, object?>> One(string text, (string, object)? param, CancellationToken ct)
    {
        var rows = await session.QueryAsync(new SqlCommandSpec(text, param is { } x ? P(x) : new Dictionary<string, object>(StringComparer.Ordinal)), ct).ConfigureAwait(false);
        return rows.Count == 1 ? rows[0] : throw new KobianException(KbCode.Internal, ErrorClass.Permanent, "Unexpected result.");
    }

    private static int Int(IReadOnlyDictionary<string, object?> row, string col) => row.GetValueOrDefault(col) switch
    {
        int i => i,
        long l => (int)l,
        _ => 0,
    };

    private static Dictionary<string, object> P(params (string Name, object Value)[] items) =>
        items.ToDictionary(i => i.Name, i => i.Value, StringComparer.Ordinal);
}
