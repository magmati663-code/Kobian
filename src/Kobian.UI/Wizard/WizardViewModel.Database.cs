using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.History;
using Kobian.UI.Localization;

namespace Kobian.UI.Wizard;

/// <summary>Uwierzytelnienie do serwera SQL w S1-db (D5.7): konto usługi Agenta (Windows) albo login SQL z magazynu sekretów Agenta.</summary>
public enum DatabaseAuth
{
    Windows,
    SqlLogin,
}

/// <summary>Wiersz listy baz w S1-db: rozmiar dla ludzi, model odzyskiwania i ostrzeżenia jako tekst (nie tylko kolor — WCAG 1.4.1).</summary>
public sealed class DatabaseRow(DatabaseItem item, int index)
{
    public DatabaseItem Item { get; } = item;

    public string Name => Item.Name;

    public bool CanBackup => Item.CanBackup;

    public bool HasNotes => Notes.Length > 0;

    public string AutomationId { get; } = "SCR-03.S1db.Db." + index.ToString(CultureInfo.InvariantCulture);

    public string SizeText => ByteSize.Format(Item.DataBytes, CultureInfo.CurrentCulture);

    public string RecoveryText => Strings.Format("Wizard.Db.Recovery", Item.RecoveryModel);

    /// <summary>Brak uprawnień do kopii najpierw (blokuje „Dalej”), potem ostrzeżenia KB (TDE, rosnący dziennik) — bez KB-3003 zdublowanego.</summary>
    public string Notes => string.Join(" · ", (Item.CanBackup ? [] : new[] { Strings.Get("Wizard.Db.NoBackupRight") })
        .Concat(Item.Warnings.Where(w => w != NoPermissionCode).Select(w => KbText.Message(w))));

    internal const string NoPermissionCode = "KB-3003";
}

/// <summary>
/// S1-db kreatora (BL-40 cz. 4, R2.5.2, SCR-03): serwer + uwierzytelnienie → DiscoverDatabases (tylko odczyt) → wybór jednej bazy (definicja v1: jedno źródło, ADR-0018).
/// „Dalej” blokowane lokalnie, gdy nie wybrano bazy albo konto nie ma BACKUP DATABASE (KB-3003 + skrypt uprawnień D5.7); resztę reguł sprawdza Agent (UpsertJob validate_only).
/// Sekretów brak: login SQL to nazwa poświadczenia w katalogu sekretów Agenta (<c>file:&lt;nazwa&gt;</c>, jak SFTP — PROWIZORYCZNIE Qb24).
/// </summary>
public sealed partial class WizardViewModel
{
    /// <summary>Usługa obsługuje bazy → w S1 wybór „pliki / baza”.</summary>
    public bool ShowSourceKinds => _service.SupportsDatabases;

    public IReadOnlyList<WizardOption<SourceKind>> SourceKinds { get; } =
        [new(SourceKind.Files, "Wizard.Source.Files"), new(SourceKind.Database, "Wizard.Source.Database")];

