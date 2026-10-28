using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Kobian.Catalog.Sqlite;

/// <summary>
/// Pojedynczy zapisujący do katalogu (D2.14 pkt 1): kolejka sterowania (przejścia stanów, audyt, outbox) ma pierwszeństwo —
/// jest opróżniana przed każdą paczką masową; paczka masowa ≤ 5 000 wierszy lub ≤ 50 ms trwania transakcji.
/// Operacje wykonywane w transakcji na jednym połączeniu; brak współbieżnych pisarzy (NFR-17, NFR-10).
/// </summary>
public sealed class CatalogWriter : IAsyncDisposable
{
    public const int MaxBulkRows = 5_000;
    public static readonly TimeSpan MaxBulkDuration = TimeSpan.FromMilliseconds(50);

    private readonly SqliteConnection _conn;
    private readonly Channel<Action<SqliteConnection, SqliteTransaction>> _bulkQ;
    private readonly Channel<(Func<SqliteConnection, SqliteTransaction, object?> Op, TaskCompletionSource<object?> Done)> _controlWork;
    private readonly Task _loop;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0);

    public CatalogWriter(string path)
    {
        _conn = CatalogBootstrap.Open(path);
        _controlWork = Channel.CreateUnbounded<(Func<SqliteConnection, SqliteTransaction, object?>, TaskCompletionSource<object?>)>(new UnboundedChannelOptions { SingleReader = true });
        _bulkQ = Channel.CreateBounded<Action<SqliteConnection, SqliteTransaction>>(new BoundedChannelOptions(100_000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Liczniki do testów i metryk (NFR-17).</summary>
    public long BulkTransactions { get; private set; }

    public long ControlTransactions { get; private set; }

    /// <summary>Operacja sterowania — własna transakcja, wykonana przed kolejną paczką masową.</summary>
    public async Task<T> ExecuteControlAsync<T>(Func<SqliteConnection, SqliteTransaction, T> op, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(op);
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _controlWork.Writer.WriteAsync((Wrap(op), tcs), ct).ConfigureAwait(false);
        _signal.Release();
        return (T)(await tcs.Task.ConfigureAwait(false))!;
    }

    /// <summary>Wiersz masowy (indeks plików, dziennik uploadu) — grupowany w paczki.</summary>
    public async ValueTask EnqueueBulkAsync(Action<SqliteConnection, SqliteTransaction> row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        await _bulkQ.Writer.WriteAsync(row, ct).ConfigureAwait(false);
        _signal.Release();
    }

    /// <summary>Czeka, aż wszystkie dotychczas zlecone wiersze masowe zostaną zatwierdzone.</summary>
    public Task FlushAsync(CancellationToken ct = default) => ExecuteControlAsync<object?>((_, _) => null, ct);

    private static Func<SqliteConnection, SqliteTransaction, object?> Wrap<T>(Func<SqliteConnection, SqliteTransaction, T> op) => (c, t) => op(c, t);

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        while (true)
        {
            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // domknięcie: opróżnij kolejki
            }
            DrainControl();
            DrainBulkBatch();
            if (ct.IsCancellationRequested && !_controlWork.Reader.TryPeek(out _) && !_bulkQ.Reader.TryPeek(out _)) return;
        }
    }

    private void DrainControl()
    {
        while (_controlWork.Reader.TryRead(out var item))
        {
            // Flush semantyka: przed operacją sterowania zatwierdź zaległe wiersze masowe zlecone wcześniej.
            if (_bulkQ.Reader.TryPeek(out _)) DrainBulkBatch(unlimited: true);
            try
            {
                using var tx = _conn.BeginTransaction();
                var result = item.Op(_conn, tx);
                tx.Commit();
                ControlTransactions++;
                item.Done.SetResult(result);
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException or ArgumentException)
            {
                item.Done.SetException(ex);
            }
        }
    }

    private void DrainBulkBatch(bool unlimited = false)
    {
        if (!_bulkQ.Reader.TryPeek(out _)) return;
        var sw = Stopwatch.StartNew();
        using var tx = _conn.BeginTransaction();
        var n = 0;
        while ((unlimited || (n < MaxBulkRows && sw.Elapsed < MaxBulkDuration)) && _bulkQ.Reader.TryRead(out var row))
        {
            row(_conn, tx);
            n++;
            // pierwszeństwo sterowania: przerwij paczkę, jeśli czeka operacja sterowania (poza trybem flush)
            if (!unlimited && _controlWork.Reader.TryPeek(out _)) break;
        }
        tx.Commit();
        BulkTransactions++;
        if (_bulkQ.Reader.TryPeek(out _)) _signal.Release();
    }

    public async ValueTask DisposeAsync()
    {
        _controlWork.Writer.TryComplete();
        _bulkQ.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        _signal.Release();
        await _loop.ConfigureAwait(false);
        await _conn.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        _signal.Dispose();
    }
}
