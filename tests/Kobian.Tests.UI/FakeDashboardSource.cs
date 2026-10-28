using Kobian.UI.Dashboard;

namespace Kobian.Tests.UI;

/// <summary>Atrapa usługi z danymi syntetycznymi (bez prawdziwych nazw klientów/ścieżek).</summary>
public sealed class FakeDashboardSource(DashboardData data) : IDashboardSource
{
    public static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    public List<string> Runs { get; } = [];

    public DashboardData Data { get; set; } = data;

    public bool Fail { get; set; }

    public List<string> Acked { get; } = [];

    public List<(string Id, int Minutes)> Silenced { get; } = [];

    /// <summary>Kod KB zwracany przez akcje na alertach (null = powodzenie).</summary>
    public string? AlertActionCode { get; set; }

    public Task<string?> AcknowledgeAlertAsync(string alertId, CancellationToken ct)
    {
        if (AlertActionCode is null) Acked.Add(alertId);
        return Task.FromResult(AlertActionCode);
    }

    public Task<string?> SilenceAlertAsync(string alertId, int minutes, CancellationToken ct)
    {
        if (AlertActionCode is null) Silenced.Add((alertId, minutes));
        return Task.FromResult(AlertActionCode);
    }

    public static IReadOnlyList<DashboardAlert> SampleAlerts() =>
    [
        new("a1", "A06", AlertSeverity.Warning, AlertState.Open, "destination", "Dysk USB", Now.AddHours(-3), "KB-2203"),
        new("a2", "A09", AlertSeverity.Security, AlertState.Notified, "job", "ERP Optima — noc", Now.AddHours(-30), "KB-2104", 1, true),
        new("a3", "A04", AlertSeverity.Critical, AlertState.Acknowledged, "job", "Dokumenty biura", Now.AddDays(-2), "KB-1510"),
        new("a4", "A99", AlertSeverity.Warning, AlertState.Silenced, "host", "", Now.AddHours(-1)),
    ];

    public Task<DashboardData> LoadAsync(CancellationToken ct) => Fail ? Task.FromException<DashboardData>(new IOException("down")) : Task.FromResult(Data);

    public Task RunJobAsync(string jobId, CancellationToken ct)
    {
        Runs.Add(jobId);
        return Task.CompletedTask;
    }

    public static DashboardData Mixed(CallerRole role = CallerRole.Operator)
    {
        var day = TimeSpan.FromDays(1);
        return new DashboardData(
        [
            new JobStatusInfo("j1", "Dokumenty biura", true, LastRunResult.Succeeded, Now.AddHours(-11.5), Now.AddHours(-11.5), Now.AddHours(12.5), day, 2, null, "NAS — Synology", 62, true),
            new JobStatusInfo("j2", "ERP Optima — noc", true, LastRunResult.Failed, Now.AddHours(-10), Now.AddDays(-3).AddHours(-2), Now.AddHours(12.5), day, 2, "KB-2104", "Serwer SFTP", 40, true),
            new JobStatusInfo("j3", "Zdjęcia", true, LastRunResult.Warning, Now.AddHours(-2), Now.AddHours(-2), Now.AddHours(22), day, 2, "KB-0107", "Dysk USB", 11, false, SkippedFiles: 3),
            new JobStatusInfo("j4", "Archiwum 2025", false, LastRunResult.Succeeded, Now.AddDays(-9), Now.AddDays(-9), null, null, 2, null, "Dysk USB", 11, false),
        ],
        [new DestinationSpace("NAS — Synology", 62), new DestinationSpace("Serwer SFTP", 40), new DestinationSpace("Dysk USB", 11)],
        role, Now.AddMinutes(-1));
    }

    public static DashboardData AllGood() => new(
        [
            new JobStatusInfo("j1", "Dokumenty biura", true, LastRunResult.Succeeded, Now.AddHours(-11.5), Now.AddHours(-11.5), Now.AddHours(12.5), TimeSpan.FromDays(1), 2, null, "NAS — Synology", 62, true),
            new JobStatusInfo("j5", "Poczta", true, LastRunResult.Succeeded, Now.AddMinutes(-50), Now.AddMinutes(-50), Now.AddMinutes(10), TimeSpan.FromHours(1), 2, null, "NAS — Synology", 62, true),
        ],
        [new DestinationSpace("NAS — Synology", 62)], CallerRole.Operator, Now);

    public static DashboardData Empty() => new([], [], CallerRole.Admin, Now);
}

/// <summary>RD-57: stała odpowiedź „administrator bez podniesienia UAC”.</summary>
public sealed class FakeElevation(bool canElevate) : Kobian.UI.Services.IElevationInfo
{
    public bool CanElevate { get; } = canElevate;
}
