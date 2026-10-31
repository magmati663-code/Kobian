using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Providers.Storage.Throttling;

/// <summary>
/// D4.16 pkt 2 (BL-84, it. 114; PROWIZORYCZNIE Qb60): równoległość celu sterowana AIMD. Start = <c>RecommendedParallelism</c> (albo wyuczony limit
/// z katalogu, jeśli młodszy niż 7 dni); sygnał limitu serwera (KB-2112) → ×0,5 (min. 1) i blokada wzrostu na 5 min; po 20 kolejnych udanych operacjach
/// +1 do wartości rekomendowanej. Sygnały w ciągu 1 s od ostatniego zmniejszenia liczą się jako jeden (ta sama fala odmów — bez spadku do 1 z jednej serii).
/// Czas blokady liczony zegarem monotonicznym; czas „wyuczenia” — zegarem ściennym (zapis do katalogu). Bezpieczna wątkowo.
/// </summary>
public sealed class AimdParallelism : IUploadParallelism
{
    public const int SuccessesToGrow = 20;
    public static readonly TimeSpan GrowthHold = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SignalDebounce = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LearnedTtl = TimeSpan.FromDays(7);
    public const int MaxParallelism = 64;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private int _current;
    private int _successes;
    private long? _lastDecreaseTs;
    private DateTimeOffset? _learnedAt;

    /// <param name="recommended">Z capabilities dostawcy (1…64).</param>
    /// <param name="learned">Wyuczony limit z katalogu (albo null).</param>
    /// <param name="learnedAt">Kiedy wyuczony; starszy niż <see cref="LearnedTtl"/> = ignorowany.</param>
    public AimdParallelism(int recommended, TimeProvider time, int? learned = null, DateTimeOffset? learnedAt = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recommended, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recommended, MaxParallelism);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        Recommended = recommended;
        _current = recommended;
        if (learned is { } l && learnedAt is { } at && _time.GetUtcNow() - at < LearnedTtl && at <= _time.GetUtcNow())
        {
            _current = Math.Clamp(l, 1, recommended);
            _learnedAt = at;
        }
    }

    public int Recommended { get; }

    public int Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Limit do zapisu w katalogu: (wartość, kiedy) gdy poniżej rekomendowanej; null = brak wyuczonego limitu (usuń wpis).</summary>
    public (int Limit, DateTimeOffset At)? Learned
    {
        get
        {
            lock (_gate) return _current < Recommended && _learnedAt is { } at ? (_current, at) : null;
        }
    }

    /// <summary>Udana operacja na celu; true = równoległość wzrosła o 1.</summary>
    public bool OnSuccess()
    {
        lock (_gate)
        {
            if (_current >= Recommended) return false;
            if (_lastDecreaseTs is { } ts && _time.GetElapsedTime(ts) < GrowthHold)
            {
                _successes = 0; // blokada wzrostu: sukcesy w tym czasie się nie kumulują
                return false;
            }
            if (++_successes < SuccessesToGrow) return false;
            _successes = 0;
            _current++;
            if (_current >= Recommended) _learnedAt = null;
            return true;
        }
    }

    /// <summary>Sygnał limitu serwera (KB-2112); true = równoległość zmniejszona.</summary>
    public bool OnLimitSignal()
    {
        lock (_gate)
        {
            _successes = 0;
            if (_lastDecreaseTs is { } ts && _time.GetElapsedTime(ts) < SignalDebounce) return false;
            _lastDecreaseTs = _time.GetTimestamp(); // także przy 1: blokada wzrostu biegnie od ostatniego sygnału
            _learnedAt = _time.GetUtcNow();
            if (_current == 1) return false;
            _current = Math.Max(1, _current / 2);
            return true;
        }
    }

    /// <summary>Czy wyjątek jest sygnałem limitu serwera (D4.16 pkt 1 — mapy błędów dostawców zwracają KB-2112).</summary>
    public static bool IsLimitSignal(Exception ex) => ex is StorageException { Code: KbCode.ServerSessionLimit };
}

/// <summary>
/// Bramka równoległości o zmiennym limicie (AIMD): <see cref="RunAsync{T}"/> czeka, aż liczba trwających operacji spadnie poniżej <see cref="AimdParallelism.Current"/>,
/// po zakończeniu zgłasza sukces albo sygnał limitu. Zmniejszenie limitu nie przerywa trwających operacji — nowe czekają, aż zejdą poniżej limitu.
/// </summary>
public sealed class AdaptiveParallelGate(AimdParallelism aimd)
{
    private readonly AimdParallelism _aimd = aimd ?? throw new ArgumentNullException(nameof(aimd));
    private readonly object _gate = new();
    private readonly LinkedList<TaskCompletionSource> _waiters = new();
    private int _inFlight;

    public int InFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> op, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(op);
        await EnterAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await op(ct).ConfigureAwait(false);
            _aimd.OnSuccess();
            return r;
        }
        catch (Exception ex) when (AimdParallelism.IsLimitSignal(ex))
        {
            _aimd.OnLimitSignal();
            throw;
        }
        finally
        {
            Leave();
        }
    }

    private Task EnterAsync(CancellationToken ct)
    {
        TaskCompletionSource tcs;
        LinkedListNode<TaskCompletionSource> node;
        lock (_gate)
        {
            if (_waiters.Count == 0 && _inFlight < _aimd.Current)
            {
                _inFlight++;
                return Task.CompletedTask;
            }
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(tcs);
        }
        if (!ct.CanBeCanceled) return tcs.Task;
        var reg = ct.Register(() =>
        {
            lock (_gate)
            {
                if (node.List is null) return; // już wpuszczony
                _waiters.Remove(node);
            }
            tcs.TrySetCanceled(ct);
        });
        return tcs.Task.ContinueWith(t => { reg.Dispose(); return t; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }

    private void Leave()
    {
        var wake = new List<TaskCompletionSource>();
        lock (_gate)
        {
            _inFlight--;
            while (_waiters.First is { } first && _inFlight < _aimd.Current)
            {
                _waiters.RemoveFirst();
                _inFlight++;
                wake.Add(first.Value);
            }
        }
        foreach (var w in wake) w.TrySetResult();
    }
}
