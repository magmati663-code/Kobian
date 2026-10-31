using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.History;
using Kobian.UI.Localization;
using Kobian.UI.Shell;

namespace Kobian.UI.Restore;

/// <summary>Rodzaj wpisu folderu kopii (BrowseTree).</summary>
public enum BrowseItemKind
{
    File,
    Directory,
    Link,
}

/// <summary>Wpis folderu kopii z usługi: nazwa (dane klienta — tylko do wyświetlenia, NFR-11), rodzaj, rozmiar, czas modyfikacji.</summary>
public sealed record BrowseItem(string Name, BrowseItemKind Kind, long Size, DateTimeOffset? ModifiedUtc);

/// <summary>Wynik BrowseTree: listing albo kod KB (np. KB-5003 — brak folderu, KB-7012 — starszy Agent).</summary>
public sealed record BrowseResult(string? Code, string Path, IReadOnlyList<BrowseItem> Items, bool Truncated);

/// <summary>Port SCR-06 do usługi: lista kopii (ListSnapshots) + folder kopii (BrowseTree).</summary>
public interface ISnapshotBrowseSource
{
    Task<IReadOnlyList<SnapshotEntry>> ListSnapshotsAsync(string? job, CancellationToken ct);

    Task<BrowseResult> BrowseAsync(string snapshotId, string path, CancellationToken ct);
}

/// <summary>Kopia na osi czasu (lewa kolumna): czas lokalny, zadanie, tryb.</summary>
public sealed record SnapshotOption(SnapshotEntry Entry, string When, string Mode, string Size, string DamageText = "")
{
    /// <summary>it. 115 (RD-70): kopia oznaczona przez `check --repair`.</summary>
    public bool IsDamaged => Entry.Damage is not null;

    public bool IsUnrecoverable => Entry.IsUnrecoverable;

    public bool IsPartial => Entry.IsPartial;

    public string Job => Entry.Job;

    public bool IsFull => string.Equals(Entry.Mode, "full", StringComparison.OrdinalIgnoreCase);

    public bool IsDatabase => string.Equals(Entry.Mode, "database", StringComparison.OrdinalIgnoreCase);

    public string AccessibleName => string.Join(", ", new[] { When, Entry.Job, Mode, Size, DamageText }.Where(s => s.Length > 0));
}

/// <summary>Segment ścieżki nad listą („Kopia z … › dokumenty › 2026”); kliknięcie wraca do tego folderu.</summary>
public sealed record BreadcrumbItem(string Text, string Path, bool IsLast);

/// <summary>Wiersz folderu: folder (otwierany), plik albo dowiązanie (tylko informacja — dowiązania nie są odtwarzane, D8.9 R8).</summary>
public sealed class BrowseRowViewModel(BrowseItem item, string path, string sizeText, string modifiedText, bool restorable = true)
{
    /// <summary>„Odtwórz…” przy wierszu: nie dla dowiązań i nie w kopii bazy (tam tylko „Odtwórz jako nową bazę…” — przegląd zrzutu it. 85).</summary>
    public bool CanRestoreItem { get; } = restorable && item.Kind != BrowseItemKind.Link;

    public BrowseItem Item { get; } = item;

    /// <summary>Ścieżka względna w kopii (z „/”).</summary>
    public string Path { get; } = path;

    public string Name => Item.Name;

    public bool IsDirectory => Item.Kind == BrowseItemKind.Directory;

    public bool IsLink => Item.Kind == BrowseItemKind.Link;

    public bool IsFile => Item.Kind == BrowseItemKind.File;

    public string SizeText { get; } = sizeText;

    public string ModifiedText { get; } = modifiedText;

    public string KindText => Strings.Get(Item.Kind switch
    {
        BrowseItemKind.Directory => "Browse.Kind.Folder",
        BrowseItemKind.Link => "Browse.Kind.Link",
        _ => "Browse.Kind.File",
    });

