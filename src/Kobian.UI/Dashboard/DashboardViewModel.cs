using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.Localization;
using Kobian.UI.Wizard;

namespace Kobian.UI.Dashboard;

/// <summary>Rola wywołującego z API (SEC-2.2) — UI pokazuje tylko akcje dostępne dla roli (6.7).</summary>
public enum CallerRole
{
    Viewer = 0,
    Operator = 1,
    Admin = 2,
}

public sealed record DestinationSpace(string Name, double FreePercent);

/// <param name="RetentionHeld">SEC-17.4: retencja hosta wstrzymana po skoku zegara (KB-6117) — baner do potwierdzenia przez Administratora.</param>
public sealed record DashboardData(IReadOnlyList<JobStatusInfo> Jobs, IReadOnlyList<DestinationSpace> Destinations, CallerRole Role, DateTimeOffset AsOfUtc, bool RetentionHeld = false, string? RuntimeCode = null, IReadOnlyList<DashboardAlert>? Alerts = null);

/// <summary>Port do usługi dla SCR-01 (AgentService.GetStatus + ListJobs). Adapter gRPC — BL-14; w testach atrapa.</summary>
public interface IDashboardSource
{
    Task<DashboardData> LoadAsync(CancellationToken ct);

    Task RunJobAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Uruchomienie z wynikiem: kod KB przy odmowie usługi (zadanie wyłączone, brak uprawnień…), null przy przyjęciu. Domyślnie opakowuje <see cref="RunJobAsync"/> (źródła bez wyniku).
    /// Adapter gRPC odrzucał wynik RunOutcome (Task&lt;RunOutcome&gt; rzutowany na Task) — odmowa Agenta znikała bez śladu, a pulpit się nie odświeżał.
    /// </summary>
    async Task<string?> StartJobAsync(string jobId, CancellationToken ct)
    {
        await RunJobAsync(jobId, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>BL-45: potwierdzenie alertu (Operator). Zwraca kod KB przy odmowie, null przy powodzeniu. Domyślnie: usługa bez tej metody (KB-7012).</summary>
    Task<string?> AcknowledgeAlertAsync(string alertId, CancellationToken ct) => Task.FromResult<string?>(Kobian.Domain.KbCode.OperationNotAvailable);

    /// <summary>BL-45: wyciszenie alertu na <paramref name="minutes"/> (1…10080; Administrator). Zwraca kod KB przy odmowie, null przy powodzeniu.</summary>
    Task<string?> SilenceAlertAsync(string alertId, int minutes, CancellationToken ct) => Task.FromResult<string?>(Kobian.Domain.KbCode.OperationNotAvailable);
}

/// <summary>Formatowanie czasu dla ludzi (6.9): „dziś 22:04”, „wczoraj 21:10”, „jutro 22:00”, inaczej data krótka; strefa = strefa użytkownika.</summary>
public static class HumanTime
{
    public static string At(DateTimeOffset? utc, DateTimeOffset nowUtc, TimeZoneInfo zone, CultureInfo culture, string emptyKey = "Time.Never")
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(culture);
        if (utc is not { } t) return Strings.Get(emptyKey, culture);
        var local = TimeZoneInfo.ConvertTime(t, zone);
        var today = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        var hm = local.ToString("HH:mm", culture);
        var days = (local.Date - today).Days;
        return days switch
        {
            0 => string.Format(culture, Strings.Get("Time.Today", culture), hm),
            -1 => string.Format(culture, Strings.Get("Time.Yesterday", culture), hm),
            1 => string.Format(culture, Strings.Get("Time.Tomorrow", culture), hm),
            _ => local.ToString("d MMM, HH:mm", culture),
        };
    }

    /// <summary>Czas trwania zaległości: ≥ 48 h w dniach, inaczej w godzinach (min. 1), w dopełniaczu („od 3 dni”).</summary>
    public static string Since(TimeSpan span, CultureInfo culture) =>
        span >= TimeSpan.FromHours(48)
            ? Plural.Text("Plural.Day", (long)span.TotalDays, culture)
            : Plural.Text("Plural.Hour", Math.Max(1, (long)span.TotalHours), culture);
}

/// <summary>Kafelek zadania na pulpicie (SCR-01): stan + jedno zdanie przyczyny + jedna główna akcja (6.3).</summary>
public sealed class JobTileViewModel(JobStatusInfo job, JobProtection protection, string detail, string lastCopy, string next, string actionText)
{
    public JobStatusInfo Job { get; } = job;

