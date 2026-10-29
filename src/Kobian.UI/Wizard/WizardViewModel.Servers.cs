using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.Localization;

namespace Kobian.UI.Wizard;

/// <summary>
/// Konfiguracja bez „magazynu sekretów” od strony użytkownika: login i hasło do serwera wpisywane w kreatorze (zapisuje je usługa — SaveCredential), oraz serwer
/// (SFTP/FTPS/WebDAV) jako ŹRÓDŁO kopii dodawany jednym panelem z kontrolą połączenia i zatwierdzeniem odcisku serwera na miejscu.
/// Puste hasło = pole „Login” jest nazwą wpisu przygotowanego wcześniej przez administratora (zachowanie sprzed tej zmiany).
/// </summary>
public sealed partial class WizardViewModel
{
    // ---------- S2: hasło do serwera celu ----------

    /// <summary>Hasło do serwera celu (SFTP/SMB/FTPS/WebDAV). Nie trafia do szkicu — tylko do SaveCredential; po zapisie czyszczone.</summary>
    [ObservableProperty]
    public partial string ServerPassword { get; set; } = "";

    /// <summary>Nazwa wpisu zapisanego przez usługę dla bieżącego serwera i loginu (null = jeszcze nie zapisano albo dane się zmieniły).</summary>
    private string? _savedDestinationCredential;

    partial void OnServerPasswordChanged(string value)
    {
        if (value.Length > 0) _savedDestinationCredential = null;
    }

    /// <summary>Login i serwer celu w bieżącym rodzaju (SMB: serwer z UNC).</summary>
    private (string Host, string Login) DestinationLogin() =>
        IsSftp ? (SftpHost.Trim(), SftpUser.Trim())
        : IsSmb ? (UncServer(DestinationPath), SmbCredential.Trim())
        : (FtpsHost.Trim(), FtpsCredential.Trim());

    private static string UncServer(string unc)
    {
        var t = unc.Trim().TrimStart('\\', '/');
        var cut = t.IndexOfAny(['\\', '/']);
        return cut < 0 ? t : t[..cut];
    }

    /// <summary>Nazwa wpisu poświadczenia celu do szkicu: zapisany przez usługę albo (bez hasła) to, co wpisano jako login.</summary>
    private string DestinationSecretName(string login) => _savedDestinationCredential ?? login;

    /// <summary>Przed walidacją S2: hasło wpisane → zapis w usłudze. false = błąd pokazany, krok nie idzie dalej.</summary>
    private async Task<bool> SaveDestinationCredentialAsync(CancellationToken ct)
    {
        if (!(IsSftp || IsSmb || IsTlsServer) || ServerPassword.Length == 0) return true;
        var (host, login) = DestinationLogin();
        if (host.Length == 0 || login.Length == 0)
        {
            ShowText(Strings.Get("Wizard.Cred.LoginRequired"));
            return false;
        }
        var name = CredentialNames.Suggest(host, login);
        if (await _service.SaveCredentialAsync(name, login, ServerPassword, ct).ConfigureAwait(true) is { } code)
        {
            ShowText(KbText.Full(code));
            return false;
        }
        _savedDestinationCredential = name;
        ServerPassword = "";
        _savedDestinationCredential = name; // OnServerPasswordChanged nie zeruje przy pustym — zostaje nazwa zapisanego wpisu
        return true;
    }

    private void ForgetDestinationCredential() => _savedDestinationCredential = null;

    // ---------- S1: serwer jako źródło ----------

    public IReadOnlyList<WizardOption<string>> SourceServerKinds { get; } =
        [new("sftp", "Wizard.Src.Kind.Sftp"), new("ftps", "Wizard.Src.Kind.Ftps"), new("webdav", "Wizard.Src.Kind.WebDav")];

    public ObservableCollection<ServerSource> SourceServers { get; } = [];

    [ObservableProperty]
    public partial bool IsAddingServer { get; set; }

    [ObservableProperty]
    public partial WizardOption<string>? SrcKind { get; set; }

    [ObservableProperty]
    public partial string SrcHost { get; set; } = "";

    [ObservableProperty]
    public partial int SrcPort { get; set; } = 22;

    [ObservableProperty]
    public partial string SrcPath { get; set; } = "";

    [ObservableProperty]
    public partial string SrcLogin { get; set; } = "";

    [ObservableProperty]
    public partial string SrcPassword { get; set; } = "";

    /// <summary>Odcisk serwera do porównania i zatwierdzenia (null = brak pytania).</summary>
    [ObservableProperty]
    public partial string? SrcPendingFingerprint { get; private set; }

    [ObservableProperty]
    public partial string SrcMessage { get; private set; } = "";

    public bool HasSrcPendingFingerprint => SrcPendingFingerprint is not null;

