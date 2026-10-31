using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Throttling;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>
/// BL-77 (it. 97), D4.16 pkt 3, SEC-24.4: dławienie otwarć sesji per cel (≤ 1 / 2 s, burst 2) i breaker celu po 3 nieudanych otwarciach w godzinie (10 min, KB-2114).
/// Zegar ręczny: czekanie kubełka przesuwa czas o żądany odstęp, bez prawdziwego snu.
/// </summary>
public sealed class SessionOpenGovernorTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public void Advance(TimeSpan d) => Interlocked.Add(ref _ticks, d.Ticks);
    }

    private readonly ManualTime _time = new();
    private readonly List<TimeSpan> _waits = [];

    private SessionOpenGovernor Governor() => new(_time, (d, _) =>
    {
        lock (_waits) _waits.Add(d);
        _time.Advance(d);
        return Task.CompletedTask;
    }, () => TimeSpan.Zero);

    private static ValueTask<int> Ok(CancellationToken _) => ValueTask.FromResult(1);

    private int _calls;

    private ValueTask<int> Fail(CancellationToken _)
    {
        _calls++;
        throw new StorageException(ErrorClass.Security, KbCode.AuthRejected, "auth");
    }

    [Fact, Trait("Decision", "D4.16"), Trait("Backlog", "BL-77")]
    public async Task Burst_of_two_then_one_open_per_two_seconds()
    {
        var g = Governor();
        for (var i = 0; i < 5; i++) await g.OpenAsync("sftp|a", Ok, _ct);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], _waits);

        _time.Advance(TimeSpan.FromMinutes(1)); // kubełek uzupełniony, ale nie ponad burst
        _waits.Clear();
        for (var i = 0; i < 3; i++) await g.OpenAsync("sftp|a", Ok, _ct);
        Assert.Equal([TimeSpan.FromSeconds(2)], _waits);
    }

    [Fact, Trait("Decision", "D4.16"), Trait("Backlog", "BL-77")]
    public async Task Targets_are_independent()
    {
        var g = Governor();
        await g.OpenAsync("sftp|a", Ok, _ct);
        await g.OpenAsync("sftp|a", Ok, _ct);
        await g.OpenAsync("smb|b", Ok, _ct);
        await g.OpenAsync("smb|b", Ok, _ct);
        Assert.Empty(_waits);
    }

    [Fact, Trait("Decision", "SEC-24.4"), Trait("Backlog", "BL-77")]
    public async Task Third_failed_open_within_an_hour_breaks_the_target_for_ten_minutes_without_contacting_the_server()
    {
        var g = Governor();
        for (var i = 0; i < 3; i++)
        {
            if (i > 0) _time.Advance(TimeSpan.FromMinutes(20));
            var ex = await Assert.ThrowsAsync<StorageException>(async () => await g.OpenAsync("sftp|a", Fail, _ct));
            Assert.Equal(KbCode.AuthRejected, ex.Code); // błąd serwera przechodzi bez zmian
        }
        Assert.Equal(3, _calls);
        Assert.NotNull(g.BrokenFor("sftp|a"));

        _time.Advance(TimeSpan.FromMinutes(5));
        var broken = await Assert.ThrowsAsync<StorageException>(async () => await g.OpenAsync("sftp|a", Fail, _ct));
        Assert.Equal((KbCode.DestinationOpenThrottled, true), (broken.Code, broken.IsTransient));
        Assert.Equal(3, _calls); // brak kontaktu z serwerem w czasie przerwy
        Assert.Equal(1, await g.OpenAsync("sftp|b", Ok, _ct)); // inny cel działa

        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(g.BrokenFor("sftp|a"));
        Assert.Equal(1, await g.OpenAsync("sftp|a", Ok, _ct));
    }

    [Fact, Trait("Decision", "SEC-24.4"), Trait("Backlog", "BL-77")]
    public async Task Failures_older_than_an_hour_do_not_count_and_success_does_not_reset_the_window()
    {
        var g = Governor();
        await Assert.ThrowsAsync<StorageException>(async () => await g.OpenAsync("smb|n", Fail, _ct));
        _time.Advance(TimeSpan.FromMinutes(61));
        await Assert.ThrowsAsync<StorageException>(async () => await g.OpenAsync("smb|n", Fail, _ct));
        await g.OpenAsync("smb|n", Ok, _ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<StorageException>(async () => await g.OpenAsync("smb|n", Fail, _ct));
        Assert.Null(g.BrokenFor("smb|n")); // pierwsza porażka wypadła z okna: 2 w ostatniej godzinie

        await Assert.ThrowsAsync<StorageException>(async () => await g.OpenAsync("smb|n", Fail, _ct));
        Assert.NotNull(g.BrokenFor("smb|n")); // 3 w godzinie mimo udanego otwarcia pomiędzy
    }

    [Fact, Trait("Backlog", "BL-77")]
    public async Task Caller_cancellation_is_not_a_failed_open_and_a_cancelled_waiter_returns_its_slot()
    {
        var g = Governor();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await g.OpenAsync<int>("sftp|a", c =>
            {
                cts.Cancel();
                c.ThrowIfCancellationRequested();
                return ValueTask.FromResult(0);
            }, cts.Token));
        }
        Assert.Null(g.BrokenFor("sftp|a"));

        var blocked = new SessionOpenGovernor(_time, (_, _) => Task.FromCanceled(new CancellationToken(true)), () => TimeSpan.Zero);
        await blocked.OpenAsync("x|1", Ok, _ct);
        await blocked.OpenAsync("x|1", Ok, _ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blocked.OpenAsync("x|1", Ok, _ct));
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await blocked.OpenAsync("x|1", Ok, _ct)); // anulowany czekający oddał rezerwację — po 2 s jest wolne otwarcie
    }

    [Fact, Trait("Backlog", "BL-77")]
    public async Task Decorator_keys_by_scheme_and_destination_id_and_keeps_the_scheme()
    {
        var inner = new CountingFactory();
        var g = Governor();
        var f = new GovernedStorageProviderFactory(inner, g);
        Assert.Equal("sftp", f.Scheme);
        var cfg = new DestinationConfig("d1", "sftp", "/kopie");
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(cfg, CredentialRole.Backup, _ct));
        Assert.Equal(3, inner.Opens);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(cfg, CredentialRole.Maintenance, _ct));
        Assert.Equal(KbCode.DestinationOpenThrottled, ex.Code);
        Assert.Equal(3, inner.Opens); // także sesja Maintenance tego celu (to samo konto/serwer)
        Assert.NotNull(g.BrokenFor(GovernedStorageProviderFactory.KeyOf(cfg)));
        await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(cfg with { Id = "d2" }, CredentialRole.Backup, _ct));
        Assert.Equal(4, inner.Opens); // inny cel nie jest zablokowany
    }

    private sealed class CountingFactory : IStorageProviderFactory
    {
        public int Opens;

        public string Scheme => "sftp";

        public ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
        {
            Opens++;
            throw new StorageException(ErrorClass.Transient, KbCode.TransientIo, "refused");
        }
    }
}
