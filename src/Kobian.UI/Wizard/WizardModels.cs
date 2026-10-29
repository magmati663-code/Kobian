using Kobian.UI.Localization;

namespace Kobian.UI.Wizard;

/// <summary>Problem walidacji kroku zwrócony przez usługę (UpsertJob validate_only — D6.7): pole + kod KB.</summary>
public sealed record StepIssue(string Field, string KbCode);

/// <summary>Rodzaj celu w kroku S2 (S1 kierunek B: lokalny/USB i SFTP — reszta dostawców później).</summary>
public enum DestinationKind
{
    Local,
    Sftp,

    /// <summary>Udział sieciowy SMB (BL-73 cz. 3, it. 94): ścieżka UNC + odwołanie do poświadczenia w magazynie sekretów usługi.</summary>
    Smb,

    /// <summary>
    /// Serwer FTPS (BL-34, it. 120): host, tryb TLS (Explicit — port 21 / Implicit — port 990, PROWIZORYCZNIE Qb68), folder uniksowy, odwołanie do poświadczenia
    /// i — gdy certyfikatu nie zaufa system — przypięty odcisk SPKI (D4.11). Jawny FTP nie jest oferowany w kreatorze (D4.3, KB-2115).
    /// </summary>
    Ftps,

    /// <summary>
    /// Serwer WebDAV przez HTTPS (Nextcloud, ownCloud, NAS Synology/QNAP, Apache/nginx): host, port (domyślnie 443), ścieżka URL folderu, odwołanie do poświadczenia
    /// i — gdy certyfikatu nie zaufa system — przypięty odcisk SPKI (D4.11, jak FTPS). Jawny HTTP nie jest oferowany.
    /// </summary>
    WebDav,
}

/// <summary>Tryb TLS celu FTPS (D4.3). Definicja zadania v1 nie ma pola trybu — tryb wynika z portu (PROWIZORYCZNIE Qb68: 990 = Implicit, inny = Explicit).</summary>
public enum FtpsTlsMode
{
    /// <summary>AUTH TLS po połączeniu (zalecany, domyślny port 21).</summary>
    Explicit,

    /// <summary>TLS od pierwszego bajtu (starsze NAS, port 990).</summary>
    Implicit,
}

/// <summary>Rodzaj źródła w S1: pliki i foldery albo baza MSSQL (S1-db, BL-40 cz. 4 — tylko gdy usługa obsługuje bazy).</summary>
public enum SourceKind
{
    Files,
    Database,
}

/// <summary>
/// Źródło bazodanowe z S1-db bez sekretów (AC-01.3): login SQL tylko jako odwołanie <c>SecretRef</c> do magazynu Agenta, pin certyfikatu jako NAZWA pliku .cer
/// w katalogu certs Agenta (Qb30). <c>Database</c> = nazwa wybranej bazy.
/// </summary>
public sealed record DatabaseSource(string Server, bool SqlLogin, string? SecretRef, string? Certificate, string Database);

/// <summary>Preset harmonogramu S3 (6.4 — tryb Prosty bez pisania wyrażeń cron).</summary>
public enum SchedulePreset
{
    Daily,
    Weekdays,
    Hourly,
    Manual,
}

/// <summary>
/// Szkic zadania w pamięci UI (D6.7: zapis dopiero w S6). Bez sekretów: hasło repozytorium idzie wyłącznie do
/// <see cref="IWizardService.CreateRepositoryAsync"/>, poświadczenia SFTP/SMB/FTPS to odwołania <c>SecretRef</c> (AC-01.3). <c>ApprovedHostKey</c> = zatwierdzona
/// tożsamość serwera: odcisk klucza hosta SFTP albo pin SPKI certyfikatu FTPS (pole „hostKey” definicji zadania, D4.11).
/// </summary>
public sealed record WizardDraft(
    string JobName,
    IReadOnlyList<string> Sources,
    DestinationKind Destination,
    string DestinationPath,
    string? SftpHost,
    int SftpPort,
    string? SftpUser,
    string? ApprovedHostKey,
    string? Cron,
    int KeepDaily,
    int KeepWeekly,
    int KeepMonthly,
    DatabaseSource? Database = null,
    string? SmbSecret = null,
    string? FtpsHost = null,
    int FtpsPort = 21,
    string? FtpsSecret = null,
    IReadOnlyList<ServerSource>? SourceServers = null);

