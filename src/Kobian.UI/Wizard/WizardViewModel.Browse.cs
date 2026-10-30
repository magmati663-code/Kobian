using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.Localization;

namespace Kobian.UI.Wizard;

/// <summary>Do czego trafi folder wybrany w oknie przeglądania serwera.</summary>
public enum ServerBrowseTarget
{
    Destination,
    Source,
}

/// <summary>
/// „Przeglądaj…” dla folderu na serwerze (SFTP/FTPS/WebDAV — cel w S2 i serwer-źródło w S1): lista podfolderów z usługi (BrowseServer), wejście w folder,
/// poziom wyżej, „Wybierz ten folder”. Pierwsze połączenie z nieznanym serwerem pokazuje odcisk do zatwierdzenia w tym samym panelu (D4.11) — zatwierdzony
/// odcisk zostaje przy celu/serwerze, więc „Dalej” / „Sprawdź i dodaj” nie pytają drugi raz.
/// </summary>
public sealed partial class WizardViewModel
{
    public ObservableCollection<string> BrowseFolders { get; } = [];

    [ObservableProperty]
    public partial bool IsBrowsingServer { get; private set; }

    /// <summary>Bieżący folder na serwerze (można wpisać ręcznie i użyć „Przejdź”).</summary>
    [ObservableProperty]
    public partial string BrowsePath { get; set; } = "/";

    [ObservableProperty]
    public partial string BrowseMessage { get; private set; } = "";

    [ObservableProperty]
    public partial string? BrowsePendingFingerprint { get; private set; }

    [ObservableProperty]
    public partial bool BrowseTruncated { get; private set; }

    public bool HasBrowseMessage => BrowseMessage.Length > 0;

    public bool HasBrowsePendingFingerprint => BrowsePendingFingerprint is not null;

    public bool CanBrowseUp => BrowsePath.TrimEnd('/').Length > 0;

    /// <summary>Przycisk „Przeglądaj…” przy ścieżce celu na serwerze.</summary>
    public bool CanBrowseServerDestination => IsSftp || IsTlsServer;

    private ServerSource? _browseServer;
    private ServerBrowseTarget _browseTarget;

    /// <summary>Zatwierdzony odcisk serwera-źródła (z panelu przeglądania) — używany przez „Sprawdź i dodaj”.</summary>
    private string? _srcApprovedKey;

    partial void OnBrowseMessageChanged(string value) => OnPropertyChanged(nameof(HasBrowseMessage));

    partial void OnBrowsePendingFingerprintChanged(string? value) => OnPropertyChanged(nameof(HasBrowsePendingFingerprint));

    partial void OnBrowsePathChanged(string value) => OnPropertyChanged(nameof(CanBrowseUp));

    /// <summary>Folder startowy: wpisana ścieżka, jeśli poprawna, inaczej korzeń serwera.</summary>
    internal static string StartPath(string typed)
    {
        var t = typed.Trim();
        return t.Length > 0 && FtpsFieldRules.PathError(t) is null ? t : "/";
    }

    internal static string ChildPath(string parent, string name) => parent.TrimEnd('/') + "/" + name;

    internal static string ParentPath(string path)
    {
        var t = path.TrimEnd('/');
        var cut = t.LastIndexOf('/');
        return cut <= 0 ? "/" : t[..cut];
    }

