using Kobian.UI.History;
using Kobian.UI.Jobs;
using Kobian.UI.Dashboard;
using Kobian.UI.Shell;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>Powłoka (6.2): SCR-02 i SCR-10 jako prawdziwe ekrany, odświeżenie przy wejściu, przejścia „Odtwórz” → SCR-06 i „Nowe zadanie” → SCR-03.</summary>
public sealed class ShellNavigationTests
{
    private static ShellViewModel Shell(FakeJobsSource jobs, FakeHistorySource history) => new(
        new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed()), new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc),
        new JobsViewModel(jobs, new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc),
        new HistoryViewModel(history, new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc));

    [Fact, Trait("Plan", "R2.6.1")]
    public void Entering_screens_refreshes_them_and_placeholders_are_gone()
    {
        var shell = Shell(new FakeJobsSource(FakeJobsSource.Mixed()), new FakeHistorySource(FakeHistorySource.Sample()));
        Assert.Same(shell.Dashboard, shell.CurrentPage);
        shell.Navigate("SCR-02");
        Assert.IsType<JobsViewModel>(shell.CurrentPage);
        Assert.Equal(4, shell.Jobs!.Rows.Count);
        shell.Navigate("SCR-10");
        Assert.IsType<HistoryViewModel>(shell.CurrentPage);
        Assert.Equal(5, shell.History!.Days.Sum(d => d.Rows.Count));
    }

    [Fact]
    public async Task Restore_from_history_goes_to_restore_screen_and_new_job_opens_wizard_placeholder()
    {
        var shell = Shell(new FakeJobsSource(FakeJobsSource.Mixed()), new FakeHistorySource(FakeHistorySource.Sample()));
        await shell.History!.RefreshAsync(TestContext.Current.CancellationToken);
        shell.History.RestoreCommand.Execute(shell.History.Days[0].Rows[0]);
        Assert.Equal("SCR-06", shell.Selected.ScreenId);
        shell.Jobs!.NewJobCommand.Execute(null);
        Assert.IsType<PlaceholderPage>(shell.CurrentPage);
        Assert.Equal("SCR-06", shell.Selected.ScreenId); // kreator poza nawigacją — zaznaczenie bez zmian
    }

    [Fact]
    public void Without_sources_screens_stay_placeholders()
    {
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Empty())));
        Assert.IsType<PlaceholderPage>(shell.Items.Single(i => i.ScreenId == "SCR-02").Page);
        Assert.IsType<PlaceholderPage>(shell.Items.Single(i => i.ScreenId == "SCR-10").Page);
    }
}
