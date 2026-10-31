using System.Collections.Concurrent;
using System.Text.Json;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Scheduler.Locks;
using Kobian.Scheduler.Runs;
using Kobian.Scheduler.Timing;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Wyzwalacze czasowe w Agencie (RD-26, BL-42, US-03): wyzwalacze „cron” z sched_triggers (strefa i polityka misfire z job_jobs) → <see cref="SchedulerCore"/>
/// (D7.9: misfire, detekcja skoku zegara) → wiersze Queued w run_runs (<see cref="CatalogSchedulerStore"/>) → <see cref="CatalogJobService.RunQueuedAsync"/>.
/// Pętla co 1 s; uruchomienia idą równolegle (limit <c>maxParallel</c>, PROWIZORYCZNIE 2), a zadanie nie startuje drugi raz (Skipped, KB-4103).
/// Start uruchomienia wymaga nabycia „wszystko albo nic” jego zasobów w <see cref="LockManager"/> (D7.10; it. 94: limit globalny + <c>smb:&lt;serwer&gt;</c>
/// z kontem wg <see cref="RunResourceLocks"/>); gdy zasób jest zajęty, wiersz zostaje Queued i czeka na kolejny obrót pętli (kolejność queued_at).
/// Uruchomienia ręczne (RunJob) czekają w tym samym <see cref="LockManager"/>, gdy <see cref="CatalogJobService.Locks"/> jest ustawione (BL-74, it. 96).
/// Zmiany w sched_triggers/job_jobs (RD-27) są wykrywane co obrót pętli (odcisk konfiguracji) i stosowane przez <see cref="SchedulerCore.Reconfigure"/> bez restartu; wyzwalacz z błędną specyfikacją jest pomijany, reszta działa (KB-0201 w <see cref="Diagnostics"/>).
/// Wyzwalacze nie-cron (zdarzenia, interwał) — kolejne iteracje.
/// </summary>
public sealed class CatalogScheduler : IDisposable
{
    private readonly string _catalogPath;
    private readonly CatalogWriter _writer;
    private readonly CatalogJobService _jobs;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, TimeSpan, TimeSpan>? _jitter;
    private readonly LockManager _locks;
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);
    private SchedulerCore? _core;
    private string _fingerprint = string.Empty;

    public CatalogScheduler(string catalogPath, CatalogWriter writer, CatalogJobService jobs, TimeProvider time, int maxParallel = 2, Func<TimeSpan, TimeSpan, TimeSpan>? jitter = null)
    {
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallel, 1);
        _locks = jobs.Locks ?? new LockManager(maxParallel); // BL-74: wspólna kolejka z uruchomieniami ręcznymi (wtedy limit = limit menedżera z usługi)
        _jitter = jitter;
    }

    public void Dispose()
    {
        // LockManager nie trzyma zasobów systemowych; metoda zostaje dla zgodności z wywołującymi (using).
    }

    /// <summary>Menedżer blokad kolejki (D7.10) — diagnostyka i testy.</summary>
    public LockManager Locks => _locks;

    /// <summary>Kody KB zdarzeń schedulera (KB-0201 błędny wyzwalacz, KB-4201 skok zegara, KB-4901 duplikat) — do logu/alertu A17.</summary>
    public ConcurrentQueue<(DateTimeOffset At, string KbCode, string? TriggerId)> Diagnostics { get; } = new();

    public int ActiveTriggerCount { get; private set; }

    /// <summary>SEC-17.4: skok zegara spełniający A17 wstrzymuje retencję hosta (null = tylko diagnostyka KB-4201).</summary>
    public RetentionHoldService? RetentionHold { get; init; }

    /// <summary>
    /// BL-26: okresowa weryfikacja V3 obracana w tej samej pętli, PO starcie kopii z kolejki (kopie mają pierwszeństwo — D7.4 priorytet Low dla V3).
    /// Musi używać tego samego <see cref="LockManager"/> co <see cref="Locks"/>. Null = bez V3 z harmonogramu.
    /// </summary>
    public CatalogVerifyScheduler? Verify { get; init; }

    public DateTimeOffset? NextFire(string triggerId) => _core?.NextFire(triggerId);

    /// <summary>Wczytuje wyzwalacze i uruchamia <see cref="SchedulerCore.OnStart"/> (zaległości wg polityki misfire).</summary>
    public void Reload()
    {
        _fingerprint = Fingerprint();
        var (triggers, jobByTrigger) = LoadTriggers();
        ActiveTriggerCount = triggers.Count;
        _core = new SchedulerCore(triggers, new CatalogSchedulerStore(_catalogPath, _writer, jobByTrigger), _time, _jitter);
        _core.OnStart();
        FlushCoreDiagnostics();
    }

    /// <summary>Wczytuje zmienione wyzwalacze do działającego rdzenia (bez OnStart: oczekujące misfire i następne wystąpienia niezmienionych zostają).</summary>
    private void ApplyChanges()
    {
        _fingerprint = Fingerprint();
        var (triggers, jobByTrigger) = LoadTriggers();
        ActiveTriggerCount = triggers.Count;
        _core!.Reconfigure(triggers, new CatalogSchedulerStore(_catalogPath, _writer, jobByTrigger));
    }

    /// <summary>Odcisk konfiguracji wyzwalaczy (trigger + zadanie); tani odczyt raz na obrót pętli, wykrywa edycję z innego połączenia.</summary>
    private string Fingerprint()
    {
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT t.trigger_id, t.job_id, t.kind, t.spec, t.enabled, j.enabled, j.time_zone, j.misfire, j.misfire_within_sec
            FROM sched_triggers t JOIN job_jobs j ON j.job_id = t.job_id ORDER BY t.trigger_id;
            """;
        using var rd = cmd.ExecuteReader();
        var sb = new System.Text.StringBuilder();
        while (rd.Read())
        {
            for (var i = 0; i < 9; i++)
                sb.Append(rd.IsDBNull(i) ? "~" : rd.GetFieldType(i) == typeof(byte[]) ? Convert.ToHexStringLower((byte[])rd[i]) : Convert.ToString(rd.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('\u001f');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Jeden obrót pętli: tick rdzenia, potem start uruchomień z kolejki. Zwraca zadania w toku (do testów).</summary>
    public void Tick(CancellationToken ct)
    {
        if (_core is null) Reload();
        else if (!string.Equals(Fingerprint(), _fingerprint, StringComparison.Ordinal)) ApplyChanges(); // RD-27: edycja harmonogramu bez restartu
        _core!.Tick();
        FlushCoreDiagnostics();
        if (ct.IsCancellationRequested) return;
        foreach (var (runId, locks) in QueuedRuns())
        {
            if (!_locks.TryAcquireAll(runId, locks)) continue; // zasób zajęty albo brak wolnego slotu — zostaje Queued (D7.4)
            // Wpis w _inFlight przed startem: szybkie uruchomienie nie może usunąć się, zanim zostanie dodane (wyciek wpisu).
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Task.Run(
                async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    await RunOneAsync(runId, ct).ConfigureAwait(false);
                },
                CancellationToken.None);
            _inFlight[runId] = task;
            start.SetResult();
        }
        Verify?.Tick(ct);
    }

    /// <summary>Czeka na zakończenie uruchomień w toku (zamknięcie Agenta, testy).</summary>
    public Task DrainAsync() => Task.WhenAll([.. _inFlight.Values, Verify?.DrainAsync() ?? Task.CompletedTask]);

    /// <summary>Liczba taktów pętli, które zakończyły się wyjątkiem (diagnostyka/testy); pętla działa dalej.</summary>
    public int TickFailures { get; private set; }

    private bool TryReload()
    {
#pragma warning disable CA1031 // katalog chwilowo niedostępny przy starcie nie może wyłączyć harmonogramu na stałe
        try
        {
            Reload();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TickFailures++;
            return false;
        }
#pragma warning restore CA1031
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Wyjątek z Reload/Tick (pełny dysk, błąd I/O katalogu, uszkodzona data w wierszu) kończył całą pętlę po cichu: zadania z harmonogramu przestawały startować do restartu usługi,
        // a API działało dalej, więc nic tego nie sygnalizowało. Teraz błąd = ten takt przepada, następny (za sekundę) próbuje ponownie.
        var loaded = TryReload();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!loaded && !(loaded = TryReload())) continue;
#pragma warning disable CA1031 // pętla schedulera nie może zginąć od jednego wyjątku
                try
                {
                    Tick(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    TickFailures++;
                }
#pragma warning restore CA1031
            }
        }
        catch (OperationCanceledException)
        {
            // zamknięcie Agenta: dokończ/anuluj uruchomienia w toku
        }
        try
        {
            await DrainAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // uruchomienia przerwane zamknięciem — stan Cancelled zapisał CatalogJobService
        }
    }

    private async Task RunOneAsync(string runId, CancellationToken ct)
    {
        try
        {
            _ = await _jobs.RunQueuedAsync(runId, ct).ConfigureAwait(false);
        }
        finally
        {
            _locks.Release(runId);
            _inFlight.TryRemove(runId, out _);
        }
    }

    /// <summary>Uruchomienia Queued (harmonogram/misfire) z zasobami z definicji zadania; brak zadania → brak zasobów (RunQueuedAsync zapisze błąd).</summary>
    private List<(string RunId, IReadOnlyList<LockRequest> Locks)> QueuedRuns()
    {
        var list = new List<(string, IReadOnlyList<LockRequest>)>();
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT r.run_id, j.type, j.definition FROM run_runs r LEFT JOIN job_jobs j ON j.job_id = r.job_id
            WHERE r.state='Queued' AND r.cause IN ('schedule','misfire') ORDER BY r.queued_at, r.run_id;
            """;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var id = rd.GetString(0);
            if (_inFlight.ContainsKey(id)) continue;
            list.Add((id, RunResourceLocks.ForBackup(rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : (byte[])rd[2])));
        }
        return list;
    }

    private void FlushCoreDiagnostics()
    {
        if (_core is null) return;
        foreach (var d in _core.Diagnostics) Diagnostics.Enqueue((d.At, d.KbCode, null));
        _core.Diagnostics.Clear();
        foreach (var j in _core.ClockJumps)
        {
            // Zapis synchroniczny w obrocie pętli (jak CatalogSchedulerStore): wstrzymanie musi być trwałe, zanim ruszy kolejka (ewentualna retencja po kopii).
            if (RetentionHold?.ReportClockJumpAsync(j.Drift, CancellationToken.None).GetAwaiter().GetResult() == true)
                Diagnostics.Enqueue((j.At, KbCode.AlertClockJump, null));
        }
        _core.ClockJumps.Clear();
    }

    private (List<TimeTrigger>, Dictionary<string, string>) LoadTriggers()
    {
        var triggers = new List<TimeTrigger>();
        var jobByTrigger = new Dictionary<string, string>(StringComparer.Ordinal);
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT t.trigger_id, t.job_id, t.spec, j.time_zone, j.misfire, j.misfire_within_sec
            FROM sched_triggers t JOIN job_jobs j ON j.job_id = t.job_id
            WHERE t.kind='cron' AND t.enabled=1 AND j.enabled=1 ORDER BY t.trigger_id;
            """;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var tid = rd.GetString(0);
            try
            {
                var cron = ParseCron((byte[])rd[2]);
                var schedule = CronSchedule.Parse(cron, rd.GetString(3));
                var policy = rd.GetString(4) switch
                {
                    "runOnceIfMissed" => MisfirePolicyKind.RunOnceIfMissed,
                    "runIfMissedWithin" => MisfirePolicyKind.RunIfMissedWithin,
                    _ => MisfirePolicyKind.Skip,
                };
                var within = TimeSpan.FromSeconds(rd.IsDBNull(5) ? 0 : rd.GetInt64(5));
                triggers.Add(new TimeTrigger(tid, rd.GetString(1), schedule, policy, within));
                jobByTrigger[tid] = rd.GetString(1);
            }
            catch (KobianException ex)
            {
                Diagnostics.Enqueue((_time.GetUtcNow(), ex.Code, tid)); // błędny wyzwalacz nie zatrzymuje pozostałych
            }
        }
        return (triggers, jobByTrigger);
    }

    internal static string ParseCron(ReadOnlySpan<byte> spec)
    {
        if (spec.Length is 0 or > 1024) throw new KobianException(KbCode.ScheduleInvalid, ErrorClass.Permanent, "Trigger spec size out of range.");
        try
        {
            using var doc = JsonDocument.Parse(spec.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("cron", out var c) && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                return c.GetString()!;
        }
        catch (JsonException ex)
        {
            throw new KobianException(KbCode.ScheduleInvalid, ErrorClass.Permanent, "Trigger spec is not valid JSON.", ex);
        }
        throw new KobianException(KbCode.ScheduleInvalid, ErrorClass.Permanent, "Trigger spec must contain a cron expression.");
    }
}