    public bool HasSrcMessage => SrcMessage.Length > 0;

    public bool HasSourceServers => SourceServers.Count > 0;

    private string? _srcSavedCredential;

    partial void OnSrcPendingFingerprintChanged(string? value) => OnPropertyChanged(nameof(HasSrcPendingFingerprint));

    partial void OnSrcMessageChanged(string value) => OnPropertyChanged(nameof(HasSrcMessage));

    partial void OnSrcKindChanged(WizardOption<string>? value)
    {
        if (value is null) return;
        if (SrcPort is 22 or 21 or 990 or 443) SrcPort = value.Value switch { "sftp" => 22, "ftps" => 21, _ => 443 };
        SrcPendingFingerprint = null;
    }

    partial void OnSrcHostChanged(string value) => ResetSrcProbe();

    partial void OnSrcLoginChanged(string value) => ResetSrcProbe();

    partial void OnSrcPasswordChanged(string value)
    {
        if (value.Length > 0) _srcSavedCredential = null;
    }

    private void ResetSrcProbe()
    {
        SrcPendingFingerprint = null;
        _srcSavedCredential = null;
    }

    [RelayCommand]
    private void ShowAddServer()
    {
        IsAddingServer = true;
        SrcKind ??= SourceServerKinds[0];
        SrcMessage = "";
    }

    [RelayCommand]
    private void CancelAddServer()
    {
        IsAddingServer = false;
        SrcPassword = "";
        SrcPendingFingerprint = null;
        SrcMessage = "";
    }

    [RelayCommand]
    private void RemoveSourceServer(ServerSource? server)
    {
        if (server is not null) SourceServers.Remove(server);
        OnPropertyChanged(nameof(HasSourceServers));
    }

    /// <summary>„Sprawdź i dodaj”: zapis loginu/hasła w usłudze → połączenie → (nieznany serwer) pytanie o odcisk → dodanie do listy źródeł.</summary>
    [RelayCommand]
    private Task AddSourceServerAsync(CancellationToken ct) => ProbeAndAddServerAsync(null, ct);

    /// <summary>„Odciski są zgodne — zatwierdź”: ponowne połączenie z przypiętym odciskiem.</summary>
    [RelayCommand]
    private Task ApproveSourceServerAsync(CancellationToken ct) => ProbeAndAddServerAsync(SrcPendingFingerprint, ct);

    private async Task ProbeAndAddServerAsync(string? hostKey, CancellationToken ct)
    {
        SrcMessage = "";
        var kind = (SrcKind ?? SourceServerKinds[0]).Value;
        var host = SrcHost.Trim();
        var path = SrcPath.Trim();
        var login = SrcLogin.Trim();
        if (FtpsFieldRules.HostError(host) is { } he) { SrcMessage = Strings.Get(he); return; }
        if (FtpsFieldRules.PortError(SrcPort) is { } pe) { SrcMessage = Strings.Get(pe); return; }
        if (FtpsFieldRules.PathError(path) is { } pa) { SrcMessage = Strings.Get(pa); return; }
        if (login.Length == 0) { SrcMessage = Strings.Get("Wizard.Cred.LoginRequired"); return; }
        IsBusy = true;
        try
        {
            if (SrcPassword.Length > 0)
            {
                var name = CredentialNames.Suggest(host, login);
                if (await _service.SaveCredentialAsync(name, login, SrcPassword, ct).ConfigureAwait(true) is { } code) { SrcMessage = KbText.Full(code); return; }
                _srcSavedCredential = name;
                SrcPassword = "";
                _srcSavedCredential = name;
            }
            var server = new ServerSource(kind, host, SrcPort, path, _srcSavedCredential ?? login, hostKey);
            if (SourceServers.Any(s => s.Label == server.Label)) { CancelAddServer(); return; }
            var r = await _service.ProbeServerAsync(server, ct).ConfigureAwait(true);
            switch (r.Outcome)
            {
                case ProbeOutcome.Ok:
                    SourceServers.Add(server);
                    OnPropertyChanged(nameof(HasSourceServers));
                    if (JobName.Length == 0) JobName = SuggestName(path.Length > 1 ? path : host);
                    IsAddingServer = false;
                    SrcPendingFingerprint = null;
                    SrcHost = SrcPath = SrcLogin = "";
                    break;
                case ProbeOutcome.UnknownIdentity:
                    SrcPendingFingerprint = r.HostKeyFingerprint;
                    SrcMessage = Strings.Get("Wizard.Src.ApproveHint");
                    break;
                default:
                    SrcMessage = KbText.Full(r.KbCode ?? "KB-0001");
                    break;
            }
        }
        catch (Exception ex) when (Kobian.UI.Services.UiFailure.IsServiceFailure(ex))
        {
            SrcMessage = Kobian.UI.Services.UiFailure.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
