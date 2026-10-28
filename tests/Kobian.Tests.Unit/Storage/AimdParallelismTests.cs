using Kobian.Agent.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Providers.Storage.Throttling;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>BL-84 (it. 114) — D4.16 pkt 2: AIMD równoległości celu (KB-2112 → ×0,5, +1 po 20 sukcesach, blokada 5 min), bramka o zmiennym limicie, wyuczony limit w katalogu (7 dni).</summary>
public sealed class AimdParallelismTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = DateTimeOffset.Parse("2026-10-17T10:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture).UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public void Advance(TimeSpan d) => Interlocked.Add(ref _ticks, d.Ticks);
    }

    private static readonly StorageException Limit = new(ErrorClass.Transient, KbCode.ServerSessionLimit, "limit");

    [Fact, Trait("Plan", "D4.16"), Trait("KB", "KB-2112")]
    public void Halves_on_limit_holds_growth_then_adds_one_per_twenty_successes()
    {
        var t = new ManualTime();
        var a = new AimdParallelism(8, t);
        Assert.Equal(8, a.Current);
        Assert.Null(a.Learned);
        Assert.True(a.OnLimitSignal());
        Assert.Equal(4, a.Current);
        Assert.False(a.OnLimitSignal()); // ta sama fala odmów (< 1 s) — jedno zmniejszenie
        Assert.Equal(4, a.Current);
        t.Advance(TimeSpan.FromSeconds(2));
        Assert.True(a.OnLimitSignal());
        Assert.Equal(2, a.Current);
        Assert.Equal((2, t.GetUtcNow()), a.Learned);
        for (var i = 0; i < 100; i++) Assert.False(a.OnSuccess()); // blokada wzrostu 5 min
        Assert.Equal(2, a.Current);
        t.Advance(AimdParallelism.GrowthHold);
        for (var i = 0; i < 19; i++) Assert.False(a.OnSuccess());
        Assert.True(a.OnSuccess()); // 20. kolejny sukces
        Assert.Equal(3, a.Current);
        for (var i = 0; i < 10; i++) a.OnSuccess();
        a.OnLimitSignal(); // sygnał zeruje serię i zmniejsza
        Assert.Equal(1, a.Current);
        t.Advance(TimeSpan.FromSeconds(2));
        Assert.False(a.OnLimitSignal()); // minimum 1
        Assert.Equal(1, a.Current);
        t.Advance(AimdParallelism.GrowthHold);
        for (var i = 0; i < 20 * 7; i++) a.OnSuccess();
        Assert.Equal(8, a.Current); // do rekomendowanej, nie wyżej
        Assert.Null(a.Learned); // na rekomendowanej — wpis do usunięcia
        for (var i = 0; i < 40; i++) Assert.False(a.OnSuccess());
        Assert.Equal(8, a.Current);
    }

    [Fact, Trait("Plan", "D4.16")]
    public void Learned_limit_is_used_for_seven_days_and_clamped()
    {
        var t = new ManualTime();
        Assert.Equal(3, new AimdParallelism(8, t, 3, t.GetUtcNow().AddDays(-6)).Current);
        Assert.Equal(8, new AimdParallelism(8, t, 3, t.GetUtcNow().AddDays(-8)).Current); // wygasł
        Assert.Equal(8, new AimdParallelism(8, t, 3, t.GetUtcNow().AddHours(1)).Current); // z przyszłości (skok zegara) — ignorowany
        Assert.Equal(4, new AimdParallelism(4, t, 10, t.GetUtcNow()).Current);
        Assert.Equal(1, new AimdParallelism(4, t, 0, t.GetUtcNow()).Current);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AimdParallelism(0, t));
        Assert.True(AimdParallelism.IsLimitSignal(Limit));
        Assert.False(AimdParallelism.IsLimitSignal(new StorageException(ErrorClass.Transient, KbCode.RepoWriteFailed, "x")));
    }

    [Fact, Trait("Plan", "D4.16")]
    public async Task Gate_never_exceeds_the_current_limit_and_shrinks_after_a_signal()
    {
        var t = new ManualTime();
        var a = new AimdParallelism(4, t);
        var gate = new AdaptiveParallelGate(a);
        var max = 0;
        var running = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Op(bool fail)
        {
            var now = Interlocked.Increment(ref running);
            lock (gate) max = Math.Max(max, now);
            await release.Task;
            Interlocked.Decrement(ref running);
            if (fail) throw Limit;
            return 1;
        }
        var first = Enumerable.Range(0, 10).Select(i => gate.RunAsync(_ => Op(i == 0), _ct)).ToList();
        await Task.Delay(50, _ct);
        Assert.Equal(4, gate.InFlight);
        Assert.Equal(4, Volatile.Read(ref running));
        release.SetResult();
        await Assert.ThrowsAsync<StorageException>(() => first[0]);
        foreach (var task in first.Skip(1)) Assert.Equal(1, await task);
        Assert.Equal(4, max);
        Assert.Equal(2, a.Current); // KB-2112 → ×0,5

        release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        max = 0;
        var second = Enumerable.Range(0, 6).Select(_ => gate.RunAsync(_ => Op(false), _ct)).ToList();
        await Task.Delay(50, _ct);
        Assert.Equal(2, Volatile.Read(ref running));
        release.SetResult();
        await Task.WhenAll(second);
        Assert.Equal(2, max);
        Assert.Equal(0, gate.InFlight);

        // anulowanie w kolejce nie zajmuje miejsca
        release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = Enumerable.Range(0, 2).Select(_ => gate.RunAsync(_ => Op(false), _ct)).ToList();
        using var cts = new CancellationTokenSource();
        var queued = gate.RunAsync(_ => Op(false), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.SetResult();
        await Task.WhenAll(busy);
        Assert.Equal(0, gate.InFlight);
    }

    [Fact, Trait("Plan", "D4.16")]
    public async Task Learned_limit_round_trips_through_the_catalog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kobian-aimd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var catalog = Path.Combine(dir, "catalog.db");
        try
        {
            CatalogBootstrap.Bootstrap(catalog, TimeProvider.System);
            var t = new ManualTime();
            await using (var w = new CatalogWriter(catalog))
            {
                var store = new CatalogParallelismStore(catalog, w, t);
                var a = store.Create("nas-1", 8);
                Assert.Equal(8, a.Current);
                a.OnLimitSignal();
                await store.SaveAsync("nas-1", a, _ct);
                Assert.Equal(4, store.Create("nas-1", 8).Current);
                Assert.Equal(8, store.Create("inny", 8).Current);
                t.Advance(TimeSpan.FromDays(8));
                Assert.Equal(8, store.Create("nas-1", 8).Current); // wygasł
                var b = new AimdParallelism(8, t);
                await store.SaveAsync("nas-1", b, _ct); // na rekomendowanej → wpis usunięty
                using var c = CatalogBootstrap.Open(catalog, readOnly: true);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM repo_dest_parallelism;";
                Assert.Equal(0L, cmd.ExecuteScalar());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }
}
