using System.Globalization;
using System.Security.Cryptography;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Engine.Restore;
using Kobian.Providers.Sources.Erp;
using Kobian.Providers.Sources.MsSql;
using Kobian.Scheduler.Runs;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Restore w Agencie (BL-43, D8.1): snapshot z katalogu (snap_snapshots: „latest”, pełny id albo jednoznaczny prefiks ≥ 8 znaków hex) →
/// definicja zadania → klucz (KeyFileLoader) → dostawca celu z poświadczeniem Backup (bez usuwania, D4.6) → <see cref="RestoreExecutor"/> (V1: fileSha256, bez nadpisywania).
/// Każde uruchomienie = wiersz run_runs (cause 'manual', snapshot_id, files, bytes_read = odtworzone bajty) + run_transitions.
/// Dziennik wznowień restore_* (D8.8) zapisuje <see cref="CatalogRestoreJournal"/>: wznowienie pomija pliki zweryfikowane w poprzedniej próbie, a nic nie jest nadpisywane (KB-5105).
/// Cel musi być ścieżką bezwzględną istniejącego katalogu. Komunikaty błędów nie zawierają ścieżek (NFR-11).
/// </summary>
public sealed class CatalogRestoreService : IDisposable
{
    public const int MinPrefixLength = 8;

    private readonly string _catalogPath;
    private readonly CatalogWriter _writer;
    private readonly IReadOnlyDictionary<string, IStorageProviderFactory> _providers;
    private readonly TimeProvider _time;

    public CatalogRestoreService(string catalogPath, CatalogWriter writer, IReadOnlyDictionary<string, IStorageProviderFactory> providers, TimeProvider time)
    {
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _slots = new Lazy<SemaphoreSlim>(() => new SemaphoreSlim(Math.Max(1, MaxDetachedParallel)));
    }

    private sealed record SnapRow(string SnapId, string JobId, string JobType, byte[] Definition);

