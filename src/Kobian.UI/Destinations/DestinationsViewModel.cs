using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.History;
using Kobian.UI.Localization;
using Kobian.UI.Shell;
using Kobian.UI.Wizard;

namespace Kobian.UI.Destinations;

/// <summary>
/// Cel z usługi (ListDestinations): schemat, korzeń, zadania. Bez sekretów. Host/port/odcisk (publiczny) — opcjonalne: ListDestinations v1 ich nie zwraca
/// (DestinationInfo = id, scheme, root), karta pokazuje je, gdy są.
/// </summary>
// PROWIZORYCZNIE Qb76: kontrakt DestinationInfo bez host/port/odcisku — pola opcjonalne tu, wypełnia je dopiero rozszerzony ListDestinations (notatki it. 120).
public sealed record DestinationItem(string Id, string Scheme, string Root, IReadOnlyList<string> Jobs, string? Host = null, int? Port = null, string? HostKey = null);

/// <summary>Wynik sprawdzenia celu (ProbeDestination): wolne/całkowite miejsce albo kod KB.</summary>
public sealed record DestinationCheck(string? Code, long? FreeBytes, long? TotalBytes, bool RepositoryExists);

/// <summary>Lista celów + nieczytelne definicje zadań.</summary>
public sealed record DestinationList(IReadOnlyList<DestinationItem> Items, int UnreadableJobs);

/// <summary>Port SCR-09 do usługi.</summary>
public interface IDestinationsSource
{
    Task<DestinationList> ListDestinationsAsync(CancellationToken ct);

    Task<DestinationCheck> CheckDestinationAsync(DestinationItem destination, CancellationToken ct);
}

/// <summary>Karta celu: nazwa (korzeń), schemat słownie, zadania, wynik sprawdzenia (miejsce z paskiem albo komunikat KB).</summary>
public sealed partial class DestinationCardViewModel(DestinationItem item, string schemeText, string jobsText) : ObservableObject
{
    public DestinationItem Item { get; } = item;

    public string Root => Item.Root;

    public string SchemeText { get; } = schemeText;

    public string JobsText { get; } = jobsText;

    /// <summary>Cel z szyfrowanym transportem (SFTP, FTPS) — ikona kłódki + słowny opis w nazwie dostępnej (kolor/ikona nie są jedynym nośnikiem).</summary>
    public bool IsEncrypted => Item.Scheme is "sftp" or "ftps";

    /// <summary>FTPS: „nas:990 · Implicit TLS · przypięty klucz SHA256:AbCd…” albo informacja, że szczegóły są w definicji zadania.</summary>
    public string DetailText { get; } = DestinationsViewModel.Details(item);

    public bool HasDetail => DetailText.Length > 0;

    /// <summary>Pełny odcisk w podpowiedzi (skrót na karcie).</summary>
    public string? FingerprintTip => Item.HostKey;

    public string AutomationId => "SCR-09.Dest." + Item.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpace), nameof(HasError), nameof(LowSpace))]
    public partial bool Checked { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckNow))]
    public partial bool IsChecking { get; internal set; }

    /// <summary>
    /// Cel sieciowy (SFTP/FTPS) bez adresu serwera w odpowiedzi ListDestinations v1 nie da się sprawdzić z listy — Agent odpowiedziałby mylącym KB-2106
    /// („folder nie istnieje”). Przycisk wyłączony z wyjaśnieniem (it. 120); lokalny/SMB — bez zmian.
    /// </summary>
    public bool CanCheck => Item.Scheme is not ("sftp" or "ftps" or "ftp") || !string.IsNullOrEmpty(Item.Host);

    public bool CanCheckNow => CanCheck && !IsChecking;

    public bool ShowCheckUnavailable => !CanCheck;

    /// <summary>Wolne/całkowite miejsce znane — pasek tylko wtedy (cel bez AVBL/statvfs: sam tekst, bez pustego „pełnego” paska).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LowSpace))]
    public partial bool SpaceKnown { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpace), nameof(UsedPercent))]
    public partial double FreePercent { get; internal set; }

    /// <summary>Pasek jak na pulpicie: zajęte miejsce.</summary>
    public double UsedPercent => 100 - FreePercent;

    [ObservableProperty]
    public partial string SpaceText { get; internal set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; internal set; } = "";

    public bool HasSpace => Checked && ErrorText.Length == 0 && SpaceText.Length > 0;

    public bool HasError => Checked && ErrorText.Length > 0;

    public bool LowSpace => HasSpace && SpaceKnown && FreePercent < Dashboard.ProtectionEvaluator.LowSpacePercent;
}

