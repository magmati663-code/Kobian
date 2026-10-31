using Kobian.Agent.Jobs;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>Runda 14: zawieszone uruchomienie jest przerywane (KB-4106, przejściowy), ale długa, postępująca praca nigdy.</summary>
public sealed class StallWatchTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(30);

    [Fact]
    public async Task Work_without_progress_is_stopped_even_when_it_ignores_cancellation()
    {
        long activity = 0;
        using var watch = new StallWatch(() => Interlocked.Read(ref activity), Timeout, _ct, poll: Poll);
        var blocked = new TaskCompletionSource(); // jak wywołanie systemowe, którego anulowanie nie przerywa
        var ex = await Assert.ThrowsAsync<KobianException>(() => watch.RunAsync(async _ => { await blocked.Task; return 1; }));
        Assert.Equal((KbCode.RunStalled, ErrorClass.Transient), (ex.Code, ex.ErrorClass));
        Assert.True(watch.Stalled);
        blocked.SetException(new IOException("odblokowane po czasie")); // późniejszy wyjątek porzuconej pracy nie ma być nieobsłużony
    }

    [Fact]
    public async Task Work_that_cooperates_with_cancellation_also_ends_as_stalled()
    {
        using var watch = new StallWatch(() => 0, Timeout, _ct, poll: Poll);
        var ex = await Assert.ThrowsAsync<KobianException>(() => watch.RunAsync<int>(async t => { await Task.Delay(Timeout * 20, t); return 1; }));
        Assert.Equal(KbCode.RunStalled, ex.Code);
    }

    [Fact]
    public async Task Slow_but_progressing_work_is_never_interrupted()
    {
        long activity = 0;
        using var watch = new StallWatch(() => Interlocked.Read(ref activity), Timeout, _ct, poll: Poll);
        var result = await watch.RunAsync(async t =>
        {
            for (var i = 0; i < 20; i++) // łącznie ~600 ms, czyli dwukrotnie dłużej niż limit, ale co 30 ms jest postęp
            {
                await Task.Delay(30, t);
                Interlocked.Increment(ref activity);
            }
            return 42;
        });
        Assert.Equal(42, result);
        Assert.False(watch.Stalled);
    }

    [Fact]
    public async Task Outer_cancellation_is_not_reported_as_a_stall_and_zero_timeout_disables_the_watch()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        using var watch = new StallWatch(() => 0, Timeout, cts.Token, poll: Poll);
        var running = watch.RunAsync<int>(async t => { await Task.Delay(Timeout * 20, t); return 1; });
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(watch.Stalled);

        using var off = new StallWatch(() => 0, TimeSpan.Zero, _ct);
        Assert.Equal(7, await off.RunAsync(async _ => { await Task.Delay(Timeout * 2, _ct); return 7; })); // bez nadzoru nic nie przerywa
    }
}
