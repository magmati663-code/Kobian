using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kobian.Api.Contracts;
using Kobian.UI.Dashboard;
using Kobian.UI.History;
using Kobian.UI.Jobs;
using Kobian.UI.Services;
using Kobian.UI.Shell;

namespace Kobian.UI;

public sealed class App : Application
{
    /// <summary>Źródło danych pulpitu; null = Agent po named pipe (KOBIAN_PIPE albo domyślna nazwa — D2.4). Testy podstawiają atrapę.</summary>
    public static IDashboardSource? DashboardSource { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Motyw z ustawień SCR-11 (system/light/dark) — od razu, bez restartu.</summary>
    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "light" => Avalonia.Styling.ThemeVariant.Light,
            "dark" => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default,
        };
    }

    /// <summary>Język z ustawień (przed utworzeniem widoków — teksty są ładowane przy tworzeniu).</summary>
    public static void ApplyLanguage(string language)
    {
        if (language is "pl" or "en") System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new System.Globalization.CultureInfo(language == "pl" ? "pl-PL" : "en-US");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsStore = new Settings.FileUiSettingsStore(Settings.FileUiSettingsStore.DefaultPath());
            var ui = settingsStore.Load();
            ApplyLanguage(ui.Language);
            ApplyTheme(ui.Theme);
            var source = DashboardSource ??= new GrpcDashboardSource(Environment.GetEnvironmentVariable("KOBIAN_PIPE") is { Length: > 0 } p ? p : ApiConventions.DefaultPipeName);
            // Jeden adapter gRPC obsługuje SCR-01, SCR-02 i SCR-10 (wspólny kanał); atrapa bez tych portów = zaślepki ekranów.
            Func<Wizard.WizardViewModel>? wizard = source is GrpcDashboardSource grpc ? () => new Wizard.WizardViewModel(grpc.CreateWizardService()) : null;
            var shell = new ShellViewModel(new DashboardViewModel(source) { Elevation = new Services.TokenElevationInfo() },
                source is IJobsSource jobs ? new JobsViewModel(jobs) : null,
                source is IHistorySource history ? new HistoryViewModel(history) : null,
                wizard,
                source is Restore.ISnapshotBrowseSource browse ? new Restore.SnapshotBrowserViewModel(browse) : null,
                source as Restore.IRestoreService,
                new Settings.SettingsViewModel(settingsStore, source as Settings.IServiceInfoSource, ApplyTheme),
                source is Destinations.IDestinationsSource dests ? new Destinations.DestinationsViewModel(dests) : null);
            desktop.MainWindow = new MainWindow { DataContext = shell };
            _ = shell.Dashboard.RefreshCommand.ExecuteAsync(null);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
