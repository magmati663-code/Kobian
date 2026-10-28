using System.Collections.Concurrent;
using System.Globalization;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Scheduler.Locks;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Okresowa weryfikacja próbkowa V3 w Agencie (BL-26; 3.7, D7.4, D3.13): co <see cref="EvaluateEvery"/> (obrót pętli <see cref="CatalogScheduler"/>) wybiera zadania,
/// którym minął termin wg <see cref="DueAt"/>, i uruchamia <see cref="CatalogCheckService.VerifyScheduledAsync"/> po nabyciu zasobów
/// <see cref="RunResourceLocks.ForVerify"/> (repo wyłączna) w TYM SAMYM <see cref="LockManager"/> co kopie — weryfikacja nigdy nie biegnie równolegle z kopią
/// tego repozytorium, a kopie czekają w kolejce na koniec weryfikacji (D7.4).
/// <list type="bullet">
/// <item>Priorytet niski (D7.4): zadanie z uruchomieniem Queued/Running (kopia, check, restore, prune) jest pomijane w tym obrocie; zasób zajęty → próba w następnym.</item>
/// <item>Bez duplikatów: jeden przebieg na zadanie w procesie; po awarii wiersz Running → Interrupted (<see cref="CatalogJobService.RecoverInterruptedAsync"/>),
/// a ponowienie nieukończonego cyklu najwcześniej po <see cref="RetryAfter"/> od ostatniej próby (last_attempt_at zapisany razem z wierszem run_runs).</item>
/// <item>Rotacja w katalogu (sched_verify_jobs.rotation) rośnie tylko po ukończonym cyklu — w ⌈100 / %⌉ ukończonych cyklach sprawdzone są wszystkie packi obecne przez ten czas.</item>
/// </list>
/// </summary>
public sealed class CatalogVerifyScheduler
{
    public const string OwnerPrefix = "verify:";

    private readonly string _catalogPath;
    private readonly CatalogCheckService _check;
    private readonly LockManager _locks;
    private readonly TimeProvider _time;
    private readonly CatalogJobService? _jobs;
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastEvaluation;

    public CatalogVerifyScheduler(string catalogPath, CatalogCheckService check, LockManager locks, TimeProvider time, CatalogJobService? jobs = null)
    {
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _jobs = jobs;
    }

    /// <summary>
    /// Odstęp ponowienia NIEUKOŃCZONEGO cyklu (cel niedostępny, blokada, awaria, zamknięcie usługi).
    /// PROWIZORYCZNIE Qb102: 1 h (bez wykładniczego odstępu) — alternatywa: polityka ponowień zadania (D7.3). Koszt zmiany: mały.
    /// </summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Jak często sprawdzać terminy (zapytanie do katalogu) — niezależnie od pętli 1 s schedulera.</summary>
    public TimeSpan EvaluateEvery { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Błędy, które nie trafiły do run_runs (wyjątek nieoczekiwany poza ścieżką usługi) — do logu; kod KB-0001.</summary>
    public ConcurrentQueue<(DateTimeOffset At, string JobId, string KbCode)> Diagnostics { get; } = new();

    public int InFlight => _inFlight.Count;

    /// <summary>
    /// Termin cyklu (czysta funkcja): polityka wyłączona albo brak kopii → null (nic do sprawdzenia). Podstawa = ostatni UKOŃCZONY cykl, a bez niego najstarszy
    /// niezapomniany snapshot zadania; termin = podstawa + interwał. Próba po ukończonym cyklu, która go nie ukończyła → najwcześniej <paramref name="retryAfter"/> po niej.
    /// </summary>
    public static DateTimeOffset? DueAt(VerifyPolicy policy, DateTimeOffset? firstSnapshot, DateTimeOffset? lastCompleted, DateTimeOffset? lastAttempt, TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled || firstSnapshot is null) return null;
        var due = (lastCompleted ?? firstSnapshot.Value) + policy.Interval;
        if (lastAttempt is { } a && (lastCompleted is null || a > lastCompleted.Value) && a + retryAfter > due) due = a + retryAfter;
        return due;
    }

