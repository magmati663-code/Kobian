using System.Globalization;
using Kobian.UI.Dashboard;
using Kobian.UI.Jobs;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>SCR-02 (R2.6.1, D6.6): lista zadań — filtr, harmonogram słownie, wstrzymanie/wznowienie, odmowa KB, role, offline.</summary>
public sealed class JobsViewModelTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static JobsViewModel Vm(FakeJobsSource src) => new(src, new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc);

    private static async Task InPolish(Func<Task> f)
    {
        var (ui, cur) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        try { await f(); }
        finally { (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = (ui, cur); }
    }

    [Fact, Trait("Plan", "R2.6.1")]
    public Task Lists_all_jobs_worst_first_with_schedule_text_and_filter_counts() => InPolish(async () =>
    {
        var vm = Vm(new FakeJobsSource(FakeJobsSource.Mixed()));
        await vm.RefreshAsync(_ct);
        Assert.Equal(["ERP Optima — noc", "Archiwum 2025", "Zdjęcia", "Dokumenty biura"], vm.Rows.Select(r => r.Name));
        Assert.Equal(["Wszystkie (4)", "Wymagają uwagi (1)", "Wstrzymane (2)"], vm.Filters.Select(f => f.Label));
        var byName = vm.Rows.ToDictionary(r => r.Name);
        Assert.Equal("codziennie o 22:00", byName["Dokumenty biura"].ScheduleText);
        Assert.Equal("w dni robocze o 21:30", byName["ERP Optima — noc"].ScheduleText);
        Assert.Equal("wstrzymany (codziennie o 22:00)", byName["Zdjęcia"].ScheduleText);
        Assert.Equal("tylko ręcznie", byName["Archiwum 2025"].ScheduleText);
        Assert.Equal(ProtectionState.Paused, byName["Zdjęcia"].State); // wstrzymany harmonogram = brak kopii automatycznych
        Assert.Equal("Wznów", byName["Zdjęcia"].ToggleText);
        Assert.Equal("Harmonogram wstrzymany — kopie automatyczne nie są wykonywane.", byName["Zdjęcia"].Detail);
        Assert.Equal("Wstrzymaj", byName["Dokumenty biura"].ToggleText);
        Assert.False(byName["Archiwum 2025"].HasSchedule);
        Assert.Equal("KB-2104", byName["ERP Optima — noc"].Code);
        Assert.Equal("SCR-02.Job.j2", byName["ERP Optima — noc"].AutomationId);
    });

    [Fact]
    public Task Filter_attention_and_paused_and_keeps_selection_after_refresh() => InPolish(async () =>
    {
        var vm = Vm(new FakeJobsSource(FakeJobsSource.Mixed()));
        await vm.RefreshAsync(_ct);
        vm.SelectedFilter = vm.Filters.Single(f => f.Filter == JobsFilter.Attention);
        Assert.Equal(["ERP Optima — noc"], vm.Rows.Select(r => r.Name));
        vm.SelectedFilter = vm.Filters.Single(f => f.Filter == JobsFilter.Paused);
        Assert.Equal(["Archiwum 2025", "Zdjęcia"], vm.Rows.Select(r => r.Name));
        await vm.RefreshAsync(_ct);
        Assert.Equal(JobsFilter.Paused, vm.SelectedFilter.Filter);
        Assert.Equal(2, vm.Rows.Count);
        Assert.False(vm.FilterEmpty);
    });

    [Fact, Trait("Plan", "6.3")]
    public async Task Interval_from_cron_enables_overdue_detection()
    {
        var data = FakeJobsSource.Mixed();
        var stale = data.Jobs[0] with { LastSuccessUtc = FakeDashboardSource.Now.AddDays(-3) }; // codziennie, ostatnia 3 dni temu
        var vm = Vm(new FakeJobsSource(data with { Jobs = [stale] }));
        await vm.RefreshAsync(_ct);
        Assert.Equal(ProtectionReason.Overdue, vm.Rows[0].Reason);
    }

    [Fact]
    public Task Pause_sends_schedule_without_cron_and_resume_restores_saved_expression() => InPolish(async () =>
    {
        var src = new FakeJobsSource(FakeJobsSource.Mixed());
        var vm = Vm(src);
        await vm.RefreshAsync(_ct);
        await vm.ToggleScheduleCommand.ExecuteAsync(vm.Rows.Single(r => r.Name == "Dokumenty biura"));
        Assert.Equal(("j1", (string?)null), src.ScheduleCalls[^1]);
        Assert.Equal("Harmonogram zadania „Dokumenty biura” wstrzymany — uruchomienie ręczne nadal działa.", vm.Notice);
        Assert.False(vm.NoticeIsError);
        Assert.Equal(ProtectionState.Paused, vm.Rows.Single(r => r.Name == "Dokumenty biura").State); // lista odświeżona po akcji

        await vm.ToggleScheduleCommand.ExecuteAsync(vm.Rows.Single(r => r.Name == "Zdjęcia"));
        Assert.Equal(("j3", (string?)"0 22 * * *"), src.ScheduleCalls[^1]);
        Assert.Equal("wstrzymany (codziennie o 22:00)", vm.Rows.Single(r => r.Name == "Dokumenty biura").ScheduleText);
    });

    [Fact, Trait("Plan", "R2.6.5")]
    public Task Refusal_shows_kb_message_with_code_and_action() => InPolish(async () =>
    {
        var src = new FakeJobsSource(FakeJobsSource.Mixed()) { RefuseWith = "KB-0104" };
        var vm = Vm(src);
        await vm.RefreshAsync(_ct);
        await vm.RunCommand.ExecuteAsync(vm.Rows[0]);
        Assert.Equal(["j2"], src.Started);
        Assert.True(vm.NoticeIsError);
        Assert.Contains("(KB-0104)", vm.Notice, StringComparison.Ordinal);
    });

    [Fact]
    public Task Run_confirms_start() => InPolish(async () =>
    {
        var src = new FakeJobsSource(FakeJobsSource.Mixed());
        var vm = Vm(src);
        await vm.RefreshAsync(_ct);
        await vm.RunCommand.ExecuteAsync(vm.Rows.Single(r => r.Name == "Dokumenty biura"));
        Assert.Equal(["j1"], src.Started);
        Assert.Equal("Uruchomiono kopię zadania „Dokumenty biura”.", vm.Notice);
    });

    [Fact, Trait("Plan", "6.7")]
    public async Task Viewer_cannot_run_or_pause()
    {
        var vm = Vm(new FakeJobsSource(FakeJobsSource.Mixed(CallerRole.Viewer)));
        await vm.RefreshAsync(_ct);
        Assert.False(vm.CanAct);
        Assert.False(vm.RunCommand.CanExecute(vm.Rows[0]));
        Assert.False(vm.ToggleScheduleCommand.CanExecute(vm.Rows[0]));
        Assert.NotEmpty(vm.RunHint);
    }

    [Fact, Trait("Plan", "R2.6.4")]
    public async Task Offline_without_data_then_stale_banner_after_data()
    {
        var src = new FakeJobsSource(FakeJobsSource.Mixed()) { Fail = true };
        var vm = Vm(src);
        await vm.RefreshAsync(_ct);
        Assert.True(vm.IsOffline);
        Assert.False(vm.IsEmpty);
        Assert.Equal("KB-9001", vm.OfflineCode);
        src.Fail = false;
        await vm.RefreshAsync(_ct);
        src.Fail = true;
        await vm.RefreshAsync(_ct);
        Assert.False(vm.IsOffline);
        Assert.True(vm.ShowStaleBanner);
        Assert.Equal(4, vm.Rows.Count);
        await vm.RunCommand.ExecuteAsync(vm.Rows[0]);
        Assert.True(vm.NoticeIsError);
        Assert.Contains("KB-9001", vm.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_list_after_successful_read_and_new_job_request()
    {
        var vm = Vm(new FakeJobsSource(new JobsData([], [], CallerRole.Admin, FakeDashboardSource.Now)));
        var asked = false;
        vm.CreateJobRequested += (_, _) => asked = true;
        await vm.RefreshAsync(_ct);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasRows);
        vm.NewJobCommand.Execute(null);
        Assert.True(asked);
    }

    [Fact, Trait("Backlog", "BL-76"), Trait("Decision", "D7.10")]
    public Task Queued_and_running_jobs_show_an_activity_pill_without_changing_protection_state() => InPolish(async () =>
    {
        var data = FakeJobsSource.Mixed();
        var jobs = data.Jobs.Select(j => j.JobId switch
        {
            "j1" => j with { Activity = ActiveRun.Queued },
            "j2" => j with { Activity = ActiveRun.Running },
            _ => j,
        }).ToList();
        var vm = Vm(new FakeJobsSource(data with { Jobs = jobs }));
        await vm.RefreshAsync(_ct);
        var byId = vm.Rows.ToDictionary(r => r.Job.JobId);
        Assert.Equal(("W kolejce", true), (byId["j1"].ActivityText, byId["j1"].HasActivity));
        Assert.Equal("W toku", byId["j2"].ActivityText);
        Assert.False(byId["j3"].HasActivity);
        Assert.Null(byId["j3"].ActivityText);
        Assert.Contains("W kolejce", byId["j1"].AccessibleName, StringComparison.Ordinal);
        var plain = Vm(new FakeJobsSource(FakeJobsSource.Mixed()));
        await plain.RefreshAsync(_ct);
        Assert.Equal(plain.Rows.Single(r => r.Job.JobId == "j1").State, byId["j1"].State); // kolejka to informacja, nie stan ochrony 6.3
    });

    [Fact, Trait("Backlog", "BL-76")]
    public async Task Activity_pill_in_english()
    {
        var (ui, cur) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            var data = FakeJobsSource.Mixed();
            var vm = Vm(new FakeJobsSource(data with { Jobs = [.. data.Jobs.Select(j => j with { Activity = ActiveRun.Queued })] }));
            await vm.RefreshAsync(_ct);
            Assert.All(vm.Rows, r => Assert.Equal("Queued", r.ActivityText));
        }
        finally { (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = (ui, cur); }
    }
}

/// <summary>Opis harmonogramu dla ludzi i cykl z cron (6.9, 6.3).</summary>
public sealed class CronTextTests
{
    [Theory]
    [InlineData("0 22 * * *", "codziennie o 22:00", 24.0)]
    [InlineData("0 * * * *", "co godzinę", 1.0)]
    [InlineData("30 * * * *", "co godzinę, w minucie :30", 1.0)]
    [InlineData("15 21 * * 1-5", "w dni robocze o 21:15", 72.0)]
    [InlineData("0 3 * * 0", "co tydzień: niedziela, 03:00", 168.0)]
    [InlineData("0 3 * * 7", "co tydzień: niedziela, 03:00", 168.0)]
    [InlineData("*/5 * * * *", "własny harmonogram (*/5 * * * *)", null)]
    [InlineData("0 2 1 * *", "własny harmonogram (0 2 1 * *)", null)]
    [InlineData("61 2 * * *", "własny harmonogram (61 2 * * *)", null)]
    [InlineData("", "własny harmonogram ()", null)]
    public void Describes_common_patterns_in_polish(string cron, string text, double? hours)
    {
        var pl = new CultureInfo("pl-PL");
        Assert.Equal(text, CronText.Describe(cron, pl));
        Assert.Equal(hours is { } h ? TimeSpan.FromHours(h) : null, CronText.Interval(cron));
    }

    [Fact]
    public void Describes_in_english() =>
        Assert.Equal("weekdays at 07:05", CronText.Describe("5 7 * * 1-5", new CultureInfo("en-US")));
}
