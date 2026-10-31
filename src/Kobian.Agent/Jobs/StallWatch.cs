using Kobian.Domain;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Nadzór zawieszonego uruchomienia (runda 14): praca uznawana jest za zawieszoną, gdy licznik aktywności nie zmienia się przez <c>timeout</c>
/// (dysk sieciowy bez odpowiedzi, serwer, który przyjął połączenie i milczy). Wtedy token pracy jest anulowany, a wywołujący dostaje KB-4106
/// (przejściowy → wykonawca przebiegów ponowi zgodnie z polityką) — nawet jeśli praca tkwi w wywołaniu systemowym, którego anulowanie nie przerywa:
/// wynik oddawany jest bez czekania na nią. Długa, ale postępująca kopia (nowe bloki, pliki, wysyłki) nigdy nie jest przerywana.
/// </summary>
public sealed class StallWatch : IDisposable
{
    private readonly CancellationTokenSource _work;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<long> _activity;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly Task _loop;

    /// <param name="activity">Rosnący licznik aktywności (każda zmiana = postęp).</param>
    /// <param name="timeout">Brak zmiany przez ten czas = zawieszenie; zero lub mniej wyłącza nadzór.</param>
    /// <param name="poll">Co ile sprawdzać (domyślnie timeout/6, najwyżej 30 s).</param>
    public StallWatch(Func<long> activity, TimeSpan timeout, CancellationToken outer, TimeProvider? time = null, TimeSpan? poll = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _timeout = timeout;
        _time = time ?? TimeProvider.System;
        _work = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var interval = poll ?? TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(30).Ticks, Math.Max(1, timeout.Ticks / 6)));
        _loop = timeout > TimeSpan.Zero ? Task.Run(() => LoopAsync(interval)) : Task.CompletedTask;
    }

    /// <summary>Token, który praca ma respektować (anulowany przez wywołującego albo przy zawieszeniu).</summary>
    public CancellationToken Token => _work.Token;

    public bool Stalled { get; private set; }

    private async Task LoopAsync(TimeSpan interval)
    {
        var last = _activity();
        var since = _time.GetTimestamp();
        using var timer = new PeriodicTimer(interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                var now = _activity();
                if (now != last)
                {
                    last = now;
                    since = _time.GetTimestamp();
                }
                else if (_time.GetElapsedTime(since) >= _timeout)
                {
                    Stalled = true;
                    await _work.CancelAsync().ConfigureAwait(false);
                    _stalled.TrySetResult();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // koniec nadzoru (praca skończona)
        }
    }

    /// <summary>Wykonuje pracę pod nadzorem; zawieszenie → <see cref="KobianException"/> KB-4106 (przejściowy), anulowanie z zewnątrz → OperationCanceledException.</summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var task = work(_work.Token);
        var first = await Task.WhenAny(task, _stalled.Task).ConfigureAwait(false);
        if (first == task)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Stalled)
            {
                throw Stall(); // praca zareagowała na anulowanie nadzoru
            }
        }
        // Praca mogła tkwić w wywołaniu nieprzerywalnym: nie czekamy na nią, a jej ewentualny późniejszy wyjątek jest obserwowany (bez „unobserved”).
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        throw Stall();
    }

    private static KobianException Stall() => new(KbCode.RunStalled, ErrorClass.Transient, "The run made no progress and was stopped.");

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { /* pętla nadzoru kończy się anulowaniem */ }
        _work.Dispose();
        _stop.Dispose();
    }
}
