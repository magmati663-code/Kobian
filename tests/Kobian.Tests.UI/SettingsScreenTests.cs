using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kobian.Api.V1;
using Kobian.UI.Dashboard;
using Kobian.UI.Services;
using Kobian.UI.Settings;
using Kobian.UI.Shell;
using Xunit;

namespace Kobian.Tests.UI;

internal sealed class MemorySettingsStore(UiSettings initial) : IUiSettingsStore
{
    public UiSettings Current { get; private set; } = initial;

    public int Saves { get; private set; }

    public UiSettings Load() => Current;

    public void Save(UiSettings settings)
    {
        Current = settings;
        Saves++;
    }
}

internal sealed class FakeServiceInfo(ServiceInfo info, AuditCheckResult audit) : IServiceInfoSource
{
    public bool Fail { get; set; }

    public Task<ServiceInfo> GetServiceInfoAsync(CancellationToken ct) => Fail ? throw new IOException("offline") : Task.FromResult(info);

    public Task<AuditCheckResult> VerifyAuditAsync(CancellationToken ct) => Fail ? throw new IOException("offline") : Task.FromResult(audit);
}

/// <summary>SCR-11 Ustawienia (it. 81): plik ustawień UI, motyw od razu, język po restarcie, informacje o usłudze, audyt; baner KB-7013 na pulpicie.</summary>
public sealed class SettingsScreenTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact, Trait("AC", "AC-31.1")]
    public void File_store_round_trips_and_survives_bad_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kb-ui-set-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "sub", "ui-settings.json");
        var store = new FileUiSettingsStore(path);
        Assert.Equal(new UiSettings(), store.Load()); // brak pliku
        store.Save(new UiSettings("en", "dark"));
        Assert.Equal(new UiSettings("en", "dark"), store.Load());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)); // bez plików tymczasowych
        File.WriteAllText(path, "{\"language\":\"xx\",\"theme\":\"neon\"}");
        Assert.Equal(new UiSettings(), store.Load());
        File.WriteAllText(path, "nie json");
        Assert.Equal(new UiSettings(), store.Load());
        File.WriteAllText(path, "{\"language\":\"" + new string('a', FileUiSettingsStore.MaxBytes) + "\"}");
        Assert.Equal(new UiSettings(), store.Load());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Theme_applies_immediately_language_needs_restart_and_both_persist()
    {
        var store = new MemorySettingsStore(new UiSettings("pl", UiSettings.System));
        var applied = new List<string>();
        var vm = new SettingsViewModel(store, null, applied.Add);
        Assert.Equal(0, store.Saves); // wczytanie nie zapisuje
        Assert.False(vm.HasService);
        vm.SelectedTheme = vm.Themes.Single(t => t.Value == "dark");
        Assert.Equal("dark", applied[^1]);
        Assert.Equal(new UiSettings("pl", "dark"), store.Current);
        Assert.False(vm.RestartNeeded);
        vm.SelectedLanguage = vm.Languages.Single(l => l.Value == "en");
        Assert.True(vm.RestartNeeded);
        Assert.Equal(new UiSettings("en", "dark"), store.Current);
        vm.SelectedLanguage = vm.Languages.Single(l => l.Value == "pl");
        Assert.False(vm.RestartNeeded);
    }

    [Fact, Trait("BL", "BL-62"), Trait("RD", "RD-42")]
    public async Task Service_info_runtime_warning_and_audit_results()
    {
        var svc = new FakeServiceInfo(new ServiceInfo("0.9.0", Kobian.UI.Dashboard.CallerRole.Operator, "KB-7013", "10.0.12"), new AuditCheckResult(false, 40, 17, "KB-6501"));
        var vm = new SettingsViewModel(new MemorySettingsStore(new UiSettings()), svc, _ => { });
        await vm.RefreshAsync(_ct);
        Assert.Equal("0.9.0", vm.VersionText);
        Assert.True(vm.RuntimeUnpatched);
        Assert.Contains("KB-7013", vm.RuntimeText, StringComparison.Ordinal);
        Assert.Contains("10.0.12", vm.RuntimeText, StringComparison.Ordinal);
        await vm.VerifyAuditCommand.ExecuteAsync(null);
        Assert.True(vm.AuditBroken);
        Assert.Contains("17", vm.AuditText, StringComparison.Ordinal);
        svc.Fail = true;
        await vm.RefreshAsync(_ct);
        Assert.True(vm.ConnectionLost);
        await vm.VerifyAuditCommand.ExecuteAsync(null);
        Assert.Contains("KB-9001", vm.AuditText, StringComparison.Ordinal);
    }

    [Fact, Trait("Plan", "D9.9")]
    public async Task Last_anchor_is_shown_in_user_time_and_hidden_after_connection_loss()
    {
        var anchor = "1284:" + new string('a', 64);
        var svc = new FakeServiceInfo(new ServiceInfo("0.9.0", Kobian.UI.Dashboard.CallerRole.Admin, null, null),
            new AuditCheckResult(true, 1284, null, null, anchor, "2026-10-17T04:55:00.000Z", 3));
        var warsaw = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Central European Standard Time" : "Europe/Warsaw");
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("pl-PL");
        var vm = new SettingsViewModel(new MemorySettingsStore(new UiSettings()), svc, _ => { }, warsaw);
        Assert.False(vm.HasLastAnchor);
        await vm.VerifyAuditCommand.ExecuteAsync(null);
        Assert.True(vm.HasLastAnchor);
        Assert.Equal(anchor, vm.LastAnchor);
        Assert.Contains("06:55", vm.LastAnchorText, StringComparison.Ordinal); // 04:55Z = 06:55 CEST
        svc.Fail = true;
        await vm.VerifyAuditCommand.ExecuteAsync(null);
        Assert.False(vm.HasLastAnchor);
    }

    [Fact]
    public void Grpc_adapter_maps_audit_reply()
    {
        Assert.Equal(new AuditCheckResult(true, 3, null, null), GrpcDashboardSource.MapAudit(new VerifyAuditReply { Result = OverallState.Ok, Entries = 3 }));
        Assert.Equal(new AuditCheckResult(false, 9, 4, "KB-6501"), GrpcDashboardSource.MapAudit(new VerifyAuditReply { Result = OverallState.Failed, Entries = 9, BrokenSeq = 4, Code = "KB-6501" }));
        Assert.Equal(new AuditCheckResult(true, 9, null, null, "9:ab", "2026-10-17T04:55:00.000Z", 2),
            GrpcDashboardSource.MapAudit(new VerifyAuditReply { Result = OverallState.Ok, Entries = 9, LastAnchor = "9:ab", LastAnchorAtUtc = "2026-10-17T04:55:00.000Z", AnchorsChecked = 2 }));
    }

    public static TheoryData<string, string, int> Shots() => new()
    {
        { "ok", "Light", 1180 },
        { "ok", "Dark", 1180 },
        { "runtime", "Light", 800 },
        { "offline", "Light", 1180 },
    };

    [Theory, MemberData(nameof(Shots)), Trait("Plan", "R2.6.1")]
    public Task Settings_screen_renders(string state, string theme, int width) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var svc = new FakeServiceInfo(new ServiceInfo("0.9.0-dev", Kobian.UI.Dashboard.CallerRole.Admin, state == "runtime" ? "KB-7013" : null, state == "runtime" ? "10.0.12" : null),
            new AuditCheckResult(state != "runtime", 1284, state == "runtime" ? 977 : null, state == "runtime" ? "KB-6501" : null,
                state == "ok" ? "1284:" + new string('3', 64) : null, state == "ok" ? "2026-10-17T04:55:00.000Z" : null, state == "ok" ? 2 : 0)) { Fail = state == "offline" };
        var vm = new SettingsViewModel(new MemorySettingsStore(new UiSettings("pl", theme == "Dark" ? "dark" : "light")), svc, _ => { });
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed())), settings: vm);
        shell.Navigate("SCR-11");
        await vm.RefreshAsync(CancellationToken.None);
        if (state is "ok" or "runtime") await vm.VerifyAuditCommand.ExecuteAsync(null);
        if (state == "runtime") vm.SelectedLanguage = vm.Languages.Single(l => l.Value == "en");
        var window = new MainWindow { DataContext = shell, Width = width, Height = width < 1000 ? 1100 : 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"SCR-11-{state}-{theme}-{width}.png");
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 10_000, "zrzut pusty");
        var visible = window.GetVisualDescendants().OfType<Control>()
            .Where(x => x.IsEffectivelyVisible).Select(Avalonia.Automation.AutomationProperties.GetAutomationId).Where(i => i is not null).ToHashSet();
        Assert.Contains("SCR-11.Title", visible);
        Assert.Contains("SCR-11.Theme", visible);
        Assert.Equal(state == "offline", visible.Contains("SCR-11.Offline"));
        Assert.Equal(state == "runtime", visible.Contains("SCR-11.RestartNeeded"));
        Assert.Equal(state is "ok" or "runtime", visible.Contains("SCR-11.AuditResult"));
        Assert.Equal(state == "ok", visible.Contains("SCR-11.AnchorValue"));
        window.Close();
    }, _ct);

    [Fact, Trait("BL", "BL-62")]
    public Task Dashboard_shows_runtime_banner() => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var src = new RuntimeSource();
        var vm = new DashboardViewModel(src, new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc);
        await vm.RefreshAsync(CancellationToken.None);
        Assert.True(vm.ShowRuntimeBanner);
        var window = new MainWindow { DataContext = new ShellViewModel(vm), Width = 1180, Height = 1100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
#pragma warning disable CS0618
        frame!.Save(Path.Combine(UiSession.SnapshotDir(), "SCR-01-runtime-Light-1180.png"));
#pragma warning restore CS0618
        Assert.Contains(window.GetVisualDescendants().OfType<Control>(), x => Avalonia.Automation.AutomationProperties.GetAutomationId(x) == "SCR-01.RuntimeBanner" && x.IsEffectivelyVisible);
        window.Close();
    }, _ct);

    private sealed class RuntimeSource : IDashboardSource
    {
        public Task<DashboardData> LoadAsync(CancellationToken ct) => Task.FromResult(FakeDashboardSource.AllGood() with { RuntimeCode = "KB-7013" });

        public Task RunJobAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }
}
