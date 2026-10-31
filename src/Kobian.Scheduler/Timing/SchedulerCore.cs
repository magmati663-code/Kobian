using Kobian.Domain;
using Kobian.Scheduler.Runs;

namespace Kobian.Scheduler.Timing;

/// <summary>Wyzwalacz czasowy (cron w strefie zadania) z polityką misfire.</summary>
public sealed record TimeTrigger(string TriggerId, string JobId, CronSchedule Schedule, MisfirePolicyKind Misfire, TimeSpan MisfireWithin);

/// <summary>Rekord uruchomienia wytworzonego przez pętlę (projekcja run_runs).</summary>
public sealed record ScheduledRun(string TriggerId, DateTimeOffset? ScheduledForUtc, string Cause, RunState State, DateTimeOffset CreatedAtUtc, DateTimeOffset? DueAtUtc);

/// <summary>Port magazynu (implementacja: CatalogWriter + SQLite; w testach — pamięć). Unikalność wystąpienia jak indeks run_occurrence (D7.7).</summary>
public interface ISchedulerStore
{
    DateTimeOffset? LastHandled(string triggerId);

    /// <summary>Zwraca false przy naruszeniu unikalności (trigger, scheduled_for) dla cause schedule/misfire.</summary>
    bool TryInsertRun(ScheduledRun run);

    void SetLastHandled(string triggerId, DateTimeOffset utc);
}

/// <summary>
/// Pętla schedulera D7.9 bez wątków i I/O: <see cref="OnStart"/> (także wybudzenie — PBT_APMRESUMEAUTOMATIC) i <see cref="Tick"/>
/// wywoływane przez host co 1 s z <see cref="TimeProvider"/>. Detekcja skoku zegara przez porównanie zegara ściennego z monotonicznym.
/// </summary>
public sealed class SchedulerCore
{
    public static readonly TimeSpan DriftThreshold = TimeSpan.FromSeconds(120);
    private IReadOnlyList<TimeTrigger> _triggers;
    private ISchedulerStore _store;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, TimeSpan, TimeSpan> _jitter;
    private readonly Dictionary<string, DateTimeOffset?> _next = new(StringComparer.Ordinal);
    private readonly List<(string TriggerId, DateTimeOffset DueAt)> _pendingMisfire = [];
    private DateTimeOffset _lastWall;
    private long _lastMono;

    public SchedulerCore(IReadOnlyList<TimeTrigger> triggers, ISchedulerStore store, TimeProvider time, Func<TimeSpan, TimeSpan, TimeSpan>? jitter = null)
    {
        _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _jitter = jitter ?? MisfirePlanner.UniformJitter;
    }

    /// <summary>Zdarzenia diagnostyczne (KB-4201 skok zegara, KB-4901 duplikat) — do logu/alertu A17.</summary>
    public List<(DateTimeOffset At, string KbCode)> Diagnostics { get; } = [];

    /// <summary>Wykryte skoki zegara z przesunięciem (obserwowany − oczekiwany upływ; dodatni = w przód) — fakty dla A17/SEC-17.4 (wstrzymanie retencji).</summary>
    public List<(DateTimeOffset At, TimeSpan Drift)> ClockJumps { get; } = [];

    public void OnStart()
    {
        var now = _time.GetUtcNow();
        foreach (var t in _triggers)
        {
            var last = _store.LastHandled(t.TriggerId) ?? now; // nowy wyzwalacz: brak zaległości
            var plan = MisfirePlanner.Plan(t.Schedule, last, now, t.Misfire, t.MisfireWithin, _jitter);
            foreach (var m in plan.Missed)
            {
                if (!_store.TryInsertRun(new ScheduledRun(t.TriggerId, m, "schedule", RunState.Missed, now, null)))
                    Diagnostics.Add((now, KbCode.OccurrenceDuplicate));
            }
            if (plan.Missed.Count > 0) _store.SetLastHandled(t.TriggerId, plan.Missed[^1]);
            if (plan.RunAt is { } runAt) _pendingMisfire.Add((t.TriggerId, runAt));
            _next[t.TriggerId] = plan.NextFire;
        }
        _lastWall = now;
        _lastMono = _time.GetTimestamp();
    }