/// <summary>Serwer jako źródło kopii (S1): SFTP/FTPS/WebDAV, folder na serwerze, wpis poświadczenia (nazwa w magazynie usługi) i zatwierdzony odcisk.</summary>
public sealed record ServerSource(string Scheme, string Host, int Port, string Root, string SecretName, string? HostKey)
{
    public string Label => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Scheme}://{(Host.Contains(':', StringComparison.Ordinal) ? "[" + Host + "]" : Host)}:{Port}{Root}");
}

/// <summary>Nazwa wpisu poświadczenia z adresu serwera i loginu („NAS.firma.pl” + „jan” → „nas.firma.pl-jan”) — jak <c>CredentialStore.SuggestName</c> w Agencie.</summary>
public static class CredentialNames
{
    public static string Suggest(string host, string login)
    {
        var raw = (host.Trim() + "-" + login.Trim()).ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        foreach (var c in raw) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        var s = sb.ToString().Trim('-', '.');
        if (s.Length == 0 || !char.IsAsciiLetterOrDigit(s[0])) s = "serwer" + s;
        s = s.Length > 64 ? s[..64] : s;
        while (s.Contains("..", StringComparison.Ordinal)) s = s.Replace("..", ".", StringComparison.Ordinal);
        return s.TrimEnd('.');
    }
}

public enum ProbeOutcome
{
    Ok,
    UnknownIdentity,
    PermanentError,
}

/// <summary>Wynik ProbeDestination: przy nieznanej tożsamości odcisk klucza hosta (SFTP) lub SPKI certyfikatu (FTPS, KB-2109) do zatwierdzenia (D4.11).</summary>
public sealed record ProbeResult(ProbeOutcome Outcome, string? KbCode = null, string? HostKeyFingerprint = null);

/// <summary>Wynik InitRepository: problemy (np. KB-0301) albo klucz odzyskiwania do przepisania (AC-28.1).</summary>
public sealed record RepositoryResult(IReadOnlyList<StepIssue> Issues, string? RecoveryKey);

/// <summary>Port do usługi (SCR-03: ListDestinations, ProbeDestination, UpsertJob(validate_only), InitRepository, RunJob — R2.6.1).</summary>
public interface IWizardService
{
    Task<IReadOnlyList<StepIssue>> ValidateStepAsync(WizardState wizardStep, WizardDraft draft, CancellationToken ct);

    Task<ProbeResult> ProbeAsync(WizardDraft draft, CancellationToken ct);

    /// <summary>Hasło przekazane tylko tutaj; implementacja nie może go logować ani zapisywać na dysk (NFR-11).</summary>
    Task<RepositoryResult> CreateRepositoryAsync(WizardDraft draft, string repositoryPassword, CancellationToken ct);

    /// <summary>UpsertJob (zapis S6); null = zapisane, inaczej kod KB.</summary>
    Task<string?> SaveAsync(WizardDraft draft, CancellationToken ct);

    /// <summary>RunJob pierwszej kopii; null = Succeeded, inaczej kod KB.</summary>
    Task<string?> RunFirstBackupAsync(string jobName, CancellationToken ct);

    /// <summary>Podgląd Keep() dla polityki z S5 (D3.12, PreviewRetention). Domyślnie brak (usługa bez podglądu) — S5 pokazuje wtedy tylko reguły.</summary>
    Task<RetentionPreview?> PreviewRetentionAsync(WizardDraft draft, CancellationToken ct) => Task.FromResult<RetentionPreview?>(null);

    /// <summary>Czy usługa obsługuje źródła bazodanowe (S1-db). Domyślnie nie — kreator pokazuje tylko pliki.</summary>
    bool SupportsDatabases => false;

    /// <summary>Discovery MSSQL dla S1-db (BL-40 cz. 3, DiscoverDatabases). Domyślnie brak (usługa bez obsługi baz) — kreator ukrywa krok S1-db.</summary>
    Task<DatabaseDiscovery?> DiscoverDatabasesAsync(DatabaseServerRequest request, CancellationToken ct) => Task.FromResult<DatabaseDiscovery?>(null);

    /// <summary>Certyfikat TLS serwera SQL do zatwierdzenia (BL-40 cz. 5, FetchSqlCertificate). Domyślnie brak — pole pliku .cer zostaje ręczne.</summary>
    Task<SqlCertificateCandidate?> FetchSqlCertificateAsync(string server, CancellationToken ct) => Task.FromResult<SqlCertificateCandidate?>(null);

    /// <summary>Zatwierdzenie odcisku: Agent pobiera certyfikat ponownie, porównuje i zapisuje pin (ApproveSqlCertificate). Domyślnie brak.</summary>
    Task<SqlCertificateApproval?> ApproveSqlCertificateAsync(string server, string sha256, string fileName, CancellationToken ct) =>
        Task.FromResult<SqlCertificateApproval?>(null);