    [RelayCommand]
    private async Task BrowseServerDestinationAsync(CancellationToken ct)
    {
        if (!CanBrowseServerDestination) return;
        var (host, login) = DestinationLogin();
        if (FtpsFieldRules.HostError(host) is { } he) { ShowText(Strings.Get(he)); return; }
        if (login.Length == 0) { ShowText(Strings.Get("Wizard.Cred.LoginRequired")); return; }
        try
        {
            if (!await SaveDestinationCredentialAsync(ct).ConfigureAwait(true)) return;
        }
        catch (Exception ex) when (Kobian.UI.Services.UiFailure.IsServiceFailure(ex))
        {
            ShowText(Kobian.UI.Services.UiFailure.Describe(ex));
            return;
        }
        var scheme = IsSftp ? "sftp" : IsFtps ? "ftps" : "webdav";
        var server = new ServerSource(scheme, host, IsSftp ? SftpPort : FtpsPort, StartPath(DestinationPath), DestinationSecretName(login), _approvedHostKey);
        await OpenBrowserAsync(ServerBrowseTarget.Destination, server, ct).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task BrowseServerSourceAsync(CancellationToken ct)
    {
        SrcMessage = "";
        var host = SrcHost.Trim();
        var login = SrcLogin.Trim();
        if (FtpsFieldRules.HostError(host) is { } he) { SrcMessage = Strings.Get(he); return; }
        if (FtpsFieldRules.PortError(SrcPort) is { } pe) { SrcMessage = Strings.Get(pe); return; }
        if (login.Length == 0) { SrcMessage = Strings.Get("Wizard.Cred.LoginRequired"); return; }
        try
        {
            if (!await SaveSourceCredentialAsync(host, login, ct).ConfigureAwait(true)) return;
        }
        catch (Exception ex) when (Kobian.UI.Services.UiFailure.IsServiceFailure(ex))
        {
            SrcMessage = Kobian.UI.Services.UiFailure.Describe(ex);
            return;
        }
        var server = new ServerSource((SrcKind ?? SourceServerKinds[0]).Value, host, SrcPort, StartPath(SrcPath), _srcSavedCredential ?? login, _srcApprovedKey);
        await OpenBrowserAsync(ServerBrowseTarget.Source, server, ct).ConfigureAwait(true);
    }

    private Task OpenBrowserAsync(ServerBrowseTarget target, ServerSource server, CancellationToken ct)
    {
        _browseTarget = target;
        _browseServer = server;
        BrowsePendingFingerprint = null;
        BrowseFolders.Clear();
        IsBrowsingServer = true;
        return LoadFoldersAsync(server.Root, ct);
    }

    private async Task LoadFoldersAsync(string path, CancellationToken ct)
    {
        if (_browseServer is null) return;
        BrowseMessage = "";
        BrowseTruncated = false;
        BrowsePath = path;
        IsBusy = true;
        try
        {
            var server = _browseServer with { Root = path };
            var r = await _service.BrowseServerAsync(server, ct).ConfigureAwait(true);
            if (r.KbCode is "KB-2104" or "KB-2109" && string.IsNullOrEmpty(server.HostKey))
            {
                // Nieznany serwer: odcisk do zatwierdzenia (bez TOFU w tle — poświadczenie nie poszło do serwera).
                var p = await _service.ProbeServerAsync(server, ct).ConfigureAwait(true);
                if (p.Outcome == ProbeOutcome.UnknownIdentity && p.HostKeyFingerprint is not null)
                {
                    BrowseFolders.Clear();
                    BrowsePendingFingerprint = p.HostKeyFingerprint;
                    BrowseMessage = Strings.Get("Wizard.Src.ApproveHint");
                    return;
                }
                BrowseMessage = KbText.Full(p.KbCode ?? r.KbCode);
                return;
            }
            BrowseFolders.Clear();
            if (r.KbCode is not null)
            {
                BrowseMessage = KbText.Full(r.KbCode);
                return;
            }
            foreach (var f in r.Folders) BrowseFolders.Add(f);
            BrowseTruncated = r.Truncated;
            if (r.Folders.Count == 0) BrowseMessage = Strings.Get("Wizard.Browse.Empty");
        }
        catch (Exception ex) when (Kobian.UI.Services.UiFailure.IsServiceFailure(ex))
        {
            BrowseMessage = Kobian.UI.Services.UiFailure.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BrowseApproveAsync(CancellationToken ct)
    {
        if (_browseServer is null || BrowsePendingFingerprint is not { } fp) return;
        _browseServer = _browseServer with { HostKey = fp };
        if (_browseTarget == ServerBrowseTarget.Destination) _approvedHostKey = fp;
        else _srcApprovedKey = fp;
        BrowsePendingFingerprint = null;
        await LoadFoldersAsync(BrowsePath, ct).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task BrowseOpenAsync(string? folder, CancellationToken ct) =>
        string.IsNullOrEmpty(folder) ? Task.CompletedTask : LoadFoldersAsync(ChildPath(BrowsePath, folder), ct);

    [RelayCommand]
    private Task BrowseUpAsync(CancellationToken ct) => LoadFoldersAsync(ParentPath(BrowsePath), ct);

    [RelayCommand]
    private Task BrowseGoAsync(CancellationToken ct)
    {
        var p = BrowsePath.Trim();
        if (FtpsFieldRules.PathError(p) is { } bad)
        {
            BrowseMessage = Strings.Get(bad);
            return Task.CompletedTask;
        }
        return LoadFoldersAsync(p, ct);
    }

    /// <summary>„Wybierz ten folder”: ścieżka trafia do pola celu albo serwera-źródła; okno się zamyka.</summary>
    [RelayCommand]
    private void BrowseChoose()
    {
        var p = BrowsePath.Trim();
        if (FtpsFieldRules.PathError(p) is { } bad)
        {
            BrowseMessage = Strings.Get(bad);
            return;
        }
        if (_browseTarget == ServerBrowseTarget.Destination) DestinationPath = p;
        else SrcPath = p;
        CloseBrowser();
    }

    [RelayCommand]
    private void BrowseCancel() => CloseBrowser();

    private void CloseBrowser()
    {
        IsBrowsingServer = false;
        BrowsePendingFingerprint = null;
        BrowseMessage = "";
        BrowseFolders.Clear();
        _browseServer = null;
    }
}
