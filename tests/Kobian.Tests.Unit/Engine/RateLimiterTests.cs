using System.Security.Cryptography;
using Kobian.Engine.Repo;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>Runda 17: limit przepustowości — harmonogram rezerwacji i działanie w wysyłce packa.</summary>
public sealed class RateLimiterTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    /// <summary>Zegar stojący w miejscu: opóźnienia „czekamy” tylko zapisujemy.</summary>
    private sealed class StillClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
        public override long TimestampFrequency => 1_000_000;
    }

    [Fact]
    public async Task Reservations_queue_up_at_the_configured_rate()
    {
        var waits = new List<TimeSpan>();
        var limiter = new RateLimiter(100_000, new StillClock(), (d, _) => { waits.Add(d); return Task.CompletedTask; });
        for (var i = 0; i < 4; i++) await limiter.WaitAsync(50_000, _ct); // 4 × 0,5 s
        Assert.Equal([TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.5)], waits); // pierwsze żądanie bez czekania
    }

    [Fact]
    public async Task Concurrent_users_share_one_budget()
    {
        var waits = new List<TimeSpan>();
        var limiter = new RateLimiter(1_000_000, new StillClock(), (d, _) => { lock (waits) waits.Add(d); return Task.CompletedTask; });
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => limiter.WaitAsync(100_000, _ct)));
        Assert.Equal(9, waits.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.9), waits.Max()); // 10 × 0,1 s łącznie, niezależnie od liczby wątków
    }

    [Theory]
    [InlineData(10_000, 16 * 1024)]
    [InlineData(1_000_000, 125_000)]
    [InlineData(100_000_000, 256 * 1024)]
    public void Slice_size_scales_with_the_limit(long limit, int expected) => Assert.Equal(expected, new RateLimiter(limit).SliceSize);

    [Fact]
    public async Task Upload_is_cut_into_slices_each_waiting_for_its_share_and_the_object_stays_intact()
    {
        var store = new MemoryStorage();
        var waits = new List<TimeSpan>();
        var limiter = new RateLimiter(200_000, new StillClock(), (d, _) => { waits.Add(d); return Task.CompletedTask; });
        await using var up = new VerifiedUploader(store.Open()) { Throttle = limiter };
        var data = RandomNumberGenerator.GetBytes(1_000_000);
        await up.UploadAsync("data/aa/packt", data, _ct);
        Assert.Equal(data, store.Objects["data/aa/packt"]);
        Assert.Equal(Math.Ceiling(1_000_000d / limiter.SliceSize) - 1, waits.Count); // każdy kawałek poza pierwszym czekał
        Assert.Equal(waits.Order(), waits); // opóźnienia rosną: każdy kawałek czeka na wszystkie poprzednie
        Assert.Equal(TimeSpan.FromSeconds(39 * 0.125), waits.Max()); // 40 kawałków po 25 000 B przy 200 000 B/s: ostatni czeka 39 × 0,125 s
    }

    [Fact]
    public void Invalid_limit_is_rejected() => Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimiter(0));
}
