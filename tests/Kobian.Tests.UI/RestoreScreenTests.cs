using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Grpc.Core;
using Kobian.Api.V1;
using Kobian.UI.Dashboard;
using Kobian.UI.History;
using Kobian.UI.Restore;
using Kobian.UI.Services;
using Kobian.UI.Shell;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>Atrapa RPC Restore: zapamiętuje żądanie, zwraca zadany wynik albo rzuca (offline/odmowa).</summary>
internal sealed class FakeRestoreService(RestoreRunResult result) : IRestoreService
{
    public List<(string Snapshot, string? Exact, string Target)> Calls { get; } = [];

    public Exception? Throw { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Wynik PlanRestore (it. 98); null = usługa bez podglądu (starszy Agent).</summary>
    public RestorePreview? Preview { get; set; }

    public int PlanCalls { get; private set; }

    public Task<RestorePreview?> PlanAsync(string snapshotId, string? exactPath, string target, CancellationToken ct)
    {
        PlanCalls++;
        return Task.FromResult(Preview);
    }

    public async Task<RestoreRunResult> RestoreAsync(string snapshotId, string? exactPath, string target, CancellationToken ct)
    {
        Calls.Add((snapshotId, exactPath, target));
        if (Gate is not null) await Gate.Task;
        if (Throw is not null) throw Throw;
        return result;
    }
}

/// <summary>SCR-07 Odtwarzanie plików (BL-64, AC-07.1–07.3): filtr z wyboru w SCR-06, walidacja folderu, wynik z KB, powłoka, adapter, zrzuty.</summary>
public sealed class RestoreScreenTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private static readonly SnapshotEntry Snap = FakeHistorySource.Sample()[0];
    private static readonly string Abs = OperatingSystem.IsWindows() ? @"D:\Odtworzone" : "/srv/odtworzone";

    private static RestoreViewModel Vm(IRestoreService s, string path = "Faktury/2026", bool dir = true) =>
        new(s, new RestoreSelection(Snap, path, dir), TimeZoneInfo.Utc);

    [Theory, Trait("AC", "AC-07.1"), Trait("BL", "BL-64")]
    [InlineData("", true, null)]
    [InlineData("Faktury/2026", true, "Faktury/2026")]
    [InlineData("Umowy/wzór umowy.docx", false, "Umowy/wzór umowy.docx")]
    public void Exact_path_follows_the_selection(string path, bool dir, string? exact) =>
        Assert.Equal(exact, Vm(new FakeRestoreService(new(RestoreRunState.Succeeded, null)), path, dir).ExactPath);

    [Fact, Trait("BL", "BL-64")]
    public async Task Start_requires_an_absolute_existing_style_path_and_sends_the_request()
    {
        var svc = new FakeRestoreService(new(RestoreRunState.Succeeded, null));
        var vm = Vm(svc);
        Assert.False(vm.CanStart);
        vm.Target = "odtworzone";
        Assert.True(vm.HasTargetError);
        Assert.False(vm.CanStart);
        vm.Target = "  " + Abs + "  ";
        Assert.True(vm.CanStart);
        Assert.Contains("Faktury", vm.PreviewText, StringComparison.Ordinal);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal((Snap.Id, "Faktury/2026", Abs), svc.Calls.Single());
        Assert.True(vm.IsDone);
        Assert.True(vm.IsSuccess);
        Assert.False(vm.HasResultDetail);
    }

    [Fact, Trait("AC", "AC-07.2"), Trait("BL", "BL-64")]
    public async Task Warning_shows_the_no_overwrite_code_and_failure_allows_retry()
    {
        var vm = Vm(new FakeRestoreService(new(RestoreRunState.Warning, "KB-5105")));
        vm.Target = Abs;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsWarning);
        Assert.Contains("KB-5105", vm.ResultDetail, StringComparison.Ordinal);

