using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kobian.UI.Dashboard;
using Kobian.UI.Shell;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>
/// Widoki renderowane naprawdę (Avalonia.Headless + Skia): okno powłoki z pulpitem w motywie jasnym i ciemnym, zrzuty PNG do przeglądu (AC-UX.3/4 — regresja wizualna później).
/// Asercje: widok się buduje bez błędów wiązań, zrzut nie jest pusty, kluczowe elementy mają AutomationId (NFR-13).
/// </summary>
public sealed class RenderTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public static TheoryData<string, string, int> Shots() => new()
    {
        { "mixed", "Light", 1180 },
        { "mixed", "Dark", 1180 },
        { "good", "Light", 1180 },
        { "empty", "Light", 1180 },
        { "mixed", "Light", 800 },
        { "offline", "Light", 1180 },
        { "stale", "Dark", 1180 },
        { "held", "Light", 1180 },
        { "alerts", "Light", 1180 },
        { "alerts", "Dark", 800 },
        { "held", "Dark", 800 },
        { "viewer", "Light", 1180 },
        { "viewer-uac", "Dark", 800 },
    };

    [Theory, MemberData(nameof(Shots)), Trait("Plan", "R2.6.1")]
    public Task Shell_with_dashboard_renders(string data, string theme, int width) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var src = new FakeDashboardSource(data switch { "good" or "stale" => FakeDashboardSource.AllGood(), "held" => FakeDashboardSource.AllGood() with { RetentionHeld = true, Role = CallerRole.Admin }, "alerts" => FakeDashboardSource.Mixed(CallerRole.Admin) with { Alerts = FakeDashboardSource.SampleAlerts() }, "empty" => FakeDashboardSource.Empty(), "viewer" or "viewer-uac" => FakeDashboardSource.Mixed(CallerRole.Viewer), _ => FakeDashboardSource.Mixed() })
        {
            Fail = data == "offline",
        };
        var vm = new DashboardViewModel(src, new Fixed(FakeDashboardSource.Now), TimeZoneInfo.Utc) { Elevation = new FakeElevation(data == "viewer-uac") };
        await vm.RefreshAsync(CancellationToken.None);
        if (data == "stale")
        {
            src.Fail = true;
            await vm.RefreshAsync(CancellationToken.None);
        }
        var window = new MainWindow { DataContext = new ShellViewModel(vm), Width = width, Height = width < 1000 ? 1500 : 1100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"SCR-01-{data}-{theme}-{width}.png");
