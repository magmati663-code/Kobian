using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;
using Kobian.Providers.Sources.MsSql;
using Kobian.Providers.Storage.Throttling;
using Kobian.Scheduler.Locks;
using Kobian.Scheduler.Runs;
using Microsoft.Data.Sqlite;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Uruchamianie zadań „files” z katalogu (BL-15, BL-42, BL-22): job_jobs → <see cref="RunExecutor"/> → <see cref="BackupPipeline"/> na dostawcy z definicji;
/// każde podejście = wiersz run_runs (+ run_transitions), udany backup = wiersz snap_snapshots odczytany z repozytorium (źródło prawdy).
/// Zapisy idą przez <see cref="CatalogWriter"/> (D2.14 pkt 1). Jedno uruchomienie zadania naraz (KB-4103). Ślady przerwane awarią (Running bez końca) trafiają do Interrupted przy starcie.
/// Backup przyrostowy, gdy w katalogu jest poprzedni snapshot tego zadania w tym repozytorium, nie jest „suspect” i da się go wczytać; w przeciwnym razie pełny (zawsze poprawny).
/// Po każdym udanym zapisie: V2 nowego snapshotu (3.7) — wynik w snap_snapshots.verify_level (2) albo suspect=1 i uruchomienie Failed z kodem KB-15xx.
/// </summary>
public sealed class CatalogJobService
{
    private readonly string _catalogPath;
    private readonly CatalogWriter _writer;
    private readonly IReadOnlyDictionary<string, IStorageProviderFactory> _providers;
    private readonly byte[] _hostId;
    private readonly TimeProvider _time;
    private readonly Func<int, CancellationToken, Task>? _retryDelay;
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);

    public CatalogJobService(string catalogPath, CatalogWriter writer, IReadOnlyDictionary<string, IStorageProviderFactory> providers, byte[] hostId, TimeProvider time, Func<int, CancellationToken, Task>? retryDelay = null)
    {
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _hostId = hostId is { Length: 16 } ? hostId : throw new ArgumentException("hostId must be 16 bytes.", nameof(hostId));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _retryDelay = retryDelay;
    }

    /// <summary>
    /// Retencja automatyczna po udanej kopii (PROWIZORYCZNIE Qb28, ADR-0017): logiczne zapomnienie wg polityki zadania, z blokadą zadania nadal trzymaną
    /// (nie ściga się z kolejną kopią). Wstrzymanie po skoku zegara (SEC-17.4) respektuje sama usługa retencji. Null = tylko ręcznie (`kobian forget`).
    /// </summary>
    public CatalogRetentionService? AutoRetention { get; init; }

    /// <summary>
    /// Fabryka sesji SQL dla zadań „database” (MSSQL) (BL-40 cz. 2; Program.cs: <c>AgentMsSql.CreateFactory</c>). Null = zadania „database” kończą się KB-0103.
    /// </summary>
    public Func<MsSqlSourceDefinition, CancellationToken, ValueTask<IMsSqlSession>>? MsSqlSessions { get; init; }

    /// <summary>RD-39 (PROWIZORYCZNIE Qb35): także przy uruchomieniu — zadanie zapisane przed zawężeniem listy nie połączy się z serwerem spoza niej (KB-3009).</summary>
    public SqlServerAllowList AllowedSqlServers { get; init; } = SqlServerAllowList.Any;

    /// <summary>Port wolnego miejsca stagingu MSSQL (P-02); null = <see cref="DriveInfoStagingVolume"/>.</summary>
    public IStagingVolume? MsSqlStagingVolume { get; init; }

    /// <summary>
    /// RD-73 cz. 2 (it. 119; PROWIZORYCZNIE Qb63): równoległe wysyłki packów (D4.16 pkt 2–3). Limit startowy = wyuczony limit celu z katalogu
    /// (<c>repo_dest_parallelism</c>) albo <c>RecommendedParallelism</c> dostawcy; otwierane jest tyle sesji Backup (główna + N−1 dodatkowych przez fabrykę,
    /// czyli przez governor otwarć sesji SFTP/SMB), a AIMD może limit tylko obniżać w trakcie. Odmowa otwarcia dodatkowej sesji = sygnał limitu, kopia jedzie
    /// na tych, które są. Po uruchomieniu limit zapisywany (także po błędzie). Null = wysyłka sekwencyjna (zachowanie sprzed it. 119).
    /// </summary>
    public CatalogParallelismStore? UploadParallelism { get; init; }

    /// <summary>Liczba sesji wysyłki packów użytych w ostatnim uruchomieniu (diagnostyka/testy); 0 = wysyłka sekwencyjna.</summary>
    public int LastUploadSessions { get; private set; }

    /// <summary>Wynik ostatniej retencji automatycznej (diagnostyka/testy); null = nie uruchamiano.</summary>
    public RetentionOutcome? LastAutoRetention { get; private set; }

    /// <summary>Stabilny, nieodwracalny identyfikator hosta (16 B) z nazwy komputera — trafia tylko do zaszyfrowanych metadanych repozytorium.</summary>
    public static byte[] HostIdFor(string machineName) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("kobian-host-v1:" + machineName))[..16];

    /// <summary>Oznacza uruchomienia zostawione w stanie Running/Queued (awaria usługi) jako Interrupted (D7.8).</summary>
    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        var now = Iso(_time.GetUtcNow());
        await _writer.ExecuteControlAsync((c, tx) =>
        {
            Exec(c, tx, "UPDATE run_runs SET state='Interrupted', finished_at=$t WHERE state IN ('Queued','Running');", ("$t", now));
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public async Task<JobOutcome> RunAsync(string jobName, CancellationToken ct)
    {
        var job = LoadJob(jobName);
        if (job is null) return new JobOutcome(JobResult.Failed, KbCode.JobNotFound, false, null);
        if (!job.Enabled)
        {
            await InsertRunAsync(RandomNumberGenerator.GetBytes(16), job.Id, 0, "manual", RunState.Skipped, ct).ConfigureAwait(false);
            return new JobOutcome(JobResult.Warning, KbCode.JobDisabled, false, null);
        }
        if (!_active.TryAdd(job.Id, 0)) return new JobOutcome(JobResult.Failed, KbCode.PreviousRunActive, false, null);
        if (Locks is null) return await ExecuteActiveAsync(job, null, ct).ConfigureAwait(false);

        // BL-74 (D7.4/D7.10): uruchomienie ręczne przechodzi przez tę samą kolejkę zasobów co harmonogram (limit globalny + smb:<serwer> z kontem).
        var owner = "manual:" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var requests = RunResourceLocks.ForBackup(job.Type, job.Definition);
        if (Locks.TryAcquireAll(owner, requests)) return await RunWithLocksAsync(job, owner, null, ct).ConfigureAwait(false);

        // BL-76 (D7.8): zasób zajęty — czekanie jest trwałe i widoczne: wiersz run_runs Queued (cause manual), który potem przechodzi
        // Queued → Running (to samo uruchomienie), Queued → Skipped/KB-4102 po limicie albo Queued → Cancelled przy anulowaniu.
        // Scheduler go nie bierze (QueuedRuns: tylko schedule/misfire), awaria usługi → Interrupted (RecoverInterruptedAsync).
        var queuedId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        bool acquired;
        try
        {
            await InsertRunAsync(Convert.FromHexString(queuedId), job.Id, 0, "manual", RunState.Queued, CancellationToken.None).ConfigureAwait(false);
            acquired = await WaitForLocksAsync(owner, requests, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _active.TryRemove(job.Id, out _); // anulowanie w kolejce: zadanie nie zostaje „zajęte”
            await EndQueuedAsync(queuedId, RunState.Cancelled, null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            _active.TryRemove(job.Id, out _);
            throw;
        }
        if (!acquired)
        {
            _active.TryRemove(job.Id, out _);
            await EndQueuedAsync(queuedId, RunState.Skipped, KbCode.QueueTimeout, CancellationToken.None).ConfigureAwait(false);
            return new JobOutcome(JobResult.Warning, KbCode.QueueTimeout, false, null);
        }
        return await RunWithLocksAsync(job, owner, queuedId, ct).ConfigureAwait(false);
    }

    private async Task<JobOutcome> RunWithLocksAsync(JobRow job, string owner, string? queuedRunIdHex, CancellationToken ct)
    {
        try
        {
            return await ExecuteActiveAsync(job, queuedRunIdHex, ct).ConfigureAwait(false);
        }
        finally
        {
            Locks!.Release(owner);
        }
    }

    /// <summary>
    /// Wspólny menedżer blokad kolejki (BL-74, D7.10) — ten sam, którego używa <see cref="CatalogScheduler"/> (scheduler bierze go stąd, gdy podany).
    /// Null = uruchomienie ręczne bez kolejki (zachowanie sprzed it. 96; testy jednostkowe usługi).
    /// </summary>
    public LockManager? Locks { get; init; }

    /// <summary>
    /// Najdłuższe czekanie uruchomienia ręcznego na zasoby, potem Skipped z KB-4102 (D7.10 queueTimeout; domyślnie 6 h).
    /// PROWIZORYCZNIE Qb46 (ADR-0029): stała dla wszystkich zadań, bez pola w definicji zadania.
    /// </summary>
    public TimeSpan ManualQueueTimeout { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Odstęp ponawiania próby nabycia blokad (jak obrót pętli schedulera, 1 s).</summary>
    public TimeSpan ManualQueuePoll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Liczba uruchomień ręcznych czekających teraz w kolejce (diagnostyka, testy, przyszły stan „W kolejce” w UI).</summary>
    public int ManualWaiting => Volatile.Read(ref _manualWaiting);

    private int _manualWaiting;

    private async Task<bool> WaitForLocksAsync(string owner, IReadOnlyList<LockRequest> requests, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + ManualQueueTimeout;
        Interlocked.Increment(ref _manualWaiting);
        try
        {
            while (!Locks!.TryAcquireAll(owner, requests))
            {
                if (_time.GetUtcNow() >= deadline) return false;
                await Task.Delay(ManualQueuePoll, _time, ct).ConfigureAwait(false);
            }
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _manualWaiting);
        }
    }

    /// <summary>
    /// Wykonuje uruchomienie utworzone wcześniej przez scheduler (wiersz run_runs w stanie Queued, cause schedule/misfire): Queued → Running → stan końcowy (D7.8).
    /// Zadanie usunięte/wyłączone albo poprzednie uruchomienie nadal trwa → wiersz kończy się jako Skipped z KB-0105/KB-4103 (bez zalegania w kolejce).
    /// </summary>
    public async Task<JobOutcome> RunQueuedAsync(string runIdHex, CancellationToken ct)
    {
        string? jobId;
        using (var c = CatalogBootstrap.Open(_catalogPath, readOnly: true))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT job_id FROM run_runs WHERE run_id=$i AND state='Queued' AND cause IN ('schedule','misfire');";
            cmd.Parameters.AddWithValue("$i", runIdHex);
            jobId = cmd.ExecuteScalar() as string;
        }
        if (jobId is null) return new JobOutcome(JobResult.Failed, KbCode.Internal, false, null);
        var job = LoadJobById(jobId);
        if (job is null || !job.Enabled)
        {
            await SkipQueuedAsync(runIdHex, KbCode.JobDisabled, CancellationToken.None).ConfigureAwait(false);
            return new JobOutcome(JobResult.Warning, KbCode.JobDisabled, false, null);
        }
        if (!_active.TryAdd(job.Id, 0))
        {
            await SkipQueuedAsync(runIdHex, KbCode.PreviousRunActive, CancellationToken.None).ConfigureAwait(false);
            return new JobOutcome(JobResult.Warning, KbCode.PreviousRunActive, false, null);
        }
        return await ExecuteActiveAsync(job, runIdHex, ct).ConfigureAwait(false);
    }

    private async Task<JobOutcome> ExecuteActiveAsync(JobRow job, string? queuedRunIdHex, CancellationToken ct)
    {
        try
        {
            var runner = new Runner(this, job, queuedRunIdHex);
            var records = await new RunExecutor(runner, _retryDelay).ExecuteAsync(job.Id, new RunConditions { JobEnabled = true }, ct).ConfigureAwait(false);
            if (runner.Last is { Result: JobResult.Succeeded } && AutoRetention is not null) await AutoRetainAsync(job, ct).ConfigureAwait(false);
            return runner.Last ?? new JobOutcome(JobResult.Failed, records.Count == 0 ? KbCode.Internal : records[^1].Code ?? KbCode.Internal, false, null);
        }
        finally
        {
            _active.TryRemove(job.Id, out _);
        }
    }

    private async Task AutoRetainAsync(JobRow job, CancellationToken ct)
    {
        try
        {
            LastAutoRetention = await AutoRetention!.ApplyAfterBackupAsync(job.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KobianException or SqliteException or IOException or OperationCanceledException)
        {
            // Retencja nie może zmienić wyniku udanej kopii; nic nie zapomniano (transakcja), spróbuje ponownie po następnej kopii.
            LastAutoRetention = new RetentionOutcome(ex is KobianException k ? k.Code : KbCode.Internal, null, 0, false, true);
        }
    }

    private sealed record JobRow(string Id, string Name, string Type, byte[] Definition, bool Enabled);

    private JobRow? LoadJob(string name) => LoadJobBy("name", name);

    private JobRow? LoadJobById(string jobId) => LoadJobBy("job_id", jobId);

    private JobRow? LoadJobBy(string column, string value)
    {
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = column == "name"
            ? "SELECT job_id, name, type, definition, enabled FROM job_jobs WHERE name = $n;"
            : "SELECT job_id, name, type, definition, enabled FROM job_jobs WHERE job_id = $n;";
        cmd.Parameters.AddWithValue("$n", value);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? new JobRow(rd.GetString(0), rd.GetString(1), rd.GetString(2), (byte[])rd[3], rd.GetInt32(4) != 0) : null;
    }

    private sealed class Runner(CatalogJobService svc, JobRow job, string? queuedRunIdHex) : IJobRunner
    {
        public JobOutcome? Last { get; private set; }

        public async Task<JobOutcome> RunAsync(string jobId, int attempt, CancellationToken ct)
        {
            byte[] runId;
            if (attempt == 0 && queuedRunIdHex is not null)
            {
                runId = Convert.FromHexString(queuedRunIdHex);
                await svc.StartQueuedAsync(queuedRunIdHex, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                runId = RandomNumberGenerator.GetBytes(16);
                await svc.InsertRunAsync(runId, job.Id, attempt, attempt == 0 ? "manual" : "retry", RunState.Running, CancellationToken.None).ConfigureAwait(false);
            }
            JobOutcome outcome;
            var stats = new SnapshotStats(0, 0, 0, 0, 0);
            try
            {
                (outcome, stats) = await svc.ExecuteAsync(job, runId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await svc.FinishRunAsync(runId, RunState.Cancelled, null, null, stats, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (KobianException ex)
            {
                // KB-3101 (AC-02.2): inna kopia/odtwarzanie/DBCC na bazie przez cały czas oczekiwania → zadanie POMINIĘTE (rejestr: severity warning, „kolejne uruchomienie wg
                // harmonogramu”). Wcześniej kończyło się jako Failed, czyli fałszywym alarmem „kopia się nie udała” przy zwykłej kolizji z cudzym backupem.
                var result = ex.Code == KbCode.SqlConcurrentOperation ? JobResult.Warning : JobResult.Failed;
                outcome = new JobOutcome(result, ex.Code, ex.ErrorClass == ErrorClass.Transient, null);
            }
            catch (StorageException ex)
            {
                outcome = new JobOutcome(JobResult.Failed, ex.Code, ex.IsTransient, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcome = new JobOutcome(JobResult.Failed, KbCode.SourcesNotReadable, false, null);
            }
#pragma warning disable CA1031 // granica wykonawcy: nieoczekiwany wyjątek = wiersz Failed (KB-0001) i dalej do RunExecutora
            catch
            {
                await svc.FinishRunAsync(runId, RunState.Failed, KbCode.Internal, null, stats, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
#pragma warning restore CA1031
            var state = outcome.Result switch { JobResult.Succeeded => RunState.Succeeded, JobResult.Warning => RunState.Warning, _ => RunState.Failed };
            await svc.FinishRunAsync(runId, state, outcome.Code, outcome.SnapshotId, stats, CancellationToken.None).ConfigureAwait(false);
            Last = outcome;
            return outcome;
        }
    }

    private async Task<(JobOutcome, SnapshotStats)> ExecuteAsync(JobRow job, byte[] runId, CancellationToken ct)
    {
        var def = JobDefinition.Parse(job.Type, job.Definition);
        if (!_providers.TryGetValue(def.DestinationScheme, out var factory)) throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Destination provider is not available.");
        using var keys = KeyFileLoader.Load(def.KeyFile, def.PasswordFile, def.RepoHeader);
        var repoHex = Convert.ToHexStringLower(keys.RepoId);
        var destId = def.DestinationId;
        var headerSha = def.RepoHeader is null ? new byte[32] : SHA256.HashData(await File.ReadAllBytesAsync(def.RepoHeader, ct).ConfigureAwait(false)); // 32 zera = repozytorium bez nagłówka (klucz deweloperski, ADR-0002)
        await EnsureRepoAsync(repoHex, destId, headerSha, ct).ConfigureAwait(false);
        if (def.MsSql is null && !Directory.Exists(def.Source)) throw new KobianException(KbCode.SourcesNotReadable, ErrorClass.Permanent, "Source does not exist.");
        if (def.MsSql is not null && MsSqlSessions is null) throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "MSSQL source is not available in this Agent.");
        if (def.MsSql is { } allowed) AllowedSqlServers.Demand(allowed.Connection.Server);

        // D3.13: blokada współdzielona na czas kopii — na OSOBNEJ sesji (sesje są jednowątkowe), odświeżana w tle; prune (wyłączna) czeka albo rezygnuje (KB-1202).
        await using var lockSession = await factory.OpenAsync(def.ToDestination(destId), CredentialRole.Backup, ct).ConfigureAwait(false);
        await using var lease = await RepoLocks.AcquireSharedAsync(lockSession, keys, RepoLockOwner.For(_hostId), _time, null, ct).ConfigureAwait(false);
        lease.StartAutoRenew();
        await using var session = await factory.OpenAsync(def.ToDestination(destId), CredentialRole.Backup, ct).ConfigureAwait(false);
        var (mode, parent, baseline) = await PlanBaselineAsync(job.Id, repoHex, session, keys, ct).ConfigureAwait(false);
        var mainUploader = new VerifiedUploader(session);
        await using var uploads = await OpenUploadSessionsAsync(factory, def.ToDestination(destId), session, mainUploader, ct).ConfigureAwait(false);
        var pipeline = new BackupPipeline(keys, mainUploader) { PackUploaders = uploads.Uploaders, Parallelism = uploads.Aimd };
        BackupResult result;
        IReadOnlyList<MsSqlWarning> sqlWarnings = [];
        if (def.MsSql is { } db)
        {
            // R2.5.1: przygotowanie (.bak COPY_ONLY + CHECKSUM w stagingu) → potok (tryb Database, klucz 14) → sprzątanie stagingu zawsze (AC-02.1).
            // Baza dedup = poprzedni snapshot zadania (kolejne .bak różnią się zwykle niewielką częścią stron — mniej wysłanych danych).
            var sql = await MsSqlSessions!(db, ct).ConfigureAwait(false);
            try
            {
                var request = new MsSqlBackupRequest(db.Database, db.StagingRoot, Convert.ToHexStringLower(runId), db.SqlStagingRoot, db.Profile)
                {
                    ConflictWait = db.ConflictWait,
                    DetectErp = true, // D5.9/D5.11, PROWIZORYCZNIE Qb103: wykryj profil ERP i zapisz zapytania kontrolne (brak SELECT → KB-3010, kopia ważna)
                };
                await using var staged = await new MsSqlBackupService(sql, MsSqlStagingVolume, null, _time).PrepareAsync(request, ct).ConfigureAwait(false);
                sqlWarnings = staged.Warnings;
                var source = new StagedFileSource(staged.StagedFile, DbSnapshotMeta.EntryName, JobDefinition.MsSqlSourceMarker);
                result = await pipeline.RunAsync(new BackupRequest(source, job.Id, _hostId, SnapshotMode.Database, null, parent, def.PackTargetSize, staged.DbMeta, _time, runId, baseline), ct).ConfigureAwait(false);
            }
            finally
            {
                if (sql is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
            }
        }
        else
        {
            result = await pipeline.RunAsync(new BackupRequest(new FileSystemSource(def.Source), job.Id, _hostId, mode, null, parent, def.PackTargetSize, Time: _time, RunId: runId, BaselineStream: baseline), ct).ConfigureAwait(false);
        }
        if (result.SnapId is null) return (result.ToJobOutcome(), result.Stats);
        var snap = await MetadataStore.LoadSnapshotAsync(session, keys, result.SnapId, ct).ConfigureAwait(false);
        await InsertSnapshotAsync(snap, repoHex, job.Id, ct).ConfigureAwait(false);

        // 3.7 + D3.14: V2 nowego snapshotu po commit, na tej samej sesji (Backup — tylko odczyt). Brak V2 = Failed; snapshot zostaje w katalogu jako „suspect”
        // (nie jest bazą przyrostu — następna kopia pełna, PlanBaselineAsync) i nie liczy się do RPO. Alert A04 = RunFinishedEvent z kodem KB-15xx (rola 9).
        var v2 = await new RepoChecker(session, keys).CheckSnapshotAsync(result.SnapId, ct).ConfigureAwait(false);
        var snapHex = Convert.ToHexStringLower(result.SnapId);
        await MarkVerifiedAsync(snapHex, v2.Status == CheckStatus.Succeeded, ct).ConfigureAwait(false);
        if (v2.Status == CheckStatus.Succeeded && baseline is null) await ClearForceFullAsync(job.Id, ct).ConfigureAwait(false); // kopia bez bazy dedup (pełna/baza od zera) spełnia wymuszenie z V3
        if (v2.Status != CheckStatus.Succeeded)
            return (new JobOutcome(JobResult.Failed, CatalogCheckService.WorstCode(v2) ?? KbCode.ReferenceIntegrityFailed, false, snapHex), result.Stats);
        var outcome = result.ToJobOutcome();
        // Ostrzeżenia przygotowania MSSQL (KB-3103 obca pełna kopia, KB-3301 TDE, KB-3401 wersja…) — kopia jest ważna, uruchomienie = Warning z pierwszym kodem.
        if (outcome.Result == JobResult.Succeeded && sqlWarnings.Count > 0) outcome = outcome with { Result = JobResult.Warning, Code = sqlWarnings[0].Code };
        return (outcome, result.Stats);
    }

    /// <summary>
    /// RD-73 cz. 2: sesje wysyłki packów. Główny uploader też jest w puli — w fazie packów nie jest używany przez nic innego (indeks i snapshot idą przez
    /// niego dopiero po zakończeniu wszystkich wysyłek, BackupPipeline.DrainAsync); dzięki temu N sesji wysyłki = N−1 dodatkowych otwarć.
    /// </summary>
    private async Task<UploadSessions> OpenUploadSessionsAsync(IStorageProviderFactory factory, DestinationConfig dest, IStorageSession main, VerifiedUploader mainUploader, CancellationToken ct)
    {
        if (UploadParallelism is null)
        {
            LastUploadSessions = 0;
            return new UploadSessions(null, null, [], [], dest.Id);
        }
        var recommended = Math.Clamp(main.Capabilities.RecommendedParallelism, 1, AimdParallelism.MaxParallelism);
        var aimd = UploadParallelism.Create(dest.Id, recommended);
        var extra = new List<IStorageSession>();
        var uploaders = new List<VerifiedUploader> { mainUploader };
        try
        {
            for (var i = 1; i < aimd.Current; i++)
            {
                IStorageSession s;
                try
                {
                    s = await factory.OpenAsync(dest, CredentialRole.Backup, ct).ConfigureAwait(false);
                }
                catch (StorageException ex) when (ex.Code == KbCode.ServerSessionLimit || ex.IsTransient)
                {
                    if (ex.Code == KbCode.ServerSessionLimit) aimd.OnLimitSignal(); // serwer nie da więcej sesji — kopia na tych, które są
                    break;
                }
                extra.Add(s);
                uploaders.Add(new VerifiedUploader(s));
            }
        }
        catch
        {
            foreach (var s in extra) await s.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        LastUploadSessions = uploaders.Count;
        return uploaders.Count > 1
            ? new UploadSessions(UploadParallelism, aimd, uploaders, extra, dest.Id)
            : new UploadSessions(UploadParallelism, aimd, [], extra, dest.Id); // jedna sesja = ścieżka sekwencyjna potoku (bez puli)
    }

    /// <summary>Sesje dodatkowe + regulator; zwolnienie = zapis wyuczonego limitu celu (best effort) i zamknięcie sesji dodatkowych.</summary>
    private sealed class UploadSessions(CatalogParallelismStore? store, AimdParallelism? aimd, IReadOnlyList<VerifiedUploader> uploaders, List<IStorageSession> extra, string destId) : IAsyncDisposable
    {
        public IReadOnlyList<VerifiedUploader> Uploaders => uploaders;

        public IUploadParallelism? Aimd => uploaders.Count > 1 ? aimd : null;

        public async ValueTask DisposeAsync()
        {
            foreach (var s in extra)
            {
#pragma warning disable CA1031 // zamknięcie sesji dodatkowej nie może przesłonić wyniku kopii
                try { await s.DisposeAsync().ConfigureAwait(false); }
                catch { /* sesja i tak porzucona */ }
#pragma warning restore CA1031
            }
            if (store is not null && aimd is not null)
            {
#pragma warning disable CA1031 // zapis wyuczonego limitu to optymalizacja — błąd katalogu nie zmienia wyniku kopii
                try { await store.SaveAsync(destId, aimd, CancellationToken.None).ConfigureAwait(false); }
                catch { /* następne uruchomienie zacznie od rekomendowanego */ }
#pragma warning restore CA1031
            }
        }
    }

    private Task<int> ClearForceFullAsync(string jobId, CancellationToken ct) =>
        _writer.ExecuteControlAsync((c, tx) =>
        {
            Exec(c, tx, "UPDATE sched_verify_jobs SET force_full=0 WHERE job_id=$j AND force_full<>0;", ("$j", jobId));
            return 0;
        }, ct);

    private Task<int> MarkVerifiedAsync(string snapHex, bool ok, CancellationToken ct) =>
        _writer.ExecuteControlAsync((c, tx) =>
        {
            Exec(c, tx, ok
                ? "UPDATE snap_snapshots SET verify_level=MAX(verify_level,2) WHERE snap_id=$i;"
                : "UPDATE snap_snapshots SET suspect=1 WHERE snap_id=$i;", ("$i", snapHex));
            return 0;
        }, ct);

    private async Task<(SnapshotMode Mode, byte[]? Parent, BaselineSet? Baseline)> PlanBaselineAsync(string jobId, string repoHex, IStorageSession session, RepoKeys keys, CancellationToken ct)
    {
        string? last;
        using (var c = CatalogBootstrap.Open(_catalogPath, readOnly: true))
        using (var cmd = c.CreateCommand())
        {
            // Najnowszy snapshot zadania; „suspect” (V2 po backupie albo check nieudany) wymusza kopię pełną (3.7) — nie przyrost od starszej bazy.
            cmd.CommandText = "SELECT snap_id, suspect FROM snap_snapshots WHERE job_id=$j AND repo_id=$r AND forgotten_at IS NULL ORDER BY time_utc DESC, rowid DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("$j", jobId);
            cmd.Parameters.AddWithValue("$r", repoHex);
            using var rd = cmd.ExecuteReader();
            last = rd.Read() && rd.GetInt64(1) == 0 ? rd.GetString(0) : null;
        }
        if (last is not null && ForceFullRequested(jobId)) last = null; // 3.7: po błędzie V3 kolejna kopia jest pełna (bez dedup na uszkodzonej bazie)
        if (last is null) return (SnapshotMode.Full, null, null);
        try
        {
            var parentId = Convert.FromHexString(last);
            var prev = await MetadataStore.LoadSnapshotAsync(session, keys, parentId, ct).ConfigureAwait(false);
            // BL-83 (RD-64): indeksy bazowe strumieniowo — po jednym, bloki od razu do tablicy dedup (32 B/blok), obiekt indeksu porzucany.
            // D3.16: odwołania do indeksów zastąpionych przez konsolidację rozwiązywane przez indeks skonsolidowany (każdy obiekt wczytany raz).
            var baseline = await BaselineSet.LoadResolvedAsync(prev.Indexes, new IndexResolver(session, keys, cacheObjects: false), ct).ConfigureAwait(false);
            return (SnapshotMode.Incremental, parentId, baseline);
        }
        catch (Exception ex) when (ex is StorageException or KobianException or FormatException)
        {
            return (SnapshotMode.Full, null, null); // poprzedni snapshot nieczytelny: pełny backup jest zawsze poprawny (nie przyrost na niepewnej bazie)
        }
    }

    /// <summary>Weryfikacja V3 z harmonogramu wykryła uszkodzenie (sched_verify_jobs.force_full) — <see cref="CatalogCheckService"/>.</summary>
    private bool ForceFullRequested(string jobId)
    {
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT force_full FROM sched_verify_jobs WHERE job_id=$j;";
        cmd.Parameters.AddWithValue("$j", jobId);
        return cmd.ExecuteScalar() is long v && v != 0;
    }

    /// <summary>Uruchomienie zadania w toku (kopia) — weryfikacja V3 z harmonogramu nie startuje dla tego zadania (D7.4).</summary>
    public bool IsActive(string jobId) => _active.ContainsKey(jobId);

    private Task<int> EnsureRepoAsync(string repoHex, string destId, byte[] headerSha, CancellationToken ct) =>
        _writer.ExecuteControlAsync((c, tx) =>
        {
            Exec(c, tx, "INSERT OR IGNORE INTO repo_repositories(repo_id,destination_id,format_version,header_sha256,state) VALUES ($r,$d,1,$h,'ok');",
                ("$r", repoHex), ("$d", destId), ("$h", headerSha));
            return 0;
        }, ct);

    private Task<int> InsertRunAsync(byte[] runId, string jobId, int attempt, string cause, RunState state, CancellationToken ct, string? skipCode = null)
    {
        var now = Iso(_time.GetUtcNow());
        var finished = state is RunState.Skipped ? now : null;
        return _writer.ExecuteControlAsync((c, tx) =>
        {
            Exec(c, tx, "INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at,started_at,finished_at,kb_code) VALUES ($i,$j,$c,$s,$a,$t,$st,$f,$k);",
                ("$i", Convert.ToHexStringLower(runId)), ("$j", jobId), ("$c", cause), ("$s", state.ToString()), ("$a", attempt), ("$t", now),
                ("$st", state == RunState.Running ? now : null), ("$f", finished), ("$k", state == RunState.Skipped ? skipCode ?? KbCode.JobDisabled : null));
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc) VALUES ($i,0,NULL,$s,$t);",
                ("$i", Convert.ToHexStringLower(runId)), ("$s", state.ToString()), ("$t", now));
            return 0;
        }, ct);
    }

    private Task<int> StartQueuedAsync(string runIdHex, CancellationToken ct)
    {
        var now = Iso(_time.GetUtcNow());
        return _writer.ExecuteControlAsync((c, tx) =>
        {
            Exec(c, tx, "UPDATE run_runs SET state='Running', started_at=$t WHERE run_id=$i AND state='Queued';", ("$t", now), ("$i", runIdHex));
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc) VALUES ($i,(SELECT COALESCE(MAX(seq)+1,0) FROM run_transitions WHERE run_id=$i),'Queued','Running',$t);", ("$i", runIdHex), ("$t", now));
            return 0;
        }, ct);
    }

    private Task<int> SkipQueuedAsync(string runIdHex, string code, CancellationToken ct) => EndQueuedAsync(runIdHex, RunState.Skipped, code, ct);

    /// <summary>Kończy uruchomienie, które nie wyszło z kolejki (Skipped/Cancelled); przejście tylko ze stanu Queued (bez nadpisania stanu końcowego).</summary>
    private Task<int> EndQueuedAsync(string runIdHex, RunState state, string? code, CancellationToken ct)
    {
        var now = Iso(_time.GetUtcNow());
        return _writer.ExecuteControlAsync((c, tx) =>
        {
            using var upd = c.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE run_runs SET state=$s, finished_at=$t, kb_code=$k WHERE run_id=$i AND state='Queued';";
            upd.Parameters.AddWithValue("$s", state.ToString());
            upd.Parameters.AddWithValue("$t", now);
            upd.Parameters.AddWithValue("$k", (object?)code ?? DBNull.Value);
            upd.Parameters.AddWithValue("$i", runIdHex);
            if (upd.ExecuteNonQuery() == 0) return 0;
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,kb_code) VALUES ($i,(SELECT COALESCE(MAX(seq)+1,0) FROM run_transitions WHERE run_id=$i),'Queued',$s,$t,$k);",
                ("$i", runIdHex), ("$s", state.ToString()), ("$t", now), ("$k", code));
            return 1;
        }, ct);
    }

    /// <summary>
    /// D9.6/D9.10 (it. 111): zdarzenie <c>pl.kobian.run.finished.v1</c> do kolejki <c>core_outbox</c> w TEJ SAMEJ transakcji co wynik uruchomienia
    /// (null = powiadomienia wyłączone). Wartość = tryb minimalny (bez nazwy zadania — SEC-9.6).
    /// </summary>
    public bool? NotifyMinimal { get; init; }

    /// <summary>
    /// BL-45: kanały powiadomień o zakończonym uruchomieniu (webhook, e-mail) — każdy dostaje własny wpis outboxa z ładunkiem w swoim trybie minimalnym.
    /// Gdy null, obowiązuje <see cref="NotifyMinimal"/> (sam webhook).
    /// </summary>
    public IReadOnlyList<Kobian.Agent.Notifications.NotifyChannel>? NotifyChannels { get; init; }

    private IReadOnlyList<Kobian.Agent.Notifications.NotifyChannel> EffectiveChannels =>
        NotifyChannels ?? (NotifyMinimal is { } m ? [new Kobian.Agent.Notifications.NotifyChannel(Kobian.Agent.Notifications.OutboxDispatcher.WebhookChannel, m)] : []);

    private Task<int> FinishRunAsync(byte[] runId, RunState state, string? code, string? snapshotId, SnapshotStats stats, CancellationToken ct)
    {
        var nowUtc = _time.GetUtcNow();
        var now = Iso(nowUtc);
        return _writer.ExecuteControlAsync((c, tx) =>
        {
            var id = Convert.ToHexStringLower(runId);
            if (EffectiveChannels is { Count: > 0 } channels) EnqueueFinished(c, tx, _hostId, id, state, code, snapshotId, stats, nowUtc, channels);
            Exec(c, tx, "UPDATE run_runs SET state=$s, finished_at=$t, kb_code=$k, snapshot_id=$n, bytes_read=$br, bytes_uploaded=$bu, files=$f WHERE run_id=$i;",
                ("$s", state.ToString()), ("$t", now), ("$k", code), ("$n", snapshotId), ("$br", stats.BytesRaw), ("$bu", stats.BytesNew), ("$f", stats.Files), ("$i", id));
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,kb_code) VALUES ($i,(SELECT COALESCE(MAX(seq)+1,0) FROM run_transitions WHERE run_id=$i),'Running',$s,$t,$k);",
                ("$i", id), ("$s", state.ToString()), ("$t", now), ("$k", code));
            return 0;
        }, ct);
    }

    /// <summary>
    /// Zdarzenie run.finished do outboxa w transakcji wyniku (wspólne dla kopii i weryfikacji V3 z harmonogramu — <see cref="CatalogCheckService"/>):
    /// jeden identyfikator zdarzenia, ładunek per kanał w jego trybie minimalnym.
    /// </summary>
    internal static void EnqueueFinished(SqliteConnection c, SqliteTransaction tx, byte[] hostId, string runHex, RunState state, string? code, string? snapshotId, SnapshotStats stats, DateTimeOffset now, IReadOnlyList<Kobian.Agent.Notifications.NotifyChannel> channels)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT r.job_id, j.name, r.attempt, r.cause, r.started_at FROM run_runs r JOIN job_jobs j ON j.job_id = r.job_id WHERE r.run_id = $i;";
        q.Parameters.AddWithValue("$i", runHex);
        using var rd = q.ExecuteReader();
        if (!rd.Read()) return;
        var (jobId, jobName, attempt, cause) = (rd.GetString(0), rd.GetString(1), rd.GetInt32(2), rd.GetString(3));
        var started = rd.IsDBNull(4) ? now : DateTimeOffset.Parse(rd.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        rd.Close();
        var status = state switch
        {
            RunState.Succeeded => "succeeded",
            RunState.Warning => "warning",
            RunState.Cancelled => "cancelled",
            RunState.Skipped => "skipped",
            _ => "failed",
        };
        if (cause is not ("schedule" or "manual" or "misfire" or "retry" or "resume" or "event")) cause = "manual";
        // Jedno zdarzenie (wspólny id — deduplikacja), ładunek per kanał w jego trybie minimalnym.
        var eventId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var finished = new Kobian.Notifications.Events.RunFinished(runHex, jobId, jobName, status, code, attempt, cause, started > now ? now : started, now,
            stats.Files, stats.BytesRaw, stats.BytesNew, 0, snapshotId);
        foreach (var ch in channels)
        {
            if (!ch.Accepts(status)) continue;
            var payload = Kobian.Notifications.Events.RunFinishedEvent.Build(eventId, Convert.ToHexStringLower(hostId), finished, ch.Minimal);
            Kobian.Agent.Notifications.OutboxDispatcher.Enqueue(c, tx, ch.Name, payload, now);
        }
    }

    private Task<int> InsertSnapshotAsync(SnapshotObject s, string repoHex, string jobId, CancellationToken ct) =>
        _writer.ExecuteControlAsync((c, tx) =>
        {
            Exec(c, tx, "INSERT INTO snap_snapshots(snap_id,repo_id,job_id,run_id,host_id,time_utc,mode,base_full,parent,root_tree,files,bytes_raw,bytes_new) VALUES ($i,$r,$j,$ru,$h,$t,$m,$bf,$p,$rt,$f,$br,$bn);",
                ("$i", Convert.ToHexStringLower(s.SnapId)), ("$r", repoHex), ("$j", jobId), ("$ru", Convert.ToHexStringLower(s.RunId)),
                ("$h", Convert.ToHexStringLower(s.HostId)), ("$t", Iso(s.Time)), ("$m", (int)s.Mode),
                ("$bf", s.BaseFullSnapId is null ? null : Convert.ToHexStringLower(s.BaseFullSnapId)),
                ("$p", s.ParentSnapId is null ? null : Convert.ToHexStringLower(s.ParentSnapId)),
                ("$rt", s.RootTreeId), ("$f", s.Stats.Files), ("$br", s.Stats.BytesRaw), ("$bn", s.Stats.BytesNew));
            return 0;
        }, ct);

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