        var off = new FakeRestoreService(new(RestoreRunState.Succeeded, null)) { Throw = new IOException("offline") };
        var vm2 = Vm(off);
        vm2.Target = Abs;
        await vm2.StartCommand.ExecuteAsync(null);
        Assert.True(vm2.IsFailure);
        Assert.Contains("KB-9001", vm2.ResultDetail, StringComparison.Ordinal);
        vm2.RetryCommand.Execute(null);
        Assert.True(vm2.IsForm);
        Assert.Equal(Abs, vm2.Target);
        Assert.Null(vm2.Result);
    }

    [Fact, Trait("BL", "BL-64")]
    public async Task Cannot_close_while_running()
    {
        var svc = new FakeRestoreService(new(RestoreRunState.Succeeded, null)) { Gate = new TaskCompletionSource() };
        var vm = Vm(svc);
        vm.Target = Abs;
        var closed = 0;
        vm.Closed += (_, _) => closed++;
        var run = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        vm.CloseCommand.Execute(null);
        Assert.Equal(0, closed);
        svc.Gate.SetResult();
        await run;
        vm.CloseCommand.Execute(null);
        Assert.Equal(1, closed);
    }

    [Fact, Trait("BL", "BL-64")]
    public async Task Shell_opens_restore_from_browser_and_returns_or_shows_history()
    {
        var browser = new SnapshotBrowserViewModel(new FakeBrowseSource(FakeHistorySource.Sample()), TimeZoneInfo.Utc);
        var history = new HistoryViewModel(new FakeHistorySource(FakeHistorySource.Sample(), FakeHistorySource.SampleRuns()), new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc);
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed())), history: history, browser: browser,
            restoreService: new FakeRestoreService(new(RestoreRunState.Succeeded, null)));
        shell.Navigate("SCR-06");
        await browser.RefreshAsync(_ct);
        browser.RestoreItemCommand.Execute(browser.Rows.Single(r => r.Name == "notatki.txt"));
        var vm = Assert.IsType<RestoreViewModel>(shell.CurrentPage);
        Assert.Equal(("notatki.txt", false), (vm.Selection.Path, vm.Selection.IsDirectory));
        vm.CloseCommand.Execute(null);
        Assert.Same(browser, shell.CurrentPage);

        browser.RestoreFolderCommand.Execute(null);
        var vm2 = Assert.IsType<RestoreViewModel>(shell.CurrentPage);
        vm2.ShowHistoryCommand.Execute(null);
        Assert.Equal("SCR-10", shell.Selected.ScreenId);
        Assert.Same(history, shell.CurrentPage);

        // bez usługi odtwarzania — zaślepka zamiast udawanego działania
        var bare = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed())), browser: browser);
        bare.OpenRestore(new RestoreSelection(Snap, "", true));
        Assert.IsType<PlaceholderPage>(bare.CurrentPage);
    }

    [Fact, Trait("BL", "BL-64")]
    public void Grpc_adapter_maps_restore_outcome()
    {
        Assert.Equal(new RestoreRunResult(RestoreRunState.Succeeded, null), GrpcDashboardSource.MapRestore(new RunOutcome { Result = OverallState.Ok }));
        Assert.Equal(new RestoreRunResult(RestoreRunState.Warning, "KB-5105"), GrpcDashboardSource.MapRestore(new RunOutcome { Result = OverallState.Warning, Code = "KB-5105" }));
        Assert.Equal(new RestoreRunResult(RestoreRunState.Failed, "KB-0001"), GrpcDashboardSource.MapRestore(new RunOutcome { Result = OverallState.Failed }));
        _ = StatusCode.PermissionDenied; // odmowa roli → KB-7011 (ścieżka w adapterze, test przez pipe w Kobian.Tests.Unit)
    }

    private static RestorePreview Conflicting(int n) =>
        new(null, 12, 9_000_000, n, [.. Enumerable.Range(1, Math.Min(n, 20)).Select(i => $"Faktury/2026/FV-{i:000}.pdf")], 0, 120L * 1024 * 1024 * 1024, null);

    [Fact, Trait("BL", "BL-64"), Trait("Plan", "R2.8.1")]
    public async Task Clean_plan_restores_in_one_click_and_shows_the_numbers()
    {
        var svc = new FakeRestoreService(new(RestoreRunState.Succeeded, null)) { Preview = new(null, 3, 2048, 0, [], 0, 1_000_000, null) };
        var vm = Vm(svc);
        vm.Target = Abs;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal((1, 1), (svc.PlanCalls, svc.Calls.Count));
        Assert.True(vm.IsDone);
        Assert.Contains("3", vm.PlanSummary, StringComparison.Ordinal);
        Assert.Contains("KB", vm.PlanSummary, StringComparison.Ordinal);
        Assert.False(vm.HasPlanConflicts);
    }

    [Fact, Trait("BL", "BL-64"), Trait("AC", "AC-07.1")]
    public async Task Conflicts_stop_before_restore_and_need_a_second_explicit_click()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("pl-PL");
        var svc = new FakeRestoreService(new(RestoreRunState.Warning, "KB-5105")) { Preview = Conflicting(8) };
        var vm = Vm(svc);
        vm.Target = Abs;
        var start = vm.StartText;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Empty(svc.Calls); // nic nie odtworzono — użytkownik widzi pominięcia
        Assert.True(vm.IsForm);
        Assert.True(vm.HasPlanConflicts);
        Assert.NotEqual(start, vm.StartText); // „Odtwórz mimo to”
        Assert.Contains("8", vm.PlanConflictsText, StringComparison.Ordinal);
        Assert.Equal(RestoreViewModel.MaxShownConflicts + 1, vm.PlanConflictSample.Count);
        Assert.Contains("3", vm.PlanConflictSample[^1], StringComparison.Ordinal); // „…i jeszcze 3”
        Assert.True(vm.CanStart);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal((1, 1), (svc.PlanCalls, svc.Calls.Count));
        Assert.True(vm.IsWarning);

        vm.RetryCommand.Execute(null); // powrót do formularza: stan folderu po odtworzeniu jest inny → podgląd od nowa
        Assert.Null(vm.Plan);
        Assert.Equal("Odtwórz", vm.StartText);
    }

    [Fact, Trait("Plan", "D8.8"), Trait("RD", "RD-68")]
    public async Task Resumable_files_are_shown_as_info_and_do_not_need_a_second_click()
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        var svc = new FakeRestoreService(new(RestoreRunState.Succeeded, null)) { Preview = new(null, 12, 9_000, 0, [], 0, null, null, Resumable: 5), Gate = new TaskCompletionSource() };
        var vm = Vm(svc);
        vm.Target = Abs;
        var run = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning); // bez zatrzymania — pominięcia z przerwanej próby to nie konflikt
        Assert.True(vm.HasPlanResumable);
        Assert.Contains("5", vm.PlanResumableText, StringComparison.Ordinal);
        Assert.Contains("7", vm.RunningText, StringComparison.Ordinal); // 12 − 5 do zapisania
        svc.Gate.SetResult();
        await run;
        Assert.True(vm.IsSuccess);
    }

    [Theory, Trait("BL", "BL-64"), Trait("AC", "AC-07.2")]
    [InlineData("KB-5106", null)]
    [InlineData(null, "KB-5002")]
    [InlineData("KB-5001", null)]
    public async Task Blocking_plan_disables_start_until_the_folder_changes(string? blocking, string? code)
    {
        var svc = new FakeRestoreService(new(RestoreRunState.Succeeded, null)) { Preview = new(code, 3, 9_000, 0, [], 0, 10, blocking) };
        var vm = Vm(svc);
        vm.Target = Abs;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Empty(svc.Calls);
        Assert.True(vm.HasPlanBlock);
        Assert.False(vm.CanStart);
        Assert.Contains(blocking ?? code!, vm.PlanBlockText, StringComparison.Ordinal);
        Assert.Equal(code is null, vm.HasPlan);

        vm.Target = Abs + "2"; // inny folder → nowy podgląd
        Assert.Null(vm.Plan);
        Assert.True(vm.CanStart);
        svc.Preview = new(null, 3, 9_000, 0, [], 0, null, null);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(2, svc.PlanCalls);
        Assert.Single(svc.Calls);
    }

    [Fact, Trait("BL", "BL-64")]
    public void Grpc_adapter_maps_the_plan()
    {
        var r = new RestorePlanReply { Files = 4, Bytes = 10, Conflicts = 1, InvalidNames = 1, Links = 2, FreeBytes = 99, BlockingCode = "KB-5106", Resumable = 2 };
        r.ConflictSample.Add("a.txt");
        var p = GrpcDashboardSource.MapPlan(r);
        Assert.Equal((null, 4L, 10L, 1L, 3L, (long?)99, "KB-5106"), (p.Code, p.Files, p.Bytes, p.Conflicts, p.Skipped, p.FreeBytes, p.BlockingCode));
        Assert.Equal(["a.txt"], p.ConflictSample);
        Assert.Equal(2, p.Resumable);
        Assert.True(p.Blocks);
        var none = GrpcDashboardSource.MapPlan(new RestorePlanReply { Code = "KB-5002" });
        Assert.Equal(((string?)"KB-5002", (long?)null), (none.Code, none.FreeBytes));
    }

    public static TheoryData<string, string, int> Shots() => new()
    {
        { "plan-conflicts", "Light", 1180 },
        { "plan-conflicts", "Dark", 800 },
        { "plan-blocked", "Light", 1180 },
        { "form", "Light", 1180 },
        { "form", "Dark", 1180 },
        { "invalid", "Light", 800 },
        { "warning", "Light", 1180 },
        { "failed", "Dark", 1180 },
        { "ok", "Light", 800 },
    };

    [Theory, MemberData(nameof(Shots)), Trait("Plan", "R2.6.1"), Trait("BL", "BL-64")]
    public Task Restore_screen_renders(string state, string theme, int width) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var result = state switch
        {
            "warning" => new RestoreRunResult(RestoreRunState.Warning, "KB-5105"),
            "failed" => new RestoreRunResult(RestoreRunState.Failed, "KB-5201"),
            _ => new RestoreRunResult(RestoreRunState.Succeeded, null),
        };
        var browser = new SnapshotBrowserViewModel(new FakeBrowseSource(FakeHistorySource.Sample()), TimeZoneInfo.Utc);
        var fake = new FakeRestoreService(result)
        {
            Preview = state switch
            {
                "plan-conflicts" => Conflicting(8),
                "plan-blocked" => new RestorePreview(null, 12, 9_000_000, 0, [], 0, 4 * 1024 * 1024, "KB-5106"),
                _ => null,
            },
        };
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed())), browser: browser, restoreService: fake);
        shell.Navigate("SCR-06");
        shell.OpenRestore(new RestoreSelection(Snap, "Faktury/2026", true));
        var vm = Assert.IsType<RestoreViewModel>(shell.CurrentPage);
        vm.Target = state == "invalid" ? "odtworzone" : Abs;
        if (state is "warning" or "failed" or "ok" or "plan-conflicts" or "plan-blocked") await vm.StartCommand.ExecuteAsync(null);
        var window = new MainWindow { DataContext = shell, Width = width, Height = width < 1000 ? 1100 : 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"SCR-07-{state}-{theme}-{width}.png");
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 10_000, "zrzut pusty");
        var visible = window.GetVisualDescendants().OfType<Control>()
            .Where(x => x.IsEffectivelyVisible).Select(Avalonia.Automation.AutomationProperties.GetAutomationId).Where(i => i is not null).ToHashSet();
        Assert.Contains("SCR-07.Title", visible);
        Assert.Contains("SCR-07.NoOverwrite", visible);
        Assert.Equal(state is "form" or "invalid" or "plan-conflicts" or "plan-blocked", visible.Contains("SCR-07.Start"));
        Assert.Equal(state == "plan-conflicts", visible.Contains("SCR-07.PlanConflicts"));
        Assert.Equal(state == "plan-blocked", visible.Contains("SCR-07.PlanBlock"));
        Assert.Equal(state is "plan-conflicts" or "plan-blocked", visible.Contains("SCR-07.PlanSummary"));
        Assert.Equal(state == "invalid", visible.Contains("SCR-07.TargetError"));
        Assert.Equal(state is "warning" or "failed" or "ok", visible.Contains("SCR-07.Result"));
        Assert.Equal(state == "failed", visible.Contains("SCR-07.Retry"));
        window.Close();
    }, _ct);
}
