using Kobian.Providers.Storage.Throttling;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>D4.9 / NFR-33 na wirtualnym zegarze (deterministycznie, bez sleepów).</summary>
public sealed class ThrottlingTests
{
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long GetTimestamp() => _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);

        public void Advance(TimeSpan d) => _ticks += d.Ticks;
    }

    [Fact]
    public void Very_low_rate_still_lets_a_full_chunk_through_eventually()
    {
        // pojemność wiadra 2×limit < największa porcja (256 KiB) przy limicie < 128 KiB/s: wcześniej _tokens >= n nigdy nie zachodziło (wieczne czekanie)
        var t = new ManualTime();
        var bucket = new TokenBucket(1000, t);
        for (var i = 0; i < 100_000; i++) // porcje czekania ≤ 100 ms, 256 KiB przy 1 kB/s = ~262 s
        {
            var wait = bucket.TryAcquire(TokenBucket.MaxChunk);
            if (wait == TimeSpan.Zero) return;
            t.Advance(wait);
        }
        Assert.Fail("A full chunk could never be acquired at a very low rate.");
    }

    /// <summary>Symulacja zapisu „ile się da”: w każdym kroku 1 ms próbujemy wysłać porcję 64 KiB.</summary>
    private static double MeasureRate(long rate, TimeSpan duration, int chunk = 64 * 1024, ManualTime? t = null, TokenBucket? bucket = null)
    {
        t ??= new ManualTime();
        bucket ??= new TokenBucket(rate, t);
        // rozładowanie początkowego zapasu (burst 2 s) — mierzymy stan ustalony, jak w oknach 10 s NFR-33
        while (bucket.TryAcquire(chunk) == TimeSpan.Zero) { }
        long sent = 0;
        var end = duration;
        var elapsed = TimeSpan.Zero;
        while (elapsed < end)
        {
            if (bucket.TryAcquire(chunk) == TimeSpan.Zero) sent += chunk;
            else
            {
                t.Advance(TimeSpan.FromMilliseconds(1));
                elapsed += TimeSpan.FromMilliseconds(1);
            }
        }
        return sent / duration.TotalSeconds;
    }

    [Theory, Trait("NFR", "NFR-33")]
    [InlineData(128 * 1024)]
    [InlineData(1024 * 1024)]
    [InlineData(10 * 1024 * 1024)]
    [InlineData(100 * 1024 * 1024)]
    public void Average_over_10s_window_within_10_percent(long rate)
    {
        var measured = MeasureRate(rate, TimeSpan.FromSeconds(10));
        Assert.InRange(measured, rate * 0.9, rate * 1.1);
    }

    [Fact, Trait("NFR", "NFR-33")]
    public void Profile_change_takes_effect_without_reset()
    {
        var t = new ManualTime();
        var b = new TokenBucket(10 * 1024 * 1024, t);
        Assert.InRange(MeasureRate(0, TimeSpan.FromSeconds(10), t: t, bucket: b), 9.0 * 1024 * 1024, 11.0 * 1024 * 1024);
        b.SetRate(2 * 1024 * 1024); // 08:00 → 2 MB/s
        Assert.InRange(MeasureRate(0, TimeSpan.FromSeconds(10), t: t, bucket: b), 1.8 * 1024 * 1024, 2.2 * 1024 * 1024);
        b.SetRate(null); // bez ograniczeń
        Assert.Equal(TimeSpan.Zero, b.TryAcquire(TokenBucket.MaxChunk));
    }

    [Fact]
    public void Burst_is_capped_at_two_seconds_of_traffic()
    {
        var t = new ManualTime();
        var b = new TokenBucket(1024 * 1024, t);
        t.Advance(TimeSpan.FromHours(1)); // długa bezczynność nie gromadzi więcej niż 2 s
        long got = 0;
        while (b.TryAcquire(64 * 1024) == TimeSpan.Zero) got += 64 * 1024;
        Assert.InRange(got, 2 * 1024 * 1024 - (64 * 1024), 2 * 1024 * 1024);
    }

    [Fact]
    public void Wait_hint_is_capped_and_chunk_size_enforced()
    {
        var t = new ManualTime();
        var b = new TokenBucket(128 * 1024, t);
        while (b.TryAcquire(TokenBucket.MaxChunk) == TimeSpan.Zero) { }
        Assert.InRange(b.TryAcquire(TokenBucket.MaxChunk), TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.TryAcquire(TokenBucket.MaxChunk + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.SetRate(0));
    }

    [Fact]
    public async Task Hierarchy_takes_from_destination_and_global_and_splits_chunks()
    {
        var t = TimeProvider.System;
        var global = new TokenBucket(null, t);
        var dest = new TokenBucket(null, t);
        var limiter = new BandwidthLimiter(global, dest);
        var chunks = new List<int>();
        await limiter.WriteAsync(new byte[600 * 1024], (m, _) => { chunks.Add(m.Length); return ValueTask.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.Equal([256 * 1024, 256 * 1024, 88 * 1024], chunks);
    }

    [Fact]
    public async Task Acquire_honours_cancellation()
    {
        var b = new TokenBucket(128 * 1024, TimeProvider.System);
        while (b.TryAcquire(TokenBucket.MaxChunk) == TimeSpan.Zero) { }
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            for (var i = 0; i < 100; i++) await b.AcquireAsync(TokenBucket.MaxChunk, cts.Token);
        });
    }
}