    public string AccessibleName => string.Join(", ", new[] { Name, KindText, SizeText, ModifiedText }.Where(s => s.Length > 0));
}

/// <summary>Żądanie odtworzenia z przeglądarki: kopia + ścieżka (folder albo plik; "" = cała kopia).</summary>
public sealed record RestoreSelection(SnapshotEntry Snapshot, string Path, bool IsDirectory);

/// <summary>
/// SCR-06 Przeglądarka kopii (R2.6.1, AC-07.1; BL-63): oś czasu kopii (ListSnapshots) + zawartość jednego folderu wybranej kopii (BrowseTree),
/// okruszki ścieżki, „W górę”, otwieranie folderów, „Odtwórz…” dla folderu/pliku (→ SCR-07). Każda zmiana kopii/folderu = nowe żądanie
/// z numerem wersji (odpowiedź spóźniona po kolejnym kliknięciu jest odrzucana). Bez połączenia: stan offline (R2.6.4).
/// </summary>
public sealed partial class SnapshotBrowserViewModel : ObservableObject, IRefreshablePage
{
    public const int MaxSnapshots = 500;

    private readonly ISnapshotBrowseSource _source;
    private readonly TimeZoneInfo _zone;
    private string? _pendingSnapshot;
    private int _version;
    private bool _loaded;

