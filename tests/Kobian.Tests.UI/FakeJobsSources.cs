using Kobian.UI.Dashboard;
using Kobian.UI.History;
using Kobian.UI.Jobs;

namespace Kobian.Tests.UI;

/// <summary>Atrapa SCR-02 (dane syntetyczne): zapisuje wywołania akcji, może odmówić kodem KB albo „zerwać połączenie”.</summary>
public sealed class FakeJobsSource(JobsData data) : IJobsSource
{
    public JobsData Data { get; set; } = data;

    public bool Fail { get; set; }

    public string? RefuseWith { get; set; }

    public List<string> Started { get; } = [];

    public List<(string Job, string? Cron)> ScheduleCalls { get; } = [];

    public Task<JobsData> LoadJobsAsync(CancellationToken ct) => Fail ? Task.FromException<JobsData>(new IOException("down")) : Task.FromResult(Data);

    public Task<string?> StartJobAsync(string job, CancellationToken ct)
    {
        if (Fail) return Task.FromException<string?>(new IOException("down"));
        Started.Add(job);
        return Task.FromResult(RefuseWith);
    }

    public Task<string?> SetScheduleAsync(string job, string? cron, CancellationToken ct)
    {
        ScheduleCalls.Add((job, cron));
        if (RefuseWith is null)
            Data = Data with { Schedules = Data.Schedules.Select(s => s.Job == job ? s with { Enabled = cron is not null, Cron = cron ?? s.Cron } : s).ToList() };
        return Task.FromResult(RefuseWith);
    }

    /// <summary>Zadania z pulpitu (Mixed) + harmonogramy: j1 codziennie, j2 dni robocze, j3 wstrzymany, j4 brak (ręczne).</summary>
    public static JobsData Mixed(CallerRole role = CallerRole.Operator)
    {
        var d = FakeDashboardSource.Mixed(role);
        var jobs = d.Jobs.Select(j => j with { Interval = null }).ToList(); // cykl ma wynikać z cron (adapter gRPC go nie podaje)
        return new JobsData(jobs,
        [
            new JobSchedule("j1", "0 22 * * *", true),
            new JobSchedule("j2", "30 21 * * 1-5", true),
            new JobSchedule("j3", "0 22 * * *", false),
        ], role, d.AsOfUtc);
    }
}

/// <summary>Atrapa SCR-10: kopie syntetyczne (czas względem <see cref="FakeDashboardSource.Now"/> = 2026-10-14 09:30 UTC).</summary>
public sealed class FakeHistorySource(IReadOnlyList<SnapshotEntry> snapshots, IReadOnlyList<RunEntry>? runs = null) : IHistorySource
{
    public IReadOnlyList<SnapshotEntry> Snapshots { get; set; } = snapshots;

    /// <summary>null = źródło bez ListRuns (zachowanie domyślne interfejsu — starszy Agent).</summary>
    public IReadOnlyList<RunEntry>? Runs { get; set; } = runs;

    public Task<IReadOnlyList<RunEntry>> ListRunsAsync(string? job, CancellationToken ct) =>
        Fail ? Task.FromException<IReadOnlyList<RunEntry>>(new IOException("down"))
             : Task.FromResult<IReadOnlyList<RunEntry>>((Runs ?? []).Where(r => job is null || r.Job == job).ToList());

    /// <summary>Uruchomienia do <see cref="Sample"/>: udany backup (ukryty — jest kopia), nieudany z KB, pominięty, odtwarzanie, weryfikacja z ostrzeżeniem.</summary>
    public static IReadOnlyList<RunEntry> SampleRuns()
    {
        var n = FakeDashboardSource.Now;
        return
        [
            new("r1", "Dokumenty biura", "backup", "Succeeded", n.AddHours(-2), null, "a1b2c3d4e5f60718"),
            new("r2", "ERP Optima — noc", "backup", "Failed", n.AddHours(-7), "KB-2104", null),
            new("r3", "Zdjęcia", "backup", "Skipped", n.AddHours(-3), "KB-4103", null),
            new("r4", "Dokumenty biura", "restore", "Succeeded", n.AddHours(-1), null, null),
            new("r5", "ERP Optima — noc", "check", "Warning", n.AddDays(-1).AddHours(-1), "KB-1502", null),
        ];
    }

    public bool Fail { get; set; }

    public Task<IReadOnlyList<SnapshotEntry>> ListSnapshotsAsync(string? job, CancellationToken ct) =>
        Fail ? Task.FromException<IReadOnlyList<SnapshotEntry>>(new IOException("down"))
             : Task.FromResult<IReadOnlyList<SnapshotEntry>>(Snapshots.Where(s => job is null || s.Job == job).ToList());

    public static IReadOnlyList<SnapshotEntry> Sample()
    {
        var n = FakeDashboardSource.Now;
        return
        [
            new("a1b2c3d4e5f60718", "Dokumenty biura", n.AddHours(-2), "incremental", 12_480, 3_221_225_472),
            new("0f1e2d3c4b5a6978", "Zdjęcia", n.AddHours(-2.5), "incremental", 1, 912),
            new("9a8b7c6d5e4f3021", "Dokumenty biura", n.AddHours(-11.5), "incremental", 12_402, 3_200_000_000),
            new("1122334455667788", "ERP Optima — noc", n.AddDays(-1).AddHours(-12), "full", 3, 52_428_800),
            new("ffeeddccbbaa9988", "Dokumenty biura", n.AddDays(-3), "full", 12_300, 3_100_000_000),
        ];
    }
}

/// <summary>Zegar stały dla testów ViewModeli (bez zależności od czasu systemowego).</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
