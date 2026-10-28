using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.Dashboard;
using Kobian.UI.Localization;
using Kobian.UI.Shell;

namespace Kobian.UI.History;

/// <summary>Kopia (snapshot) z usługi — ListSnapshots. Tylko metadane (NFR-11): ID, zadanie, czas, tryb, liczba plików, rozmiar danych źródłowych.</summary>
/// <remarks>it. 115 (RD-70): <c>Damage</c> = null | "partial" | "unrecoverable" — wynik `check --repair` (kopia nieodtwarzalna nie jest proponowana do odtworzenia).</remarks>
public sealed record SnapshotEntry(string Id, string Job, DateTimeOffset TimeUtc, string Mode, long Files, long BytesRaw, string? Damage = null, long DamagedFiles = 0)
{
    public bool IsUnrecoverable => Damage == "unrecoverable";

    public bool IsPartial => Damage == "partial";

    /// <summary>Krótka etykieta szkód (pigułka) albo "" dla kopii bez szkód.</summary>
    public string DamageLabel(System.Globalization.CultureInfo c) => IsUnrecoverable ? Strings.Get("Snapshot.Damage.Unrecoverable", c)
        : IsPartial ? string.Format(c, Strings.Get("Snapshot.Damage.Partial", c), Plural.Text("Plural.File", DamagedFiles, c)) : "";
}

/// <summary>
/// Uruchomienie z usługi (ListRuns, BL-60): rodzaj backup/restore/check, stan maszyny uruchomień, czas (zakończenia, inaczej startu), kod KB.
/// Tylko metadane (NFR-11).
/// </summary>
public sealed record RunEntry(string RunId, string Job, string Kind, string State, DateTimeOffset TimeUtc, string? Code, string? SnapshotId);

/// <summary>Port SCR-10 do usługi: kopie (ListSnapshots) + uruchomienia (ListRuns — także nieudane, pominięte, odtwarzania i weryfikacje).</summary>
public interface IHistorySource
{
    Task<IReadOnlyList<SnapshotEntry>> ListSnapshotsAsync(string? job, CancellationToken ct);

    /// <summary>Domyślnie brak (źródło bez ListRuns, np. starszy Agent) — historia pokazuje wtedy tylko udane kopie (Qb19).</summary>
    Task<IReadOnlyList<RunEntry>> ListRunsAsync(string? job, CancellationToken ct) => Task.FromResult<IReadOnlyList<RunEntry>>([]);
}

/// <summary>Wygląd pigułki wiersza: tryb kopii albo wynik uruchomienia (tekst zawsze obecny — kolor nie jest jedynym nośnikiem, WCAG 1.4.1).</summary>
public enum HistoryPill
{
    Incremental,
    Full,
    Neutral,
    Warning,
    Failed,
}

/// <summary>Rozmiary dla ludzi (6.9): jednostki binarne z polskimi/angielskimi separatorami („1,5 GB”), bez ułamków dla bajtów.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        if (bytes < 1024) return Math.Max(0, bytes).ToString(c) + " B";
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return v.ToString(v >= 100 ? "0" : "0.#", c) + " " + Units[u];
    }
}

/// <summary>
/// Wiersz historii: kopia (z „Odtwórz”) albo uruchomienie bez kopii (nieudane, pominięte, w toku, odtwarzanie, weryfikacja) z kodem KB zamiast ID.
/// Skrót ID 8 znaków jak w CLI (AC-37.1).
/// </summary>
public sealed class HistoryRowViewModel
{
    public HistoryRowViewModel(SnapshotEntry entry, string time, string modeText, string filesText, string sizeText, string damageText = "")
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Job = entry.Job;
        TimeUtc = entry.TimeUtc;
        Time = time;
        // it. 115 (RD-70): kopia uszkodzona — pigułka z tekstem szkód (kolor nie jest jedynym nośnikiem, WCAG 1.4.1)
        ModeText = damageText.Length > 0 ? damageText : modeText;
        Pill = entry.IsUnrecoverable ? HistoryPill.Failed : entry.IsPartial ? HistoryPill.Warning
            : string.Equals(entry.Mode, "full", StringComparison.OrdinalIgnoreCase) ? HistoryPill.Full : HistoryPill.Incremental;
        FilesText = filesText;
        SizeText = sizeText;
        ShortId = entry.Id.Length > 8 ? entry.Id[..8] : entry.Id;
        SortKey = entry.Id;
    }

    public HistoryRowViewModel(RunEntry run, string time, string statusText, HistoryPill pill)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
        Job = run.Job;
        TimeUtc = run.TimeUtc;
        Time = time;
        ModeText = statusText;
        Pill = pill;
        FilesText = "";
        SizeText = "";
        ShortId = run.Code ?? "";
        SortKey = run.RunId;
    }

    /// <summary>Kopia — null dla wiersza uruchomienia.</summary>
    public SnapshotEntry? Entry { get; }

    public RunEntry? Run { get; }

    public DateTimeOffset TimeUtc { get; }

    internal string SortKey { get; }

    public string Time { get; }

    public string Job { get; }

    public string ModeText { get; }

    public HistoryPill Pill { get; }

    public bool IsFull => Pill == HistoryPill.Full;

    public bool IsFailed => Pill == HistoryPill.Failed;

    public bool IsWarning => Pill == HistoryPill.Warning;

    /// <summary>Kopia nieodtwarzalna (`check --repair`) nie ma „Odtwórz”; częściowo uszkodzona — ma (reszta plików odtwarzalna).</summary>
    public bool CanRestore => Entry is { IsUnrecoverable: false };

    public string FilesText { get; }

    public string SizeText { get; }

    /// <summary>Skrót ID kopii albo kod KB uruchomienia.</summary>
    public string ShortId { get; }

    public bool HasCode => ShortId.Length > 0;

    public string AccessibleName => string.Join(", ", new[] { Time, Job, ModeText, FilesText, SizeText, Entry is null ? ShortId : "" }.Where(s => s.Length > 0));
}

