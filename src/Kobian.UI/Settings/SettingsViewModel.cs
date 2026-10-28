using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.Localization;
using Kobian.UI.Shell;
using Kobian.UI.Wizard;

namespace Kobian.UI.Settings;

/// <summary>Opcja przełącznika (segmenty): wartość + tekst.</summary>
public sealed record SettingOption(string Value, string Label, string AutomationId)
{
    public override string ToString() => Label;
}

/// <summary>
/// SCR-11 Ustawienia (R2.6.1, it. 81): wygląd (motyw — od razu), język (po ponownym uruchomieniu — teksty ładowane przy tworzeniu widoków),
/// informacje o usłudze (wersja, rola, poziom poprawek .NET KB-7013) i sprawdzenie łańcucha audytu (VerifyAudit). Ustawienia zapisywane lokalnie.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IRefreshablePage
{
    private readonly IUiSettingsStore _store;
    private readonly IServiceInfoSource? _service;
    private readonly Action<string> _applyTheme;
    private readonly string _startLanguage;
    private bool _loading;
    private readonly TimeZoneInfo _zone;

    public SettingsViewModel(IUiSettingsStore store, IServiceInfoSource? service, Action<string> applyTheme, TimeZoneInfo? zone = null)
    {
        _zone = zone ?? TimeZoneInfo.Local;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _service = service;
        _applyTheme = applyTheme ?? throw new ArgumentNullException(nameof(applyTheme));
        var c = CultureInfo.CurrentUICulture;
        Themes = [.. UiSettings.Themes.Select(t => new SettingOption(t, Strings.Get("Settings.Theme." + t, c), "SCR-11.Theme." + t))];
        Languages = [.. UiSettings.Languages.Select(l => new SettingOption(l, Strings.Get("Settings.Lang." + l, c), "SCR-11.Lang." + l))];
        var s = store.Load();
        _startLanguage = s.Language;
        _loading = true;
        SelectedTheme = Themes.First(t => t.Value == s.Theme);
        SelectedLanguage = Languages.First(l => l.Value == s.Language);
        _loading = false;
    }

    public string Title { get; } = Strings.Get("Nav.Settings");

    public ObservableCollection<SettingOption> Themes { get; }

    public ObservableCollection<SettingOption> Languages { get; }

    [ObservableProperty]
    public partial SettingOption SelectedTheme { get; set; }

    [ObservableProperty]
    public partial SettingOption SelectedLanguage { get; set; }

    public bool RestartNeeded => SelectedLanguage.Value != _startLanguage;

    [ObservableProperty]
    public partial ServiceInfo? Info { get; private set; }

    [ObservableProperty]
    public partial bool ConnectionLost { get; private set; }

    [ObservableProperty]
    public partial string AuditText { get; private set; } = "";

    [ObservableProperty]
    public partial bool AuditBroken { get; private set; }

    public bool HasService => _service is not null;

    public bool HasInfo => Info is not null;

    public string VersionText => Info?.AgentVersion ?? "—";

    public string RoleText => Info is null ? "—" : Strings.Get("Settings.Role." + Info.Role);

    public bool RuntimeUnpatched => Info?.RuntimeCode is not null;

    /// <summary>Krótko (przegląd zrzutu it. 81: pełny tekst KB był za długi w wierszu tabeli): wersja + kod + jedna akcja.</summary>
    public string RuntimeText => Info?.RuntimeCode is { } code
        ? string.Format(CultureInfo.CurrentCulture, Strings.Get("Settings.RuntimeBad"), Info.RuntimeVersion ?? "?", code)
        : Strings.Get("Settings.RuntimeOk");

    public bool HasAuditResult => AuditText.Length > 0;

    partial void OnSelectedThemeChanged(SettingOption value)
    {
        if (value is null) return;
        _applyTheme(value.Value);
        Persist();
    }

    partial void OnSelectedLanguageChanged(SettingOption value)
    {
        if (value is null) return;
        Persist();
        OnPropertyChanged(nameof(RestartNeeded));
    }

    private void Persist()
    {
        if (_loading || SelectedTheme is null || SelectedLanguage is null) return;
        try
        {
            _store.Save(new UiSettings(SelectedLanguage.Value, SelectedTheme.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // brak zapisu w profilu — ustawienie działa do końca sesji
        }
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (_service is null) return;
        try
        {
            Info = await _service.GetServiceInfoAsync(ct).ConfigureAwait(true);
            ConnectionLost = false;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            ConnectionLost = true;
        }
        RaiseInfo();
    }

    [RelayCommand]
    private async Task VerifyAuditAsync(CancellationToken ct)
    {
        if (_service is null) return;
        try
        {
            var r = await _service.VerifyAuditAsync(ct).ConfigureAwait(true);
            AuditBroken = !r.Intact;
            LastAnchor = r.LastAnchor;
            LastAnchorText = r.LastAnchor is null ? "" : string.Format(CultureInfo.CurrentCulture, Strings.Get("Settings.AnchorLine"), AnchorTime(r.LastAnchorAtUtc));
            var c = CultureInfo.CurrentCulture;
            AuditText = r.Intact
                ? string.Format(c, Strings.Get("Settings.AuditOk"), Plural.Text("Plural.Entry", r.Entries, CultureInfo.CurrentUICulture))
                : r.BrokenSeq is { } b
                    ? string.Format(c, Strings.Get("Settings.AuditBroken"), b) + " (" + (r.Code ?? "KB-6501") + ")"
                    : KbText.Full(r.Code ?? "KB-0001");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            AuditBroken = true;
            AuditText = KbText.Full("KB-9001");
            LastAnchor = null;
            LastAnchorText = "";
        }
        OnPropertyChanged(nameof(HasAuditResult));
        OnPropertyChanged(nameof(HasLastAnchor));
    }

    /// <summary>D9.9 (it. 104): ostatnia kotwica „seq:sha256hex” — tekst do skopiowania i zapisania POZA serwerem (`kobian audit verify --anchor`).</summary>
    [ObservableProperty]
    public partial string? LastAnchor { get; private set; }

    [ObservableProperty]
    public partial string LastAnchorText { get; private set; } = "";

    public bool HasLastAnchor => LastAnchor is not null;

    /// <summary>Czas kotwicy w strefie użytkownika; nieczytelny → tekst „—”.</summary>
    private string AnchorTime(string? atUtc) =>
        DateTimeOffset.TryParse(atUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? TimeZoneInfo.ConvertTime(t, _zone).ToString("g", CultureInfo.CurrentCulture)
            : "—";

    private void RaiseInfo()
    {
        OnPropertyChanged(nameof(HasInfo));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(RuntimeUnpatched));
        OnPropertyChanged(nameof(RuntimeText));
    }
}