    public SnapshotBrowserViewModel(ISnapshotBrowseSource source, TimeZoneInfo? zone = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public string Title { get; } = Strings.Get("Browse.Title");

    public ObservableCollection<SnapshotOption> Snapshots { get; } = [];

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public ObservableCollection<BrowseRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial SnapshotOption? SelectedSnapshot { get; set; }

    /// <summary>Bieżąca ścieżka w kopii ("" = korzeń).</summary>
    [ObservableProperty]
    public partial string CurrentPath { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool ConnectionLost { get; private set; }

    /// <summary>Kod KB ostatniego listowania (np. KB-5003); null = OK.</summary>
    [ObservableProperty]
    public partial string? ErrorCode { get; private set; }

    [ObservableProperty]
    public partial bool Truncated { get; private set; }

    [ObservableProperty]
    public partial string FolderSummary { get; private set; } = "";

    public bool IsOffline => ConnectionLost && !_loaded;

    public bool ShowStaleBanner => ConnectionLost && _loaded;

    public string? OfflineCode => IsOffline ? "KB-9001" : null;

    public bool IsEmpty => _loaded && Snapshots.Count == 0 && !ConnectionLost;

    public bool HasSnapshots => Snapshots.Count > 0;

    public bool HasError => ErrorCode is not null;

    public string ErrorText => ErrorCode is null ? "" : Kobian.UI.Wizard.KbText.Full(ErrorCode);

    public bool HasRows => Rows.Count > 0;

    public bool IsFolderEmpty => SelectedSnapshot is not null && !IsLoading && ErrorCode is null && Rows.Count == 0 && !ConnectionLost;

    public bool CanGoUp => CurrentPath.Length > 0;

    public bool CanRestore => SelectedSnapshot is { IsUnrecoverable: false } && ErrorCode is null && !IsDatabaseSnapshot;

    /// <summary>it. 115 (RD-70): komunikat o szkodach wybranej kopii (częściowa — reszta plików odtwarzalna; nieodtwarzalna — odtwarzanie zablokowane).</summary>
    public bool HasDamageNotice => SelectedSnapshot?.IsDamaged == true;

    public bool IsUnrecoverableSnapshot => SelectedSnapshot?.IsUnrecoverable == true;

    public string DamageNotice => SelectedSnapshot switch
    {
        { IsUnrecoverable: true } => Strings.Get("Browse.Damage.Unrecoverable"),
        { IsPartial: true } s => string.Format(CultureInfo.CurrentCulture, Strings.Get("Browse.Damage.Partial"), Plural.Text("Plural.File", s.Entry.DamagedFiles)),
        _ => "",
    };

    /// <summary>Kopia bazy (tryb Database): zamiast plików — „Odtwórz jako nową bazę…” (SCR-08).</summary>
    public bool IsDatabaseSnapshot => SelectedSnapshot?.IsDatabase == true;

    /// <summary>Żądanie odtworzenia bazy (SCR-08) — obsługuje powłoka.</summary>
    public event EventHandler<SnapshotEntry>? DatabaseRestoreRequested;

    [RelayCommand]
    private void RestoreDatabase()
    {
        if (SelectedSnapshot is { IsDatabase: true, IsUnrecoverable: false } s) DatabaseRestoreRequested?.Invoke(this, s.Entry);
    }

    public string RestoreFolderText => CurrentPath.Length == 0 ? Strings.Get("Browse.RestoreAll") : Strings.Get("Browse.RestoreFolder");

    /// <summary>Żądanie odtworzenia (SCR-07) — obsługuje powłoka.</summary>
    public event EventHandler<RestoreSelection>? RestoreRequested;

    /// <summary>Wejście z historii (SCR-10 „Odtwórz”): po załadowaniu listy zostanie wybrana ta kopia.</summary>
    public void OpenSnapshot(string snapshotId)
    {
        _pendingSnapshot = snapshotId;
        if (_loaded && Snapshots.FirstOrDefault(s => s.Entry.Id == snapshotId) is { } s) SelectedSnapshot = s;
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var all = await _source.ListSnapshotsAsync(null, ct).ConfigureAwait(true);
            ConnectionLost = false;
            _loaded = true;
            var keep = _pendingSnapshot ?? SelectedSnapshot?.Entry.Id;
            _pendingSnapshot = null;
            var c = CultureInfo.CurrentUICulture;
            Snapshots.Clear();
            foreach (var s in all.OrderByDescending(s => s.TimeUtc).ThenBy(s => s.Id, StringComparer.Ordinal).Take(MaxSnapshots))
                Snapshots.Add(Option(s, c));
            var target = Snapshots.FirstOrDefault(s => s.Entry.Id == keep) ?? Snapshots.FirstOrDefault();
            if (ReferenceEquals(target, SelectedSnapshot) && target is not null) await LoadAsync(CurrentPath, ct).ConfigureAwait(true);
            else SelectedSnapshot = target;
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

    partial void OnSelectedSnapshotChanged(SnapshotOption? value)
    {
        CurrentPath = "";
        if (value is null)
        {
            Rows.Clear();
            Breadcrumbs.Clear();
            RaiseDerived();
            return;
        }
        _ = LoadAsync("", CancellationToken.None);
    }

    [RelayCommand]
    private Task Open(BrowseRowViewModel? row) => row is { IsDirectory: true } ? LoadAsync(row.Path, CancellationToken.None) : Task.CompletedTask;

    [RelayCommand]
    private Task GoTo(BreadcrumbItem? crumb) => crumb is null ? Task.CompletedTask : LoadAsync(crumb.Path, CancellationToken.None);

    [RelayCommand]
    private Task Up()
    {
        if (CurrentPath.Length == 0) return Task.CompletedTask;
        var i = CurrentPath.LastIndexOf('/');
        return LoadAsync(i < 0 ? "" : CurrentPath[..i], CancellationToken.None);
    }

    [RelayCommand]
    private void RestoreFolder()
    {
        if (SelectedSnapshot is { IsUnrecoverable: false } s && ErrorCode is null) RestoreRequested?.Invoke(this, new RestoreSelection(s.Entry, CurrentPath, true));
    }

    [RelayCommand]
    private void RestoreItem(BrowseRowViewModel? row)
    {
        if (SelectedSnapshot is { IsDatabase: false, IsUnrecoverable: false } s && row is { IsLink: false }) RestoreRequested?.Invoke(this, new RestoreSelection(s.Entry, row.Path, row.IsDirectory));
    }

    /// <summary>Listowanie folderu (wersjonowane: wynik nieaktualnego żądania jest pomijany).</summary>
    internal async Task LoadAsync(string path, CancellationToken ct)
    {
        if (SelectedSnapshot is not { } snap) return;
        var version = ++_version;
        IsLoading = true;
        RaiseDerived();
        try
        {
            var r = await _source.BrowseAsync(snap.Entry.Id, path, ct).ConfigureAwait(true);
            if (version != _version) return;
            ConnectionLost = false;
            ErrorCode = r.Code;
            Truncated = r.Code is null && r.Truncated;
            CurrentPath = r.Code is null ? r.Path : path;
            BuildRows(r.Code is null ? r.Items : [], CurrentPath);
            BuildBreadcrumbs(snap, CurrentPath);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            if (version == _version) ConnectionLost = true;
        }
        finally
        {
            if (version == _version) IsLoading = false;
            RaiseDerived();
        }
    }

    private void BuildRows(IReadOnlyList<BrowseItem> items, string path)
    {
        var c = CultureInfo.CurrentUICulture;
        Rows.Clear();
        long files = 0, bytes = 0;
        foreach (var i in items)
        {
            var rel = path.Length == 0 ? i.Name : path + "/" + i.Name;
            var modified = i.ModifiedUtc is { } m ? TimeZoneInfo.ConvertTime(m, _zone).ToString("g", c) : "";
            Rows.Add(new BrowseRowViewModel(i, rel, i.Kind == BrowseItemKind.File ? ByteSize.Format(i.Size, c) : "", modified, !IsDatabaseSnapshot && !IsUnrecoverableSnapshot));
            if (i.Kind == BrowseItemKind.File)
            {
                files++;
                bytes += i.Size;
            }
        }
        var dirs = items.Count(i => i.Kind == BrowseItemKind.Directory);
        var parts = new List<string>();
        if (dirs > 0) parts.Add(Plural.Text("Plural.Folder", dirs, c));
        if (files > 0) parts.Add(Plural.Text("Plural.File", files, c) + " (" + ByteSize.Format(bytes, c) + ")");
        FolderSummary = string.Join(" · ", parts); // bez „0 folderów” (przegląd zrzutu it. 76)
    }

    private void BuildBreadcrumbs(SnapshotOption snap, string path)
    {
        Breadcrumbs.Clear();
        var parts = path.Length == 0 ? [] : path.Split('/');
        Breadcrumbs.Add(new BreadcrumbItem(string.Format(CultureInfo.CurrentCulture, Strings.Get("Browse.RootCrumb"), snap.When), "", parts.Length == 0));
        for (var i = 0; i < parts.Length; i++)
            Breadcrumbs.Add(new BreadcrumbItem(parts[i], string.Join('/', parts[..(i + 1)]), i == parts.Length - 1));
    }

    private SnapshotOption Option(SnapshotEntry s, CultureInfo c) => new(s,
        TimeZoneInfo.ConvertTime(s.TimeUtc, _zone).ToString("g", c),
        Strings.Get(ModeKey(s.Mode), c),
        ByteSize.Format(s.BytesRaw, c),
        s.DamageLabel(c));

    internal static string ModeKey(string mode) => mode.ToLowerInvariant() switch
    {
        "full" => "History.Mode.Full",
        "database" => "History.Mode.Database",
        _ => "History.Mode.Incremental",
    };

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(ShowStaleBanner));
        OnPropertyChanged(nameof(OfflineCode));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSnapshots));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsFolderEmpty));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(IsDatabaseSnapshot));
        OnPropertyChanged(nameof(RestoreFolderText));
        OnPropertyChanged(nameof(HasDamageNotice));
        OnPropertyChanged(nameof(IsUnrecoverableSnapshot));
        OnPropertyChanged(nameof(DamageNotice));
    }
}