    public string Name => Job.Name;

    public ProtectionState State => protection.State;

    public ProtectionReason Reason => protection.Reason;

    public string StateText => Strings.Get("State." + protection.State);

    public string Detail { get; } = detail;

    public string LastCopy { get; } = lastCopy;

    public string Next { get; } = next;

    public string ActionText { get; } = actionText;

    public string? Destination => Job.DestinationName;

    public bool Verified => Job.LastSuccessVerifyLevel >= 2;

    public string VerifyText => Verified ? Strings.Format("Verify.Level", Job.LastSuccessVerifyLevel) : Strings.Get("Verify.NotVerified");

    /// <summary>Czy akcja główna to „uruchom kopię” (pozostałe prowadzą do ekranu naprawy).</summary>
    public bool RunsJob => protection.Reason is ProtectionReason.Ok or ProtectionReason.Overdue or ProtectionReason.NeverSucceeded;

    public string? Code => protection.State is ProtectionState.AtRisk or ProtectionState.Warning ? Job.LastCode : null;

    /// <summary>Pełny tekst dla czytnika ekranu (AutomationProperties.Name — R2.6.3).</summary>
    public string AccessibleName => $"{Name}: {StateText}. {Detail}";
}

public sealed record DestinationTile(string Name, double FreePercent, bool Low)
{
    public double UsedPercent => 100 - FreePercent;

    public string FreeText => Strings.Format(Low ? "Space.FreeLow" : "Space.Free", Math.Round(FreePercent).ToString(CultureInfo.CurrentCulture));

