using System.Globalization;
using Kobian.UI.History;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>SCR-10 (R2.6.1): historia kopii — grupy dni w strefie użytkownika, kolejność, filtr zadania, odtwarzanie, limit, offline.</summary>
public sealed class HistoryViewModelTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static HistoryViewModel Vm(FakeHistorySource src, TimeZoneInfo? zone = null) => new(src, new FixedTime(FakeDashboardSource.Now), zone ?? TimeZoneInfo.Utc);

    private static async Task InPolish(Func<Task> f)
    {
        var (ui, cur) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        try { await f(); }
        finally { (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = (ui, cur); }
    }

    [Fact, Trait("BL", "BL-25")]
    public Task Prune_runs_are_named_space_reclaim_with_warning_variant() => InPolish(async () =>
    {
        var n = FakeDashboardSource.Now;
        var vm = Vm(new FakeHistorySource(FakeHistorySource.Sample(),
            [new RunEntry("p1", "Zdjęcia", "prune", "Succeeded", n.AddMinutes(-20), null, null), new RunEntry("p2", "Zdjęcia", "prune", "Warning", n.AddMinutes(-10), "KB-1603", null)]));
        await vm.RefreshAsync(_ct);
        var rows = vm.Days[0].Rows.Where(r => r.Run is not null).ToList();
        Assert.Equal(["zwalnianie miejsca z uwagami", "zwalnianie miejsca"], rows.Select(r => r.ModeText));
        Assert.All(rows, r => Assert.False(r.CanRestore));
    });

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "R2.6.1")]
    public Task Runs_without_copy_are_listed_with_code_and_no_restore() => InPolish(async () =>
    {
        var vm = Vm(new FakeHistorySource(FakeHistorySource.Sample(), FakeHistorySource.SampleRuns()));
        await vm.RefreshAsync(_ct);
        var today = vm.Days[0];
        Assert.Equal("Dziś", today.Header);
        Assert.Equal("2 kopie · nieudane: 1", today.CountText);
        Assert.Equal(["08:30", "07:30", "07:00", "06:30", "02:30"], today.Rows.Select(r => r.Time)); // udany backup r1 = kopia, nie drugi wiersz
        var restore = today.Rows[0];
        Assert.Equal(("odtwarzanie", false, false, false), (restore.ModeText, restore.CanRestore, restore.IsFailed, restore.HasCode));
        var skipped = today.Rows[3];
        Assert.Equal(("pominięta", "KB-4103", false), (skipped.ModeText, skipped.ShortId, skipped.CanRestore));
        Assert.Equal(HistoryPill.Neutral, skipped.Pill);
        var failed = today.Rows[4];
        Assert.Equal(("nieudana", "KB-2104", true), (failed.ModeText, failed.ShortId, failed.IsFailed));
        Assert.Contains("KB-2104", failed.AccessibleName, StringComparison.Ordinal);
        Assert.Equal("", failed.FilesText);
        var check = vm.Days[1].Rows.Single(r => r.Run is not null);
        Assert.Equal(("weryfikacja z uwagami", true), (check.ModeText, check.IsWarning));

        // Restore z wiersza uruchomienia nie wywołuje SCR-06.
        SnapshotEntry? asked = null;
        vm.RestoreRequested += (_, e) => asked = e;
        vm.RestoreCommand.Execute(failed);
        Assert.Null(asked);
        vm.RestoreCommand.Execute(today.Rows[1]);
        Assert.Equal("a1b2c3d4e5f60718", asked?.Id);

        vm.SelectedJob = vm.JobOptions.Single(o => o.Job == "ERP Optima — noc");
        Assert.Equal(["02:30", "08:30", "21:30"], vm.Days.SelectMany(d => d.Rows).Select(r => r.Time));
    });

    [Fact, Trait("BL", "BL-60")]
    public async Task Source_without_list_runs_keeps_copy_only_history()
    {
        var vm = Vm(new FakeHistorySource(FakeHistorySource.Sample()));
        await vm.RefreshAsync(_ct);
        Assert.All(vm.Days.SelectMany(d => d.Rows), r => Assert.True(r.CanRestore));
        Assert.Equal(5, vm.Days.Sum(d => d.Rows.Count));
    }

    [Fact, Trait("Plan", "R2.6.1")]
    public Task Groups_by_local_day_newest_first_with_polish_texts() => InPolish(async () =>
    {
        var vm = Vm(new FakeHistorySource(FakeHistorySource.Sample()));
        await vm.RefreshAsync(_ct);
        Assert.Equal(["Dziś", "Wczoraj", "Poniedziałek, 12 października 2026", "Niedziela, 11 października 2026"], vm.Days.Select(d => d.Header));
        Assert.Equal(["2 kopie", "1 kopia", "1 kopia", "1 kopia"], vm.Days.Select(d => d.CountText));
        var first = vm.Days[0].Rows[0];
        Assert.Equal("07:30", first.Time);
        Assert.Equal("Dokumenty biura", first.Job);
        Assert.Equal("przyrostowa", first.ModeText);
        Assert.Equal(12480.ToString("N0", CultureInfo.CurrentCulture) + " plików", first.FilesText); // separator tysięcy wg kultury
        Assert.Equal("3 GB", first.SizeText);
        Assert.Equal("a1b2c3d4", first.ShortId);
        Assert.Equal("1 plik", vm.Days[0].Rows[1].FilesText);
        Assert.Equal("912 B", vm.Days[0].Rows[1].SizeText);
        Assert.True(vm.Days[2].Rows[0].IsFull);
        Assert.Equal("pełna", vm.Days[2].Rows[0].ModeText);
        Assert.Equal("5 kopii · ostatnia dziś 07:30", vm.Summary);
        Assert.Equal(["Wszystkie zadania", "Dokumenty biura", "ERP Optima — noc", "Zdjęcia"], vm.JobOptions.Select(o => o.Text));
    });

    [Fact]
    public async Task Day_boundary_follows_users_time_zone()
    {
        // 22:00 UTC 13.10 = 00:00 14.10 w Warszawie (CEST, UTC+2) → „Dziś”, nie „Wczoraj”.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var snaps = new List<SnapshotEntry> { new("aa", "A", new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.Zero), "full", 1, 1) };
        var vm = Vm(new FakeHistorySource(snaps), zone);
        await InPolish(() => vm.RefreshAsync(_ct));
        Assert.Equal("Dziś", vm.Days.Single().Header);
        Assert.Equal("00:00", vm.Days[0].Rows[0].Time);
    }

    [Fact]
    public Task Job_filter_narrows_list_and_survives_refresh() => InPolish(async () =>
    {
        var vm = Vm(new FakeHistorySource(FakeHistorySource.Sample()));
        await vm.RefreshAsync(_ct);
        vm.SelectedJob = vm.JobOptions.Single(o => o.Job == "Dokumenty biura");
        Assert.Equal(3, vm.Days.Sum(d => d.Rows.Count));
        Assert.All(vm.Days.SelectMany(d => d.Rows), r => Assert.Equal("Dokumenty biura", r.Job));
        await vm.RefreshAsync(_ct);
        Assert.Equal("Dokumenty biura", vm.SelectedJob.Job);
        Assert.Equal(3, vm.Days.Sum(d => d.Rows.Count));
    });

    [Fact]
    public async Task Restore_action_raises_request_with_snapshot()
    {
        var vm = Vm(new FakeHistorySource(FakeHistorySource.Sample()));
        await vm.RefreshAsync(_ct);
        SnapshotEntry? asked = null;
        vm.RestoreRequested += (_, s) => asked = s;
        vm.RestoreCommand.Execute(vm.Days[0].Rows[0]);
        Assert.Equal("a1b2c3d4e5f60718", asked?.Id);
    }

    [Fact]
    public async Task Long_history_is_capped_and_flagged()
    {
        var n = FakeDashboardSource.Now;
        var many = Enumerable.Range(0, HistoryViewModel.MaxRows + 20)
            .Select(i => new SnapshotEntry(i.ToString("x16", CultureInfo.InvariantCulture), "A", n.AddHours(-i), "incremental", 1, 1)).ToList();
        var vm = Vm(new FakeHistorySource(many));
        await vm.RefreshAsync(_ct);
        Assert.True(vm.Truncated);
        Assert.Equal(HistoryViewModel.MaxRows, vm.Days.Sum(d => d.Rows.Count));
    }

    [Fact, Trait("Plan", "R2.6.4")]
    public async Task Empty_and_offline_states_are_distinct()
    {
        var src = new FakeHistorySource([]) { Fail = true };
        var vm = Vm(src);
        await vm.RefreshAsync(_ct);
        Assert.True(vm.IsOffline);
        Assert.False(vm.IsEmpty);
        src.Fail = false;
        await vm.RefreshAsync(_ct);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.IsOffline);
        Assert.Equal("", vm.Summary);
    }

    [Fact, Trait("Backlog", "BL-76")]
    public Task Queued_run_is_labelled_as_queued_not_running() => InPolish(async () =>
    {
        var n = FakeDashboardSource.Now;
        var vm = Vm(new FakeHistorySource([],
            [new RunEntry("q1", "Zdjęcia", "backup", "Queued", n.AddMinutes(-5), null, null), new RunEntry("r1", "Zdjęcia", "backup", "Running", n.AddMinutes(-4), null, null)]));
        await vm.RefreshAsync(_ct);
        var rows = vm.Days.SelectMany(d => d.Rows).ToList();
        Assert.Equal(["w toku", "w kolejce"], rows.Select(r => r.ModeText));
        Assert.All(rows, r => Assert.Equal(HistoryPill.Neutral, r.Pill));
    });
}

/// <summary>Rozmiary dla ludzi (6.9).</summary>
public sealed class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1,5 KB")]
    [InlineData(157_286_400L, "150 MB")]
    [InlineData(5_368_709_120L, "5 GB")]
    [InlineData(-5L, "0 B")]
    public void Formats_in_polish(long bytes, string text) => Assert.Equal(text, ByteSize.Format(bytes, new CultureInfo("pl-PL")));

    [Fact]
    public void Formats_in_english() => Assert.Equal("1.5 KB", ByteSize.Format(1536, new CultureInfo("en-US")));
}
