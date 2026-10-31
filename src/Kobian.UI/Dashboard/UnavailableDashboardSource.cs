namespace Kobian.UI.Dashboard;

/// <summary>Źródło bez usługi: pulpit pokazuje baner „Brak połączenia” (R2.6.4, KB-9001). Zastępowane adapterem gRPC (BL-14).</summary>
public sealed class UnavailableDashboardSource : IDashboardSource
{
    public Task<DashboardData> LoadAsync(CancellationToken ct) => Task.FromException<DashboardData>(new IOException("Kobian service is not reachable."));

    public Task RunJobAsync(string jobId, CancellationToken ct) => Task.FromException(new IOException("Kobian service is not reachable."));
}
