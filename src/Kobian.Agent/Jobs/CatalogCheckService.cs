using System.Globalization;
using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Scheduler.Runs;

namespace Kobian.Agent.Jobs;

/// <summary>Wynik <c>check</c> w Agencie: <c>Code</c> = najgorszy kod (błąd przed ostrzeżeniem), <c>Report</c> = null, gdy weryfikacja się nie rozpoczęła (np. brak zadania).</summary>
public sealed record CheckOutcome(CheckStatus Status, string? Code, CheckReport? Report, IReadOnlyList<SnapshotDamage>? Damage = null, IndexRecoveryResult? Recovery = null);

/// <summary>Wynik cyklu V3 z harmonogramu: <c>Completed</c> = repozytorium sprawdzone (raport; rotacja przesunięta), <c>RunId</c> = wiersz run_runs (null = zadanie nie istnieje), <c>Rotation</c> = sprawdzony kubełek.</summary>
public sealed record VerifyCycleOutcome(bool Completed, string? RunId, long Rotation, CheckOutcome Outcome);

/// <summary>
/// <c>check</c> repozytorium zadania w Agencie (BL-26, 3.7 V2/V3): zadanie z katalogu (job_jobs po nazwie) → definicja → klucz (KeyFileLoader) →
/// dostawca celu z poświadczeniem Backup (tylko odczyt, D4.6) → <see cref="RepoChecker"/>. Każdy przebieg = wiersz run_runs (cause 'manual', reason 'check').
/// Rotacja V3 domyślnie = numer tygodnia ISO (rok·53 + tydzień), więc cotygodniowe przebiegi z tym samym % pokrywają wszystkie packi (3.7).
/// Snapshoty dotknięte błędem V2/V3 dostają w katalogu suspect=1 — następna kopia zadania jest pełna (3.7). V2 nowego snapshotu po każdym backupie robi <see cref="CatalogJobService"/>.
/// PROWIZORYCZNIE: alert A04 tylko jako kod KB-15xx w wyniku uruchomienia (wysyłka powiadomień — rola 9, BL-6x).
/// </summary>
public sealed class CatalogCheckService
{
    public const int MaxJobNameLength = 200;

    private readonly string _catalogPath;
    private readonly CatalogWriter _writer;
    private readonly IReadOnlyDictionary<string, IStorageProviderFactory> _providers;
    private readonly TimeProvider _time;

