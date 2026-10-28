namespace Kobian.Providers.Storage.Throttling;

/// <summary>
/// Kubełek tokenów D4.9 na zegarze monotonicznym (<see cref="TimeProvider.GetTimestamp"/>; niezależny od DST i zmian zegara).
/// Pojemność = 2 s ruchu przy bieżącym limicie; zmiana limitu bez resetu zawartości (profil czasowy). Limit null = bez ograniczeń.
/// Bezpieczny wątkowo; oczekiwanie w porcjach ≤ 100 ms (reakcja na anulowanie i zmianę profilu).
/// </summary>
public sealed class TokenBucket
{
    public const int MaxChunk = 256 * 1024;
    public const long MinRatePerStream = 128 * 1024;
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(100);
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private double _tokens;
    private long _last;
    private long? _rate;

    public TokenBucket(long? bytesPerSecond, TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        SetRate(bytesPerSecond);
        _tokens = Burst;
        _last = _time.GetTimestamp();
    }

    public long? Rate
    {
        get
        {
            lock (_gate) return _rate;
        }
    }

    private double Burst => _rate is { } r ? 2.0 * r : double.PositiveInfinity;

    /// <summary>Zmiana limitu (OnMinuteTick, profil czasowy). Zawartość przycinana do nowej pojemności, bez resetu.</summary>
    public void SetRate(long? bytesPerSecond)
    {
        if (bytesPerSecond is <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        lock (_gate)
        {
            Refill();
            _rate = bytesPerSecond;
            _tokens = Math.Min(_tokens, Burst);
        }
    }

    /// <summary>Próba pobrania n bajtów; zwraca czas do ponowienia (0 = pobrano).</summary>
    public TimeSpan TryAcquire(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, MaxChunk);
        lock (_gate)
        {
            if (_rate is null) return TimeSpan.Zero;
            Refill();
            if (_tokens >= n)
            {
                _tokens -= n;
                return TimeSpan.Zero;
            }
            var wait = TimeSpan.FromSeconds((n - _tokens) / _rate.Value);
            return wait < MaxWait ? wait : MaxWait;
        }
    }

    public async ValueTask AcquireAsync(int n, CancellationToken ct)
    {
        while (true)
        {
            var wait = TryAcquire(n);
            if (wait == TimeSpan.Zero) return;
            await Task.Delay(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait, _time, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        if (_rate is { } r)
            _tokens = Math.Min(2.0 * r, _tokens + (_time.GetElapsedTime(_last, now).TotalSeconds * r));
        _last = now;
    }
}

/// <summary>Hierarchia D4.9: zapis pobiera tokeny najpierw z kubełka celu, potem globalnego; porcje ≤ 256 KiB.</summary>
public sealed class BandwidthLimiter
{
    private readonly TokenBucket _global;
    private readonly TokenBucket _destination;

    public BandwidthLimiter(TokenBucket global, TokenBucket destination)
    {
        _global = global ?? throw new ArgumentNullException(nameof(global));
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        for (var off = 0; off < buffer.Length; off += TokenBucket.MaxChunk)
        {
            var chunk = buffer.Slice(off, Math.Min(TokenBucket.MaxChunk, buffer.Length - off));
            await _destination.AcquireAsync(chunk.Length, ct).ConfigureAwait(false);
            await _global.AcquireAsync(chunk.Length, ct).ConfigureAwait(false);
            await write(chunk, ct).ConfigureAwait(false);
        }
    }
}