/// <summary>Grupa dnia (nagłówek „Dziś”, „Wczoraj” albo data) z liczbą kopii.</summary>
public sealed record HistoryDayGroup(string Header, string CountText, IReadOnlyList<HistoryRowViewModel> Rows);

/// <summary>Pozycja filtra zadania; <see cref="Job"/> = null — wszystkie zadania.</summary>
public sealed record HistoryJobOption(string? Job, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// SCR-10 Historia (R2.6.1): kopie od najnowszej, pogrupowane wg dnia w strefie użytkownika, filtr zadania, akcja „Odtwórz” (→ SCR-06).
/// Bez połączenia: stan offline/baner jak na pulpicie (R2.6.4).
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject, IRefreshablePage
{
    /// <summary>Limit wierszy w widoku (ochrona UI przed bardzo długą historią; reszta przez filtr zadania).</summary>
    public const int MaxRows = 500;

    private readonly IHistorySource _source;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private IReadOnlyList<SnapshotEntry> _all = [];
    private IReadOnlyList<RunEntry> _runs = [];
    private bool _loaded;

    public HistoryViewModel(IHistorySource source, TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        JobOptions = [new HistoryJobOption(null, Strings.Get("History.AllJobs"))];
        SelectedJob = JobOptions[0];
    }

    public ObservableCollection<HistoryDayGroup> Days { get; } = [];

    public ObservableCollection<HistoryJobOption> JobOptions { get; }

    [ObservableProperty]
    public partial HistoryJobOption SelectedJob { get; set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool ConnectionLost { get; private set; }

    [ObservableProperty]
    public partial bool Truncated { get; private set; }

    public string Title { get; } = Strings.Get("Nav.History");

    public bool HasRows => Days.Count > 0;

    public bool IsEmpty => _loaded && Days.Count == 0 && !ConnectionLost;

    public bool IsOffline => ConnectionLost && !_loaded;

    public bool ShowStaleBanner => ConnectionLost && _loaded;

    public string? OfflineCode => IsOffline ? "KB-9001" : null;

    /// <summary>Żądanie odtworzenia z wybranej kopii (SCR-06) — obsługuje powłoka.</summary>
    public event EventHandler<SnapshotEntry>? RestoreRequested;

    [RelayCommand]
    private void Restore(HistoryRowViewModel? row)
    {
        if (row?.Entry is { IsUnrecoverable: false } e) RestoreRequested?.Invoke(this, e);
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            _all = await _source.ListSnapshotsAsync(null, ct).ConfigureAwait(true);
            _runs = await _source.ListRunsAsync(null, ct).ConfigureAwait(true);
            ConnectionLost = false;
            _loaded = true;
            RebuildJobs();
            Rebuild();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            ConnectionLost = true;
        }
        finally
        {
            IsLoading = false;
            RaiseDerived();
        }
    }

    partial void OnSelectedJobChanged(HistoryJobOption value)
    {
        if (_loaded) Rebuild();
    }

    private void RebuildJobs()
    {
        var keep = SelectedJob?.Job;
        var c = CultureInfo.CurrentUICulture;
        var jobs = _all.Select(s => s.Job).Concat(_runs.Select(r => r.Job)).Distinct(StringComparer.Ordinal).Order(StringComparer.Create(c, ignoreCase: true)).ToList();
        JobOptions.Clear();
        JobOptions.Add(new HistoryJobOption(null, Strings.Get("History.AllJobs", c)));
        foreach (var j in jobs) JobOptions.Add(new HistoryJobOption(j, j));
        SelectedJob = JobOptions.FirstOrDefault(o => o.Job == keep) ?? JobOptions[0];
    }

    internal void Rebuild()
    {
        var c = CultureInfo.CurrentUICulture;
        var now = _time.GetUtcNow();
        var today = TimeZoneInfo.ConvertTime(now, _zone).Date;
        var job = SelectedJob?.Job;
        bool Match(string j) => job is null || string.Equals(j, job, StringComparison.Ordinal);
        var list = _all.Where(s => Match(s.Job)).OrderByDescending(s => s.TimeUtc).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        var rowsAll = list.Select(s => new HistoryRowViewModel(s,
                TimeZoneInfo.ConvertTime(s.TimeUtc, _zone).ToString("HH:mm", c),
                Strings.Get(Kobian.UI.Restore.SnapshotBrowserViewModel.ModeKey(s.Mode), c),
                Plural.Text("Plural.File", s.Files, c),
                ByteSize.Format(s.BytesRaw, c),
                s.DamageLabel(c)))
            // Uruchomienie, które dało kopię, jest już widoczne jako kopia; reszta (nieudane, pominięte, w toku, restore, check) — osobny wiersz.
            .Concat(_runs.Where(r => Match(r.Job) && !(r.Kind == "backup" && r.SnapshotId is not null)).Select(r => RunRow(r, c)))
            .OrderByDescending(r => r.TimeUtc).ThenBy(r => r.SortKey, StringComparer.Ordinal).ToList();
        Truncated = rowsAll.Count > MaxRows;
        Days.Clear();
        foreach (var g in rowsAll.Take(MaxRows).GroupBy(r => TimeZoneInfo.ConvertTime(r.TimeUtc, _zone).Date))
        {
            var rows = g.ToList();
            var copies = rows.Count(r => r.Entry is not null);
            var failed = rows.Count(r => r.IsFailed);
            var days = (g.Key - today).Days;
            var header = days switch
            {
                0 => Strings.Get("History.Today", c),
                -1 => Strings.Get("History.Yesterday", c),
                _ => Capitalize(g.Key.ToString("dddd, d MMMM yyyy", c), c),
            };
            var count = Plural.Text("Plural.Copy", copies, c);
            if (failed > 0) count += " · " + string.Format(c, Strings.Get("History.FailedCount", c), failed);
            Days.Add(new HistoryDayGroup(header, count, rows));
        }
        Summary = list.Count == 0 ? "" : string.Format(c, Strings.Get("History.Summary", c), Plural.Text("Plural.Copy", list.Count, c),
            HumanTime.At(list[0].TimeUtc, now, _zone, c));
        RaiseDerived();
    }

    /// <summary>Tekst i wygląd wiersza uruchomienia: rodzaj (odtwarzanie/weryfikacja) albo stan kopii; kolor wg wyniku.</summary>
    internal HistoryRowViewModel RunRow(RunEntry r, CultureInfo c)
    {
        var pill = r.State switch
        {
            "Failed" => HistoryPill.Failed,
            "Warning" or "Missed" or "Interrupted" or "Cancelled" => HistoryPill.Warning,
            _ => HistoryPill.Neutral,
        };
        var key = r.Kind switch
        {
            "restore" => "History.Kind.Restore",
            "check" => "History.Kind.Check",
            "forget" => "History.Kind.Forget",
            "prune" => "History.Kind.Prune",
            _ => r.State switch
            {
                "Failed" => "History.Run.Failed",
                "Warning" => "History.Run.Warning",
                "Skipped" => "History.Run.Skipped",
                "Missed" => "History.Run.Missed",
                "Interrupted" => "History.Run.Interrupted",
                "Cancelled" => "History.Run.Cancelled",
                "Queued" => "History.Run.Queued", // BL-76: ręczne uruchomienie czekające na zasób (D7.4) albo zaplanowane przed startem
                "Running" or "Paused" => "History.Run.Running",
                _ => "History.Run.Other",
            },
        };
        // Odtwarzanie/weryfikacja z problemem: jedna krótka fraza (mieści się w pigułce), np. „weryfikacja z uwagami”.
        if (r.Kind is "restore" or "check" or "forget" or "prune" && pill != HistoryPill.Neutral) key += pill == HistoryPill.Failed ? ".Failed" : ".Warning";
        var text = Strings.Get(key, c);
        return new HistoryRowViewModel(r, TimeZoneInfo.ConvertTime(r.TimeUtc, _zone).ToString("HH:mm", c), text, pill);
    }

    private static string Capitalize(string s, CultureInfo c) => s.Length == 0 ? s : char.ToUpper(s[0], c) + s[1..];

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(ShowStaleBanner));
        OnPropertyChanged(nameof(OfflineCode));
    }
}