    /// <summary>Jeden obrót: zadania z terminem ≤ teraz → start w tle (zwraca identyfikatory zadań uruchomionych w tym obrocie).</summary>
    public IReadOnlyList<string> Tick(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (ct.IsCancellationRequested) return [];
        if (_lastEvaluation is { } last && now >= last && now - last < EvaluateEvery) return [];
        _lastEvaluation = now;
        var started = new List<string>();
        foreach (var (jobId, def) in DueJobs(now))
        {
            if (_inFlight.ContainsKey(jobId) || _jobs?.IsActive(jobId) == true) continue;
            var owner = OwnerPrefix + jobId;
            if (!_locks.TryAcquireAll(owner, RunResourceLocks.ForVerify(def))) continue; // kopia na tym repo albo brak slotu — następny obrót (D7.4)
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Task.Run(
                async () =>
                {
                    await gate.Task.ConfigureAwait(false);
                    await RunOneAsync(jobId, owner, ct).ConfigureAwait(false);
                },
                CancellationToken.None);
            _inFlight[jobId] = task;
            gate.SetResult();
            started.Add(jobId);
        }
        return started;
    }

    /// <summary>Czeka na przebiegi w toku (zamknięcie Agenta, testy).</summary>
    public Task DrainAsync() => Task.WhenAll(_inFlight.Values);

    private async Task RunOneAsync(string jobId, string owner, CancellationToken ct)
    {
        try
        {
            _ = await _check.VerifyScheduledAsync(jobId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // zamknięcie Agenta: wiersz Cancelled zapisała usługa; cykl nieukończony — ponowienie po RetryAfter
        }
#pragma warning disable CA1031 // pętla weryfikacji nie może zatrzymać schedulera; wiersz Failed (KB-0001) zapisała usługa
        catch (Exception)
        {
            Diagnostics.Enqueue((_time.GetUtcNow(), jobId, KbCode.Internal));
        }
#pragma warning restore CA1031
        finally
        {
            _locks.Release(owner);
            _inFlight.TryRemove(jobId, out _);
        }
    }

    /// <summary>Zadania włączone, bez uruchomienia w toku (Queued/Running), z poprawną definicją i terminem ≤ <paramref name="now"/> (kolejność: najstarszy termin).</summary>
    private List<(string JobId, JobDefinition Def)> DueJobs(DateTimeOffset now)
    {
        var due = new List<(DateTimeOffset At, string JobId, JobDefinition Def)>();
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT j.job_id, j.type, j.definition, v.last_completed_at, v.last_attempt_at,
                   (SELECT MIN(s.time_utc) FROM snap_snapshots s WHERE s.job_id = j.job_id AND s.forgotten_at IS NULL)
            FROM job_jobs j LEFT JOIN sched_verify_jobs v ON v.job_id = j.job_id
            WHERE j.enabled = 1
              AND NOT EXISTS (SELECT 1 FROM run_runs r WHERE r.job_id = j.job_id AND r.state IN ('Queued','Running','Paused'))
            ORDER BY j.job_id LIMIT 1000;
            """;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            JobDefinition def;
            try
            {
                def = JobDefinition.Parse(rd.GetString(1), (byte[])rd[2]);
            }
            catch (Exception ex) when (ex is KobianException or Kobian.Application.Storage.StorageException)
            {
                continue; // błędna definicja: kopia zgłosi KB-0103; weryfikacja nie ma czego otworzyć
            }
            var at = DueAt(def.EffectiveVerify, Time(rd, 5), Time(rd, 3), Time(rd, 4), RetryAfter);
            if (at is { } t && t <= now) due.Add((t, rd.GetString(0), def));
        }
        return [.. due.OrderBy(d => d.At).ThenBy(d => d.JobId, StringComparer.Ordinal).Select(d => (d.JobId, d.Def))];
    }

    private static DateTimeOffset? Time(Microsoft.Data.Sqlite.SqliteDataReader rd, int i) =>
        rd.IsDBNull(i) ? null : DateTimeOffset.Parse(rd.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