    public void Tick()
    {
        var wall = _time.GetUtcNow();
        var mono = _time.GetTimestamp();
        var drift = (wall - _lastWall) - _time.GetElapsedTime(_lastMono, mono);
        if (drift.Duration() > DriftThreshold)
        {
            Diagnostics.Add((wall, KbCode.ClockJump));
            ClockJumps.Add((wall, drift));
            foreach (var t in _triggers)
            {
                var from = Max(wall, _store.LastHandled(t.TriggerId) ?? wall);
                _next[t.TriggerId] = t.Schedule.NextAfter(from);
            }
        }

        foreach (var t in _triggers)
        {
            // Bez pętli „nadrabiania”: przy skoku w przód wystąpienia z przeskoczonego okresu nie są uruchamiane seriami (patrz A17).
            if (_next[t.TriggerId] is { } due && due <= wall)
            {
                var last = _store.LastHandled(t.TriggerId);
                if (last is null || due > last)
                {
                    if (!_store.TryInsertRun(new ScheduledRun(t.TriggerId, due, "schedule", RunState.Queued, wall, wall)))
                        Diagnostics.Add((wall, KbCode.OccurrenceDuplicate));
                    _store.SetLastHandled(t.TriggerId, due);
                }
                _next[t.TriggerId] = t.Schedule.NextAfter(Max(wall, due));
            }
        }

        for (var i = _pendingMisfire.Count - 1; i >= 0; i--)
        {
            var (tid, dueAt) = _pendingMisfire[i];
            if (dueAt > wall) continue;
            _store.TryInsertRun(new ScheduledRun(tid, null, "misfire", RunState.Queued, wall, wall));
            _pendingMisfire.RemoveAt(i);
        }
        _lastWall = wall;
        _lastMono = mono;
    }

    /// <summary>
    /// Zmiana zestawu wyzwalaczy w locie (RD-27, edycja harmonogramu bez restartu Agenta). Wyzwalacze bez zmiany zachowują następne wystąpienie i oczekujące misfire;
    /// nowy lub zmieniony (wyrażenie, strefa, zadanie) liczy następne wystąpienie od „teraz” — zaległości dotyczą przestoju, nie edycji; usunięte tracą stan.
    /// </summary>
    public void Reconfigure(IReadOnlyList<TimeTrigger> triggers, ISchedulerStore store)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(store);
        var now = _time.GetUtcNow();
        var old = _triggers.ToDictionary(t => t.TriggerId, StringComparer.Ordinal);
        _triggers = triggers;
        _store = store;
        var ids = triggers.Select(t => t.TriggerId).ToHashSet(StringComparer.Ordinal);
        foreach (var k in _next.Keys.Where(k => !ids.Contains(k)).ToList()) _next.Remove(k);
        _pendingMisfire.RemoveAll(p => !ids.Contains(p.TriggerId));
        foreach (var t in triggers)
        {
            if (old.TryGetValue(t.TriggerId, out var o) && _next.ContainsKey(t.TriggerId) && o.JobId == t.JobId
                && string.Equals(o.Schedule.Text, t.Schedule.Text, StringComparison.Ordinal) && string.Equals(o.Schedule.Zone.Id, t.Schedule.Zone.Id, StringComparison.Ordinal))
                continue;
            _next[t.TriggerId] = t.Schedule.NextAfter(now);
            _pendingMisfire.RemoveAll(p => p.TriggerId == t.TriggerId);
        }
    }

    public DateTimeOffset? NextFire(string triggerId) => _next.TryGetValue(triggerId, out var n) ? n : null;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
