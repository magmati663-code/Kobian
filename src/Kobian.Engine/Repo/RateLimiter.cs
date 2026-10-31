namespace Kobian.Engine.Repo;

/// <summary>
/// Limit przepustowości (runda 17): jeden limiter dzielony przez wszystkie wysyłki Agenta, więc suma równoległych sesji mieści się w limicie.
/// Harmonogram „następny wolny moment”: każde żądanie rezerwuje czas <c>bajty / limit</c> po poprzednim; wywołujący czeka do swojego początku.
/// Krótkie zrywy (do jednego kawałka) przechodzą bez czekania, średnia z dłuższego okresu jest równa limitowi.
/// </summary>
public sealed class RateLimiter
{
    private readonly double _bytesPerSecond;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private long _nextTicks;

    public RateLimiter(long bytesPerSecond, TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        _bytesPerSecond = bytesPerSecond;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((d, ct) => Task.Delay(d, _time, ct));
    }

    public long BytesPerSecond => (long)_bytesPerSecond;

    /// <summary>Kawałek wysyłki: wystarczająco mały, by limit był równomierny nawet przy 4 MiB blokach i niskich limitach.</summary>
    public int SliceSize => (int)Math.Clamp(_bytesPerSecond / 8, 16 * 1024, 256 * 1024);

    /// <summary>Rezerwuje czas na <paramref name="bytes"/> i czeka, aż nadejdzie.</summary>
    public Task WaitAsync(int bytes, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var start = Math.Max(now, _nextTicks);
            _nextTicks = start + (long)(bytes / _bytesPerSecond * _time.TimestampFrequency);
            wait = TimeSpan.FromSeconds((double)(start - now) / _time.TimestampFrequency);
        }
        return wait > TimeSpan.Zero ? _delay(wait, ct) : Task.CompletedTask;
    }
}
