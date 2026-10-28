using System.Collections.Concurrent;
using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Throttling;

/// <summary>
/// D4.16 pkt 3 i SEC-24.4 (BL-77, it. 97): dławienie otwarć sesji per cel — kubełek ≤ 1 otwarcie na 2 s (burst 2, jitter),
/// a po 3 nieudanych otwarciach w ciągu godziny „breaker celu” otwiera się na 10 min: kolejne próby kończą się od razu KB-2114 (przejściowy),
/// bez kontaktu z serwerem. Chroni przed auto-blokadą IP (fail2ban, NAS — PM-26) i blokadą konta po błędnym haśle (SEC-24.4).
/// Liczy się każde nieudane otwarcie (także uwierzytelnienie), oprócz anulowania przez wywołującego. Udane otwarcie nie zeruje okna —
/// limit „3 na godzinę” dotyczy konta i adresu, a nie serii (PROWIZORYCZNIE Qb47, ADR-0030).
/// Zegar monotoniczny (<see cref="TimeProvider.GetTimestamp"/>): skok zegara ściennego nie skraca ani nie wydłuża przerwy.
/// Stan tylko w pamięci procesu Agenta (restart = czysty stan; wyuczony limit w dest_targets to AIMD z pkt 2, nie ten breaker).
/// </summary>
public sealed class SessionOpenGovernor
{
    public static readonly TimeSpan DefaultOpenInterval = TimeSpan.FromSeconds(2);
    public const int DefaultBurst = 2;
    public const int DefaultFailureThreshold = 3;
    public static readonly TimeSpan DefaultFailureWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultBreakDuration = TimeSpan.FromMinutes(10);

    /// <summary>Górna granica liczby celów w pamięci (ochrona przed rozrostem; najstarsze bez blokady są zapominane).</summary>
    public const int MaxTargets = 1024;

    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan> _jitter;
    private readonly ConcurrentDictionary<string, Target> _targets = new(StringComparer.Ordinal);

    public SessionOpenGovernor(TimeProvider time, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<TimeSpan>? jitter = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _delay = delay ?? ((d, ct) => Task.Delay(d, _time, ct));
        _jitter = jitter ?? (() => TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)));
    }

    public TimeSpan OpenInterval { get; init; } = DefaultOpenInterval;

    public int Burst { get; init; } = DefaultBurst;

    public int FailureThreshold { get; init; } = DefaultFailureThreshold;

    public TimeSpan FailureWindow { get; init; } = DefaultFailureWindow;

    public TimeSpan BreakDuration { get; init; } = DefaultBreakDuration;

    /// <summary>Czas do zamknięcia breakera celu (null = zamknięty) — diagnostyka, probe, testy.</summary>
    public TimeSpan? BrokenFor(string key)
    {
        if (!_targets.TryGetValue(key, out var t)) return null;
        lock (t) return Remaining(t);
    }

    /// <summary>Otwiera sesję przez kubełek i breaker celu <paramref name="key"/>; wyjątek z <paramref name="open"/> jest zgłaszany dalej bez zmian.</summary>
    public async ValueTask<T> OpenAsync<T>(string key, Func<CancellationToken, ValueTask<T>> open, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(open);
        var t = TargetFor(key);
        TimeSpan wait;
        lock (t)
        {
            ThrowIfBroken(t);
            Refill(t);
            wait = t.Tokens >= 1 ? TimeSpan.Zero : TimeSpan.FromTicks((long)((1 - t.Tokens) * OpenInterval.Ticks));
            t.Tokens -= 1; // rezerwacja — kolejni czekają w kolejności zgłoszeń (wartość ujemna = dług kubełka)
        }
        if (wait > TimeSpan.Zero)
        {
            try
            {
                await _delay(wait + _jitter(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (t) t.Tokens = Math.Min(Burst, t.Tokens + 1); // anulowane w kolejce nie zużywa otwarcia
                throw;
            }
            lock (t) ThrowIfBroken(t); // breaker mógł się otworzyć w trakcie czekania
        }
        try
        {
            return await open(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // anulowanie przez wywołującego nie jest nieudanym otwarciem
        }
        catch
        {
            RecordFailure(t);
            throw;
        }
    }

    private Target TargetFor(string key)
    {
        if (_targets.Count >= MaxTargets && !_targets.ContainsKey(key)) Trim();
        return _targets.GetOrAdd(key, _ => new Target(Burst, _time.GetTimestamp()));
    }

    private void Trim()
    {
        foreach (var (k, t) in _targets)
        {
            bool idle;
            lock (t) idle = Remaining(t) is null && t.Failures.Count == 0;
            if (idle) _targets.TryRemove(k, out _);
        }
    }

    private void RecordFailure(Target t)
    {
        lock (t)
        {
            var now = _time.GetTimestamp();
            t.Failures.Enqueue(now);
            while (t.Failures.Count > 0 && _time.GetElapsedTime(t.Failures.Peek(), now) >= FailureWindow) t.Failures.Dequeue();
            if (t.Failures.Count < FailureThreshold) return;
            t.BrokenSince = now;
            t.Failures.Clear(); // po przerwie od nowa: kolejne 3 nieudane → znów 10 min
        }
    }

    private void ThrowIfBroken(Target t)
    {
        if (Remaining(t) is { } left)
        {
            throw new StorageException(ErrorClass.Transient, KbCode.DestinationOpenThrottled,
                $"Session opening to this destination is paused for {Math.Ceiling(left.TotalMinutes)} min after repeated failures.");
        }
    }

    private TimeSpan? Remaining(Target t)
    {
        if (t.BrokenSince is not { } since) return null;
        var left = BreakDuration - _time.GetElapsedTime(since);
        if (left > TimeSpan.Zero) return left;
        t.BrokenSince = null;
        return null;
    }

    private void Refill(Target t)
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(t.Last, now);
        t.Last = now;
        if (elapsed <= TimeSpan.Zero) return;
        t.Tokens = Math.Min(Burst, t.Tokens + (elapsed.Ticks / (double)OpenInterval.Ticks));
    }

    private sealed class Target(int burst, long now)
    {
        public double Tokens = burst;
        public long Last = now;
        public long? BrokenSince;
        public readonly Queue<long> Failures = new();
    }
}

/// <summary>Dekorator dostawcy (D4.16 pkt 3): każde <see cref="IStorageProviderFactory.OpenAsync"/> przechodzi przez <see cref="SessionOpenGovernor"/>; klucz = schemat + identyfikator celu.</summary>
public sealed class GovernedStorageProviderFactory(IStorageProviderFactory inner, SessionOpenGovernor governor) : IStorageProviderFactory
{
    private readonly IStorageProviderFactory _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly SessionOpenGovernor _governor = governor ?? throw new ArgumentNullException(nameof(governor));

    public string Scheme => _inner.Scheme;

    public static string KeyOf(DestinationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Scheme + "|" + config.Id;
    }

    public ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct) =>
        _governor.OpenAsync(KeyOf(config), c => _inner.OpenAsync(config, role, c), ct);
}
