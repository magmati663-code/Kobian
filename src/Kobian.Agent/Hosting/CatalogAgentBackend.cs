using System.Globalization;
using Grpc.Core;
using Kobian.Agent.Jobs;
using Kobian.Api.Contracts;
using Kobian.Api.V1;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Microsoft.Data.Sqlite;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Backend Agenta nad katalogiem SQLite (odczyt: status i snapshoty). RunJob działa przez <see cref="CatalogJobService"/> (gdy podany); Restore działa przez <see cref="CatalogRestoreService"/> (gdy podany), inaczej KB-7012.
/// Uczciwie: nie udajemy wykonania.
/// </summary>
public sealed class CatalogAgentBackend(string catalogPath, string agentVersion, CatalogJobService? jobs = null, RepoInitService? init = null, CatalogRestoreService? restore = null, ScheduleService? schedules = null, CatalogCheckService? check = null, TimeProvider? time = null, DestinationProbeService? probe = null, JobDraftValidator? validator = null, CatalogRetentionService? retention = null, CatalogPruneService? prune = null, RetentionHoldService? hold = null) : IAgentBackend
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Stan zadań dla pulpitu (BL-60): także wyłączone (<c>disabled</c>), następne uruchomienie i cykl, ostatnia nieuszkodzona kopia z poziomem weryfikacji, schemat celu.</summary>
    public Task<GetStatusResponse> GetStatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var status = CatalogStatusReader.ReadStatus(catalogPath, agentVersion, _time.GetUtcNow());
        if (RuntimeCode is { } rc)
        {
            status.RuntimeCode = rc; // BL-62: niezałatany .NET — ostrzeżenie dla administratora
            status.RuntimeVersion = Environment.Version.ToString();
            if (status.State == OverallState.Ok) status.State = OverallState.Warning;
        }
        return Task.FromResult(status);
    }

    /// <summary>Cele (SCR-09, it. 82): z definicji zadań, pogrupowane po schemacie i korzeniu.</summary>
    public Task<ListDestinationsReply> ListDestinationsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CatalogStatusReader.ReadDestinations(catalogPath));
    }

    /// <summary>Historia uruchomień (BL-60, SCR-10) — także nieudane, pominięte, restore i check.</summary>
    public Task<ListRunsResponse> ListRunsAsync(string? job, int? limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CatalogStatusReader.ReadRuns(catalogPath, job, limit));
    }

    /// <summary>Odstęp odpytywania katalogu przez <see cref="WatchRunAsync"/> (testy skracają).</summary>
    public TimeSpan WatchInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Górna granica czasu jednego strumienia — klient po niej wznawia (limit zasobów Agenta, DoS). PROWIZORYCZNIE 12 h.</summary>
    public TimeSpan WatchMaxDuration { get; init; } = TimeSpan.FromHours(12);

    /// <summary>
    /// SCR-07 (it. 134): zmiany jednego uruchomienia z katalogu (odczyt tylko-do-odczytu, bez blokowania zapisów). Wysyła stan początkowy, potem wyłącznie
    /// zmiany (stan, liczniki), a po stanie końcowym wiadomość <c>final</c>. Nieznany bieg → jedna wiadomość final z KB-0107.
    /// </summary>
    public async IAsyncEnumerable<RunProgress> WatchRunAsync(string runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + WatchMaxDuration;
        (string State, long Files, long Uploaded, long Read, string? Code)? last = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var found = CatalogStatusReader.ReadRun(catalogPath, runId);
            if (found is null)
            {
                yield return new RunProgress { Final = true, Run = new RunInfo { RunId = runId.Length <= 64 ? runId : string.Empty, State = "Failed", Result = OverallState.Failed, Code = Kobian.Domain.KbCode.RunNotFound } };
                yield break;
            }
            var (run, bytesRead) = found.Value;
            var terminal = CatalogStatusReader.IsTerminal(run.State);
            var now = (run.State, run.HasFiles ? run.Files : 0L, run.HasBytesUploaded ? run.BytesUploaded : 0L, bytesRead, run.HasCode ? run.Code : null);
            if (terminal || last is null || last.Value != now)
            {
                last = now;
                yield return new RunProgress { Run = run, BytesRead = bytesRead, Final = terminal };
                if (terminal) yield break;
            }
            if (DateTime.UtcNow >= deadline) yield break;
            await Task.Delay(WatchInterval, ct).ConfigureAwait(false);
        }
    }

    public Task<ListSnapshotsResponse> ListSnapshotsAsync(string? job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = new ListSnapshotsResponse();
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.snap_id, COALESCE(j.name, s.job_id), s.time_utc, s.mode, COALESCE(s.files, 0), COALESCE(s.bytes_raw, 0), COALESCE(s.damage, ''), COALESCE(s.damaged_files, 0)
            FROM snap_snapshots s LEFT JOIN job_jobs j ON j.job_id = s.job_id
            WHERE s.forgotten_at IS NULL AND ($job IS NULL OR j.name = $job)
            ORDER BY s.time_utc DESC LIMIT 1000;
            """;
        cmd.Parameters.AddWithValue("$job", (object?)job ?? DBNull.Value);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            response.Snapshots.Add(new Api.V1.SnapshotInfo
            {
                Id = rd.GetString(0),
                Job = rd.GetString(1),
                TimeUnixSeconds = DateTimeOffset.Parse(rd.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds(),
                Mode = ModeName(rd.GetInt32(3)),
                Files = rd.GetInt64(4),
                BytesRaw = rd.GetInt64(5),
                Damage = rd.GetString(6),
                DamagedFiles = rd.GetInt64(7),
            });
        }
        return Task.FromResult(response);
    }

    /// <summary>
    /// Uruchomienie zadania (BL-15). <c>wait=true</c>: czeka na wynik; <c>wait=false</c>: start w tle, od razu OK (wynik w status/snapshots; błąd startu — KB-0104).
    /// Bez <see cref="CatalogJobService"/> (host tylko do odczytu) → KB-7012.
    /// </summary>
    public async Task<RunOutcome> RunJobAsync(string job, bool wait, CancellationToken ct)
    {
        if (jobs is null) return NotAvailable();
        if (wait) return ToProto(await jobs.RunAsync(job, ct).ConfigureAwait(false));
        _ = Task.Run(() => jobs.RunAsync(job, CancellationToken.None), CancellationToken.None)
            .ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); // wynik w katalogu; wyjątek obserwowany
        return new RunOutcome { Result = OverallState.Ok };
    }


    /// <summary>
    /// Restore snapshotu do istniejącego katalogu (BL-43): zawsze czeka na wynik, nigdy nie nadpisuje istniejących plików (KB-5105 → Warning).
    /// Bez <see cref="CatalogRestoreService"/> (host tylko do odczytu) → KB-7012.
    /// </summary>
    public async Task<RunOutcome> RestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct) =>
        restore is null ? NotAvailable() : ToProto(await restore.RestoreAsync(snapshot, pathFilter, target, ct).ConfigureAwait(false));

    /// <summary>RD-81: start odtworzenia w tle (detach=true) — od razu run_id; błąd walidacji celu/snapshotu = Failed z kodem KB, bez run_id. Bez usługi → KB-7012.</summary>
    public async Task<RunOutcome> RestoreDetachedAsync(string snapshot, string? pathFilter, IReadOnlyList<string> exactPaths, string target, CancellationToken ct)
    {
        if (restore is null) return NotAvailable();
        var (runId, failure) = await restore.StartRestoreAsync(snapshot, pathFilter, exactPaths.Count == 0 ? null : exactPaths, target, ct).ConfigureAwait(false);
        if (failure is not null) return ToProto(failure);
        return new RunOutcome { Result = OverallState.Ok, RunId = runId };
    }

    /// <summary>Odtworzenie bazy (BL-68, SCR-08): przez CatalogRestoreService z fabryką sesji SQL; bez niej → KB-7012.</summary>
    public async Task<RestoreDatabaseReply> RestoreDatabaseAsync(RestoreDatabaseArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (restore is null) return new RestoreDatabaseReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        var r = await restore.RestoreDatabaseAsync(args.Snapshot, args.HasTargetDatabase ? args.TargetDatabase : null, ct).ConfigureAwait(false);
        var reply = new RestoreDatabaseReply { Result = r.Outcome.Result switch { JobResult.Succeeded => OverallState.Ok, JobResult.Warning => OverallState.Warning, _ => OverallState.Failed }, TargetDatabase = r.TargetDatabase ?? "", SnapshotId = r.Outcome.SnapshotId ?? "" };
        if (r.Outcome.Code is { } c) reply.Code = c;
        return reply;
    }

    /// <summary>Podgląd odtworzenia (SCR-07, R2.8.1, it. 98): ten sam wybór snapshotu/ścieżek co Restore, bez zapisu danych; bez usługi → KB-7012.</summary>
    public async Task<RestorePlanReply> PlanRestoreAsync(RestoreRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (restore is null) return new RestorePlanReply { Code = KbCode.OperationNotAvailable };
        var o = await restore.PlanAsync(request.Snapshot, request.HasPathFilter ? request.PathFilter : null,
            request.ExactPaths.Count == 0 ? null : [.. request.ExactPaths], request.Target, ct).ConfigureAwait(false);
        var reply = new RestorePlanReply { SnapshotId = o.SnapshotId ?? "" };
        if (o.Code is { } c) reply.Code = c;
        if (o.Plan is { } p)
        {
            reply.Files = p.Files;
            reply.Bytes = p.Bytes;
            reply.NewDirectories = p.NewDirectories;
            reply.Conflicts = p.Conflicts;
            reply.ConflictBytes = p.ConflictBytes;
            reply.ConflictSample.AddRange(p.ConflictSample);
            reply.InvalidNames = p.InvalidNames;
            reply.Links = p.Links;
            reply.MissingBlocks = p.MissingBlocks;
            reply.Packs = p.Packs;
            reply.ReadBytes = p.ReadBytes;
            if (p.FreeBytes is { } f) reply.FreeBytes = f;
            if (p.BlockingCode is { } b) reply.BlockingCode = b;
            reply.Resumable = p.Resumable;
        }
        return reply;
    }

    /// <summary>Restore dokładnych ścieżek (SCR-07, it. 80): plik albo folder z zawartością, bez wzorców.</summary>
    public async Task<RunOutcome> RestoreExactAsync(string snapshot, IReadOnlyList<string> exactPaths, string target, CancellationToken ct) =>
        restore is null ? NotAvailable() : ToProto(await restore.RestoreExactAsync(snapshot, exactPaths, target, ct).ConfigureAwait(false));

    /// <summary>Przeglądarka kopii (BL-63, SCR-06): folder snapshotu przez <see cref="CatalogRestoreService"/> (ten sam wybór snapshotu i dostęp do celu co restore); bez niej → KB-7012.</summary>
    public async Task<BrowseTreeReply> BrowseTreeAsync(BrowseTreeArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (restore is null) return new BrowseTreeReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        var o = await restore.BrowseAsync(args.Snapshot, args.Path, ct).ConfigureAwait(false);
        var reply = new BrowseTreeReply { Result = o.Listing is null ? OverallState.Failed : OverallState.Ok, SnapshotId = o.SnapshotId ?? "" };
        if (o.Code is not null) reply.Code = o.Code;
        if (o.Listing is { } l)
        {
            reply.Path = l.Path;
            reply.Truncated = l.Truncated;
            foreach (var e in l.Entries)
            {
                var info = new BrowseEntryInfo
                {
                    Name = e.Name,
                    Size = e.Size,
                    Kind = e.Kind switch
                    {
                        Kobian.Engine.Restore.BrowseEntryKind.Directory => Api.V1.BrowseEntryKind.Directory,
                        Kobian.Engine.Restore.BrowseEntryKind.File => Api.V1.BrowseEntryKind.File,
                        _ => Api.V1.BrowseEntryKind.Link,
                    },
                };
                if (e.ModifiedUtc is { } m) info.ModifiedUnixMs = m.ToUnixTimeMilliseconds();
                reply.Entries.Add(info);
            }
        }
        return reply;
    }

    /// <summary>
    /// Inicjalizacja repozytorium i zadania (BL-24). Błędy domenowe → kod KB w odpowiedzi (komunikaty bez sekretów); bez <see cref="RepoInitService"/> → KB-7012.
    /// Identyfikatory w odpowiedzi nie zawierają materiału tajnego. Hasło czytane z pliku po stronie Agenta.
    /// </summary>
    public async Task<InitJobReply> InitJobAsync(InitJobArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (init is null) return new InitJobReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        try
        {
            var r = await init.InitAsync(new InitJobRequest(args.JobName, args.Source, args.DestinationScheme, args.DestinationRoot, args.PasswordFile,
                args.HasPackTargetSize ? args.PackTargetSize : null, RemoteOf(args)), ct).ConfigureAwait(false);
            return new InitJobReply { Result = OverallState.Ok, JobId = r.JobId, RepoId = r.RepoIdHex, KeyId = r.KeyIdHex };
        }
        catch (KobianException ex)
        {
            return new InitJobReply { Result = OverallState.Failed, Code = ex.Code };
        }
        catch (StorageException ex)
        {
            return new InitJobReply { Result = OverallState.Failed, Code = ex.Code };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InitJobReply { Result = OverallState.Failed, Code = KbCode.SourcesNotReadable };
        }
    }

    /// <summary>Harmonogramy zadań (RD-27): odczyt zawsze z katalogu; wyłącznie dane konfiguracji, bez sekretów.</summary>
    public Task<ListSchedulesResponse> ListSchedulesAsync(string? job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = new ListSchedulesResponse();
        foreach (var s in ScheduleService.List(catalogPath, job))
            response.Schedules.Add(new ScheduleInfo { Job = s.Job, TriggerId = s.TriggerId, Cron = s.Cron, TimeZone = s.TimeZone, Enabled = s.Enabled });
        return Task.FromResult(response);
    }

    /// <summary>Ustawienie (cron) lub wyłączenie (brak cron) harmonogramu zadania; błędy domenowe → kod KB w odpowiedzi; bez <see cref="ScheduleService"/> → KB-7012.</summary>
    public async Task<SetScheduleReply> SetScheduleAsync(SetScheduleArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (schedules is null) return new SetScheduleReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        var r = args.HasCron ? await schedules.SetAsync(args.Job, args.Cron, ct).ConfigureAwait(false) : await schedules.ClearAsync(args.Job, ct).ConfigureAwait(false);
        var reply = new SetScheduleReply { Result = r.Code is null ? OverallState.Ok : OverallState.Failed };
        if (r.Code is not null) reply.Code = r.Code;
        if (r.TriggerId is not null) reply.TriggerId = r.TriggerId;
        return reply;
    }

    /// <summary>Weryfikacja repozytorium zadania (BL-26): zawsze czeka na wynik; bez <see cref="CatalogCheckService"/> → KB-7012. Znaleziska obcięte do 100.</summary>
    public async Task<CheckReply> CheckAsync(CheckArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (check is null) return new CheckReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        var o = await check.CheckAsync(args.Job, args.HasSamplePercent ? args.SamplePercent : 0, args.HasRotation ? args.Rotation : null, args.Repair, ct).ConfigureAwait(false);
        var reply = new CheckReply { Result = o.Status switch { CheckStatus.Succeeded => OverallState.Ok, CheckStatus.Warning => OverallState.Warning, _ => OverallState.Failed } };
        if (o.Code is not null) reply.Code = o.Code;
        if (o.Report is { } r)
        {
            reply.Level = r.Level;
            reply.Snapshots = r.Snapshots;
            reply.Packs = r.Packs;
            reply.PacksRead = r.PacksRead;
            reply.BlocksVerified = r.BlocksVerified;
            reply.DamagedSnapshots.Add(r.DamagedSnapshots);
            reply.Issues = r.Issues.Count;
            foreach (var i in r.Issues.Take(100)) reply.Findings.Add(new CheckFinding { Code = i.Code, Target = i.Target });
        }
        foreach (var d in o.Damage ?? [])
        {
            reply.Damage.Add(new SnapshotDamageInfo
            {
                SnapshotId = d.SnapshotId,
                State = d.State switch { Kobian.Engine.Repo.SnapshotDamageState.Partial => "partial", Kobian.Engine.Repo.SnapshotDamageState.Unrecoverable => "unrecoverable", _ => "intact" },
                DamagedFiles = d.DamagedFiles,
                DamagedDirectories = d.DamagedDirectories,
                IntactFiles = d.IntactFiles,
                IndexRecovered = d.IndexRecovered,
            });
        }
        if (o.Recovery is { } rec)
        {
            reply.RecoveredPacks = rec.RecoveredPacks;
            reply.UnreadablePacks = rec.UnreadablePacks;
        }
        return reply;
    }

    /// <summary>ProbeDestination (BL-60): bez <see cref="DestinationProbeService"/> → KB-7012. Odpowiedź zawiera tylko publiczny odcisk i liczby.</summary>
    public async Task<ProbeDestinationReply> ProbeDestinationAsync(ProbeDestinationArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (probe is null) return new ProbeDestinationReply { Status = ProbeStatus.Failed, Code = KbCode.OperationNotAvailable };
        if (args.Destination is null) return new ProbeDestinationReply { Status = ProbeStatus.Failed, Code = KbCode.JobDefinitionInvalid };
        var r = await probe.ProbeAsync(ToRequest(args.Destination), ct).ConfigureAwait(false);
        var reply = new ProbeDestinationReply
        {
            Status = r.Status switch { DestinationProbeStatus.Ok => ProbeStatus.Ok, DestinationProbeStatus.UnknownIdentity => ProbeStatus.UnknownIdentity, _ => ProbeStatus.Failed },
            RepositoryExists = r.RepositoryExists,
        };
        if (r.Code is not null) reply.Code = r.Code;
        if (r.Identity is { } id)
        {
            reply.IdentityKind = id.Kind;
            reply.IdentityFingerprint = id.Fingerprint;
        }
        if (r.FreeBytes is { } f) reply.FreeBytes = f;
        if (r.TotalBytes is { } t) reply.TotalBytes = t;
        return reply;
    }

    /// <summary>
    /// UpsertJob (BL-60, D6.7). validate_only: walidacja pól nowego zadania (kody KB per pole).
    /// Zapis (validate_only=false) dotyczy zadania utworzonego przez InitRepository: włącza je i ustawia/wyłącza harmonogram (Qb24).
    /// Zapis nieistniejącego zadania → KB-0104 (nowe zadanie powstaje razem z repozytorium); zmiana źródła/celu istniejącego zadania — jeszcze nie (pola ignorowane, PROWIZORYCZNIE).
    /// </summary>
    public async Task<UpsertJobReply> UpsertJobAsync(UpsertJobArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ct.ThrowIfCancellationRequested();
        if (validator is null || (!args.ValidateOnly && schedules is null)) return new UpsertJobReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        var reply = new UpsertJobReply();
        if (args.ValidateOnly)
        {
            foreach (var p in validator.Validate(ToDraft(args.JobName, args.Sources, args.Destination, args.HasCron ? args.Cron : null, args.Database)))
                reply.Issues.Add(new FieldIssue { Field = p.Field, Code = p.Code });
        }
        else if (args.HasCron && !validator.ValidCron(args.Cron))
        {
            reply.Issues.Add(new FieldIssue { Field = "schedule", Code = KbCode.ScheduleInvalid });
        }
        var policy = args.Retention is null ? null : ToPolicy(args.Retention);
        if (policy is not null && (OutOfRange(args.Retention!) || JobValidator.ValidateRetention(policy).Count > 0)) // zakres przed walidacją (it. 86)
            reply.Issues.Add(new FieldIssue { Field = "retention", Code = KbCode.RetentionInvalid });
        if (reply.Issues.Count > 0) return Fail(reply, reply.Issues[0].Code);
        if (args.ValidateOnly) return reply; // Result = Ok

        var sched = schedules!;
        var code = policy is null ? null : await sched.SetRetentionAsync(args.JobName, policy, ct).ConfigureAwait(false);
        code ??= await sched.EnableJobAsync(args.JobName, ct).ConfigureAwait(false);
        if (code is null)
        {
            var s = args.HasCron ? await sched.SetAsync(args.JobName, args.Cron, ct).ConfigureAwait(false) : await sched.ClearAsync(args.JobName, ct).ConfigureAwait(false);
            code = s.Code;
        }
        return code is null ? reply : Fail(reply, code);
    }

    /// <summary>
    /// InitRepository (Qb20/Qb24): walidacja jak UpsertJob + hasło (SEC-6.5) → repozytorium na celu (opcjonalnie z kluczem odzyskiwania) + zadanie WYŁĄCZONE.
    /// Hasło nie trafia do odpowiedzi, logów ani komunikatów; klucz odzyskiwania wraca jednorazowo.
    /// </summary>
    public async Task<InitRepositoryReply> InitRepositoryAsync(InitRepositoryArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        var reply = new InitRepositoryReply();
        if (validator is null || init is null) return Fail(reply, KbCode.OperationNotAvailable);
        var draft = ToDraft(args.JobName, args.Sources, args.Destination, args.HasCron ? args.Cron : null, args.Database);
        foreach (var p in validator.Validate(draft)) reply.Issues.Add(new FieldIssue { Field = p.Field, Code = p.Code });
        foreach (var p in JobValidator.ValidatePassword(args.Password, null, Environment.MachineName, CommonPasswords))
            reply.Issues.Add(new FieldIssue { Field = "password", Code = p.KbCode });
        var policy = args.Retention is null ? null : ToPolicy(args.Retention);
        if (policy is not null && (OutOfRange(args.Retention!) || JobValidator.ValidateRetention(policy).Count > 0)) // zakres przed walidacją (it. 86)
            reply.Issues.Add(new FieldIssue { Field = "retention", Code = KbCode.RetentionInvalid });
        if (reply.Issues.Count > 0) return Fail(reply, reply.Issues[0].Code);
        try
        {
            DatabaseInit? db = null;
            if (draft.Database is { } dd)
            {
                var (dbCode, options) = validator.ResolveDatabase(dd); // Validate wyżej przeszło — tu tylko opcje połączenia (pin jako pełna ścieżka w certs)
                if (dbCode is not null) return Fail(reply, dbCode);
                db = new DatabaseInit(options!, dd.Server.SecretRef, dd.Name);
            }
            var r = await init.InitFromWizardAsync(new WizardInitRequest(args.JobName, db is null ? draft.Sources[0] : "", draft.Destination.Scheme, draft.Destination.Root, args.Password,
                args.CreateRecoveryKey, JobDraftValidator.DefaultTimeZone, draft.Cron, policy, db, RemoteOf(draft.Destination)), ct).ConfigureAwait(false);
            reply.JobId = r.JobId;
            reply.RepoId = r.RepoIdHex;
            if (r.RecoveryKey is not null) reply.RecoveryKey = r.RecoveryKey;
            return reply;
        }
        catch (KobianException ex)
        {
            if (ex.Code == KbCode.ObjectAlreadyExists) reply.Issues.Add(new FieldIssue { Field = "destination", Code = ex.Code });
            else if (ex.Code == KbCode.JobDefinitionInvalid && args.Database is not null) reply.Issues.Add(new FieldIssue { Field = "database", Code = ex.Code }); // np. staging Agenta nie jest ścieżką Windows (Qb31)
            return Fail(reply, ex.Code);
        }
        catch (StorageException ex)
        {
            reply.Issues.Add(new FieldIssue { Field = "destination", Code = ex.Code });
            return Fail(reply, ex.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(reply, KbCode.AccessDenied);
        }
    }

    /// <summary>Retencja zadania (BL-25): podgląd (dry_run) albo logiczne zapomnienie; bez <see cref="CatalogRetentionService"/> → KB-7012.</summary>
    public async Task<ApplyRetentionReply> ApplyRetentionAsync(ApplyRetentionArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (retention is null) return new ApplyRetentionReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable, DryRun = args.DryRun };
        var o = await retention.ApplyAsync(args.Job, args.DryRun, ct).ConfigureAwait(false);
        var reply = new ApplyRetentionReply
        {
            Result = o.Code is null ? OverallState.Ok : o.Held ? OverallState.Warning : OverallState.Failed,
            HasPolicy = o.HasPolicy, DryRun = o.DryRun, Forgotten = o.Forgotten, Held = o.Held,
        };
        if (o.Code is not null) reply.Code = o.Code;
        if (o.Plan is { } plan)
        {
            reply.Kept = plan.Keep.Count;
            foreach (var (id, why) in plan.Keep.OrderBy(k => k.Key, StringComparer.Ordinal)) reply.Items.Add(new RetentionItem { SnapshotId = id, Keep = true, Reasons = (uint)why });
            foreach (var id in plan.Forget) reply.Items.Add(new RetentionItem { SnapshotId = id, Keep = false });
            if (o.DryRun) reply.Forgotten = plan.Forget.Count; // podgląd: ile BYŁOBY zapomnianych
        }
        return reply;
    }

    /// <summary>Zwolnienie wstrzymania retencji (SEC-17.4; rola Administrator w MethodPolicy). Bez <see cref="RetentionHoldService"/> → KB-7012.</summary>
    public async Task<ReleaseRetentionHoldReply> ReleaseRetentionHoldAsync(CancellationToken ct)
    {
        if (hold is null) return new ReleaseRetentionHoldReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        return new ReleaseRetentionHoldReply { Result = OverallState.Ok, Released = await hold.ReleaseAsync(ct).ConfigureAwait(false) };
    }

    /// <summary>Prune dwufazowy (BL-25 cz. 4): liczby obiektów i bajtów, bez nazw plików (NFR-11). Bez <see cref="CatalogPruneService"/> z zapisem katalogu → KB-7012.</summary>
    public async Task<PruneReply> PruneAsync(PruneArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (prune is null) return new PruneReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        var o = await prune.PruneAsync(args.Job, ct).ConfigureAwait(false);
        var reply = new PruneReply
        {
            Result = o.Code switch
            {
                null => OverallState.Ok,
                KbCode.RepoLockInvalid or KbCode.PruneStoppedInconsistent or KbCode.RepoLocked or KbCode.IndexConsolidationIncomplete or KbCode.AlertClockJump => OverallState.Warning,
                _ => OverallState.Failed,
            },
            GraceSeconds = (long)prune.Grace.TotalSeconds,
            Marked = o.Marked,
            Unmarked = o.Unmarked,
            LocksCleaned = o.LocksCleaned,
            IndexesConsolidated = o.Consolidation?.Superseded ?? 0,
            IndexesWritten = o.Consolidation?.Written.Count ?? 0,
            ActiveIndexes = o.Consolidation?.ActiveAfter ?? 0,
        };
        if (o.Code is not null) reply.Code = o.Code;
        if (o.RunId is not null) reply.RunId = o.RunId;
        if ((o.Deleted?.StopObject ?? o.Plan?.BlockedObject) is { } stop) reply.StopObject = stop;
        if (o.Deleted is { } d)
        {
            reply.DeletedSnapshots = d.Deleted.Count(x => x.Kind == "snapshot");
            reply.DeletedIndexes = d.Deleted.Count(x => x.Kind == "index");
            reply.DeletedPacks = d.Deleted.Count(x => x.Kind == "pack");
            reply.DeletedBytes = d.DeletedBytes;
        }
        if (o.Plan is { } p) reply.Pending = p.Dead.Count - (o.Deleted?.Deleted.Count ?? 0);
        return reply;
    }

    /// <summary>Plan prune (BL-25 cz. 3, D3.7) — tylko podgląd; liczby obiektów i bajtów, bez nazw plików. Bez <see cref="CatalogPruneService"/> → KB-7012.</summary>
    public async Task<PlanPruneReply> PlanPruneAsync(PlanPruneArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (prune is null) return new PlanPruneReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        var o = await prune.PlanAsync(args.Job, ct).ConfigureAwait(false);
        var reply = new PlanPruneReply { Result = o.Code is null ? OverallState.Ok : OverallState.Failed, GraceSeconds = (long)PrunePlanner.DefaultGrace.TotalSeconds };
        if (o.Code is not null) reply.Code = o.Code;
        if (o.Plan is { } p)
        {
            if (p.BlockedObject is not null) reply.BlockedObject = p.BlockedObject;
            reply.LiveSnapshots = p.LiveSnapshots;
            reply.LivePacks = p.LivePacks;
            reply.LiveBytes = p.LiveBytes;
            reply.DeadSnapshots = p.Dead.Count(d => d.Kind == "snapshot");
            reply.DeadIndexes = p.Dead.Count(d => d.Kind == "index");
            reply.DeadPacks = p.Dead.Count(d => d.Kind == "pack");
            reply.DeadBytes = p.DeadBytes;
        }
        return reply;
    }

    /// <summary>
    /// Podgląd retencji (D3.12): wystąpienia harmonogramu z ostatnich ~3 lat (maks. 20 000; brak cron = codziennie 21:00) jako syntetyczne kopie → Keep().
    /// Wynik: liczba zachowanych i wiek najstarszej (dni). Bez katalogu i celu.
    /// </summary>
    public Task<PreviewRetentionReply> PreviewRetentionAsync(PreviewRetentionArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ct.ThrowIfCancellationRequested();
        var reply = new PreviewRetentionReply();
        var policy = args.Retention is null ? null : ToPolicy(args.Retention);
        if (policy is null || OutOfRange(args.Retention!) || JobValidator.ValidateRetention(policy).Count > 0) // zakres przed walidacją (it. 86)
            return Task.FromResult(Fail(reply, KbCode.RetentionInvalid));
        var cron = args.HasCron ? args.Cron : "0 21 * * *";
        Kobian.Scheduler.Timing.CronSchedule schedule;
        try
        {
            if (cron.Length > ScheduleService.MaxCronLength) throw new KobianException(KbCode.ScheduleInvalid, ErrorClass.Permanent, "Cron too long.");
            schedule = Kobian.Scheduler.Timing.CronSchedule.Parse(cron, JobDraftValidator.DefaultTimeZone);
        }
        catch (KobianException)
        {
            return Task.FromResult(Fail(reply, KbCode.ScheduleInvalid));
        }
        var now = _time.GetUtcNow();
        // Okno symulacji: ≤ 3 lata i ≤ ~20 000 wystąpień (częsty harmonogram = krótsze okno; Between ma własny limit 100 000 liczony od NAJSTARSZEGO).
        var next = schedule.Preview(now, 2);
        var gap = next.Count == 2 ? next[1] - next[0] : TimeSpan.FromDays(1);
        var window = TimeSpan.FromTicks(Math.Min(TimeSpan.FromDays(3 * 366).Ticks, Math.Max(TimeSpan.FromDays(1).Ticks, gap.Ticks * 20_000)));
        var occ = schedule.Between(now - window, now);
        var snaps = occ.Count > 20_000 ? occ.Skip(occ.Count - 20_000) : occ;
        var list = snaps.Select((t, i) => new Kobian.Engine.Retention.RetentionSnapshot("p" + i.ToString(CultureInfo.InvariantCulture), t)).ToList();
        var plan = Kobian.Engine.Retention.RetentionPlanner.Plan(list, policy, now, schedule.Zone);
        reply.Kept = plan.Keep.Count;
        if (plan.Keep.Count > 0)
            reply.OldestDays = (int)Math.Floor((now - list.Where(s => plan.Keep.ContainsKey(s.Id)).Min(s => s.TimeUtc)).TotalDays);
        return Task.FromResult(reply);
    }

    private static PreviewRetentionReply Fail(PreviewRetentionReply r, string code)
    {
        r.Result = OverallState.Failed;
        r.Code = code;
        return r;
    }

    /// <summary>Te same granice co parser definicji v1 (0…10 000) — inaczej zapisana definicja nie dałaby się później wczytać.</summary>
    internal static bool OutOfRange(RetentionSpec r) =>
        new[] { r.KeepLast, r.KeepDaily, r.KeepWeekly, r.KeepMonthly, r.KeepYearly, r.KeepWithinDays, r.LockDays }.Any(v => v is < 0 or > JobDefinition.MaxRetentionCount);

    internal static RetentionPolicy ToPolicy(RetentionSpec r) => new(r.KeepLast, r.KeepDaily, r.KeepWeekly, r.KeepMonthly, r.KeepYearly,
        TimeSpan.FromDays(Math.Clamp(r.KeepWithinDays, -1, JobDefinition.MaxRetentionCount)), TimeSpan.FromDays(Math.Clamp(r.LockDays, -1, JobDefinition.MaxRetentionCount)));

    private static readonly BuiltInCommonPasswords CommonPasswords = new();

    private static UpsertJobReply Fail(UpsertJobReply r, string code)
    {
        r.Result = OverallState.Failed;
        r.Code = code;
        return r;
    }

    private static InitRepositoryReply Fail(InitRepositoryReply r, string code)
    {
        r.Result = OverallState.Failed;
        r.Code = code;
        return r;
    }

    private static JobDraft ToDraft(string name, IEnumerable<string> sources, DestinationSpec? d, string? cron, DatabaseSourceSpec? db = null) =>
        new(name, [.. sources], d is null ? new DestinationRequest("", "") : ToRequest(d), cron, db is null ? null : ToDraft(db));

    internal static DatabaseDraft ToDraft(DatabaseSourceSpec db) => new(
        new DatabaseDiscoveryRequest(db.Server, db.HasAuth ? db.Auth : null, db.HasSecretRef ? db.SecretRef : null, db.HasCertificate ? db.Certificate : null,
            Strict: false, db.AllowLocalhostUntrusted),
        db.Name);

    /// <summary>Część zdalna celu z InitJob (null, gdy nic nie podano — cel lokalny).</summary>
    internal static RemoteDestination? RemoteOf(InitJobArgs a) =>
        a.HasSecretRef || a.HasHost || a.HasPort || a.HasHostKey
            ? new RemoteDestination(a.HasSecretRef ? a.SecretRef : null, a.HasHost ? a.Host : null, a.HasPort ? a.Port : null, a.HasHostKey ? a.HostKey : null)
            : null;

    /// <summary>Część zdalna celu ze szkicu kreatora (null dla celu bez poświadczenia i parametrów SFTP).</summary>
    internal static RemoteDestination? RemoteOf(DestinationRequest d) =>
        d.SecretRef is null && d.Host is null && d.Port is null && d.HostKey is null ? null : new RemoteDestination(d.SecretRef, d.Host, d.Port, d.HostKey);

    internal static DestinationRequest ToRequest(DestinationSpec d) => new(d.Scheme, d.Root, d.HasHost ? d.Host : null, d.HasPort ? d.Port : null,
        d.HasSecretRef ? d.SecretRef : null, d.HasHostKey ? d.HostKey : null);

    private static RunOutcome ToProto(JobOutcome o)
    {
        var r = new RunOutcome { Result = o.Result switch { JobResult.Succeeded => OverallState.Ok, JobResult.Warning => OverallState.Warning, _ => OverallState.Failed } };
        if (o.Code is not null) r.Code = o.Code;
        if (o.SnapshotId is not null) r.SnapshotId = o.SnapshotId;
        return r;
    }

    /// <summary>Eksport/import zadań (US-29, BL-47); ustawiane przez host (Program.cs). Brak → KB-7012.</summary>
    public JobTransferService? JobTransfer { get; init; }

    /// <summary>ExportJobs (US-29): dokument kanoniczny kobian-export v1, bez sekretów. Nieistniejąca nazwa → KB-0104, za duży → KB-0121.</summary>
    public Task<ExportJobsReply> ExportJobsAsync(ExportJobsArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ct.ThrowIfCancellationRequested();
        if (JobTransfer is null) return Task.FromResult(new ExportJobsReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable });
        var r = JobTransfer.Export([.. args.Jobs]);
        var reply = new ExportJobsReply { Result = r.Code is null ? OverallState.Ok : OverallState.Failed, Jobs = r.Jobs };
        if (r.Code is { } code) reply.Code = code;
        if (r.Document is { } d) reply.Document = Google.Protobuf.ByteString.CopyFrom(d);
        foreach (var p in r.Problems) reply.Issues.Add(new ImportIssue { Path = p.Path, Code = p.Code, Reason = p.Reason });
        return Task.FromResult(reply);
    }

    /// <summary>ImportJobs (US-29, AC-29.1–29.2, SEC-6.7): validate_only = plan; zapis wszystko albo nic, zadania WYŁĄCZONE.</summary>
    public async Task<ImportJobsReply> ImportJobsAsync(ImportJobsArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (JobTransfer is null) return new ImportJobsReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        var policy = args.OnConflict switch
        {
            ImportConflictMode.Skip => ImportConflictPolicy.Skip,
            ImportConflictMode.Overwrite => ImportConflictPolicy.Overwrite,
            ImportConflictMode.Rename => ImportConflictPolicy.Rename,
            _ => ImportConflictPolicy.Fail,
        };
        var r = await JobTransfer.ImportAsync(args.Document.ToByteArray(), args.ValidateOnly, policy, ct).ConfigureAwait(false);
        var reply = new ImportJobsReply { Result = r.Code is null ? OverallState.Ok : OverallState.Failed, Applied = r.Applied };
        if (r.Code is { } code) reply.Code = code;
        foreach (var p in r.Problems) reply.Issues.Add(new ImportIssue { Path = p.Path, Code = p.Code, Reason = p.Reason });
        foreach (var ch in r.Changes)
        {
            var c = new Api.V1.ImportChange { Job = ch.Job, Kind = ch.Kind, Action = ch.Action, NeedsCredentials = ch.NeedsCredentials, MissingKeyMaterial = ch.MissingKeyMaterial };
            if (ch.NewName is { } n) c.NewName = n;
            reply.Changes.Add(c);
        }
        return reply;
    }

    /// <summary>Discovery MSSQL (BL-40 cz. 3); ustawiane przez host (Program.cs). Brak → KB-7012.</summary>
    public DatabaseDiscoveryService? Discovery { get; init; }

    /// <summary>BL-62: wynik <see cref="RuntimeGuard.EvaluateCurrent"/> przy starcie (KB-7013) albo null.</summary>
    public string? RuntimeCode { get; init; }

    /// <summary>RD-42: łańcuch audytu (weryfikacja). Null → KB-7012.</summary>
    public Kobian.Agent.Audit.AuditLogService? Audit { get; init; }

    /// <summary>Weryfikacja łańcucha audytu (SEC-19.1): niezgodność → Failed KB-6501 z pierwszym złym seq.</summary>
    public Task<VerifyAuditReply> VerifyAuditAsync(CancellationToken ct) => VerifyAuditAsync(new VerifyAuditArgs(), ct);

    /// <summary>
    /// Weryfikacja (it. 99): schemat audytu (RD-51, KB-6502), łańcuch (KB-6501), kotwice z katalogu i kotwica zewnętrzna „seq:sha256hex” (D9.9, KB-6503).
    /// Zły format kotwicy → Failed KB-0103 bez weryfikacji.
    /// </summary>
    public Task<VerifyAuditReply> VerifyAuditAsync(VerifyAuditArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ct.ThrowIfCancellationRequested();
        if (Audit is null) return Task.FromResult(new VerifyAuditReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable });
        (long, byte[])? external = null;
        if (args.HasAnchor)
        {
            external = Kobian.Agent.Audit.AuditAnchor.TryParse(args.Anchor);
            if (external is null) return Task.FromResult(new VerifyAuditReply { Result = OverallState.Failed, Code = KbCode.JobDefinitionInvalid });
        }
        var v = Audit.Verify(external);
        var reply = new VerifyAuditReply
        {
            Result = v.Ok ? OverallState.Ok : OverallState.Failed,
            Entries = v.Entries,
            LastHash = v.LastHash is null ? "" : Convert.ToHexStringLower(v.LastHash),
            AnchorsChecked = v.AnchorsChecked,
        };
        if (v.Code is { } code) reply.Code = code;
        if (v.BrokenSeq is { } b) reply.BrokenSeq = b;
        if (v.LastAnchor is { } a)
        {
            reply.LastAnchor = a.Text;
            reply.LastAnchorAtUtc = a.AtUtc;
        }
        return Task.FromResult(reply);
    }

    /// <summary>Katalog tożsamości serwerów celów (BL-35, D4.11); ustawiany przez host. Brak → KB-7012.</summary>
    public ServerIdentityStore? Identities { get; init; }

    /// <summary>ListServerIdentities (SCR-09): oczekujące najpierw; stan „wygasłe” wyliczany z zegara Agenta (24 h).</summary>
    public Task<ListServerIdentitiesReply> ListServerIdentitiesAsync(ListServerIdentitiesArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ct.ThrowIfCancellationRequested();
        if (Identities is null) return Task.FromResult(new ListServerIdentitiesReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable });
        if (args.HasDestinationId && !ServerIdentityStore.IsDestinationId(args.DestinationId))
            return Task.FromResult(new ListServerIdentitiesReply { Result = OverallState.Failed, Code = KbCode.JobDefinitionInvalid });
        var now = Identities.Time.GetUtcNow();
        var (list, requirePin) = Identities.List(args.HasDestinationId ? args.DestinationId : null);
        var reply = new ListServerIdentitiesReply { Result = OverallState.Ok };
        foreach (var e in list)
        {
            var r = e.Row;
            var info = new ServerIdentityInfo
            {
                DestinationId = e.DestinationId,
                Kind = r.Kind,
                Fingerprint = r.Fingerprint,
                State = ToProto(r, now),
                Endpoint = r.Endpoint,
                FirstSeenUnixSeconds = r.FirstSeen.ToUnixTimeSeconds(),
                LastSeenUnixSeconds = r.LastSeen.ToUnixTimeSeconds(),
                TrustedByCa = r.State == IdentityState.Pinned && !r.IsEffectivePin,
            };
            if (r.Display is { } d) info.Display = d;
            if (r.DecidedBy is { } by) info.DecidedBy = by;
            if (r.DecidedAt is { } at) info.DecidedAtUnixSeconds = at.ToUnixTimeSeconds();
            reply.Identities.Add(info);
        }
        reply.RequirePinDestinations.Add(requirePin);
        return Task.FromResult(reply);
    }

    /// <summary>
    /// ApproveServerIdentity (D4.11, SEC-4.1): zatwierdzić/odrzucić można wyłącznie odcisk OCZEKUJĄCY dla tego celu, porównany dokładnie (porządkowo) —
    /// klient nie może wprowadzić dowolnego pinu (KB-2141). Rola Administrator sprawdzona w interceptorze; tu dodatkowo kształt danych (KB-0103).
    /// </summary>
    public async Task<ApproveServerIdentityReply> ApproveServerIdentityAsync(ApproveServerIdentityArgs args, string actorSid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Identities is null) return new ApproveServerIdentityReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        if (!ServerIdentityStore.IsDestinationId(args.DestinationId) || !ServerIdentityMachine.IsFingerprint(args.Fingerprint)
            || args.Decision is not (ServerIdentityDecision.Add or ServerIdentityDecision.Replace or ServerIdentityDecision.Reject))
            return new ApproveServerIdentityReply { Result = OverallState.Failed, Code = KbCode.JobDefinitionInvalid };
        ServerIdentityEvent ev = args.Decision == ServerIdentityDecision.Reject
            ? new ServerIdentityEvent.Reject(args.Fingerprint, actorSid)
            : new ServerIdentityEvent.Approve(args.Fingerprint, args.Decision == ServerIdentityDecision.Replace, actorSid);
        var t = await Identities.ApplyAsync(args.DestinationId, ev, ct).ConfigureAwait(false);
        if (t.Code is { } code) return new ApproveServerIdentityReply { Result = OverallState.Failed, Code = code };
        var row = t.Rows.First(r => string.Equals(r.Fingerprint, args.Fingerprint, StringComparison.Ordinal));
        return new ApproveServerIdentityReply { Result = OverallState.Ok, State = ToProto(row, Identities.Time.GetUtcNow()) };
    }

    /// <summary>Ewaluacja alertów hosta (BL-45); ustawiana przez host. Brak (np. błąd danych reguł, tryb awaryjny katalogu) → KB-7012.</summary>
    public Kobian.Agent.Notifications.AlertService? Alerts { get; init; }

    /// <summary>AckAlert (D9.7): potwierdzenie alertu w stanie „notified” (Operator+).</summary>
    public async Task<AlertActionReply> AckAlertAsync(AckAlertArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Alerts is null) return new AlertActionReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        if (!Kobian.Agent.Notifications.AlertService.IsAlertId(args.AlertId)) return new AlertActionReply { Result = OverallState.Failed, Code = KbCode.JobDefinitionInvalid };
        return ToReply(await Alerts.AcknowledgeAsync(args.AlertId, ct).ConfigureAwait(false));
    }

    /// <summary>SilenceAlert (D9.7): wyciszenie 1…10080 min (maszyna stanów skraca alerty bezpieczeństwa do 24 h) — Administrator.</summary>
    public async Task<AlertActionReply> SilenceAlertAsync(SilenceAlertArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Alerts is null) return new AlertActionReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        if (!Kobian.Agent.Notifications.AlertService.IsAlertId(args.AlertId) || args.DurationMinutes is < 1 or > 10080)
            return new AlertActionReply { Result = OverallState.Failed, Code = KbCode.JobDefinitionInvalid };
        return ToReply(await Alerts.SilenceAsync(args.AlertId, TimeSpan.FromMinutes(args.DurationMinutes), ct).ConfigureAwait(false));
    }

    private static AlertActionReply ToReply(Kobian.Agent.Notifications.AlertActionResult r) =>
        r.Code is { } code
            ? new AlertActionReply { Result = OverallState.Failed, Code = code }
            : new AlertActionReply { Result = OverallState.Ok, State = r.State };

    /// <summary>SetServerIdentityPolicy (SEC-14.5): „wysokie ryzyko” = pin wymagany mimo ważnego łańcucha CA.</summary>
    public async Task<SetServerIdentityPolicyReply> SetServerIdentityPolicyAsync(SetServerIdentityPolicyArgs args, string actorSid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Identities is null) return new SetServerIdentityPolicyReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        if (!ServerIdentityStore.IsDestinationId(args.DestinationId) || !ServerIdentityMachine.IsActor(actorSid))
            return new SetServerIdentityPolicyReply { Result = OverallState.Failed, Code = KbCode.JobDefinitionInvalid };
        await Identities.SetRequirePinAsync(args.DestinationId, args.RequirePin, actorSid, ct).ConfigureAwait(false);
        return new SetServerIdentityPolicyReply { Result = OverallState.Ok };
    }

    internal static Api.V1.ServerIdentityState ToProto(ServerIdentityRow r, DateTimeOffset now) => r.State switch
    {
        IdentityState.Pending => r.IsExpired(now) ? Api.V1.ServerIdentityState.Expired : Api.V1.ServerIdentityState.Pending,
        IdentityState.Pinned => Api.V1.ServerIdentityState.Pinned,
        IdentityState.Rejected => Api.V1.ServerIdentityState.Rejected,
        _ => Api.V1.ServerIdentityState.Retired,
    };

    /// <summary>Zatwierdzanie certyfikatów serwerów SQL (BL-40 cz. 5); ustawiane przez host. Brak → KB-7012.</summary>
    public ServerCertificateService? SqlCertificates { get; init; }

    /// <summary>FetchSqlCertificate: odcisk i dane certyfikatu do pokazania Operatorowi (bez zapisu). Nazwa serwera nie trafia do logów (NFR-11).</summary>
    public async Task<FetchSqlCertificateReply> FetchSqlCertificateAsync(FetchSqlCertificateArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (SqlCertificates is null) return new FetchSqlCertificateReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        var o = await SqlCertificates.FetchAsync(args.Server, args.Strict, ct).ConfigureAwait(false);
        if (o.Certificate is not { } c) return new FetchSqlCertificateReply { Result = OverallState.Failed, Code = o.Code ?? KbCode.Internal };
        return new FetchSqlCertificateReply
        {
            Result = OverallState.Ok,
            Sha256 = c.Sha256Hex,
            Subject = c.Subject,
            Issuer = c.Issuer,
            NotBeforeUnixSeconds = c.NotBefore.ToUnixTimeSeconds(),
            NotAfterUnixSeconds = c.NotAfter.ToUnixTimeSeconds(),
            SelfIssued = c.SelfIssued,
            SystemTrusted = c.SystemTrusted,
            SuggestedName = o.SuggestedName ?? "",
        };
    }

    /// <summary>ApproveSqlCertificate: ponowne pobranie, porównanie odcisku (KB-3007) i zapis pinu (KB-3008 przy innej treści pod tą nazwą).</summary>
    public async Task<ApproveSqlCertificateReply> ApproveSqlCertificateAsync(ApproveSqlCertificateArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (SqlCertificates is null) return new ApproveSqlCertificateReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        var a = await SqlCertificates.ApproveAsync(args.Server, args.Strict, args.Sha256, args.FileName, ct).ConfigureAwait(false);
        return a.FileName is { } name
            ? new ApproveSqlCertificateReply { Result = OverallState.Ok, FileName = name }
            : new ApproveSqlCertificateReply { Result = OverallState.Failed, Code = a.Code ?? KbCode.Internal };
    }

    /// <summary>
    /// DiscoverDatabases (BL-40 cz. 3, SCR-03 S1-db): lista baz z uprawnieniami i ostrzeżeniami KB. Nazwy baz trafiają wyłącznie do odpowiedzi
    /// dla uwierzytelnionego Operatora (pipe lokalny), nigdy do logów/zdarzeń (NFR-11). Bez nazwy hosta/instancji w odpowiedzi.
    /// </summary>
    public async Task<DiscoverDatabasesReply> DiscoverDatabasesAsync(DiscoverDatabasesArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Discovery is null) return new DiscoverDatabasesReply { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };
        var o = await Discovery.DiscoverAsync(new DatabaseDiscoveryRequest(
            args.Server,
            args.HasAuth ? args.Auth : null,
            args.HasSecretRef ? args.SecretRef : null,
            args.HasCertificate ? args.Certificate : null,
            args.Strict,
            args.AllowLocalhostUntrusted), ct).ConfigureAwait(false);
        if (o.Result is not { } r) return new DiscoverDatabasesReply { Result = OverallState.Failed, Code = o.Code ?? KbCode.Internal };
        var reply = new DiscoverDatabasesReply
        {
            Result = r.Warnings.Count > 0 ? OverallState.Warning : OverallState.Ok,
            ServerMajor = r.Server.Major,
            ServerVersion = r.Server.Version,
            ServerEdition = r.Server.Edition,
            Express = r.Server.Express,
            Support = r.Server.Support == Kobian.Providers.Sources.MsSql.SqlSupport.Conditional ? "conditional" : "supported",
            Truncated = r.Truncated,
        };
        reply.Warnings.AddRange(r.Warnings);
        foreach (var d in r.Databases)
        {
            var info = new SqlDatabaseInfo
            {
                Name = d.Name,
                RecoveryModel = d.RecoveryModel,
                DataBytes = d.DataBytes,
                ReadOnly = d.ReadOnly,
                Encrypted = d.Encrypted,
                CompatibilityLevel = d.CompatibilityLevel,
                CanBackup = d.CanBackup,
                CanVerifyOnly = d.CanVerifyOnly,
            };
            info.Warnings.AddRange(d.Warnings);
            reply.Databases.Add(info);
        }
        if (o.PermissionScript is { } script) reply.PermissionScript = script;
        return reply;
    }

    private static RunOutcome NotAvailable() => new() { Result = OverallState.Failed, Code = KbCode.OperationNotAvailable };

    internal static OverallState MapState(string state) => CatalogStatusReader.MapState(state);

    internal static string ModeName(int mode) => mode switch
    {
        0 => "full",
        1 => "incremental",
        2 => "differential",
        3 => "database",
        _ => "unknown",
    };
}
