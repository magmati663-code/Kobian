using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kobian.Api.V1;
using Kobian.UI.Dashboard;
using Kobian.UI.History;
using Kobian.UI.Restore;
using Kobian.UI.Services;
using Kobian.UI.Shell;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>Usługa odtwarzania plików i baz (atrapa): wynik bazy zadany w teście.</summary>
internal sealed class FakeDbRestore(DbRestoreResult result) : IRestoreService, IDatabaseRestoreService
{
    public List<(string Snapshot, string? Target)> Calls { get; } = [];

    public Exception? Throw { get; set; }

    public Task<RestoreRunResult> RestoreAsync(string snapshotId, string? exactPath, string target, CancellationToken ct) =>
        Task.FromResult(new RestoreRunResult(RestoreRunState.Succeeded, null));

    public Task<DbRestoreResult> RestoreDatabaseAsync(string snapshotId, string? targetDatabase, CancellationToken ct)
    {
        Calls.Add((snapshotId, targetDatabase));
        return Throw is not null ? throw Throw : Task.FromResult(result);
    }
}

/// <summary>SCR-08 Odtwarzanie bazy (BL-68 cz. 3, AC-32.1): wejście z przeglądarki dla kopii bazy, nazwa docelowa, wynik, adapter, zrzuty.</summary>
public sealed class DbRestoreScreenTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static readonly SnapshotEntry DbSnap = new("dbdbdbdb11223344", "ERP Optima — baza firmowa", FakeDashboardSource.Now.AddHours(-1), "database", 1, 52_428_800);

    private static IReadOnlyList<SnapshotEntry> Snaps() => [DbSnap, .. FakeHistorySource.Sample()];

    [Fact, Trait("AC", "AC-32.1"), Trait("BL", "BL-68")]
    public async Task Default_name_is_sent_as_null_and_custom_name_is_validated()
    {
        var svc = new FakeDbRestore(new DbRestoreResult(RestoreRunState.Succeeded, null, "Firma_restore_20261014"));
        var vm = new DbRestoreViewModel(svc, DbSnap, TimeZoneInfo.Utc);
        Assert.True(vm.CanStart);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal((DbSnap.Id, (string?)null), svc.Calls.Single());
        Assert.True(vm.IsSuccess);
        Assert.Contains("Firma_restore_20261014", vm.ResultTitle, StringComparison.Ordinal);

        var vm2 = new DbRestoreViewModel(svc, DbSnap, TimeZoneInfo.Utc) { TargetName = new string('a', 129) };
        Assert.True(vm2.HasNameError);
        Assert.False(vm2.CanStart);
        vm2.TargetName = "  Firma_test  ";
        await vm2.StartCommand.ExecuteAsync(null);
        Assert.Equal("Firma_test", svc.Calls[^1].Target);
    }

    [Fact, Trait("AC", "AC-32.2")]
    public async Task Existing_database_and_offline_show_codes_and_allow_retry()
    {
        var vm = new DbRestoreViewModel(new FakeDbRestore(new DbRestoreResult(RestoreRunState.Failed, "KB-3501", null)), DbSnap, TimeZoneInfo.Utc);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsFailure);
        Assert.Contains("KB-3501", vm.ResultDetail, StringComparison.Ordinal);
        vm.RetryCommand.Execute(null);
        Assert.True(vm.IsForm);
        var off = new DbRestoreViewModel(new FakeDbRestore(new DbRestoreResult(RestoreRunState.Succeeded, null, "x")) { Throw = new IOException("offline") }, DbSnap, TimeZoneInfo.Utc);
        await off.StartCommand.ExecuteAsync(null);
        Assert.Contains("KB-9001", off.ResultDetail, StringComparison.Ordinal);
    }

    [Fact, Trait("AC", "AC-32.1"), Trait("D", "D5.11")]
    public async Task Control_query_mismatch_is_a_warning_with_the_database_name_not_a_failure()
    {
        var vm = new DbRestoreViewModel(new FakeDbRestore(new DbRestoreResult(RestoreRunState.Warning, "KB-3505", "Firma_restore_20261014")), DbSnap, TimeZoneInfo.Utc);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsWarning);
        Assert.False(vm.IsSuccess);
        Assert.False(vm.IsFailure); // baza istnieje — nie oferujemy „Ponów”
        Assert.Contains("Firma_restore_20261014", vm.ResultTitle, StringComparison.Ordinal);
        Assert.Contains("KB-3505", vm.ResultDetail, StringComparison.Ordinal);
    }

    [Fact, Trait("BL", "BL-68")]
    public async Task Browser_offers_database_restore_only_for_database_snapshots_and_shell_opens_it()
    {
        var browser = new SnapshotBrowserViewModel(new FakeBrowseSource(Snaps()), TimeZoneInfo.Utc);
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed())), browser: browser,
            restoreService: new FakeDbRestore(new DbRestoreResult(RestoreRunState.Succeeded, null, "x")));
        await browser.RefreshAsync(_ct);
        Assert.Equal(DbSnap.Id, browser.SelectedSnapshot!.Entry.Id);
        Assert.True(browser.IsDatabaseSnapshot);
        Assert.False(browser.CanRestore); // plików z kopii bazy nie odtwarzamy tą drogą
        Assert.Equal("kopia bazy", browser.SelectedSnapshot.Mode);
        browser.RestoreDatabaseCommand.Execute(null);
        var vm = Assert.IsType<DbRestoreViewModel>(shell.CurrentPage);
        Assert.Equal(DbSnap.Id, vm.Snapshot.Id);
        vm.CloseCommand.Execute(null);
        browser.SelectedSnapshot = browser.Snapshots[1];
        Assert.False(browser.IsDatabaseSnapshot);
        Assert.True(browser.CanRestore);
    }

    [Fact]
    public void Grpc_adapter_maps_database_restore()
    {
        Assert.Equal(new DbRestoreResult(RestoreRunState.Succeeded, null, "N"), GrpcDashboardSource.MapDbRestore(new RestoreDatabaseReply { Result = OverallState.Ok, TargetDatabase = "N" }));
        Assert.Equal(new DbRestoreResult(RestoreRunState.Failed, "KB-3501", null), GrpcDashboardSource.MapDbRestore(new RestoreDatabaseReply { Result = OverallState.Failed, Code = "KB-3501" }));
        // D5.11: KB-3505 = baza utworzona, ale niezgodna z zapytaniami kontrolnymi → ostrzeżenie z nazwą bazy (nie błąd)
        Assert.Equal(new DbRestoreResult(RestoreRunState.Warning, "KB-3505", "N"), GrpcDashboardSource.MapDbRestore(new RestoreDatabaseReply { Result = OverallState.Warning, Code = "KB-3505", TargetDatabase = "N" }));
    }

    public static TheoryData<string, string, int> Shots() => new()
    {
        { "form", "Light", 1180 },
        { "done", "Dark", 1180 },
        { "exists", "Light", 800 },
        { "browser", "Light", 1180 },
    };

    [Theory, MemberData(nameof(Shots)), Trait("Plan", "R2.6.1"), Trait("BL", "BL-68")]
    public Task Db_restore_screen_renders(string state, string theme, int width) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var result = state == "exists" ? new DbRestoreResult(RestoreRunState.Failed, "KB-3501", null) : new DbRestoreResult(RestoreRunState.Succeeded, null, "Firma_restore_20261014");
        var browser = new SnapshotBrowserViewModel(new FakeBrowseSource(Snaps()), TimeZoneInfo.Utc);
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed())), browser: browser, restoreService: new FakeDbRestore(result));
        shell.Navigate("SCR-06");
        await browser.RefreshAsync(CancellationToken.None);
        if (state != "browser")
        {
            browser.RestoreDatabaseCommand.Execute(null);
            var vm = Assert.IsType<DbRestoreViewModel>(shell.CurrentPage);
            if (state == "exists") vm.TargetName = "Firma";
            if (state is "done" or "exists") await vm.StartCommand.ExecuteAsync(null);
        }
        var window = new MainWindow { DataContext = shell, Width = width, Height = width < 1000 ? 1100 : 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"SCR-08-{state}-{theme}-{width}.png");
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 10_000, "zrzut pusty");
        var visible = window.GetVisualDescendants().OfType<Control>()
            .Where(x => x.IsEffectivelyVisible).Select(Avalonia.Automation.AutomationProperties.GetAutomationId).Where(i => i is not null).ToHashSet();
        if (state == "browser")
        {
            Assert.Contains("SCR-06.RestoreDatabase", visible);
            Assert.DoesNotContain("SCR-06.RestoreFolder", visible);
        }
        else
        {
            Assert.Contains("SCR-08.Title", visible);
            Assert.Equal(state == "form", visible.Contains("SCR-08.Start"));
            Assert.Equal(state is "done" or "exists", visible.Contains("SCR-08.Result"));
        }
        window.Close();
    }, _ct);
}