    /// <summary>Login i hasło wpisane w kreatorze → magazyn sekretów usługi (SaveCredential). null = zapisane, inaczej kod KB. Hasło nie jest nigdzie zapamiętywane w UI.</summary>
    Task<string?> SaveCredentialAsync(string name, string login, string password, CancellationToken ct) => Task.FromResult<string?>("KB-7012");

    /// <summary>Sprawdzenie serwera-źródła (ProbeDestination dla podanego serwera): tożsamość do zatwierdzenia, dostęp, folder.</summary>
    Task<ProbeResult> ProbeServerAsync(ServerSource server, CancellationToken ct) => Task.FromResult(new ProbeResult(ProbeOutcome.PermanentError, "KB-7012"));
}

/// <summary>Certyfikat przedstawiony przez serwer SQL (tylko do pokazania i zatwierdzenia): odcisk SHA-256, podmiot, wystawca, ważność.</summary>
public sealed record SqlCertificateCandidate(
    string? KbCode,
    string Sha256,
    string Subject,
    string Issuer,
    DateTimeOffset NotAfter,
    bool SelfIssued,
    bool SystemTrusted,
    string SuggestedName);

/// <summary>Wynik zatwierdzenia: kod KB (KB-3007 zmiana certyfikatu, KB-3008 nazwa zajęta) albo nazwa zapisanego pliku .cer.</summary>
public sealed record SqlCertificateApproval(string? KbCode, string? FileName);

/// <summary>Serwer SQL wskazany w S1-db: login SQL tylko jako odwołanie do magazynu sekretów Agenta, pin certyfikatu jako nazwa pliku (Qb30).</summary>
public sealed record DatabaseServerRequest(string Server, bool SqlLogin = false, string? SecretRef = null, string? Certificate = null, bool AllowLocalhostUntrusted = false);

/// <summary>Baza na liście S1-db: rozmiar danych, możliwość kopii i kody KB ostrzeżeń (TDE, log rośnie, brak uprawnień).</summary>
public sealed record DatabaseItem(string Name, string RecoveryModel, long DataBytes, bool CanBackup, bool Encrypted, IReadOnlyList<string> Warnings);

/// <summary>Wynik discovery: kod KB przy porażce, inaczej serwer (wersja, Express, wsparcie warunkowe D5.8), bazy i skrypt uprawnień D5.7.</summary>
public sealed record DatabaseDiscovery(
    string? KbCode,
    int ServerMajor,
    bool Express,
    bool Conditional,
    IReadOnlyList<DatabaseItem> Databases,
    bool Truncated,
    string? PermissionScript);

/// <summary>Wynik podglądu retencji: ile kopii zostaje po ustaleniu się stanu i jak daleko wstecz sięga najstarsza (dni).</summary>
public sealed record RetentionPreview(int Kept, int OldestDays);

/// <summary>Pozycja paska kroków 1–6 (stan: bieżący/ukończony/przyszły).</summary>
public sealed record WizardStepItem(int Number, string Text, bool IsCurrent, bool IsDone)
{
    public string AutomationId => "SCR-03.Step." + Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Glyph => IsDone ? "✓" : Number.ToString(System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>Opcja wyboru (cel, harmonogram) w kontrolce segmentowej — baza niegeneryczna dla szablonów z compiled bindings.</summary>
public abstract record WizardOption(string Key)
{
    public string Label => Strings.Get(Key);

    public abstract string AutomationId { get; }
}

public sealed record WizardOption<T>(T Value, string Key) : WizardOption(Key)
{
    public override string AutomationId => "SCR-03.Option." + Value;
}

/// <summary>Grupa klucza z numerem do wyświetlenia (AC-28.1: „przepisz grupę 3”).</summary>
public sealed record KeyGroupItem(int Number, string Text);

/// <summary>Grupy klucza odzyskiwania i wybór dwóch do przepisania (AC-28.1).</summary>
public static class RecoveryKeyText
{
    /// <summary>Podział na grupy rozdzielone „-” lub spacją (format klucza wyznacza usługa).</summary>
    public static IReadOnlyList<string> Groups(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Porównanie bez wielkości liter i spacji, w czasie stałym względem treści (nie ujawnia, która część się różni).</summary>
    public static bool Matches(string expected, string? typed)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var a = System.Text.Encoding.UTF8.GetBytes(expected.Trim().ToUpperInvariant());
        var b = System.Text.Encoding.UTF8.GetBytes((typed ?? "").Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant());
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
