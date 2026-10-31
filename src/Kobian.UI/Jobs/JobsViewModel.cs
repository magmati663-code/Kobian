using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.Dashboard;
using Kobian.UI.Localization;
using Kobian.UI.Shell;
using Kobian.UI.Wizard;

namespace Kobian.UI.Jobs;

/// <summary>Filtr listy zadań (SCR-02): wszystkie / wymagające uwagi (Uwaga + Zagrożone) / wstrzymane.</summary>
public enum JobsFilter
{
    All = 0,
    Attention = 1,
    Paused = 2,
}

/// <summary>Pozycja filtra z liczbą zadań (segmenty nad listą).</summary>
public sealed record JobsFilterOption(JobsFilter Filter, string Text, int Count)
{
    public string Label => $"{Text} ({Count.ToString(CultureInfo.CurrentCulture)})";

    public string AutomationId => "SCR-02.Filter." + Filter;
}

/// <summary>Wiersz zadania (SCR-02): stan jak na pulpicie (ta sama funkcja 6.3) + harmonogram + akcje uruchom/wstrzymaj/wznów.</summary>
public sealed class JobRowViewModel(JobStatusInfo job, JobProtection protection, JobSchedule? schedule, string detail, string lastCopy, string scheduleText)
{
    public JobStatusInfo Job { get; } = job;

    public string Name => Job.Name;

    public ProtectionState State => protection.State;

    public ProtectionReason Reason => protection.Reason;

    public string StateText => Strings.Get("State." + protection.State);

    public string Detail { get; } = detail;

    public string LastCopy { get; } = lastCopy;

    public JobSchedule? Schedule { get; } = schedule;

    public string ScheduleText { get; } = scheduleText;

    public bool HasSchedule => Schedule is not null;

    /// <summary>Harmonogram aktywny — przycisk „Wstrzymaj”; wyłączony z zachowanym wyrażeniem — „Wznów”.</summary>
    public bool ScheduleActive => Schedule is { Enabled: true };

    public string ToggleText => Strings.Get(ScheduleActive ? "Jobs.Pause" : "Jobs.Resume");

    /// <summary>BL-76: „W kolejce” (czeka na zasób, D7.4) albo „W toku”; null = nic nie trwa.</summary>
    public string? ActivityText => Job.Activity switch
    {
        ActiveRun.Queued => Strings.Get("Jobs.Activity.Queued"),
        ActiveRun.Running => Strings.Get("Jobs.Activity.Running"),
        _ => null,
    };

    public bool HasActivity => ActivityText is not null;

    public string? Code => protection.State is ProtectionState.AtRisk or ProtectionState.Warning ? Job.LastCode : null;

    public string AutomationId => "SCR-02.Job." + Job.JobId;

    public string AccessibleName => HasActivity ? $"{Name}: {StateText}, {ActivityText}. {Detail} {ScheduleText}." : $"{Name}: {StateText}. {Detail} {ScheduleText}.";
}

/// <summary>
/// SCR-02 Zadania (R2.6.1, D6.6): lista wszystkich zadań z filtrem, stanem ochrony i harmonogramem. Akcje od roli Operator (6.7);
/// odmowa usługi pokazywana jako komunikat KB + akcja (R2.6.5), brak połączenia jak na pulpicie (R2.6.4, KB-9001).
/// </summary>
public sealed partial class JobsViewModel : ObservableObject, IRefreshablePage
{
    private readonly IJobsSource _source;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly List<JobRowViewModel> _all = [];
    private bool _loaded;

    public JobsViewModel(IJobsSource source, TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        Filters = [];
        RebuildFilters();
        SelectedFilter = Filters[0];
    }

    public ObservableCollection<JobRowViewModel> Rows { get; } = [];

    public ObservableCollection<JobsFilterOption> Filters { get; }

