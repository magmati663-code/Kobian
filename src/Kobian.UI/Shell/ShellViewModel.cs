using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kobian.UI.Dashboard;
using Kobian.UI.History;
using Kobian.UI.Jobs;
using Kobian.UI.Localization;
using Kobian.UI.Restore;
using Kobian.UI.Wizard;

namespace Kobian.UI.Shell;

/// <summary>Pozycja nawigacji (6.2): ID ekranu z inwentarza R2.6.1, tekst, ikona (klucz zasobu), widok.</summary>
public sealed record NavItem(string ScreenId, string Key, string IconKey, object Page)
{
    public string Text => Strings.Get(Key);

    public string AutomationId => "Shell.Nav." + ScreenId;
}

/// <summary>Ekran jeszcze niezbudowany — uczciwa informacja zamiast pustej strony.</summary>
public sealed record PlaceholderPage(string Title)
{
    public string Message { get; } = Strings.Get("Nav.ComingSoon");
}

/// <summary>Powłoka aplikacji: nawigacja boczna + bieżący ekran (D6.6 — jeden ViewModel na ekran).</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Func<WizardViewModel>? _wizardFactory;
    private readonly IRestoreService? _restoreService;

    /// <summary>Powłoka; <paramref name="jobs"/>/<paramref name="history"/>/<paramref name="wizardFactory"/> = null — ekran jeszcze bez źródła danych (zaślepka).</summary>
    public ShellViewModel(DashboardViewModel dashboard, JobsViewModel? jobs = null, HistoryViewModel? history = null, Func<WizardViewModel>? wizardFactory = null,
        SnapshotBrowserViewModel? browser = null, IRestoreService? restoreService = null, Settings.SettingsViewModel? settings = null,
        Destinations.DestinationsViewModel? destinations = null)
    {
        Settings = settings;
        Browser = browser;
        _restoreService = restoreService;
        if (browser is not null)
        {
            browser.RestoreRequested += (_, selection) => OpenRestore(selection);
            browser.DatabaseRestoreRequested += (_, snap) => OpenDatabaseRestore(snap);
        }
        _wizardFactory = wizardFactory;
        Dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        Jobs = jobs;
        History = history;
        Items =
        [
            new NavItem("SCR-01", "Nav.Dashboard", "Kb.Icon.Nav.Dashboard", dashboard),
            new NavItem("SCR-02", "Nav.Jobs", "Kb.Icon.Nav.Jobs", (object?)jobs ?? new PlaceholderPage(Strings.Get("Nav.Jobs"))),
            new NavItem("SCR-06", "Nav.Restore", "Kb.Icon.Nav.Restore", (object?)browser ?? new PlaceholderPage(Strings.Get("Nav.Restore"))),
            new NavItem("SCR-10", "Nav.History", "Kb.Icon.Nav.History", (object?)history ?? new PlaceholderPage(Strings.Get("Nav.History"))),
            new NavItem("SCR-09", "Nav.Destinations", "Kb.Icon.Nav.Destinations", (object?)destinations ?? new PlaceholderPage(Strings.Get("Nav.Destinations"))),
            new NavItem("SCR-11", "Nav.Settings", "Kb.Icon.Nav.Settings", (object?)settings ?? new PlaceholderPage(Strings.Get("Nav.Settings"))),
        ];
        Selected = Items[0];
        dashboard.CreateJobRequested += (_, _) => OpenWizard();
        if (jobs is not null) jobs.CreateJobRequested += (_, _) => OpenWizard();
        if (history is not null)
        {
            history.RestoreRequested += (_, snap) =>
            {
                browser?.OpenSnapshot(snap.Id); // „Odtwórz” z historii → przeglądarka z tą kopią (SCR-06)
                Navigate("SCR-06");
            };
        }
    }

    public DashboardViewModel Dashboard { get; }

    public JobsViewModel? Jobs { get; }

    public HistoryViewModel? History { get; }

    public SnapshotBrowserViewModel? Browser { get; }

    public Settings.SettingsViewModel? Settings { get; }

    /// <summary>Przejście do ekranu z inwentarza (np. „Odtwórz” z historii → SCR-06).</summary>
    public void Navigate(string screenId)
    {
        if (Items.FirstOrDefault(i => i.ScreenId == screenId) is { } item) Selected = item;
    }

    /// <summary>
    /// Kreator SCR-03 (poza nawigacją — zaznaczenie zostaje). Każde otwarcie = nowy szkic (D6.7). Zakończenie/anulowanie → powrót
    /// do ekranu z nawigacji z odświeżeniem (nowe zadanie widoczne od razu). Bez usługi kreatora — uczciwa zaślepka.
    /// </summary>
    public void OpenWizard()
    {
        if (_wizardFactory is null)
        {
            CurrentPage = new PlaceholderPage(Strings.Get("Nav.NewJob"));
            return;
        }
        var wizard = _wizardFactory();
        wizard.Finished += (_, _) =>
        {
            if (!ReferenceEquals(CurrentPage, wizard)) return;
            CurrentPage = Selected.Page;
            if (Selected.Page is IRefreshablePage page) _ = page.RefreshAsync(CancellationToken.None);
        };
        CurrentPage = wizard;
    }

    /// <summary>
    /// SCR-07 odtwarzanie (poza nawigacją, jak kreator): z przeglądarki kopii; „Gotowe”/„Anuluj” → powrót do SCR-06, „Pokaż w historii” → SCR-10
    /// z odświeżeniem (uruchomienie restore widoczne od razu). Bez usługi — uczciwa zaślepka.
    /// </summary>
    public void OpenRestore(RestoreSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (_restoreService is null)
        {
            CurrentPage = new PlaceholderPage(Strings.Get("Restore.Title"));
            return;
        }
        var vm = new RestoreViewModel(_restoreService, selection);
        vm.Closed += (_, _) =>
        {
            if (ReferenceEquals(CurrentPage, vm)) CurrentPage = Selected.Page;
        };
        vm.ShowHistoryRequested += (_, _) =>
        {
            if (!ReferenceEquals(CurrentPage, vm)) return;
            if (Selected.ScreenId == "SCR-10")
            {
                CurrentPage = Selected.Page;
                if (Selected.Page is IRefreshablePage page) _ = page.RefreshAsync(CancellationToken.None);
            }
            else Navigate("SCR-10");
        };
        CurrentPage = vm;
    }

    /// <summary>SCR-08 odtwarzanie bazy (poza nawigacją, jak SCR-07): usługa = ta sama co SCR-07, jeśli obsługuje RestoreDatabase; inaczej zaślepka.</summary>
    public void OpenDatabaseRestore(Kobian.UI.History.SnapshotEntry snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_restoreService is not IDatabaseRestoreService db)
        {
            CurrentPage = new PlaceholderPage(Strings.Get("DbRestore.Title"));
            return;
        }
        var vm = new DbRestoreViewModel(db, snapshot);
        vm.Closed += (_, _) =>
        {
            if (ReferenceEquals(CurrentPage, vm)) CurrentPage = Selected.Page;
        };
        vm.ShowHistoryRequested += (_, _) =>
        {
            if (ReferenceEquals(CurrentPage, vm)) Navigate("SCR-10");
        };
        CurrentPage = vm;
    }

    /// <summary>Wejście na ekran z danymi z usługi = odświeżenie (bez blokowania UI; błędy połączenia obsługuje sam ekran).</summary>
    partial void OnSelectedChanged(NavItem oldValue, NavItem newValue)
    {
        if (newValue is null) return; // ListBox może chwilowo wyzerować zaznaczenie — ekran zostaje
        CurrentPage = newValue.Page;
        if (oldValue is not null && newValue.Page is IRefreshablePage page) _ = page.RefreshAsync(CancellationToken.None);
    }

    /// <summary>Bieżący ekran (zwykle strona zaznaczonej pozycji nawigacji; kreator SCR-03 poza nawigacją).</summary>
    [ObservableProperty]
    public partial object? CurrentPage { get; private set; }

    public ObservableCollection<NavItem> Items { get; }

    [ObservableProperty]
    public partial NavItem Selected { get; set; }

    public string Title { get; } = Strings.Get("App.Title");

    public string Tagline { get; } = Strings.Get("App.Tagline");
}