    /// <summary>Stan miejsca słownie — kolor paska nie jest jedyną informacją (WCAG 1.4.1).</summary>
    public string AccessibleName => $"{Name}: {FreeText}";
}

/// <summary>
/// SCR-01 Pulpit (6.3, D6.6): stan globalny = najgorszy z zadań, kafelki zadań, miejsce na celach. Bez logiki w code-behind.
/// Akcja „Uruchom” dostępna od roli Operator (inaczej wyszarzona z wyjaśnieniem — 6.7).
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, Shell.IRefreshablePage
{
    private readonly IDashboardSource _source;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;

    public DashboardViewModel(IDashboardSource source, TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public ObservableCollection<JobTileViewModel> Jobs { get; } = [];

    public ObservableCollection<DestinationTile> Destinations { get; } = [];

    /// <summary>Aktywne alerty (D9.7) — najpierw bezpieczeństwo/krytyczne, jak je zwraca Agent; zmiana kolejności tylko stabilna wg wagi.</summary>
    public ObservableCollection<AlertTileViewModel> Alerts { get; } = [];

    public bool HasAlerts => Alerts.Count > 0;

    /// <summary>Wynik ostatniej akcji na alercie (sukces albo komunikat KB); pusty = brak.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlertMessage))]
    public partial string AlertMessage { get; private set; } = "";

    [ObservableProperty]
    public partial bool AlertMessageIsError { get; private set; }

    public bool HasAlertMessage => AlertMessage.Length > 0;

    /// <summary>Domyślny czas wyciszenia w polu wyboru przy przycisku „Wycisz”: 4 h (Qb106: wybór 1 h / 4 h / 24 h / 7 dni w <see cref="AlertTileViewModel.SilenceOptions"/>).</summary>
    public const int DefaultSilenceMinutes = 240;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJobs), nameof(IsEmpty), nameof(StateText))]
    public partial ProtectionState? Overall { get; private set; }

    [ObservableProperty]
    public partial string Headline { get; private set; } = "";

    [ObservableProperty]
    public partial string Subline { get; private set; } = "";

    [ObservableProperty]
    public partial string? PrimaryActionText { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string UpdatedText { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunJobCommand), nameof(ActCommand))]
    [NotifyPropertyChangedFor(nameof(ShowReadOnlyBanner), nameof(ReadOnlyDetail))]
    public partial CallerRole Role { get; private set; }

    /// <summary>RD-57: informacja o UAC procesu UI (dobór tekstu banera podglądu); domyślnie brak.</summary>
    public Services.IElevationInfo Elevation { get; init; } = Services.NoElevation.Instance;

    /// <summary>
    /// RD-57 (6.7): baner „tryb podglądu” po udanym odczycie, gdy Agent nadał rolę Podgląd — akcje są wyszarzone, a użytkownik dostaje powód i drogę wyjścia
    /// (administrator bez podniesienia UAC → „Uruchom jako administrator”; inni → grupa „Kobian Operators”). Bez połączenia baner znika (rola nieznana).
    /// </summary>
    public bool ShowReadOnlyBanner => _loaded && !ConnectionLost && Role == CallerRole.Viewer;

    public string ReadOnlyDetail => Strings.Get(Elevation.CanElevate ? "ReadOnly.Elevate" : "ReadOnly.AskOperator");

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool ConnectionLost { get; private set; }

    public bool HasJobs => Overall is not null;

    /// <summary>Sekcja miejsca na celach tylko, gdy usługa podała dane (kontrakt bez celów — Qb18).</summary>
    public bool HasDestinations => Destinations.Count > 0;

    /// <summary>Pusty pulpit tylko po udanym odczycie z usługi — brak połączenia nie jest „brakiem zadań” (R2.6.4).</summary>
    public bool IsEmpty => Overall is null && _loaded && !ConnectionLost;

    /// <summary>Brak połączenia i brak jakichkolwiek danych: nagłówek SCR-16 zamiast pulpitu (KB-9001).</summary>
    public bool IsOffline => ConnectionLost && !_loaded;

    /// <summary>Baner „dane mogą być nieaktualne” — gdy są dane z wcześniejszego odczytu, a połączenie zerwane.</summary>
    public bool ShowStaleBanner => ConnectionLost && _loaded;

    private bool _loaded;

    /// <summary>SEC-17.4: baner „porządkowanie kopii wstrzymane” (KB-6117) — kopie są wykonywane, nic nie jest zapominane do potwierdzenia przez Administratora.</summary>
    [ObservableProperty]
    public partial bool ShowRetentionHoldBanner { get; private set; }

    /// <summary>BL-62: usługa na niezałatanym .NET (KB-7013) — ostrzeżenie, kopie działają.</summary>
    [ObservableProperty]
    public partial bool ShowRuntimeBanner { get; private set; }

    /// <summary>Drugie zdanie banera zależy od roli: Administrator widzi, jak potwierdzić; pozostali — kogo poprosić.</summary>
    public string RetentionHoldDetail => Strings.Get(Role >= CallerRole.Admin ? "Retention.Held.Admin" : "Retention.Held.Other");

    /// <summary>Polecenie potwierdzenia — tylko dla Administratora (osobny wiersz, bez łamania w środku polecenia).</summary>
    public string? RetentionHoldCommand => Role >= CallerRole.Admin ? "kobian retention release --confirm" : null;

    public string StateText => Overall is { } s ? Strings.Get("State." + s) : "";

    public bool CanRun => Role >= CallerRole.Operator;

    /// <summary>Kod KB stanu „usługa nie działa” (R2.6.4) — pokazywany tylko bez połączenia.</summary>
    public string? OfflineCode => IsOffline ? (AccessDenied ? "KB-7001" : "KB-9001") : null;

    /// <summary>
    /// Usługa działa, ale odmówiła połączenia (Windows: konto spoza „Kobian Operators”/„Kobian Admins” i bez podniesienia UAC — ACL pipe).
    /// Wcześniej pokazywane jako „usługa nie działa”, co wprowadzało w błąd przy działającej usłudze.
    /// </summary>
    public bool AccessDenied { get; private set; }

    internal static bool IsAccessDenied(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is UnauthorizedAccessException) return true;
        return false;
    }

    public string RunHint => CanRun ? "" : Strings.Get("Action.RequiresOperator");

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var data = await _source.LoadAsync(ct).ConfigureAwait(true);
            ConnectionLost = false;
            AccessDenied = false;
            _loaded = true;
            Apply(data);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            ConnectionLost = true; // R2.6.4: dane z poprzedniego odczytu zostają, baner „Brak połączenia”
            AccessDenied = IsAccessDenied(ex);
            if (!_loaded)
            {
                Headline = Strings.Get(AccessDenied ? "Offline.Denied.Headline" : "Offline.Headline");
                Subline = Strings.Get(AccessDenied ? "Offline.Denied.Detail" : "Offline.Detail");
                PrimaryActionText = Strings.Get("Offline.Retry");
                Summary = "";
            }
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsOffline));
            OnPropertyChanged(nameof(ShowStaleBanner));
            OnPropertyChanged(nameof(ShowReadOnlyBanner));
            OnPropertyChanged(nameof(OfflineCode));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunJobAsync(JobTileViewModel? tile, CancellationToken ct) =>
        tile is null ? Task.CompletedTask : StartAsync(tile, ct);

    private Task StartAsync(JobTileViewModel tile, CancellationToken ct) =>
        ActOnAlert(() => _source.StartJobAsync(tile.Job.JobId, ct), string.Format(CultureInfo.CurrentCulture, Strings.Get("Jobs.Started"), tile.Job.Name), ct);

    /// <summary>Ekran docelowy akcji naprawczej (np. SCR-09 przy kluczu serwera) — nawigacja w powłoce; null = akcja wykonana na miejscu.</summary>
    public event EventHandler<JobTileViewModel>? NavigationRequested;

    /// <summary>Jedna główna akcja kafelka (6.3): uruchomienie kopii albo przejście do ekranu naprawy.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ActAsync(JobTileViewModel? tile, CancellationToken ct)
    {
        if (tile is null) return;
        if (tile.RunsJob) await StartAsync(tile, ct).ConfigureAwait(true);
        else NavigationRequested?.Invoke(this, tile);
    }

    [RelayCommand]
    private Task AcknowledgeAlertAsync(AlertTileViewModel? alert, CancellationToken ct) =>
        alert is null || !alert.CanAcknowledge ? Task.CompletedTask
            : ActOnAlert(() => _source.AcknowledgeAlertAsync(alert.Id, ct), Strings.Get("Alert.Acknowledged"), ct);

    [RelayCommand]
    private Task SilenceAlertAsync(AlertTileViewModel? alert, CancellationToken ct) =>
        alert is null || !alert.CanSilence ? Task.CompletedTask
            : ActOnAlert(() => _source.SilenceAlertAsync(alert.Id, alert.SelectedSilence.Minutes, ct), Strings.Format("Alert.Silenced", alert.SelectedSilence.Label), ct);

    private async Task ActOnAlert(Func<Task<string?>> call, string okText, CancellationToken ct)
    {
        try
        {
            var code = await call().ConfigureAwait(true);
            if (code is null)
            {
                AlertMessageIsError = false;
                AlertMessage = okText;
                await RefreshAsync(ct).ConfigureAwait(true); // nowy stan z usługi, nie zgadywany lokalnie
                return;
            }
            var action = KbText.Action(code);
            AlertMessageIsError = true;
            AlertMessage = $"{KbText.Message(code)} ({code})" + (action is null ? "" : " — " + action);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            AlertMessageIsError = true;
            var code = Kobian.UI.Services.UiFailure.CodeOf(ex);
            if (code == Kobian.UI.Services.UiFailure.Offline)
            {
                ConnectionLost = _loaded || ConnectionLost;
                AlertMessage = $"{Strings.Get("Offline.Headline")} (KB-9001)";
                OnPropertyChanged(nameof(ShowStaleBanner));
            }
            else
            {
                AlertMessage = $"{KbText.Message(code)} ({code})"; // odmowa roli/klient zbyt stary/błąd usługi — usługa działa, więc bez baneru „brak połączenia”
            }
        }
    }

    /// <summary>Akcja główna pulpitu = akcja najgorszego zadania; bez zadań — kreator (SCR-03).</summary>
    [RelayCommand]
    private Task PrimaryAsync(CancellationToken ct)
    {
        if (IsOffline) return RefreshAsync(ct);
        // Duży przycisk nie może omijać roli: Podgląd (Viewer) ma akcje wyszarzone, a ten przycisk wysyłał RunJob / otwierał kreator, który zawodził dopiero przy zapisie.
        // Przejście do ekranu naprawy (akcja bez uruchomienia) jest tylko nawigacją i zostaje dostępne.
        if (Jobs.Count > 0) return Jobs[0].RunsJob && !CanRun ? Task.CompletedTask : ActAsync(Jobs[0], ct);
        if (!CanRun) return Task.CompletedTask;
        CreateJobRequested?.Invoke(this, EventArgs.Empty); // pusty pulpit → kreator SCR-03
        return Task.CompletedTask;
    }

    /// <summary>Żądanie otwarcia kreatora zadania (SCR-03) z pustego pulpitu.</summary>
    public event EventHandler? CreateJobRequested;

    internal void Apply(DashboardData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var now = _time.GetUtcNow();
        var c = CultureInfo.CurrentUICulture;
        Role = data.Role;
        ShowRetentionHoldBanner = data.RetentionHeld;
        ShowRuntimeBanner = data.RuntimeCode is not null; // BL-62: KB-7013
        OnPropertyChanged(nameof(RetentionHoldDetail));
        OnPropertyChanged(nameof(RetentionHoldCommand));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunHint));
        Jobs.Clear();
        var evaluated = data.Jobs.Select(j => (Job: j, P: ProtectionEvaluator.Evaluate(j, now)))
            .OrderByDescending(x => x.P.State).ThenBy(x => x.Job.Name, StringComparer.Create(c, ignoreCase: true)).ToList();
        foreach (var (job, p) in evaluated)
            Jobs.Add(new JobTileViewModel(job, p, DetailText(job, p, now, c), HumanTime.At(job.LastSuccessUtc, now, _zone, c),
                HumanTime.At(job.NextRunUtc, now, _zone, c, "Time.NotScheduled"), Strings.Get("Action." + p.Reason, c)));
        Destinations.Clear();
        foreach (var d in data.Destinations.OrderBy(d => d.FreePercent))
            Destinations.Add(new DestinationTile(d.Name, Math.Clamp(d.FreePercent, 0, 100), d.FreePercent < ProtectionEvaluator.LowSpacePercent));

        OnPropertyChanged(nameof(HasDestinations));
        Alerts.Clear();
        foreach (var a in (data.Alerts ?? []).OrderByDescending(a => a.Severity).ThenByDescending(a => a.State is AlertState.Open or AlertState.Notified))
            Alerts.Add(new AlertTileViewModel(a, data.Role, HumanTime.At(a.OpenedUtc, now, _zone, c)));
        OnPropertyChanged(nameof(HasAlerts));
        Overall = ProtectionEvaluator.Overall(evaluated.Select(e => e.P));
        if (Overall is not { } state)
        {
            Headline = Strings.Get("Hero.Empty", c);
            Subline = Strings.Get("Hero.EmptyHint", c);
            PrimaryActionText = Strings.Get("Action.NewJob", c);
            Summary = "";
        }
        else
        {
            Headline = Strings.Get("Hero." + state, c);
            var worst = Jobs[0]; // posortowane: najgorszy pierwszy — jego przyczyna i akcja są „jedną główną akcją” pulpitu
            Subline = evaluated.Count == 1 || state == ProtectionState.Protected ? HeroLine(evaluated, now, c) : $"{worst.Name}: {worst.Detail}";
            PrimaryActionText = worst.ActionText;
            var ok = evaluated.Count(e => e.P.State == ProtectionState.Protected);
            Summary = string.Format(c, Strings.Get("Hero.Summary", c), ok, evaluated.Count);
        }
        UpdatedText = string.Format(c, Strings.Get("Dashboard.UpdatedAt", c), HumanTime.At(data.AsOfUtc, now, _zone, c));
    }

    private string HeroLine(List<(JobStatusInfo Job, JobProtection P)> jobs, DateTimeOffset now, CultureInfo c)
    {
        if (jobs.Count == 1 && jobs[0].P.Reason != ProtectionReason.Ok) return Jobs[0].Detail;
        var last = jobs.Max(j => j.Job.LastSuccessUtc);
        var next = jobs.Where(j => j.Job.NextRunUtc is not null).Min(j => j.Job.NextRunUtc);
        return string.Format(c, Strings.Get("Reason.Ok", c), HumanTime.At(last, now, _zone, c), HumanTime.At(next, now, _zone, c, "Time.NotScheduled"));
    }

    internal static string DetailText(JobStatusInfo j, JobProtection p, DateTimeOffset now, CultureInfo c) => p.Reason switch
    {
        ProtectionReason.Ok => Strings.Get("Reason.OkTile", c), // ostatnia/następna kopia są w polach kafelka — bez powtórzeń
        ProtectionReason.Overdue => string.Format(c, Strings.Get("Reason.Overdue", c), HumanTime.Since(now - j.LastSuccessUtc!.Value, c)),
        ProtectionReason.LowSpace => string.Format(c, Strings.Get("Reason.LowSpace", c), Math.Round(j.DestinationFreePercent ?? 0).ToString(c)),
        ProtectionReason.LastRunWarning when j.SkippedFiles > 0 => string.Format(c, Strings.Get("Reason.LastRunWarningFiles", c), Plural.Text("Plural.File", j.SkippedFiles, c)),
        _ => Strings.Get("Reason." + p.Reason, c),
    };
}