    public IReadOnlyList<WizardOption<DatabaseAuth>> DatabaseAuths { get; } =
        [new(DatabaseAuth.Windows, "Wizard.Db.Auth.Windows"), new(DatabaseAuth.SqlLogin, "Wizard.Db.Auth.Sql")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDatabaseSource), nameof(IsFilesSource))]
    public partial WizardOption<SourceKind>? SelectedSourceKind { get; set; }

    public bool IsDatabaseSource => SelectedSourceKind?.Value == SourceKind.Database;

    public bool IsFilesSource => !IsDatabaseSource;

    [ObservableProperty]
    public partial string DbServer { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSqlLogin))]
    public partial WizardOption<DatabaseAuth>? SelectedDatabaseAuth { get; set; }

    public bool IsSqlLogin => SelectedDatabaseAuth?.Value == DatabaseAuth.SqlLogin;

    /// <summary>Nazwa poświadczenia SQL w katalogu sekretów Agenta (plik zakłada administrator — W-25); nie login ani hasło.</summary>
    [ObservableProperty]
    public partial string DbCredential { get; set; } = "";

    /// <summary>Nazwa zatwierdzonego pliku .cer w katalogu certs Agenta (pin, Qb30); pusta = walidacja łańcucha zaufania systemu.</summary>
    [ObservableProperty]
    public partial string DbCertificate { get; set; } = "";

    public ObservableCollection<DatabaseRow> Databases { get; } = [];

    public bool HasDatabases => Databases.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourcesText))]
    public partial DatabaseRow? SelectedDatabase { get; set; }

    /// <summary>Wersja serwera, Express, wsparcie warunkowe (KB-3401), lista niepełna — jedna linia nad listą.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerInfo))]
    public partial string ServerInfo { get; private set; } = "";

    public bool HasServerInfo => ServerInfo.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPermissionScript))]
    public partial string? PermissionScript { get; private set; }

    public bool HasPermissionScript => PermissionScript is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermissionScriptToggleText))]
    public partial bool IsPermissionScriptVisible { get; set; }

    public string PermissionScriptToggleText => Strings.Get(IsPermissionScriptVisible ? "Wizard.Db.HideScript" : "Wizard.Db.ShowScript");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverDatabasesCommand))]
    public partial bool IsDiscovering { get; private set; }

    private int _discoveryVersion;

    // Zmiana serwera/uwierzytelnienia unieważnia listę — wybór bazy z innego serwera nie może przejść do zapisu.
    partial void OnDbServerChanged(string value)
    {
        ClearDiscovery();
        ClearCertificate();
    }

    partial void OnSelectedDatabaseAuthChanged(WizardOption<DatabaseAuth>? value) => ClearDiscovery();

    partial void OnDbCredentialChanged(string value) => ClearDiscovery();

    partial void OnDbCertificateChanged(string value) => ClearDiscovery();

    partial void OnSelectedSourceKindChanged(WizardOption<SourceKind>? value)
    {
        ClearMessages();
        OnPropertyChanged(nameof(SourcesText));
    }

    partial void OnSelectedDatabaseChanged(DatabaseRow? value)
    {
        if (value is not null && JobName.Length == 0) JobName = value.Name;
    }

    private void ClearDiscovery()
    {
        _discoveryVersion++;
        Databases.Clear();
        SelectedDatabase = null;
        ServerInfo = "";
        PermissionScript = null;
        IsPermissionScriptVisible = false;
        OnPropertyChanged(nameof(HasDatabases));
    }

    private bool CanDiscover() => !IsDiscovering;

    /// <summary>DiscoverDatabases w Agencie (Operator, tylko odczyt). Błąd → komunikat KB; wynik starszego żądania (po zmianie serwera) odrzucany.</summary>
    [RelayCommand(CanExecute = nameof(CanDiscover))]
    private async Task DiscoverDatabasesAsync(CancellationToken ct)
    {
        ClearMessages();
        ClearDiscovery();
        var version = _discoveryVersion;
        IsDiscovering = true;
        try
        {
            var r = await _service.DiscoverDatabasesAsync(ServerRequest(), ct).ConfigureAwait(true);
            if (version != _discoveryVersion) return;
            if (r is null)
            {
                Show([new StepIssue("database", OperationNotAvailable)]);
                return;
            }
            if (r.KbCode is not null)
            {
                Show([new StepIssue("database", r.KbCode)]);
                return;
            }
            var i = 0;
            foreach (var d in r.Databases) Databases.Add(new DatabaseRow(d, i++));
            OnPropertyChanged(nameof(HasDatabases));
            ServerInfo = ServerLine(r);
            PermissionScript = r.PermissionScript;
            var usable = Databases.Where(d => d.CanBackup).ToList();
            if (usable.Count == 1) SelectedDatabase = usable[0]; // jedna baza do wyboru — bez dodatkowego kliknięcia
        }
        catch (Exception ex) when (Kobian.UI.Services.UiFailure.IsServiceFailure(ex))
        {
            if (version == _discoveryVersion) ShowText(ex is IOException ? $"{Strings.Get("Offline.Headline")} ({AgentOffline})" : KbText.Full(Kobian.UI.Services.UiFailure.CodeOf(ex))); // jak SCR-02 (R2.6.4)
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    [RelayCommand]
    private void TogglePermissionScript() => IsPermissionScriptVisible = !IsPermissionScriptVisible;

    // ---- Zatwierdzenie certyfikatu serwera SQL (BL-40 cz. 5, Qb32): odcisk do porównania → Agent zapisuje pin → pole .cer wypełnione ----

    /// <summary>Usługa umie pobrać certyfikat (Agent z FetchSqlCertificate); inaczej zostaje ręczne pole nazwy pliku.</summary>
    public bool SupportsCertificateFetch => _service.SupportsDatabases;

    /// <summary>Certyfikat oczekujący na decyzję Operatora (null = brak).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingCertificate), nameof(CertificateFingerprintText), nameof(CertificateDetailsText))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCertificateCommand))]
    public partial SqlCertificateCandidate? PendingCertificate { get; private set; }

    public bool HasPendingCertificate => PendingCertificate is not null;

    /// <summary>Odcisk SHA-256 w grupach po 4 znaki, 8 grup w wierszu — do porównania na głos/z ekranem administratora SQL.</summary>
    public string CertificateFingerprintText => PendingCertificate is { } c ? GroupFingerprint(c.Sha256) : "";

    public string CertificateDetailsText => PendingCertificate is { } c ? CertificateDetails(c) : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCertificateStatus))]
    public partial string CertificateStatus { get; private set; } = "";

    public bool HasCertificateStatus => CertificateStatus.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCertificateCommand), nameof(ApproveCertificateCommand))]
    public partial bool IsFetchingCertificate { get; private set; }

    private int _certificateVersion;

    private void ClearCertificate()
    {
        _certificateVersion++;
        PendingCertificate = null;
        CertificateStatus = "";
    }

    private bool CanFetchCertificate() => !IsFetchingCertificate;

    /// <summary>FetchSqlCertificate: Agent odczytuje certyfikat z handshake TLS (bez logowania) i pokazuje odcisk; niczego nie zapisuje.</summary>
    [RelayCommand(CanExecute = nameof(CanFetchCertificate))]
    private async Task FetchCertificateAsync(CancellationToken ct)
    {
        ClearMessages();
        ClearCertificate();
        var version = _certificateVersion;
        IsFetchingCertificate = true;
        try
        {
            var r = await _service.FetchSqlCertificateAsync(DbServer.Trim(), ct).ConfigureAwait(true);
            if (version != _certificateVersion) return;
            if (r is null) Show([new StepIssue("database", OperationNotAvailable)]);
            else if (r.KbCode is not null) Show([new StepIssue("database", r.KbCode)]);
            else PendingCertificate = r;
        }
        catch (Exception ex) when (Kobian.UI.Services.UiFailure.IsServiceFailure(ex))
        {
            if (version == _certificateVersion) ShowText(ex is IOException ? $"{Strings.Get("Offline.Headline")} ({AgentOffline})" : KbText.Full(Kobian.UI.Services.UiFailure.CodeOf(ex)));
        }
        finally
        {
            IsFetchingCertificate = false;
        }
    }

    private bool CanApproveCertificate() => PendingCertificate is not null && !IsFetchingCertificate;

    /// <summary>
    /// ApproveSqlCertificate: Agent pobiera certyfikat ponownie i zapisuje pin tylko przy zgodnym odcisku (KB-3007 inaczej).
    /// Sukces → nazwa pliku w polu certyfikatu (lista baz unieważniona — discovery idzie już z pinem).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApproveCertificate))]
    private async Task ApproveCertificateAsync(CancellationToken ct)
    {
        if (PendingCertificate is not { } c) return;
        ClearMessages();
        var version = _certificateVersion;
        IsFetchingCertificate = true;
        try
        {
            var r = await _service.ApproveSqlCertificateAsync(DbServer.Trim(), c.Sha256, c.SuggestedName, ct).ConfigureAwait(true);
            if (version != _certificateVersion) return;
            if (r?.FileName is { } name)
            {
                DbCertificate = name;
                PendingCertificate = null;
                CertificateStatus = Strings.Format("Wizard.Db.CertApproved", name);
            }
            else
            {
                Show([new StepIssue("database", r?.KbCode ?? OperationNotAvailable)]);
                if (r?.KbCode == CertificateChangedCode) PendingCertificate = null; // odcisk nieaktualny — pobierz i porównaj ponownie
            }
        }
        catch (Exception ex) when (Kobian.UI.Services.UiFailure.IsServiceFailure(ex))
        {
            if (version == _certificateVersion) ShowText(ex is IOException ? $"{Strings.Get("Offline.Headline")} ({AgentOffline})" : KbText.Full(Kobian.UI.Services.UiFailure.CodeOf(ex)));
        }
        finally
        {
            IsFetchingCertificate = false;
        }
    }

    [RelayCommand]
    private void RejectCertificate() => ClearCertificate();

    internal const string CertificateChangedCode = "KB-3007";

    internal static string GroupFingerprint(string hex)
    {
        var groups = Enumerable.Range(0, (hex.Length + 3) / 4).Select(i => hex.Substring(i * 4, Math.Min(4, hex.Length - i * 4))).ToList();
        return string.Join("\n", Enumerable.Range(0, (groups.Count + 7) / 8).Select(l => string.Join(' ', groups.Skip(l * 8).Take(8))));
    }

    internal static string CertificateDetails(SqlCertificateCandidate c)
    {
        var line = Strings.Format("Wizard.Db.CertDetails", c.Subject, c.Issuer, c.NotAfter.ToLocalTime().ToString("d", CultureInfo.CurrentCulture));
        if (c.SelfIssued) line += " · " + Strings.Get("Wizard.Db.CertSelfIssued");
        if (c.SystemTrusted) line += " · " + Strings.Get("Wizard.Db.CertTrusted");
        return line;
    }

    internal const string OperationNotAvailable = "KB-7012";

    internal const string AgentOffline = "KB-9001";

    /// <summary>„SQL Server 2022 (16) · Express · …” — wersja handlowa z numeru głównego (D5.8), ostrzeżenia jako tekst.</summary>
    internal static string ServerLine(DatabaseDiscovery r)
    {
        var parts = new List<string> { Strings.Format("Wizard.Db.ServerVersion", ProductYear(r.ServerMajor), r.ServerMajor) };
        if (r.Express) parts.Add(Strings.Get("Wizard.Db.Express"));
        if (r.Conditional) parts.Add(KbText.Message("KB-3401"));
        if (r.Databases.Count == 0) parts.Add(Strings.Get("Wizard.Db.Empty"));
        if (r.Truncated) parts.Add(Strings.Get("Wizard.Db.Truncated"));
        return string.Join(" · ", parts);
    }

    internal static string ProductYear(int major) => major switch
    {
        11 => "2012",
        12 => "2014",
        13 => "2016",
        14 => "2017",
        15 => "2019",
        16 => "2022",
        17 => "2025",
        _ => major.ToString(CultureInfo.InvariantCulture),
    };

    private string? CredentialRef => IsSqlLogin && DbCredential.Trim().Length > 0 ? "file:" + DbCredential.Trim() : null;

    private string? CertificateName => DbCertificate.Trim().Length > 0 ? DbCertificate.Trim() : null;

    internal DatabaseServerRequest ServerRequest() => new(DbServer.Trim(), IsSqlLogin, CredentialRef, CertificateName);

    private DatabaseSource? DatabaseDraft() => IsDatabaseSource && SelectedDatabase is { } db
        ? new DatabaseSource(DbServer.Trim(), IsSqlLogin, CredentialRef, CertificateName, db.Name)
        : null;

    /// <summary>Strażnik S1-db przed walidacją w usłudze: wybrana baza z prawem kopii. Fałsz = komunikat już pokazany.</summary>
    private bool DatabaseStepReady()
    {
        if (SelectedDatabase is null)
        {
            ShowText(Strings.Get("Wizard.Db.SelectFirst"));
            return false;
        }
        if (!SelectedDatabase.CanBackup)
        {
            Show([new StepIssue("database", DatabaseRow.NoPermissionCode)]);
            if (HasPermissionScript) IsPermissionScriptVisible = true; // akcja naprawcza od razu widoczna (6.3)
            return false;
        }
        return true;
    }
}