#pragma warning disable CS0618 // Avalonia 12: nowe przeciążenie z BitmapEncoderOptions — PNG domyślne wystarcza
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 10_000, "zrzut pusty");
        var ids = window.GetVisualDescendantsIds();
        Assert.Contains("SCR-01.PrimaryAction", ids);
        Assert.Contains("Shell.Nav", ids);
        Assert.Equal(data == "held", window.GetVisualDescendants().OfType<Avalonia.Controls.Control>()
            .Any(x => Avalonia.Automation.AutomationProperties.GetAutomationId(x) == "SCR-01.RetentionHoldBanner" && x.IsEffectivelyVisible));
        Assert.Equal(data == "alerts", ids.Contains("SCR-01.AlertAck") && ids.Contains("SCR-01.AlertSilence"));
        Assert.Equal(data.StartsWith("viewer", StringComparison.Ordinal), window.GetVisualDescendants().OfType<Avalonia.Controls.Control>()
            .Any(x => Avalonia.Automation.AutomationProperties.GetAutomationId(x) == "SCR-01.ReadOnlyBanner" && x.IsEffectivelyVisible));
        window.Close();
    }, _ct);

    public static TheoryData<string, string, int> ScreenShots() => new()
    {
        { "SCR-02", "Light", 1180 },
        { "SCR-02", "Dark", 1180 },
        { "SCR-02", "Light", 800 },
        { "SCR-02-offline", "Light", 1180 },
        { "SCR-10", "Light", 1180 },
        { "SCR-10", "Dark", 1180 },
        { "SCR-10", "Light", 800 },
    };

    /// <summary>SCR-02 (lista zadań) i SCR-10 (historia) w powłoce: zrzuty do przeglądu + AutomationId kluczowych elementów (NFR-13).</summary>
    [Theory, MemberData(nameof(ScreenShots)), Trait("Plan", "R2.6.1")]
    public Task Jobs_and_history_screens_render(string screen, string theme, int width) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var clock = new Fixed(FakeDashboardSource.Now);
        var jobsData = FakeJobsSource.Mixed();
        jobsData = jobsData with { Jobs = [.. jobsData.Jobs.Select(j => j.JobId == "j1" ? j with { Activity = Kobian.UI.Dashboard.ActiveRun.Queued } : j)] }; // BL-76: pigułka „W kolejce”
        var jobsSrc = new FakeJobsSource(jobsData) { Fail = screen.EndsWith("offline", StringComparison.Ordinal) };
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed()), clock, TimeZoneInfo.Utc),
            new Kobian.UI.Jobs.JobsViewModel(jobsSrc, clock, TimeZoneInfo.Utc),
            new Kobian.UI.History.HistoryViewModel(new FakeHistorySource(FakeHistorySource.Sample(), FakeHistorySource.SampleRuns()), clock, TimeZoneInfo.Utc));
        var id = screen[..6];
        shell.Navigate(id);
        if (id == "SCR-02") await shell.Jobs!.RefreshAsync(CancellationToken.None);
        else await shell.History!.RefreshAsync(CancellationToken.None);
        if (screen == "SCR-02" && theme == "Light" && width > 1000) // komunikat akcji widoczny na jednym zrzucie
            await shell.Jobs!.ToggleScheduleCommand.ExecuteAsync(shell.Jobs.Rows.Single(r => r.Name == "Zdjęcia"));
        var window = new MainWindow { DataContext = shell, Width = width, Height = width < 1000 ? 1500 : 1000 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"{screen}-{theme}-{width}.png");
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 10_000, "zrzut pusty");
        var ids = window.GetVisualDescendantsIds();
        Assert.Contains(id + ".Title", ids);
        if (screen == "SCR-02") Assert.Contains("SCR-02.Job.j2", ids);
        if (screen == "SCR-02") Assert.Contains("SCR-02.Activity", ids);
        if (screen == "SCR-02-offline") Assert.Contains("SCR-02.Offline", ids);
        if (id == "SCR-10") Assert.Contains("SCR-10.JobFilter", ids);
        window.Close();
    }, _ct);

    public static TheoryData<string, string, int> WizardShots() => new()
    {
        { "S1", "Light", 1180 },
        { "S1", "Dark", 1180 },
        { "S2-sftp", "Light", 800 },
        { "S3", "Light", 1180 },
        { "S4-key", "Light", 1180 },
        { "S4-key", "Dark", 800 },
        { "S5", "Light", 1180 },
        { "S5", "Dark", 800 },
        { "S6", "Light", 1180 },
        { "done", "Light", 1180 },
        { "S1db", "Light", 1180 },
        { "S1db", "Dark", 800 },
        { "S1db-script", "Light", 1180 },
        { "S1db-cert", "Light", 1180 },
        { "S1db-cert", "Dark", 800 },
    };

    /// <summary>SCR-03 kreator w powłoce: kolejne kroki na atrapie usługi; zrzuty do przeglądu (AC-UX.3 — wąsko bez paska kroków, bez poziomego przewijania).</summary>
    [Theory, MemberData(nameof(WizardShots)), Trait("Plan", "R2.6.1"), Trait("AC", "AC-UX.3")]
    public Task Wizard_steps_render(string step, string theme, int width) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var db = step.StartsWith("S1db", StringComparison.Ordinal);
        var svc = new FakeWizardService { SupportsDatabases = db, Discovery = FakeWizardService.SampleDiscovery(), Certificate = FakeWizardService.SampleCertificate() };
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed()), new Fixed(FakeDashboardSource.Now), TimeZoneInfo.Utc),
            wizardFactory: () => new Kobian.UI.Wizard.WizardViewModel(svc));
        shell.OpenWizard();
        var vm = (Kobian.UI.Wizard.WizardViewModel)shell.CurrentPage!;
        if (db)
        {
            vm.SelectedSourceKind = vm.SourceKinds[1];
            vm.DbServer = @"SRV01\SQLEXPRESS";
            await vm.DiscoverDatabasesCommand.ExecuteAsync(null);
            vm.SelectedDatabase = vm.Databases[step == "S1db" ? 0 : 2];
            if (step == "S1db-script") await vm.NextCommand.ExecuteAsync(null); // Kadry bez uprawnień → KB-3003 + skrypt
            if (step == "S1db-cert") await vm.FetchCertificateCommand.ExecuteAsync(null); // odcisk do porównania (BL-40 cz. 5)
        }
        else foreach (var p in new[] { @"C:\Users\Anna\Documents", @"D:\Firma\Faktury" })
        {
            vm.NewSource = p;
            vm.AddSourceCommand.Execute(null);
        }
        if (step != "S1" && !db)
        {
            await vm.NextCommand.ExecuteAsync(null);
            if (step == "S2-sftp")
            {
                vm.SelectedDestination = vm.Destinations[1];
                vm.SftpHost = "nas.biuro.example.invalid";
                vm.SftpUser = "kopie";
                vm.DestinationPath = "/volume1/kobian";
            }
            else
            {
                vm.DestinationPath = @"E:\KopieKobian";
                await vm.NextCommand.ExecuteAsync(null);
            }
        }
        if (step is "S4-key" or "S5" or "S6" or "done")
        {
            vm.SelectedSchedule = vm.Schedules[1];
            await vm.NextCommand.ExecuteAsync(null);
            vm.Password = vm.PasswordConfirm = FakeWizardService.RandomSecret();
            await vm.NextCommand.ExecuteAsync(null);
        }
        if (step is "S5" or "S6" or "done")
        {
            var g = Kobian.UI.Wizard.RecoveryKeyText.Groups(svc.RecoveryKey!);
            vm.ChallengeFirst = g[vm.ChallengeGroups.First - 1];
            vm.ChallengeSecond = g[vm.ChallengeGroups.Second - 1];
            await vm.NextCommand.ExecuteAsync(null);
            if (step == "S5")
            {
                vm.KeepMonthly = 24; // podgląd odświeża się po zmianie liczby
                await vm.RefreshRetentionPreviewAsync();
            }
            else
            {
                await vm.NextCommand.ExecuteAsync(null);
            }
        }
        if (step == "done") await vm.NextCommand.ExecuteAsync(null);
        var window = new MainWindow { DataContext = shell, Width = width, Height = width < 1000 ? 1300 : 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"SCR-03-{step}-{theme}-{width}.png");
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 10_000, "zrzut pusty");
        var ids = window.GetVisualDescendantsIds();
        Assert.Contains("SCR-03.Next", ids);
        Assert.Contains("SCR-03.Panel", ids);
        if (db) Assert.Contains("SCR-03.S1db.List", ids);
        if (step == "S1db-script") Assert.Contains("SCR-03.S1db.Script", ids);
        if (step == "S1db-cert") Assert.Contains("SCR-03.S1db.CertificateFingerprint", ids);
        var rail = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Control>()
            .Single(c => Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "SCR-03.Steps");
        Assert.Equal(width > 1000, rail.IsEffectivelyVisible); // wąsko: podpis „Krok n z 6” zamiast paska
        window.Close();
    }, _ct);

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

internal static class VisualIds
{
    public static HashSet<string> GetVisualDescendantsIds(this Visual root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root))
            if (v is Control c && Avalonia.Automation.AutomationProperties.GetAutomationId(c) is { Length: > 0 } id) set.Add(id);
        return set;
    }
}
