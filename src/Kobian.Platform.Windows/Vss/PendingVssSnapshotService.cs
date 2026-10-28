using Kobian.Application.Worker;
using Kobian.Domain;

namespace Kobian.Platform.Windows.Vss;

/// <summary>
/// Requester VSS — ZAŚLEPKA do czasu SP-01 (CsWin32 albo AlphaVSS: wybór i audyt pakietu, test na 5 systemach) i jawnego deskryptora
/// w CoInitializeSecurity (D2.8, SEC-12.4). Zawsze KB-1406 — Worker i protokół są gotowe, brakuje wyłącznie wywołań COM (it. 103, W-44).
/// </summary>
public sealed class PendingVssSnapshotService : IVssSnapshotService
{
    public Task<IVssSnapshot> CreateAsync(IReadOnlyList<string> volumes, CancellationToken ct) =>
        Task.FromException<IVssSnapshot>(new VssException(KbCode.VssSnapshotFailed, "VSS requester is not implemented yet (SP-01)."));
}