    public Task<JobOutcome> RestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct) =>
        RestoreCoreAsync(snapshot, pathFilter, null, target, ct);

    /// <summary>Odtworzenie dokładnie wybranych plików/folderów (SCR-07, it. 80, Qb34) — bez wzorców; ścieżki niepoprawne/nieistniejące → KB-5003.</summary>
    public Task<JobOutcome> RestoreExactAsync(string snapshot, IReadOnlyCollection<string> exactPaths, string target, CancellationToken ct) =>
        RestoreCoreAsync(snapshot, null, exactPaths ?? throw new ArgumentNullException(nameof(exactPaths)), target, ct);

    private async Task<JobOutcome> RestoreCoreAsync(string snapshot, string? pathFilter, IReadOnlyCollection<string>? exactPaths, string target, CancellationToken ct)
    {
        var (row, runId, failure) = await PrepareAsync(snapshot, target, ct).ConfigureAwait(false);
        return failure ?? await RunPreparedAsync(row!, runId, pathFilter, exactPaths, target, ct).ConfigureAwait(false);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _detached = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lazy<SemaphoreSlim> _slots;

    /// <summary>RD-82: ile odtworzeń w tle działa naraz (kolejne czekają). Odczytywane przy pierwszym starcie w tle.</summary>
    public int MaxDetachedParallel { get; init; } = 2;

    /// <summary>Anuluje odtworzenia w tle (zamykanie Agenta): trwające kończą się stanem Cancelled, oczekujące w kolejce także.</summary>
    public void CancelDetached() => _shutdown.Cancel();

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        if (_slots.IsValueCreated) _slots.Value.Dispose();
    }

    /// <summary>
    /// RD-81 (it. 137): start odtworzenia bez czekania na wynik — walidacja celu i wybór snapshotu synchronicznie (błąd = JobOutcome Failed, bez wiersza),
    /// potem wiersz run_runs (Running) i odtwarzanie w tle; wynik trafia do katalogu (SCR-07 śledzi go przez WatchRun po zwróconym run_id).
    /// </summary>
    public async Task<(string? RunId, JobOutcome? Failure)> StartRestoreAsync(string snapshot, string? pathFilter, IReadOnlyCollection<string>? exactPaths, string target, CancellationToken ct)
    {
        var (row, runId, failure) = await PrepareAsync(snapshot, target, ct).ConfigureAwait(false);
        if (failure is not null) return (null, failure);
        var task = Task.Run(async () =>
        {
            var slots = _slots.Value;
            try
            {
                // RD-82: nadmiarowe odtworzenia czekają w kolejce (wiersz Running), zamiast ścigać się o dysk i cel; zamknięcie Agenta je anuluje.
                await slots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await FinishRunAsync(runId, RunState.Cancelled, null, row!.SnapId, 0, 0, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            try { _ = await RunPreparedAsync(row!, runId, pathFilter, exactPaths, target, _shutdown.Token).ConfigureAwait(false); }
#pragma warning disable CA1031 // zadanie w tle: wyjątek jest już odnotowany w wierszu run_runs (Failed/Cancelled); nie może zabić procesu Agenta
            catch (Exception) { /* wynik w katalogu */ }
#pragma warning restore CA1031
            finally { slots.Release(); }
        }, CancellationToken.None);
        _detached[task] = 0;
        _ = task.ContinueWith(t => _detached.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return (Convert.ToHexStringLower(runId), null);
    }

    /// <summary>Czeka na zakończenie odtworzeń uruchomionych przez <see cref="StartRestoreAsync"/> (zamykanie hosta, testy).</summary>
    public Task WhenDetachedIdleAsync() => Task.WhenAll(_detached.Keys.ToArray());

    private async Task<(SnapRow? Row, byte[] RunId, JobOutcome? Failure)> PrepareAsync(string snapshot, string target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return (null, [], Failed(KbCode.RestoreTargetAccessDenied));
        var (row, found) = Resolve(snapshot ?? "");
        if (row is null) return (null, [], Failed(found));
        var runId = RandomNumberGenerator.GetBytes(16);
        await InsertRunAsync(runId, row.JobId, ct).ConfigureAwait(false);
        return (row, runId, null);
    }

    private async Task<JobOutcome> RunPreparedAsync(SnapRow row, byte[] runId, string? pathFilter, IReadOnlyCollection<string>? exactPaths, string target, CancellationToken ct)
    {
        JobOutcome outcome;
        long files = 0, bytes = 0;
        try
        {
            (outcome, files, bytes) = await ExecuteAsync(row, pathFilter, exactPaths, target, Convert.ToHexStringLower(runId), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishRunAsync(runId, RunState.Cancelled, null, row.SnapId, 0, 0, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (KobianException ex)
        {
            outcome = new JobOutcome(JobResult.Failed, ex.Code, ex.ErrorClass == ErrorClass.Transient, row.SnapId);
        }
        catch (StorageException ex)
        {
            outcome = new JobOutcome(JobResult.Failed, ex.Code, ex.IsTransient, row.SnapId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome = new JobOutcome(JobResult.Failed, KbCode.RestoreTargetAccessDenied, false, row.SnapId);
        }
#pragma warning disable CA1031 // granica wykonawcy: nieoczekiwany wyjątek = wiersz Failed (KB-0001) i dalej
        catch
        {
            await FinishRunAsync(runId, RunState.Failed, KbCode.Internal, row.SnapId, files, bytes, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
#pragma warning restore CA1031
        var state = outcome.Result switch { JobResult.Succeeded => RunState.Succeeded, JobResult.Warning => RunState.Warning, _ => RunState.Failed };
        await FinishRunAsync(runId, state, outcome.Code, row.SnapId, files, bytes, CancellationToken.None).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Wywołujący do dziennika wznowień (caller_sid). PROWIZORYCZNIE Qb56: stała do czasu przekazania SID z interceptora.</summary>
    public string JournalCaller { get; init; } = "agent";

    /// <summary>Pliki uznane za odtworzone z poprzedniej, przerwanej próby tego samego planu (ostatnie wywołanie; D8.8) — diagnostyka/testy.</summary>
    public long LastResumed { get; private set; }

    private async Task<(JobOutcome, long, long)> ExecuteAsync(SnapRow row, string? pathFilter, IReadOnlyCollection<string>? exactPaths, string target, string runHex, CancellationToken ct)
    {
        var def = JobDefinition.Parse(row.JobType, row.Definition);
        if (!_providers.TryGetValue(def.DestinationScheme, out var factory)) throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Destination provider is not available.");
        using var keys = KeyFileLoader.Load(def.KeyFile, def.PasswordFile, def.RepoHeader);
        var destId = DestinationId(def);
        await using var session = await factory.OpenAsync(def.ToDestination(destId), CredentialRole.Backup, ct).ConfigureAwait(false);
        var snap = await MetadataStore.LoadSnapshotAsync(session, keys, Convert.FromHexString(row.SnapId), ct).ConfigureAwait(false);
        using var reader = new RepoReader(session, keys);
        var indexRecovered = await reader.LoadIndexesWithFallbackAsync(snap, ct).ConfigureAwait(false); // it. 110: brak indeksu → indeksy z check --repair
        // D8.8 (it. 107): dziennik wznowień — ten sam plan (snapshot, cel, wybór) przejmuje niedokończone próby; zweryfikowane pliki nie są konfliktem.
        var normalizedExact = exactPaths is null ? null : ExactPathSelection.Normalize(exactPaths);
        var journal = await CatalogRestoreJournal.OpenAsync(_writer, runHex, row.SnapId, target, JournalCaller,
            CatalogRestoreJournal.PlanHash(row.SnapId, target, pathFilter, normalizedExact), ct).ConfigureAwait(false);
        // RD-60 (it. 108): postęp w wierszu run_runs (files, bytes_read) najwyżej co ProgressInterval — `kobian runs` i SCR-10 widzą odtwarzanie w toku.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var executor = new RestoreExecutor(reader)
        {
            Journal = journal,
            Progress = (files, bytes) =>
            {
                if (clock.Elapsed - lastReport < ProgressInterval) return;
                lastReport = clock.Elapsed;
                _ = WriteProgressAsync(runHex, files, bytes);
            },
        };
        RestoreResult? r = null;
        try
        {
            r = exactPaths is not null
                ? await executor.RestoreExactAsync(snap, target, exactPaths, ct).ConfigureAwait(false)
                : await executor.RestoreAsync(snap, target, string.IsNullOrEmpty(pathFilter) ? null : pathFilter, ct).ConfigureAwait(false);
        }
        finally
        {
            // Done tylko po zakończeniu bez porażki; przerwanie/anulowanie/błąd = sesja wznawialna (Failed)
            await journal.CompleteAsync(r is not null && r.Status != RestoreStatus.Failed, r?.Files ?? 0, r?.Bytes ?? 0, CancellationToken.None).ConfigureAwait(false);
        }
        LastResumed = r.Resumed;
        var result = r.Status switch { RestoreStatus.Succeeded => JobResult.Succeeded, RestoreStatus.Warning => JobResult.Warning, _ => JobResult.Failed };
        var code = WorstCode(r);
        if (indexRecovered && result == JobResult.Succeeded) (result, code) = (JobResult.Warning, KbCode.IndexRecoveredFromPacks); // KB-1505: odtworzono, ale bez indeksu kopii
        return (new JobOutcome(result, code, false, row.SnapId), r.Files, r.Bytes);
    }

    /// <summary>Odstęp zapisu postępu odtwarzania do katalogu (RD-60).</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(1);

    private async Task WriteProgressAsync(string runHex, long files, long bytes)
    {
        try
        {
            await _writer.ExecuteControlAsync((c, tx) =>
            {
                Exec(c, tx, "UPDATE run_runs SET files=$f, bytes_read=$b WHERE run_id=$i AND state='Running';", ("$f", files), ("$b", bytes), ("$i", runHex));
                return 0;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ObjectDisposedException)
        {
            // postęp jest informacyjny — błąd zapisu nie przerywa odtwarzania
        }
    }

    /// <summary>BL-68: fabryka sesji SQL (jak w CatalogJobService; Program.cs: AgentMsSql.CreateFactory). Null → odtwarzanie baz niedostępne (KB-7012).</summary>
    public Func<MsSqlSourceDefinition, CancellationToken, ValueTask<IMsSqlSession>>? MsSqlSessions { get; init; }

    /// <summary>Wolne miejsce w stagingu (kontrola przed pobraniem .bak: 1,1 × rozmiar → inaczej KB-3102, jak P-02 przy kopii).</summary>
    public IStagingVolume StagingVolume { get; init; } = new DriveInfoStagingVolume();

    /// <summary>RD-39: lista dozwolonych serwerów SQL także przy odtwarzaniu.</summary>
    public SqlServerAllowList AllowedSqlServers { get; init; } = SqlServerAllowList.Any;

    /// <summary>Wynik odtworzenia bazy: stan jak restore plików + nazwa nowej bazy.</summary>
    public sealed record DatabaseRestoreOutcome(JobOutcome Outcome, string? TargetDatabase);

    /// <summary>
    /// Odtworzenie bazy MSSQL z kopii trybu Database do NOWEJ bazy (BL-68 cz. 2, SCR-08, AC-32.1; PROWIZORYCZNIE Qb39):
    /// snapshot → definicja zadania „database” → lista dozwolonych serwerów → <c>database.bak</c> (RestoreExecutor, dokładna ścieżka, V1 fileSha256)
    /// do świeżego katalogu stagingu Agenta → <see cref="MsSqlRestoreService"/> (ścieżka widziana przez SQL Server: sqlStaging albo staging) →
    /// sprzątanie stagingu ZAWSZE. Wiersz run_runs (reason 'restore'). Nadpisanie istniejącej bazy — nigdy (KB-3501).
    /// </summary>
    public async Task<DatabaseRestoreOutcome> RestoreDatabaseAsync(string snapshot, string? targetDatabase, CancellationToken ct)
    {
        if (MsSqlSessions is null) return new(Failed(KbCode.OperationNotAvailable), null);
        var (row, found) = Resolve(snapshot ?? "");
        if (row is null) return new(Failed(found), null);
        var runId = RandomNumberGenerator.GetBytes(16);
        var runHex = Convert.ToHexStringLower(runId);
        await InsertRunAsync(runId, row.JobId, ct).ConfigureAwait(false);
        string? stagingDir = null;
        JobOutcome outcome;
        string? created = null;
        long bytes = 0;
        IReadOnlyDictionary<string, string>? dbMeta = null;
        ControlCheckResult? controlCheck = null;
        try
        {
            var def = JobDefinition.Parse(row.JobType, row.Definition);
            if (def.MsSql is not { } db) throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Snapshot is not a database backup.");
            AllowedSqlServers.Demand(db.Connection.Server);
            if (!_providers.TryGetValue(def.DestinationScheme, out var factory)) throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Destination provider is not available.");
            // Kontrola wstępna PRZED pobraniem .bak (dziesiątki GB): nazwa docelowa, istnienie bazy, CREATE ANY DATABASE. Osobna, krótka sesja SQL — pobranie może trwać długo.
            var preflightSql = await MsSqlSessions(db, ct).ConfigureAwait(false);
            try
            {
                await new MsSqlRestoreService(preflightSql, _time).PreflightAsync(db.Database, targetDatabase, ct).ConfigureAwait(false);
            }
            finally
            {
                if (preflightSql is IAsyncDisposable pd) await pd.DisposeAsync().ConfigureAwait(false);
            }
            stagingDir = Path.Combine(db.StagingRoot, "restore-" + runHex);
            Directory.CreateDirectory(stagingDir);
            using (var keys = KeyFileLoader.Load(def.KeyFile, def.PasswordFile, def.RepoHeader))
            {
                await using var session = await factory.OpenAsync(def.ToDestination(DestinationId(def)), CredentialRole.Backup, ct).ConfigureAwait(false);
                var snap = await MetadataStore.LoadSnapshotAsync(session, keys, Convert.FromHexString(row.SnapId), ct).ConfigureAwait(false);
                using var reader = new RepoReader(session, keys);
                await reader.LoadIndexesWithFallbackAsync(snap, ct).ConfigureAwait(false);
                var root = await new SnapshotBrowser(reader).ListAsync(snap, "", ct).ConfigureAwait(false);
                var bak = root.Entries.FirstOrDefault(e => e.Name == DbSnapshotMeta.EntryName && e.Kind == BrowseEntryKind.File)
                    ?? throw new KobianException(KbCode.SnapshotPathNotFound, ErrorClass.Permanent, "Database snapshot has no backup file.");
                if (StagingVolume.FreeBytes(db.StagingRoot) is { } free && free < bak.Size + bak.Size / 10)
                    throw new KobianException(KbCode.SqlStagingSpace, ErrorClass.Permanent, "Not enough staging space for the backup file."); // przed pobraniem — nic nie zapisane
                var r = await new RestoreExecutor(reader).RestoreExactAsync(snap, stagingDir, [DbSnapshotMeta.EntryName], ct).ConfigureAwait(false);
                if (r.Status != RestoreStatus.Succeeded) throw new KobianException(WorstCode(r) ?? KbCode.RestorePackMissing, ErrorClass.Integrity, "Backup file could not be restored to staging.");
                bytes = r.Bytes;
                dbMeta = snap.DbMeta;
            }
            var sqlRoot = db.SqlStagingRoot ?? db.StagingRoot;
            var sqlPath = sqlRoot.TrimEnd('\\') + "\\restore-" + runHex + "\\" + DbSnapshotMeta.EntryName;
            var sql = await MsSqlSessions(db, ct).ConfigureAwait(false);
            try
            {
                var (erp, controls) = ReadControls(dbMeta);
                var result = await new MsSqlRestoreService(sql, _time).RestoreAsync(
                    new MsSqlRestoreRequest(sqlPath, db.Database, targetDatabase) { Profile = erp, Controls = controls }, ct).ConfigureAwait(false);
                created = result.TargetDatabase;
                controlCheck = result.ControlCheck;
            }
            finally
            {
                if (sql is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
            }
            // D5.11: niezgodność z zapytaniami kontrolnymi → Warning KB-3505 (baza istnieje pod nową nazwą; decyzja co dalej należy do administratora).
            outcome = controlCheck is { Passed: false }
                ? new JobOutcome(JobResult.Warning, KbCode.SqlRestoreControlMismatch, false, row.SnapId)
                : new JobOutcome(JobResult.Succeeded, null, false, row.SnapId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishRunAsync(runId, RunState.Cancelled, null, row.SnapId, 0, 0, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (KobianException ex)
        {
            outcome = new JobOutcome(JobResult.Failed, ex.Code, ex.ErrorClass == ErrorClass.Transient, row.SnapId);
        }
        catch (StorageException ex)
        {
            outcome = new JobOutcome(JobResult.Failed, ex.Code, ex.IsTransient, row.SnapId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome = new JobOutcome(JobResult.Failed, KbCode.RestoreTargetAccessDenied, false, row.SnapId);
        }
        finally
        {
            if (stagingDir is not null) CleanStaging(stagingDir); // AC-02.1 analogicznie: bez .bak w stagingu po operacji
        }
        await FinishRunAsync(runId, outcome.Result switch { JobResult.Succeeded => RunState.Succeeded, JobResult.Warning => RunState.Warning, _ => RunState.Failed }, outcome.Code, row.SnapId, created is null ? 0 : 1, bytes, CancellationToken.None).ConfigureAwait(false);
        return new(outcome, created);
    }

    /// <summary>D5.11: profil ERP i zapytania kontrolne z <c>dbMeta</c> snapshotu (nierozpoznany profil, brak lub uszkodzony zapis = bez kontroli).</summary>
    internal static (ErpProfile? Profile, IReadOnlyList<ControlRecord>? Controls) ReadControls(IReadOnlyDictionary<string, string>? dbMeta)
    {
        if (dbMeta is null || !dbMeta.TryGetValue("erpProfile", out var id) || !dbMeta.TryGetValue(ControlSet.MetaKey, out var json)) return (null, null);
        var profile = ErpProfileCatalog.LoadBuiltIn().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        return profile is not null && ControlSet.TryParse(json, out var records) ? (profile, records) : (null, null);
    }

    private static void CleanStaging(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // pozostałość sprzątnie następna kopia (świeży katalog na uruchomienie) — nie maskuje wyniku odtworzenia
        }
    }

    /// <summary>Wynik podglądu odtworzenia (it. 98): plan albo kod KB; <see cref="SnapshotId"/> = pełny id rozwiązanego snapshotu.</summary>
    public sealed record PlanOutcome(RestorePlan? Plan, string? Code, string? SnapshotId);

    /// <summary>Planer folderu docelowego (testy: wolne miejsce i kontrola zapisu). Domyślnie DriveInfo + plik próbny.</summary>
    public Func<RepoReader, RestorePlanner> Planners { get; init; } = r => new RestorePlanner(r);

    /// <summary>
    /// Podgląd odtworzenia (SCR-07, R2.8.1 PlanRestore — dry-run, it. 98): snapshot jak w restore, ten sam wybór (filtr albo dokładne ścieżki),
    /// cel jak w restore (ścieżka bezwzględna istniejącego folderu). Tylko odczyt drzew i indeksów; bez wiersza run_runs; dane nie są pobierane.
    /// </summary>
    public async Task<PlanOutcome> PlanAsync(string snapshot, string? pathFilter, IReadOnlyCollection<string>? exactPaths, string target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return new PlanOutcome(null, KbCode.RestoreTargetAccessDenied, null);
        var (row, code) = Resolve(snapshot ?? "");
        if (row is null) return new PlanOutcome(null, code, null);
        try
        {
            var def = JobDefinition.Parse(row.JobType, row.Definition);
            if (!_providers.TryGetValue(def.DestinationScheme, out var factory)) return new PlanOutcome(null, KbCode.JobDefinitionInvalid, row.SnapId);
            using var keys = KeyFileLoader.Load(def.KeyFile, def.PasswordFile, def.RepoHeader);
            await using var session = await factory.OpenAsync(def.ToDestination(DestinationId(def)), CredentialRole.Backup, ct).ConfigureAwait(false);
            var snap = await MetadataStore.LoadSnapshotAsync(session, keys, Convert.FromHexString(row.SnapId), ct).ConfigureAwait(false);
            using var reader = new RepoReader(session, keys);
            await reader.LoadIndexesWithFallbackAsync(snap, ct).ConfigureAwait(false);
            var configured = Planners(reader);
            var normalized = exactPaths is null ? null : ExactPathSelection.Normalize(exactPaths);
            var planner = new RestorePlanner(reader)
            {
                FreeBytes = configured.FreeBytes,
                CanWrite = configured.CanWrite,
                Journal = CatalogRestoreJournal.Peek(_catalogPath, CatalogRestoreJournal.PlanHash(row.SnapId, target, string.IsNullOrEmpty(pathFilter) ? null : pathFilter, normalized)),
            };
            var plan = exactPaths is not null
                ? await planner.PlanExactAsync(snap, target, exactPaths, ct).ConfigureAwait(false)
                : await planner.PlanAsync(snap, target, string.IsNullOrEmpty(pathFilter) ? null : pathFilter, ct).ConfigureAwait(false);
            return new PlanOutcome(plan, null, row.SnapId);
        }
        catch (KobianException ex)
        {
            return new PlanOutcome(null, ex.Code, row.SnapId);
        }
        catch (StorageException ex)
        {
            return new PlanOutcome(null, ex.Code, row.SnapId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PlanOutcome(null, KbCode.RestoreTargetAccessDenied, row.SnapId);
        }
    }

    /// <summary>Wynik przeglądania kopii (BL-63, SCR-06): listing albo kod KB; <see cref="SnapshotId"/> = pełny id rozwiązanego snapshotu.</summary>
    public sealed record BrowseOutcome(BrowseListing? Listing, string? Code, string? SnapshotId);

    /// <summary>
    /// Listowanie folderu snapshotu (BL-63, SCR-06) — tylko odczyt drzew, bez danych plików, bez wiersza run_runs (operacja niemodyfikująca).
    /// Snapshot jak w restore („latest”, pełny id, prefiks ≥ 8 hex). Błędy → kod KB (KB-5002 snapshot, KB-5003 ścieżka, KB-1xxx/KB-2xxx repo/cel).
    /// </summary>
    public async Task<BrowseOutcome> BrowseAsync(string snapshot, string? path, CancellationToken ct)
    {
        var (row, code) = Resolve(snapshot ?? "");
        if (row is null) return new BrowseOutcome(null, code, null);
        try
        {
            SnapshotBrowser.SplitPath(path); // walidacja przed otwarciem celu
            var def = JobDefinition.Parse(row.JobType, row.Definition);
            if (!_providers.TryGetValue(def.DestinationScheme, out var factory)) return new BrowseOutcome(null, KbCode.JobDefinitionInvalid, row.SnapId);
            using var keys = KeyFileLoader.Load(def.KeyFile, def.PasswordFile, def.RepoHeader);
            await using var session = await factory.OpenAsync(def.ToDestination(DestinationId(def)), CredentialRole.Backup, ct).ConfigureAwait(false);
            var snap = await MetadataStore.LoadSnapshotAsync(session, keys, Convert.FromHexString(row.SnapId), ct).ConfigureAwait(false);
            using var reader = new RepoReader(session, keys);
            await reader.LoadIndexesWithFallbackAsync(snap, ct).ConfigureAwait(false);
            var listing = await new SnapshotBrowser(reader).ListAsync(snap, path, ct).ConfigureAwait(false);
            return new BrowseOutcome(listing, null, row.SnapId);
        }
        catch (KobianException ex)
        {
            return new BrowseOutcome(null, ex.Code, row.SnapId);
        }
        catch (StorageException ex)
        {
            return new BrowseOutcome(null, ex.Code, row.SnapId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BrowseOutcome(null, KbCode.TransientIo, row.SnapId);
        }
    }

    private static string DestinationId(JobDefinition def) =>
        def.DestinationId;

    /// <summary>Kod „najgorszego” problemu: skrót/pack (Failed) przed pozostałymi; brak problemów → null.</summary>
    internal static string? WorstCode(RestoreResult r) =>
        r.Issues.Count == 0 ? null
        : r.Issues.FirstOrDefault(i => i.Code is KbCode.RestoreHashMismatch or KbCode.RestorePackMissing)?.Code ?? r.Issues[0].Code;

    /// <summary>Prefiks odwołania „najnowsza kopia zadania” (BL-70): <c>latest:&lt;nazwa zadania&gt;</c>; samo „latest” = najnowsza ze wszystkich zadań.</summary>
    public const string LatestOfJobPrefix = "latest:";

    /// <summary>Najdłuższa nazwa zadania w odwołaniu (nazwy zadań są krótkie; dłuższe = brak kopii, bez zapytania).</summary>
    public const int MaxJobNameLength = 256;

    private (SnapRow? Row, string Code) Resolve(string reference)
    {
        var trimmed = reference.Trim();
        string? job = null;
        if (trimmed.StartsWith(LatestOfJobPrefix, StringComparison.OrdinalIgnoreCase))
        {
            job = trimmed[LatestOfJobPrefix.Length..];
            if (job.Length is 0 or > MaxJobNameLength) return (null, KbCode.SnapshotNotFound);
            trimmed = "latest";
        }
        var r = trimmed.ToLowerInvariant();
        var latest = r == "latest";
        if (!latest && (r.Length < MinPrefixLength || r.Length > 64 || !r.All(Uri.IsHexDigit))) return (null, KbCode.SnapshotNotFound);
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.snap_id, s.job_id, j.type, j.definition
            FROM snap_snapshots s JOIN job_jobs j ON j.job_id = s.job_id
            WHERE s.forgotten_at IS NULL AND ($latest = 1 OR s.snap_id LIKE $p ESCAPE '\') AND ($job IS NULL OR j.name = $job)
            ORDER BY s.time_utc DESC, s.rowid DESC LIMIT 2;
            """;
        cmd.Parameters.AddWithValue("$latest", latest ? 1 : 0);
        cmd.Parameters.AddWithValue("$p", latest ? "" : r + "%");
        cmd.Parameters.AddWithValue("$job", (object?)job ?? DBNull.Value);
        using var rd = cmd.ExecuteReader();
        var rows = new List<SnapRow>();
        while (rd.Read()) rows.Add(new SnapRow(rd.GetString(0), rd.GetString(1), rd.GetString(2), (byte[])rd[3]));
        if (rows.Count == 0 || (!latest && rows.Count > 1)) return (null, KbCode.SnapshotNotFound); // brak albo niejednoznaczny prefiks
        return (rows[0], KbCode.SnapshotNotFound);
    }

    private static JobOutcome Failed(string code) => new(JobResult.Failed, code, false, null);

    private Task<int> InsertRunAsync(byte[] runId, string jobId, CancellationToken ct)
    {
        var now = Iso(_time.GetUtcNow());
        return _writer.ExecuteControlAsync((c, tx) =>
        {
            var id = Convert.ToHexStringLower(runId);
            Exec(c, tx, "INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at,started_at) VALUES ($i,$j,'manual','Running',0,$t,$t);", ("$i", id), ("$j", jobId), ("$t", now));
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason) VALUES ($i,0,NULL,'Running',$t,'restore');", ("$i", id), ("$t", now));
            return 0;
        }, ct);
    }

    private Task<int> FinishRunAsync(byte[] runId, RunState state, string? code, string snapId, long files, long bytes, CancellationToken ct)
    {
        var now = Iso(_time.GetUtcNow());
        return _writer.ExecuteControlAsync((c, tx) =>
        {
            var id = Convert.ToHexStringLower(runId);
            Exec(c, tx, "UPDATE run_runs SET state=$s, finished_at=$t, kb_code=$k, snapshot_id=$n, bytes_read=$b, files=$f WHERE run_id=$i;",
                ("$s", state.ToString()), ("$t", now), ("$k", code), ("$n", snapId), ("$b", bytes), ("$f", files), ("$i", id));
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason,kb_code) VALUES ($i,1,'Running',$s,$t,'restore',$k);",
                ("$i", id), ("$s", state.ToString()), ("$t", now), ("$k", code));
            return 0;
        }, ct);
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