/// <summary>
/// SCR-09 Cele (R2.6.1, it. 82): cele z definicji zadań (schemat, korzeń, zadania) i „Sprawdź” — ProbeDestination na żądanie (połączenie z celem na koncie
/// usługi: wolne miejsce, obecność repozytorium, błąd KB). Edycja celów i sekretów (UpsertDestination/SetSecret) — później (Admin, step-up).
/// </summary>
public sealed partial class DestinationsViewModel(IDestinationsSource source) : ObservableObject, IRefreshablePage
{
    private readonly IDestinationsSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private bool _loaded;

    public string Title { get; } = Strings.Get("Nav.Destinations");

    public ObservableCollection<DestinationCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    public partial bool ConnectionLost { get; private set; }

    [ObservableProperty]
    public partial string UnreadableText { get; private set; } = "";

    public bool HasUnreadable => UnreadableText.Length > 0;

    public bool IsOffline => ConnectionLost && !_loaded;

    public bool ShowStaleBanner => ConnectionLost && _loaded;

    public string? OfflineCode => IsOffline ? "KB-9001" : null;

    public bool IsEmpty => _loaded && Cards.Count == 0 && !ConnectionLost;

    public bool HasCards => Cards.Count > 0;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var list = await _source.ListDestinationsAsync(ct).ConfigureAwait(true);
            ConnectionLost = false;
            _loaded = true;
            var c = CultureInfo.CurrentUICulture;
            Cards.Clear();
            foreach (var d in list.Items)
                Cards.Add(new DestinationCardViewModel(d, Strings.Get("Dest.Scheme." + (d.Scheme is "local" or "sftp" or "smb" or "ftp" or "ftps" ? d.Scheme : "other"), c),
                    string.Format(CultureInfo.CurrentCulture, Strings.Get("Dest.UsedBy", c), string.Join(", ", d.Jobs))));
            UnreadableText = list.UnreadableJobs > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Get("Dest.Unreadable", c), list.UnreadableJobs) : "";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            ConnectionLost = true;
        }
        OnPropertyChanged(nameof(HasUnreadable));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(ShowStaleBanner));
        OnPropertyChanged(nameof(OfflineCode));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasCards));
    }

    [RelayCommand]
    private async Task CheckAsync(DestinationCardViewModel? card)
    {
        if (card is null || card.IsChecking) return;
        card.IsChecking = true;
        try
        {
            var r = await _source.CheckDestinationAsync(card.Item, CancellationToken.None).ConfigureAwait(true);
            Apply(card, r);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Apply(card, new DestinationCheck("KB-9001", null, null, false));
        }
        catch (InvalidOperationException)
        {
            Apply(card, new DestinationCheck("KB-7011", null, null, false));
        }
        finally
        {
            card.IsChecking = false;
        }
    }

    /// <summary>Linia szczegółów celu FTPS (tryb TLS z portu — Qb68; odcisk skrócony). Inne schematy: pusta (bez zmian względem it. 82).</summary>
    internal static string Details(DestinationItem d)
    {
        if (d.Scheme != "ftps") return "";
        if (d.Host is null && d.Port is null && d.HostKey is null) return Strings.Get("Dest.Ftps.NoDetails");
        var parts = new List<string>();
        var port = d.Port ?? Wizard.FtpsFieldRules.ExplicitPort;
        if (d.Host is { Length: > 0 } h) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{(h.Contains(':', StringComparison.Ordinal) ? "[" + h + "]" : h)}:{port}"));
        parts.Add(Wizard.WizardViewModel.ModeText(port));
        parts.Add(d.HostKey is { Length: > 0 } k ? Strings.Format("Dest.Ftps.Pin", Wizard.FtpsFieldRules.ShortFingerprint(k)) : Strings.Get("Dest.Ftps.SystemTrust"));
        return string.Join(" · ", parts);
    }

    internal static void Apply(DestinationCardViewModel card, DestinationCheck r)
    {
        var c = CultureInfo.CurrentCulture;
        card.ErrorText = r.Code is { } code ? KbText.Full(code) : "";
        card.SpaceKnown = r.Code is null && r.FreeBytes is not null && r.TotalBytes is > 0;
        if (r.Code is null && r.FreeBytes is { } free && r.TotalBytes is { } total && total > 0)
        {
            card.FreePercent = Math.Clamp(100.0 * free / total, 0, 100);
            card.SpaceText = string.Format(c, Strings.Get("Dest.Space"), ByteSize.Format(free, c), ByteSize.Format(total, c), Math.Round(card.FreePercent))
                + (card.FreePercent < Dashboard.ProtectionEvaluator.LowSpacePercent ? " — " + Strings.Get("Dest.LowSpace") : ""); // kolor nie jest jedynym nośnikiem (WCAG 1.4.1)
        }
        else
        {
            card.FreePercent = 0;
            card.SpaceText = r.Code is null ? Strings.Get("Dest.SpaceUnknown") : "";
        }
        card.Checked = true;
    }
}
