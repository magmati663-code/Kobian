using System.Collections.ObjectModel;
using System.Globalization;
using System.Resources;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kobian.UI.Jobs;
using Kobian.UI.Localization;

namespace Kobian.UI.Wizard;

/// <summary>Zlokalizowane komunikaty KB z KbMessages.resx (R2.6.5; generowane tools/kb.py resx).</summary>
public static class KbText
{
    private static readonly ResourceManager Rm = new("Kobian.UI.Resources.KbMessages", typeof(KbText).Assembly);

    public static string Message(string kbCode, CultureInfo? culture = null) =>
        Rm.GetString(kbCode.Replace('-', '_') + "_Message", culture ?? CultureInfo.CurrentUICulture) ?? kbCode;

    public static string? Action(string kbCode, CultureInfo? culture = null) =>
        Rm.GetString(kbCode.Replace('-', '_') + "_Action", culture ?? CultureInfo.CurrentUICulture);

    /// <summary>Komunikat + kod + jedna akcja naprawcza (6.3, AC-01.2).</summary>
    public static string Full(string kbCode)
    {
        var action = Action(kbCode);
        return $"{Message(kbCode)} ({kbCode})" + (action is null ? "" : " — " + action);
    }
}

/// <summary>
/// ViewModel kreatora (SCR-03, D6.7): szkic w pamięci, „Dalej” wywołuje walidację w usłudze (validate_only), dopiero potem przejście
/// maszyny stanów <see cref="WizardFlow"/>. Problemy prezentowane jako komunikat KB + jedna akcja (6.3, AC-01.2). Efekty uboczne
/// (repozytorium, zapis, pierwsza kopia) wyłącznie przez usługę.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private static readonly string[] StateBound =
    [
        nameof(StepNumber), nameof(CanGoBack), nameof(CanCancel), nameof(Steps), nameof(StepCaption), nameof(NextText), nameof(IsS1), nameof(IsS2),
        nameof(IsS2Identity), nameof(IsS3), nameof(IsS4), nameof(IsS4Key), nameof(IsS5), nameof(IsS6), nameof(IsFinal), nameof(DestinationText),
        nameof(SourcesText), nameof(RetentionText), nameof(FinalText), nameof(SchedulePreview), nameof(FtpsServerText),
    ];

    private readonly IWizardService _service;
    private string? _approvedHostKey;
    private string? _finalCode;
    private bool _ranFirstBackup;

    public WizardViewModel(IWizardService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Destinations = [new(DestinationKind.Local, "Wizard.Dest.Local"), new(DestinationKind.Sftp, "Wizard.Dest.Sftp"), new(DestinationKind.Smb, "Wizard.Dest.Smb"),
            new(DestinationKind.Ftps, "Wizard.Dest.Ftps"), new(DestinationKind.WebDav, "Wizard.Dest.WebDav")];
        Schedules =
        [
            new(SchedulePreset.Daily, "Wizard.Schedule.Daily"), new(SchedulePreset.Weekdays, "Wizard.Schedule.Weekdays"),
            new(SchedulePreset.Hourly, "Wizard.Schedule.Hourly"), new(SchedulePreset.Manual, "Wizard.Schedule.Manual"),
        ];
        SelectedDestination = Destinations[0];
        SelectedSchedule = Schedules[0];
        SelectedFtpsMode = FtpsModes[0];
        SelectedSourceKind = SourceKinds[0];
        SelectedDatabaseAuth = DatabaseAuths[0];
        Sources.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSources));
    }

    /// <summary>Kreator zakończony (Done/Cancelled) — powłoka wraca do poprzedniego ekranu.</summary>
    public event EventHandler? Finished;

    // ---------- stan maszyny ----------

    [ObservableProperty]
    public partial WizardState State { get; private set; } = WizardState.S1Sources;

    [ObservableProperty]
    public partial bool RepositoryCreated { get; set; }

    [ObservableProperty]
    public partial bool RunNow { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanCancel))]
    public partial bool IsBusy { get; private set; }

    public ObservableCollection<string> Messages { get; } = [];

    public bool HasMessages => Messages.Count > 0;

    public int StepNumber => WizardFlow.StepNumber(State);

    // W trakcie operacji usługi (IsBusy) Wstecz/Anuluj są zablokowane: „Anuluj” podczas zapisu zostawiało zadanie utworzone przez Agenta mimo komunikatu, że nie powstaje,
    // a „Wstecz” w trakcie walidacji pozwalało spóźnionej odpowiedzi przesunąć kreator z powrotem do przodu.
    public bool CanGoBack => !IsBusy && WizardFlow.CanGoBack(State, Context(true));

    public bool CanCancel => !IsBusy && !IsFinal && State != WizardState.Cancelled;

    public string Title { get; } = Strings.Get("Nav.NewJob");

    public IReadOnlyList<WizardStepItem> Steps =>
        Enumerable.Range(1, 6).Select(n => new WizardStepItem(n, Strings.Get("Wizard.Step." + n.ToString(CultureInfo.InvariantCulture)),
            n == StepNumber && !IsFinal, n < StepNumber || IsFinal)).ToList();

    public string StepCaption => Strings.Format("Wizard.StepOf", StepNumber, 6);

    public string NextText => State switch
    {
        WizardState.S2Identity => Strings.Get("Wizard.Identity.Approve"),
        WizardState.S4Security => Strings.Get("Wizard.Security.Create"),
        WizardState.S4RecoveryKey => Strings.Get("Wizard.Key.Confirm"),
        WizardState.S6Summary => Strings.Get("Wizard.Save"),
        WizardState.Done => Strings.Get("Wizard.Close"),
        _ => Strings.Get("Wizard.Next"),
    };

    public bool IsS1 => State == WizardState.S1Sources;

    public bool IsS2 => State is WizardState.S2Destination or WizardState.S2Probe;

    public bool IsS2Identity => State == WizardState.S2Identity;

    public bool IsS3 => State == WizardState.S3Schedule;

    public bool IsS4 => State == WizardState.S4Security;

    public bool IsS4Key => State == WizardState.S4RecoveryKey;

    public bool IsS5 => State == WizardState.S5Retention;

    public bool IsS6 => State == WizardState.S6Summary;

    public bool IsFinal => State is WizardState.Saved or WizardState.FirstBackup or WizardState.MiniTest or WizardState.Done;

    // ---------- S1 źródła ----------

    public ObservableCollection<string> Sources { get; } = [];

    public bool HasSources => Sources.Count > 0;

    [ObservableProperty]
    public partial string NewSource { get; set; } = "";

    // ---------- S2 cel ----------

    public IReadOnlyList<WizardOption<DestinationKind>> Destinations { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSftp), nameof(IsSmb), nameof(IsFtps), nameof(IsWebDav), nameof(IsTlsServer), nameof(IsFtpsIdentity), nameof(IsSftpIdentity), nameof(PathWatermark), nameof(PathHint))]
    public partial WizardOption<DestinationKind> SelectedDestination { get; set; }

    public bool IsSftp => SelectedDestination?.Value == DestinationKind.Sftp;

    public bool IsSmb => SelectedDestination?.Value == DestinationKind.Smb;

    /// <summary>
    /// Nazwa wpisu poświadczenia SMB w magazynie sekretów usługi (PROWIZORYCZNIE Qb24/ADR-0007 jak SFTP: odwołanie <c>file:&lt;nazwa&gt;</c>;
    /// hasło zakłada administrator — kreator go nie przyjmuje ani nie zapisuje, AC-01.3).
    /// </summary>
    [ObservableProperty]
    public partial string SmbCredential { get; set; } = "";

    [ObservableProperty]
    public partial string DestinationPath { get; set; } = "";

    [ObservableProperty]
    public partial string SftpHost { get; set; } = "";

    [ObservableProperty]
    public partial int SftpPort { get; set; } = 22;

    [ObservableProperty]
    public partial string SftpUser { get; set; } = "";

    /// <summary>Odcisk klucza hosta z probe (S2Identity) — do porównania z odciskiem od administratora serwera (D4.11).</summary>
    [ObservableProperty]
    public partial string? PendingHostKey { get; private set; }

    // ---------- S3 harmonogram ----------

    public IReadOnlyList<WizardOption<SchedulePreset>> Schedules { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleNeedsTime), nameof(SchedulePreview), nameof(Cron))]
    public partial WizardOption<SchedulePreset> SelectedSchedule { get; set; }

    /// <summary>Godzina HH:mm (PROWIZORYCZNIE Qb20: domyślnie 21:00 — poza godzinami pracy biura).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchedulePreview), nameof(Cron))]
    public partial string ScheduleTime { get; set; } = "21:00";

    public bool ScheduleNeedsTime => SelectedSchedule?.Value is SchedulePreset.Daily or SchedulePreset.Weekdays;

    /// <summary>Wyrażenie cron z presetu (null = tylko ręcznie); niepoprawna godzina → wyrażenie niepoprawne, odrzuci je walidacja (KB-0201).</summary>
    public string? Cron
    {
        get
        {
            var preset = SelectedSchedule?.Value ?? SchedulePreset.Manual;
            if (preset == SchedulePreset.Manual) return null;
            if (preset == SchedulePreset.Hourly) return "0 * * * *";
            if (!TimeOnly.TryParseExact(ScheduleTime.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return InvalidCron;
            var days = preset == SchedulePreset.Weekdays ? "1-5" : "*";
            return string.Create(CultureInfo.InvariantCulture, $"{t.Minute} {t.Hour} * * {days}");
        }
    }

    /// <summary>Wartość celowo niepoprawna dla crona — usługa zwróci KB-0201 (jedno źródło reguł, D6.7).</summary>
    public const string InvalidCron = "invalid";

    public string SchedulePreview => Cron switch
    {
        null => Strings.Get("Wizard.Schedule.ManualHint"),
        InvalidCron => Strings.Get("Wizard.Schedule.BadTime"),
        var c => CronText.Describe(c),
    };

    // ---------- S4 zabezpieczenie ----------

    /// <summary>Hasło repozytorium — tylko w pamięci do wywołania InitRepository, potem usuwane z VM (NFR-11).</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string PasswordConfirm { get; set; } = "";

    public IReadOnlyList<string> RecoveryKeyGroups { get; private set; } = [];

    public IReadOnlyList<KeyGroupItem> NumberedKeyGroups => RecoveryKeyGroups.Select((g, i) => new KeyGroupItem(i + 1, g)).ToList();

    /// <summary>Numery (od 1) dwóch losowych, różnych grup do przepisania (AC-28.1).</summary>
    public (int First, int Second) ChallengeGroups { get; private set; }

    public string ChallengeFirstLabel => Strings.Format("Wizard.Key.Group", ChallengeGroups.First);

    public string ChallengeSecondLabel => Strings.Format("Wizard.Key.Group", ChallengeGroups.Second);

    [ObservableProperty]
    public partial string ChallengeFirst { get; set; } = "";

    [ObservableProperty]
    public partial string ChallengeSecond { get; set; } = "";

    // ---------- S5 retencja ----------

    /// <summary>PROWIZORYCZNIE Qb20: preset „Prosty” 7 dni / 4 tygodnie / 12 miesięcy do czasu podglądu Keep() (D3.12).</summary>
    [ObservableProperty]
    public partial int KeepDaily { get; set; } = 7;

    [ObservableProperty]
    public partial int KeepWeekly { get; set; } = 4;

    [ObservableProperty]
    public partial int KeepMonthly { get; set; } = 12;

    public string RetentionText => Strings.Format("Wizard.Retention.Summary", KeepDaily, KeepWeekly, KeepMonthly);

    /// <summary>Podgląd Keep() z usługi (D3.12): „Zostanie ok. 23 kopii, najstarsza sprzed ok. 12 miesięcy”. Pusty, gdy usługa nie podaje podglądu.</summary>
    [ObservableProperty]
    public partial string RetentionPreviewText { get; private set; } = "";

    public bool HasRetentionPreview => RetentionPreviewText.Length > 0;

    private int _previewVersion;

    partial void OnKeepDailyChanged(int value) => _ = RefreshRetentionPreviewAsync();

    partial void OnKeepWeeklyChanged(int value) => _ = RefreshRetentionPreviewAsync();

    partial void OnKeepMonthlyChanged(int value) => _ = RefreshRetentionPreviewAsync();

    partial void OnRetentionPreviewTextChanged(string value) => OnPropertyChanged(nameof(HasRetentionPreview));

    /// <summary>Odświeża podgląd; wynik starszego żądania jest odrzucany (numer wersji) — widać zawsze podgląd bieżących wartości. Błąd usługi = brak podglądu, nie blokuje kroku.</summary>
    public async Task RefreshRetentionPreviewAsync()
    {
        var version = ++_previewVersion;
        try
        {
            var p = await _service.PreviewRetentionAsync(Draft(), CancellationToken.None).ConfigureAwait(true);
            if (version != _previewVersion) return;
            RetentionPreviewText = p is null ? "" : Strings.Format("Wizard.Retention.Preview", Plural.Text("Plural.Copy", p.Kept, CultureInfo.CurrentUICulture), AgeText(p.OldestDays));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            if (version == _previewVersion) RetentionPreviewText = "";
        }
    }

    /// <summary>Wiek „ok. 12 mies.” / „ok. 3 tyg.” / „7 dni” (6.9 — czas dla ludzi; skróty omijają odmianę liczebników).</summary>
    internal static string AgeText(int days) => days switch
    {
        >= 60 => Strings.Format("Wizard.Retention.Age.Months", (int)Math.Round(days / 30.44)),
        >= 14 => Strings.Format("Wizard.Retention.Age.Weeks", days / 7),
        _ => Strings.Format("Wizard.Retention.Age.Days", Math.Max(days, 0)),
    };

    // ---------- S6 podsumowanie ----------

    [ObservableProperty]
    public partial string JobName { get; set; } = "";

    public string DestinationText => IsSftp
        ? string.Create(CultureInfo.InvariantCulture, $"sftp://{SftpUser}@{SftpHost}:{SftpPort}/{DestinationPath.TrimStart('/')}")
        : IsFtps ? FtpsDestinationText()
        : IsWebDav ? WebDavDestinationText()
        : DestinationPath.Trim(); // jak w szkicu (Draft) — podsumowanie S6 pokazuje to, co trafi do usługi

    public string SourcesText => IsDatabaseSource
        ? (SelectedDatabase is { } db ? Strings.Format("Wizard.Db.SourceText", db.Name, DbServer.Trim()) : "")
        : string.Join(Environment.NewLine, Sources);

    public string FinalText => State switch
    {
        WizardState.FirstBackup => Strings.Get("Wizard.Final.Running"),
        WizardState.Done when _finalCode is not null => Strings.Format("Wizard.Final.Failed", KbText.Full(_finalCode)),
        WizardState.Done when _ranFirstBackup => Strings.Get("Wizard.Final.Protected"),
        _ => Strings.Get("Wizard.Final.Saved"),
    };

    /// <summary>Szkic bez sekretów; zadanie „database” nie ma folderów źródłowych (ADR-0018).</summary>
    public WizardDraft Draft() => new(JobName.Trim(), IsDatabaseSource ? [] : Sources.ToList(), SelectedDestination.Value, DestinationPath.Trim(),
        IsSftp ? SftpHost.Trim() : null, SftpPort, IsSftp ? SftpUser.Trim() : null, IsSftp || IsTlsServer ? _approvedHostKey : null, Cron, KeepDaily, KeepWeekly, KeepMonthly,
        DatabaseDraft(), IsSmb ? SmbCredential.Trim() : null, IsTlsServer ? FtpsHost.Trim() : null, FtpsPort, IsTlsServer ? FtpsCredential.Trim() : null);

    // ---------- polecenia ----------

    [RelayCommand]
    private void AddSource()
    {
        var p = NewSource.Trim();
        if (p.Length == 0 || Sources.Contains(p, StringComparer.OrdinalIgnoreCase)) return;
        Sources.Add(p);
        NewSource = "";
        if (JobName.Length == 0) JobName = SuggestName(p);
    }

    [RelayCommand]
    private void RemoveSource(string? path)
    {
        if (path is not null) Sources.Remove(path);
    }

    private bool CanNext() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            ClearMessages();
            switch (State)
            {
                case WizardState.S2Identity:
                    _approvedHostKey = PendingHostKey; // zatwierdzenie odcisku; step-up Admin egzekwuje usługa przy zapisie (D4.11)
                    Apply(WizardEvent.IdentityApproved);
                    await ProbeAsync(ct).ConfigureAwait(true);
                    break;
                case WizardState.S4Security:
                    await CreateRepositoryAsync(ct).ConfigureAwait(true);
                    break;
                case WizardState.S4RecoveryKey:
                    ConfirmRecoveryKey();
                    break;
                case WizardState.S6Summary:
                    await SaveAsync(ct).ConfigureAwait(true);
                    break;
                case WizardState.Done or WizardState.Cancelled:
                    Finished?.Invoke(this, EventArgs.Empty);
                    break;
                case WizardState.S1Sources when IsDatabaseSource && !DatabaseStepReady():
                    break;
                case WizardState.S2Destination when IsTlsServer && !ValidateFtpsFields():
                    break; // błędy przy polach; usługa nie jest wołana
                default:
                    var issues = await _service.ValidateStepAsync(State, Draft(), ct).ConfigureAwait(true);
                    Show(issues);
                    Apply(WizardEvent.Next, valid: issues.Count == 0);
                    if (State == WizardState.S2Probe) await ProbeAsync(ct).ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception ex) when (Kobian.UI.Services.UiFailure.IsServiceFailure(ex))
        {
            // Awaria usługi w trakcie kroku (zerwany pipe, przekroczony termin InitRepository 5 min, odmowa roli, starszy Agent): komunikat KB zamiast zamknięcia aplikacji.
            // Po przekroczeniu terminu InitRepository/Upsert Agent mógł już dokończyć operację — kreator zostaje na tym kroku, a kolejne „Dalej” może zwrócić „repozytorium istnieje”
            // albo „nazwa zajęta” (to NIE jest ponowienie idempotentne); wtedy zadanie jest już na liście zadań (SCR-02).
            ShowText(KbText.Full(Kobian.UI.Services.UiFailure.CodeOf(ex)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (IsBusy) return;
        ClearMessages();
        Apply(WizardEvent.Back);
    }

    /// <summary>Anulowanie: przed zapisem zadanie nie powstaje (D6.7 reguła 2); utworzone repo zostaje „niepowiązane” (SCR-09).</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!CanCancel) return;
        WipeSecrets();
        Apply(WizardEvent.Cancel);
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private async Task ProbeAsync(CancellationToken ct)
    {
        var r = await _service.ProbeAsync(Draft(), ct).ConfigureAwait(true);
        switch (r.Outcome)
        {
            case ProbeOutcome.Ok:
                Apply(WizardEvent.ProbeOk);
                break;
            case ProbeOutcome.UnknownIdentity:
                PendingHostKey = r.HostKeyFingerprint;
                Apply(WizardEvent.ProbeUnknownIdentity);
                break;
            default:
                Show(r.KbCode is null ? [] : [new StepIssue("destination", r.KbCode)]);
                // Przypięty klucz/SPKI inny niż przedstawiony (KB-2104/KB-2109): to alarm, nie pytanie — pokazujemy odcisk do porównania poza pasmem, bez zatwierdzania.
                if (r.HostKeyFingerprint is { Length: > 0 } fp) ShowText(Strings.Format("Wizard.Identity.Presented", fp));
                Apply(WizardEvent.ProbePermanentError);
                break;
        }
    }

    private async Task CreateRepositoryAsync(CancellationToken ct)
    {
        if (!string.Equals(Password, PasswordConfirm, StringComparison.Ordinal))
        {
            ShowText(Strings.Get("Wizard.Security.Mismatch"));
            return;
        }
        var r = await _service.CreateRepositoryAsync(Draft(), Password, ct).ConfigureAwait(true);
        if (r.Issues.Count > 0)
        {
            Show(r.Issues);
            return;
        }
        WipeSecrets(); // hasło niepotrzebne po utworzeniu repo — nie trzymamy go w VM
        var groups = r.RecoveryKey is { } key ? RecoveryKeyText.Groups(key) : [];
        if (groups.Count >= 2)
        {
            RecoveryKeyGroups = groups;
            var first = RandomNumberGenerator.GetInt32(groups.Count);
            var second = (first + 1 + RandomNumberGenerator.GetInt32(groups.Count - 1)) % groups.Count;
            ChallengeGroups = (Math.Min(first, second) + 1, Math.Max(first, second) + 1);
            OnPropertyChanged(nameof(RecoveryKeyGroups));
            OnPropertyChanged(nameof(NumberedKeyGroups));
            OnPropertyChanged(nameof(ChallengeFirstLabel));
            OnPropertyChanged(nameof(ChallengeSecondLabel));
        }
        Apply(WizardEvent.RepositoryCreated);
        // PROWIZORYCZNIE Qb20: usługa bez klucza odzyskiwania (InitJob go dziś nie zwraca) — ekran S4RecoveryKey pomijany, brak klucza widoczny w podsumowaniu.
        if (groups.Count < 2) Apply(WizardEvent.RecoveryKeyConfirmed);
    }

    private void ConfirmRecoveryKey()
    {
        var ok = RecoveryKeyGroups.Count >= 2
                 && RecoveryKeyText.Matches(RecoveryKeyGroups[ChallengeGroups.First - 1], ChallengeFirst)
                 && RecoveryKeyText.Matches(RecoveryKeyGroups[ChallengeGroups.Second - 1], ChallengeSecond);
        if (!ok)
        {
            ShowText(Strings.Get("Wizard.Key.Mismatch"));
            return;
        }
        RecoveryKeyGroups = [];
        ChallengeFirst = ChallengeSecond = "";
        OnPropertyChanged(nameof(RecoveryKeyGroups));
        OnPropertyChanged(nameof(NumberedKeyGroups));
        Apply(WizardEvent.RecoveryKeyConfirmed);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        var code = await _service.SaveAsync(Draft(), ct).ConfigureAwait(true);
        if (code is not null)
        {
            Show([new StepIssue("job", code)]);
            return;
        }
        Apply(WizardEvent.SaveSucceeded);
        Apply(WizardEvent.Next); // Saved → FirstBackup (RunNow) albo Done
        if (State != WizardState.FirstBackup) return;
        _ranFirstBackup = true;
        _finalCode = await _service.RunFirstBackupAsync(JobName.Trim(), ct).ConfigureAwait(true);
        if (_finalCode is not null)
        {
            Apply(WizardEvent.FirstBackupFailed);
            return;
        }
        Apply(WizardEvent.FirstBackupSucceeded);
        // PROWIZORYCZNIE Qb20: mini-test D6.4 (odtworzenie próbki) jeszcze bez RPC — stan MiniTest zamykany od razu;
        // V2 nowego snapshotu Agent wykonuje po każdym backupie (it. 56), więc „Chronione” nie jest deklaracją bez weryfikacji.
        Apply(WizardEvent.MiniTestFinished);
    }

    /// <summary>Zdarzenia z usługi (probe, InitRepository, zapis, pierwsza kopia).</summary>
    public void Apply(WizardEvent e, bool valid = true)
    {
        if (e == WizardEvent.RepositoryCreated) RepositoryCreated = true;
        var next = WizardFlow.Next(State, e, Context(valid));
        if (next == State) return;
        State = next;
        foreach (var p in StateBound) OnPropertyChanged(p);
        if (next == WizardState.S5Retention) _ = RefreshRetentionPreviewAsync();
    }

    private void Show(IReadOnlyList<StepIssue> issues)
    {
        foreach (var i in issues) Messages.Add(KbText.Full(i.KbCode));
        OnPropertyChanged(nameof(HasMessages));
    }

    private void ShowText(string text)
    {
        Messages.Add(text);
        OnPropertyChanged(nameof(HasMessages));
    }

    private void ClearMessages()
    {
        Messages.Clear();
        OnPropertyChanged(nameof(HasMessages));
    }

    private void WipeSecrets()
    {
        // string w .NET jest niezmienny — nie da się go wyzerować (ograniczenie TextBox); usuwamy referencje, by nie przetrwał w VM.
        Password = PasswordConfirm = "";
    }

    private WizardContext Context(bool valid) => new(valid, RepositoryCreated, RunNow);

    private static string SuggestName(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
        return name.Length > 0 ? name : path;
    }
}
