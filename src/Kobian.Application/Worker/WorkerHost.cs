using Kobian.Domain;

namespace Kobian.Application.Worker;

/// <summary>Migawka VSS żyjąca do <see cref="IAsyncDisposable.DisposeAsync"/> (D2.8: śmierć Workera zwalnia ją automatycznie).</summary>
public interface IVssSnapshot : IAsyncDisposable
{
    Guid SnapshotSetId { get; }

    /// <summary>Wolumin („C:\”) → ścieżka urządzenia migawki.</summary>
    IReadOnlyDictionary<string, string> Devices { get; }
}

/// <summary>Port requestera VSS (D2.8, D3.6). Adapter Windows — SP-01 (CsWin32/AlphaVSS, wymaga audytu pakietu); błąd → <see cref="VssException"/> z kodem KB.</summary>
public interface IVssSnapshotService
{
    Task<IVssSnapshot> CreateAsync(IReadOnlyList<string> volumes, CancellationToken ct);
}

public sealed class VssException : Exception
{
    public VssException(string code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    public VssException()
        : this(KbCode.VssSnapshotFailed, "VSS snapshot failed")
    {
    }

    public VssException(string message)
        : this(KbCode.VssSnapshotFailed, message)
    {
    }

    public VssException(string message, Exception inner)
        : this(KbCode.VssSnapshotFailed, message, inner)
    {
    }

    public string Code { get; }
}

/// <summary>
/// Pętla procesu Worker (rola „vss”, D2.8): ramki ze stdin → <see cref="WorkerJson.Validate(WorkerRequest)"/> → obsługa → odpowiedź na stdout.
/// Najwyżej JEDNA migawka na proces; vss.release albo EOF (rodzic zamknął pipe/zginął) → zwolnienie migawki i koniec.
/// Naruszenie protokołu → koniec z kodem <see cref="ExitProtocol"/> (bez odpowiedzi — rodzic i tak zabija proces).
/// </summary>
public sealed class VssWorkerHost(IVssSnapshotService vss)
{
    public const int ExitOk = 0;
    public const int ExitProtocol = 3;

    private IVssSnapshot? _snapshot;

    public async Task<int> RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            while (true)
            {
                var frame = await WorkerFrames.ReadAsync(input, ct).ConfigureAwait(false);
                if (frame is null) return ExitOk; // EOF — rodzic skończył
                var req = WorkerJson.Validate(WorkerJson.Read<WorkerRequest>(frame));
                var (resp, stop) = await HandleAsync(req, ct).ConfigureAwait(false);
                await WorkerFrames.WriteAsync(output, WorkerJson.Write(resp), ct).ConfigureAwait(false);
                if (stop) return ExitOk;
            }
        }
        catch (WorkerProtocolException)
        {
            return ExitProtocol;
        }
        finally
        {
            if (_snapshot is not null) await _snapshot.DisposeAsync().ConfigureAwait(false);
            _snapshot = null;
        }
    }

    private async Task<(WorkerResponse, bool Stop)> HandleAsync(WorkerRequest req, CancellationToken ct)
    {
        switch (req.Op)
        {
            case WorkerOps.Ping:
                return (new WorkerResponse(req.Id, true), false);
            case WorkerOps.VssRelease:
                return (new WorkerResponse(req.Id, true), true);
            default: // vss.create
                if (_snapshot is not null) return (new WorkerResponse(req.Id, false, KbCode.VssSnapshotFailed), false); // jedna migawka na proces
                try
                {
                    _snapshot = await vss.CreateAsync(req.Volumes!, ct).ConfigureAwait(false);
                    return (new WorkerResponse(req.Id, true, null, _snapshot.Devices, _snapshot.SnapshotSetId.ToString("D")), false);
                }
                catch (VssException ex)
                {
                    return (new WorkerResponse(req.Id, false, KbCode.IsKnown(ex.Code) ? ex.Code : KbCode.VssSnapshotFailed), false);
                }
        }
    }
}

/// <summary>
/// Strona Agenta (D2.8): jedno żądanie naraz, limit czasu na odpowiedź; każda niezgodność (ramka, JSON, Id, kod spoza rejestru, ścieżka urządzenia) → <see cref="WorkerProtocolException"/>
/// i klient przestaje być używalny (właściciel procesu go zabija). Strumienie: stdin/stdout procesu potomnego (anonimowy pipe).
/// </summary>
public sealed class WorkerClient(Stream toWorker, Stream fromWorker, TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(10); // VSS potrafi trwać minuty (writery) — limit z D3.6/SP-01 do kalibracji
    private int _nextId;
    private bool _broken;

    public async Task<WorkerResponse> CallAsync(string op, IReadOnlyList<string>? volumes, CancellationToken ct)
    {
        if (_broken) throw new WorkerProtocolException("Worker connection is broken.");
        var req = WorkerJson.Validate(new WorkerRequest(++_nextId, op, volumes)); // własne żądanie też musi być poprawne
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            await WorkerFrames.WriteAsync(toWorker, WorkerJson.Write(req), cts.Token).ConfigureAwait(false);
            var frame = await WorkerFrames.ReadAsync(fromWorker, cts.Token).ConfigureAwait(false)
                ?? throw new WorkerProtocolException("Worker closed the connection.");
            return WorkerJson.Validate(WorkerJson.Read<WorkerResponse>(frame), req.Id);
        }
        catch (Exception ex) when (ex is WorkerProtocolException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            _broken = true;
            throw ex as WorkerProtocolException ?? new WorkerProtocolException(ex is IOException ? "Worker pipe failed." : "Worker did not answer in time.", ex);
        }
    }
}