    public CatalogCheckService(string catalogPath, CatalogWriter writer, IReadOnlyDictionary<string, IStorageProviderFactory> providers, TimeProvider time)
    {
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Identyfikator hosta w odtworzonym indeksie (jak w indeksach kopii).</summary>
    public byte[] HostId { get; init; } = CatalogJobService.HostIdFor(Environment.MachineName);

    /// <summary>Rotacja V3 dla chwili <paramref name="utc"/>: rok ISO · 53 + tydzień ISO (kolejne tygodnie = kolejne kubełki).</summary>
    public static long RotationFor(DateTimeOffset utc) => ((long)ISOWeek.GetYear(utc.UtcDateTime) * 53) + ISOWeek.GetWeekOfYear(utc.UtcDateTime);

    public Task<CheckOutcome> CheckAsync(string job, int samplePercent, long? rotation, CancellationToken ct) => CheckAsync(job, samplePercent, rotation, repair: false, ct);

    /// <summary>
    /// <paramref name="repair"/> (D8.10, it. 109; PROWIZORYCZNIE Qb57): po check — ocena zakresu strat dotkniętych snapshotów (<see cref="RepoDamageAssessor"/>)
    /// i zapis w katalogu (snap_snapshots.damage/damaged_files, suspect=1 → następna kopia pełna). Repozytorium nie jest zmieniane, nic nie jest usuwane.
    /// </summary>
    public async Task<CheckOutcome> CheckAsync(string job, int samplePercent, long? rotation, bool repair, CancellationToken ct)
    {
        if (samplePercent is < 0 or > 100) return new CheckOutcome(CheckStatus.Failed, KbCode.JobDefinitionInvalid, null);
        if (string.IsNullOrWhiteSpace(job) || job.Length > MaxJobNameLength) return new CheckOutcome(CheckStatus.Failed, KbCode.JobNotFound, null);
        var row = FindJob(job);
        if (row is null) return new CheckOutcome(CheckStatus.Failed, KbCode.JobNotFound, null);
        var runId = RandomNumberGenerator.GetBytes(16);
        await InsertRunAsync(runId, row.Value.JobId, ct).ConfigureAwait(false);
        CheckOutcome outcome;
        try
        {
            var def = JobDefinition.Parse(row.Value.Type, row.Value.Definition);
            if (!_providers.TryGetValue(def.DestinationScheme, out var factory))
                throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Destination provider is not available.");
            using var keys = KeyFileLoader.Load(def.KeyFile, def.PasswordFile, def.RepoHeader);
            var destId = def.DestinationId;
            await using var session = await factory.OpenAsync(def.ToDestination(destId), CredentialRole.Backup, ct).ConfigureAwait(false);
            var repoHex = Convert.ToHexStringLower(keys.RepoId);
            var options = new CheckOptions(samplePercent, rotation ?? RotationFor(_time.GetUtcNow())) { KnownVerifiedPacks = samplePercent > 0 ? ReadVerifiedPacks(repoHex) : null };
            var report = await new RepoChecker(session, keys).CheckAsync(options, ct).ConfigureAwait(false);
            if (report.DamagedSnapshots.Count > 0) await MarkSuspectAsync(repoHex, report.DamagedSnapshots, ct).ConfigureAwait(false);
            if (report.Level >= 3) await RecordV3Async(repoHex, report, ct).ConfigureAwait(false); // BL-26: packi i verify_level 3 także z ręcznego check
            List<SnapshotDamage>? damage = null;
            IndexRecoveryResult? recovery = null;
            if (repair)
            {
                // it. 110 (D8.10/D3.10): packi-sieroty → nowy indeks „recovered” z trailerów (append-only), zanim ocenimy szkody
                recovery = await new RepoIndexRecovery(session, keys, HostId).RecoverAsync(write: true, ct).ConfigureAwait(false);
                var assessor = new RepoDamageAssessor(session, keys);
                var bad = RepoDamageAssessor.BadPacks(report);
                damage = [];
                foreach (var snap in report.DamagedSnapshots) damage.Add(await assessor.AssessAsync(snap, bad, ct).ConfigureAwait(false));
                await RecordDamageAsync(Convert.ToHexStringLower(keys.RepoId), damage, ct).ConfigureAwait(false);
            }
            outcome = new CheckOutcome(report.Status, WorstCode(report), report, damage, recovery);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishRunAsync(runId, RunState.Cancelled, null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (KobianException ex)
        {
            outcome = new CheckOutcome(CheckStatus.Failed, ex.Code, null);
        }
        catch (StorageException ex)
        {
            outcome = new CheckOutcome(CheckStatus.Failed, ex.Code, null);
        }
#pragma warning disable CA1031 // granica wykonawcy: nieoczekiwany wyjątek = wiersz Failed (KB-0001) i dalej
        catch
        {
            await FinishRunAsync(runId, RunState.Failed, KbCode.Internal, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
#pragma warning restore CA1031
        var state = outcome.Status switch { CheckStatus.Succeeded => RunState.Succeeded, CheckStatus.Warning => RunState.Warning, _ => RunState.Failed };
        await FinishRunAsync(runId, state, outcome.Code, CancellationToken.None).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Kanały run.finished (outbox, D9.6) dla weryfikacji z harmonogramu — te same co kopie (alert A04 z kodem KB-15xx); null/puste = bez zdarzeń.</summary>
    public IReadOnlyList<Kobian.Agent.Notifications.NotifyChannel>? NotifyChannels { get; init; }

    /// <summary>Parametry dzierżawy repozytorium (D3.13) dla weryfikacji z harmonogramu; null = domyślne (TTL 1800 s, odświeżanie 5 min).</summary>
    public RepoLockOptions? LeaseOptions { get; init; }

    /// <summary>
    /// Jeden cykl V3 z harmonogramu (BL-26, 3.7) dla zadania <paramref name="jobId"/> — wołany przez <see cref="CatalogVerifyScheduler"/> PO nabyciu zasobów kolejki
    /// (<see cref="RunResourceLocks.ForVerify"/>). Ta sama ścieżka co RPC Check (definicja → klucz → sesja Backup, tylko odczyt), a ponadto:
    /// <list type="bullet">
    /// <item>wiersz run_runs z cause 'schedule' i przejściem z powodem 'verify' (trigger_id NULL — nie myli się z wystąpieniami cron) oraz last_attempt_at w sched_verify_jobs — w JEDNEJ transakcji;</item>
    /// <item>dzierżawa współdzielona repozytorium na celu (D3.13) — prune innego hosta (wyłączna) nie usunie packów w trakcie;</item>
    /// <item>próbka stabilna z kubełkiem <c>rotation</c> zapamiętanym w katalogu; rotacja rośnie tylko po UKOŃCZONYM cyklu (raport), więc przerwany cykl powtarza ten sam kubełek;</item>
    /// <item>wynik w jednej transakcji: run_runs (kod KB-15xx przy uszkodzeniu), sched_verify_packs, verify_level 3, suspect, force_full, zdarzenie run.finished w outboxie.</item>
    /// </list>
    /// Niczego nie usuwa (rola Backup).
    /// </summary>
    public async Task<VerifyCycleOutcome> VerifyScheduledAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var row = FindJobById(jobId);
        if (row is null) return new VerifyCycleOutcome(false, null, 0, new CheckOutcome(CheckStatus.Failed, KbCode.JobNotFound, null));
        var rotation = ReadRotation(jobId);
        var runHex = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var started = _time.GetUtcNow();
        await _writer.ExecuteControlAsync((c, tx) =>
        {
            var now = Iso(started);
            Exec(c, tx, "INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at,started_at) VALUES ($i,$j,'schedule','Running',0,$t,$t);", ("$i", runHex), ("$j", jobId), ("$t", now));
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason) VALUES ($i,0,NULL,'Running',$t,'verify');", ("$i", runHex), ("$t", now));
            Exec(c, tx, "INSERT INTO sched_verify_jobs(job_id,last_attempt_at,last_run_id) VALUES ($j,$t,$i) ON CONFLICT(job_id) DO UPDATE SET last_attempt_at=$t, last_run_id=$i;",
                ("$j", jobId), ("$t", now), ("$i", runHex));
            return 0;
        }, ct).ConfigureAwait(false);

        CheckReport? report = null;
        string? repoHex = null;
        var sample = 0;
        CheckOutcome outcome;
        try
        {
            var def = JobDefinition.Parse(row.Value.Type, row.Value.Definition);
            sample = def.EffectiveVerify.SamplePercent;
            if (!_providers.TryGetValue(def.DestinationScheme, out var factory))
                throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Destination provider is not available.");
            using var keys = KeyFileLoader.Load(def.KeyFile, def.PasswordFile, def.RepoHeader);
            repoHex = Convert.ToHexStringLower(keys.RepoId);
            var dest = def.ToDestination(def.DestinationId);
            // D3.13: dzierżawa współdzielona na OSOBNEJ sesji (sesje są jednowątkowe), odświeżana w tle — jak w kopii.
            await using var lockSession = await factory.OpenAsync(dest, CredentialRole.Backup, ct).ConfigureAwait(false);
            await using var lease = await RepoLocks.AcquireSharedAsync(lockSession, keys, RepoLockOwner.For(HostId), _time, LeaseOptions, ct).ConfigureAwait(false);
            lease.StartAutoRenew();
            await using var session = await factory.OpenAsync(dest, CredentialRole.Backup, ct).ConfigureAwait(false);
            var options = new CheckOptions(sample, rotation) { StableSample = true, KnownVerifiedPacks = ReadVerifiedPacks(repoHex) };
            report = await new RepoChecker(session, keys).CheckAsync(options, ct).ConfigureAwait(false);
            // Kopie z katalogu (niezapomniane) muszą być na celu: pusty/obcy nośnik nie może dać „Succeeded” (KB-1511 → suspect, A04).
            var missing = MissingOnTarget(jobId, repoHex, report.SnapshotsListed);
            if (missing.Count > 0)
            {
                report = report with
                {
                    Status = CheckStatus.Failed,
                    DamagedSnapshots = [.. report.DamagedSnapshots.Union(missing, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    Issues = [.. missing.Select(m => new CheckIssue(KbCode.VerifySnapshotMissing, "snapshot:" + m, m)), .. report.Issues],
                };
            }
            outcome = new CheckOutcome(report.Status, ScheduledCode(report), report);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishVerifyAsync(jobId, runHex, RunState.Cancelled, null, null, null, 0, started, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (KobianException ex)
        {
            outcome = new CheckOutcome(CheckStatus.Failed, ex.Code, null);
        }
        catch (StorageException ex)
        {
            outcome = new CheckOutcome(CheckStatus.Failed, ex.Code, null);
        }
#pragma warning disable CA1031 // granica wykonawcy: nieoczekiwany wyjątek = wiersz Failed (KB-0001), cykl nieukończony
        catch
        {
            await FinishVerifyAsync(jobId, runHex, RunState.Failed, KbCode.Internal, null, null, 0, started, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
#pragma warning restore CA1031
        var state = outcome.Status switch { CheckStatus.Succeeded => RunState.Succeeded, CheckStatus.Warning => RunState.Warning, _ => RunState.Failed };
        await FinishVerifyAsync(jobId, runHex, state, outcome.Code, report, repoHex, sample, started, CancellationToken.None).ConfigureAwait(false);
        return new VerifyCycleOutcome(report is not null, runHex, rotation, outcome);
    }

    /// <summary>
    /// Kod KB uruchomienia V3 z harmonogramu: przy uszkodzeniu zawsze z zakresu KB-15xx (alert A04) — pierwszy kod 15xx raportu albo KB-1510 (np. błąd AEAD/HMAC bloku);
    /// same sieroty → KB-1504 (Warning); poprawny → null.
    /// </summary>
    internal static string? ScheduledCode(CheckReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Status != CheckStatus.Failed) return WorstCode(r);
        return r.Issues.FirstOrDefault(i => i.Code.StartsWith("KB-15", StringComparison.Ordinal) && i.Code != KbCode.CheckOrphanObject)?.Code ?? KbCode.VerifyScheduledFailed;
    }

    private List<string> MissingOnTarget(string jobId, string repoHex, IReadOnlyList<string> listed)
    {
        var onTarget = new HashSet<string>(listed, StringComparer.Ordinal);
        var missing = new List<string>();
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT snap_id FROM snap_snapshots WHERE job_id=$j AND repo_id=$r AND forgotten_at IS NULL ORDER BY snap_id;";
        cmd.Parameters.AddWithValue("$j", jobId);
        cmd.Parameters.AddWithValue("$r", repoHex);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            if (!onTarget.Contains(rd.GetString(0))) missing.Add(rd.GetString(0));
        return missing;
    }

    private long ReadRotation(string jobId)
    {
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rotation FROM sched_verify_jobs WHERE job_id=$j;";
        cmd.Parameters.AddWithValue("$j", jobId);
        return cmd.ExecuteScalar() is long r ? r : 0;
    }

    private HashSet<string> ReadVerifiedPacks(string repoHex)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT pack_id FROM sched_verify_packs WHERE repo_id=$r AND ok=1;";
        cmd.Parameters.AddWithValue("$r", repoHex);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) set.Add(rd.GetString(0));
        return set;
    }

    private Task<int> RecordV3Async(string repoHex, CheckReport report, CancellationToken ct)
    {
        var now = Iso(_time.GetUtcNow());
        return _writer.ExecuteControlAsync((c, tx) =>
        {
            RecordV3(c, tx, repoHex, report, now);
            return 0;
        }, ct);
    }

    /// <summary>
    /// Wynik V3 w katalogu: packi sprawdzone (ok=1) i z błędem (ok=0), snapshoty uszkodzone → suspect, w pełni sprawdzone (wszystkie packi ok teraz lub wcześniej) →
    /// verify_level 3 — tylko nieuszkodzone i tylko w tym repozytorium.
    /// </summary>
    private static void RecordV3(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string repoHex, CheckReport report, string now)
    {
        foreach (var p in report.VerifiedPacks)
            Exec(c, tx, "INSERT INTO sched_verify_packs(repo_id,pack_id,verified_at,ok) VALUES ($r,$p,$t,1) ON CONFLICT(repo_id,pack_id) DO UPDATE SET verified_at=$t, ok=1;", ("$r", repoHex), ("$p", p), ("$t", now));
        foreach (var p in report.FailedPacks)
            Exec(c, tx, "INSERT INTO sched_verify_packs(repo_id,pack_id,verified_at,ok) VALUES ($r,$p,$t,0) ON CONFLICT(repo_id,pack_id) DO UPDATE SET verified_at=$t, ok=0;", ("$r", repoHex), ("$p", p), ("$t", now));
        foreach (var snap in report.DamagedSnapshots)
            Exec(c, tx, "UPDATE snap_snapshots SET suspect=1 WHERE snap_id=$s AND repo_id=$r;", ("$s", snap), ("$r", repoHex));
        foreach (var snap in report.FullyVerifiedSnapshots)
            Exec(c, tx, "UPDATE snap_snapshots SET verify_level=MAX(verify_level,3) WHERE snap_id=$s AND repo_id=$r AND suspect=0;", ("$s", snap), ("$r", repoHex));
    }

    private Task<int> FinishVerifyAsync(string jobId, string runHex, RunState state, string? code, CheckReport? report, string? repoHex, int sample, DateTimeOffset started, CancellationToken ct)
    {
        var nowUtc = _time.GetUtcNow();
        var now = Iso(nowUtc);
        return _writer.ExecuteControlAsync((c, tx) =>
        {
            if (report is not null && repoHex is not null) RecordV3(c, tx, repoHex, report, now);
            Exec(c, tx, "UPDATE run_runs SET state=$s, finished_at=$t, kb_code=$k, files=$f, bytes_read=$b WHERE run_id=$i;",
                ("$s", state.ToString()), ("$t", now), ("$k", code), ("$f", report?.PacksRead), ("$b", report?.BytesRead), ("$i", runHex));
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason,kb_code) VALUES ($i,(SELECT COALESCE(MAX(seq)+1,0) FROM run_transitions WHERE run_id=$i),'Running',$s,$t,'verify',$k);",
                ("$i", runHex), ("$s", state.ToString()), ("$t", now), ("$k", code));
            if (report is not null)
            {
                // Cykl ukończony: następny kubełek; błąd V3 → kolejna kopia pełna (3.7). Warunek last_run_id — tylko bieżący przebieg przesuwa rotację (brak podwójnego przesunięcia).
                Exec(c, tx, "UPDATE sched_verify_jobs SET rotation=rotation+1, sample_percent=$p, last_completed_at=$t, last_status=$s, last_kb_code=$k, "
                    + "force_full=CASE WHEN $ff=1 THEN 1 ELSE force_full END WHERE job_id=$j AND last_run_id=$i;",
                    ("$p", sample), ("$t", now), ("$s", state.ToString()), ("$k", code), ("$ff", report.Status == CheckStatus.Failed ? 1 : 0), ("$j", jobId), ("$i", runHex));
            }
            if (NotifyChannels is { Count: > 0 } channels && state != RunState.Cancelled)
            {
                var stats = new Kobian.Format.Metadata.SnapshotStats(report?.PacksRead ?? 0, 0, report?.BytesRead ?? 0, 0, 0);
                CatalogJobService.EnqueueFinished(c, tx, HostId, runHex, state, code, null, stats, nowUtc < started ? started : nowUtc, channels);
            }
            return 0;
        }, ct);
    }

    private (string JobId, string Type, byte[] Definition)? FindJobById(string jobId)
    {
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT job_id, type, definition FROM job_jobs WHERE job_id = $j;";
        cmd.Parameters.AddWithValue("$j", jobId);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.GetString(0), rd.GetString(1), (byte[])rd[2]) : null;
    }

    /// <summary>Najgorszy kod raportu: pierwszy błąd (nie KB-1504), inaczej ostrzeżenie KB-1504, inaczej null.</summary>
    internal static string? WorstCode(CheckReport r) =>
        r.Issues.FirstOrDefault(i => i.Code != KbCode.CheckOrphanObject)?.Code ?? (r.Issues.Count > 0 ? r.Issues[0].Code : null);

    private (string JobId, string Type, byte[] Definition)? FindJob(string name)
    {
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT job_id, type, definition FROM job_jobs WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.GetString(0), rd.GetString(1), (byte[])rd[2]) : null;
    }

    /// <summary>Dotknięte snapshoty → suspect=1 (3.7: następna kopia zadania będzie pełna; snapshot nie jest bazą przyrostu). Tylko wiersze tego repozytorium; id z raportu to hex.</summary>
    private Task<int> MarkSuspectAsync(string repoHex, IReadOnlyList<string> damaged, CancellationToken ct) =>
        _writer.ExecuteControlAsync((c, tx) =>
        {
            foreach (var snap in damaged)
                Exec(c, tx, "UPDATE snap_snapshots SET suspect=1 WHERE snap_id=$s AND repo_id=$r;", ("$s", snap), ("$r", repoHex));
            return 0;
        }, ct);

    /// <summary>Wynik oceny w katalogu (tylko wiersze tego repozytorium): damage + damaged_files; snapshot z uszkodzeniem nie jest bazą przyrostu (suspect).</summary>
    private Task<int> RecordDamageAsync(string repoHex, IReadOnlyList<SnapshotDamage> damage, CancellationToken ct) =>
        _writer.ExecuteControlAsync((c, tx) =>
        {
            foreach (var d in damage)
            {
                var state = d.State switch { SnapshotDamageState.Partial => "partial", SnapshotDamageState.Unrecoverable => "unrecoverable", _ => null };
                Exec(c, tx, "UPDATE snap_snapshots SET damage=$d, damaged_files=$f, suspect=CASE WHEN $d IS NULL THEN suspect ELSE 1 END WHERE snap_id=$s AND repo_id=$r;",
                    ("$d", state), ("$f", d.DamagedFiles), ("$s", d.SnapshotId), ("$r", repoHex));
            }
            return 0;
        }, ct);

    private Task<int> InsertRunAsync(byte[] runId, string jobId, CancellationToken ct)
    {
        var now = Iso(_time.GetUtcNow());
        return _writer.ExecuteControlAsync((c, tx) =>
        {
            var id = Convert.ToHexStringLower(runId);
            Exec(c, tx, "INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at,started_at) VALUES ($i,$j,'manual','Running',0,$t,$t);", ("$i", id), ("$j", jobId), ("$t", now));
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason) VALUES ($i,0,NULL,'Running',$t,'check');", ("$i", id), ("$t", now));
            return 0;
        }, ct);
    }

    private Task<int> FinishRunAsync(byte[] runId, RunState state, string? code, CancellationToken ct)
    {
        var now = Iso(_time.GetUtcNow());
        return _writer.ExecuteControlAsync((c, tx) =>
        {
            var id = Convert.ToHexStringLower(runId);
            Exec(c, tx, "UPDATE run_runs SET state=$s, finished_at=$t, kb_code=$k WHERE run_id=$i;", ("$s", state.ToString()), ("$t", now), ("$k", code), ("$i", id));
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason,kb_code) VALUES ($i,1,'Running',$s,$t,'check',$k);",
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