    [ObservableProperty]
    public partial JobsFilterOption SelectedFilter { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(ToggleScheduleCommand))]
    public partial CallerRole Role { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool ConnectionLost { get; private set; }

    /// <summary>Wynik ostatniej akcji (KB + akcja albo potwierdzenie); null = brak komunikatu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    [ObservableProperty]
    public partial bool NoticeIsError { get; private set; }

    public bool HasNotice => Notice is not null;

    public string Title { get; } = Strings.Get("Nav.Jobs");

    public bool CanAct => Role >= CallerRole.Operator;

    public string RunHint => CanAct ? "" : Strings.Get("Action.RequiresOperator");

    public bool HasRows => Rows.Count > 0;

    /// <summary>Brak zadań po udanym odczycie — zachęta do kreatora (SCR-03).</summary>
    public bool IsEmpty => _loaded && _all.Count == 0 && !ConnectionLost;

    /// <summary>Są zadania, ale filtr nic nie zwraca.</summary>
    public bool FilterEmpty => _all.Count > 0 && Rows.Count == 0;

    public bool IsOffline => ConnectionLost && !_loaded;

    public bool ShowStaleBanner => ConnectionLost && _loaded;

    public string? OfflineCode => IsOffline ? "KB-9001" : null;

    /// <summary>Żądanie kreatora nowego zadania (SCR-03) — obsługuje powłoka.</summary>
    public event EventHandler? CreateJobRequested;

    [RelayCommand]
    private void NewJob() => CreateJobRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var data = await _source.LoadJobsAsync(ct).ConfigureAwait(true);
            ConnectionLost = false;
            _loaded = true;
            Apply(data);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            ConnectionLost = true; // R2.6.4: poprzednie dane zostają z banerem
        }
        finally
        {
            IsLoading = false;
            RaiseDerived();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RunAsync(JobRowViewModel? row, CancellationToken ct)
    {
        if (row is null) return;
        // Po przyjęciu odświeżamy listę: pigułka „W kolejce/W toku” (BL-76) pojawia się od razu i nie zachęca do drugiego kliknięcia (drugie uruchomienie).
        if (await Act(() => _source.StartJobAsync(row.Job.JobId, ct), string.Format(CultureInfo.CurrentCulture, Strings.Get("Jobs.Started"), row.Name), ct).ConfigureAwait(true))
            await RefreshAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Wstrzymanie (SetSchedule bez cron — wyrażenie zostaje) albo wznowienie zachowanym wyrażeniem. Bez harmonogramu: brak akcji.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ToggleScheduleAsync(JobRowViewModel? row, CancellationToken ct)
    {
        if (row?.Schedule is not { } s) return;
        var pause = s.Enabled;
        var ok = string.Format(CultureInfo.CurrentCulture, Strings.Get(pause ? "Jobs.Paused" : "Jobs.Resumed"), row.Name);
        if (await Act(() => _source.SetScheduleAsync(row.Job.JobId, pause ? null : s.Cron, ct), ok, ct).ConfigureAwait(true))
            await RefreshAsync(ct).ConfigureAwait(true);
    }

    private async Task<bool> Act(Func<Task<string?>> call, string okText, CancellationToken ct)
    {
        try
        {
            var code = await call().ConfigureAwait(true);
            if (code is null)
            {
                Show(okText, error: false);
                return true;
            }
            var action = KbText.Action(code);
            Show($"{KbText.Message(code)} ({code})" + (action is null ? "" : " — " + action), error: true);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            ConnectionLost = _loaded || ConnectionLost;
            Show($"{Strings.Get("Offline.Headline")} (KB-9001)", error: true);
            RaiseDerived();
        }
        ct.ThrowIfCancellationRequested();
        return false;
    }

    private void Show(string text, bool error)
    {
        NoticeIsError = error;
        Notice = text;
    }

    partial void OnSelectedFilterChanged(JobsFilterOption value) => ApplyFilter();

    internal void Apply(JobsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var now = _time.GetUtcNow();
        var c = CultureInfo.CurrentUICulture;
        Role = data.Role;
        var schedules = data.Schedules.GroupBy(s => s.Job, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _all.Clear();
        foreach (var raw in data.Jobs)
        {
            schedules.TryGetValue(raw.JobId, out var sched);
            // Wstrzymany harmonogram = brak kopii automatycznych → stan „Wstrzymane” (6.3); cykl z cron, gdy usługa go nie podała.
            var job = raw with
            {
                Enabled = raw.Enabled && sched is not { Enabled: false },
                Interval = raw.Interval ?? (sched is { Enabled: true } ? CronText.Interval(sched.Cron) : null),
            };
            var p = ProtectionEvaluator.Evaluate(job, now);
            var schedText = sched is null ? Strings.Get("Jobs.Manual", c)
                : sched.Enabled ? CronText.Describe(sched.Cron, c)
                : string.Format(c, Strings.Get("Jobs.SchedulePaused", c), CronText.Describe(sched.Cron, c));
            var detail = raw.Enabled && sched is { Enabled: false } ? Strings.Get("Reason.SchedulePaused", c) : Dashboard.DashboardViewModel.DetailText(job, p, now, c);
            _all.Add(new JobRowViewModel(job, p, sched, detail, HumanTime.At(job.LastSuccessUtc, now, _zone, c), schedText));
        }
        _all.Sort((a, b) => b.State != a.State ? b.State.CompareTo(a.State) : StringComparer.Create(c, ignoreCase: true).Compare(a.Name, b.Name));
        var keep = SelectedFilter?.Filter ?? JobsFilter.All;
        RebuildFilters();
        SelectedFilter = Filters.First(f => f.Filter == keep);
        ApplyFilter();
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(RunHint));
    }

    private static bool Matches(JobRowViewModel r, JobsFilter f) => f switch
    {
        JobsFilter.Attention => r.State is ProtectionState.AtRisk or ProtectionState.Warning,
        JobsFilter.Paused => r.State == ProtectionState.Paused,
        _ => true,
    };

    private void RebuildFilters()
    {
        Filters.Clear();
        foreach (var f in Enum.GetValues<JobsFilter>())
            Filters.Add(new JobsFilterOption(f, Strings.Get("Jobs.Filter." + f), _all.Count(r => Matches(r, f))));
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        var f = SelectedFilter?.Filter ?? JobsFilter.All;
        foreach (var r in _all.Where(r => Matches(r, f))) Rows.Add(r);
        RaiseDerived();
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(FilterEmpty));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(ShowStaleBanner));
        OnPropertyChanged(nameof(OfflineCode));
    }
}
