using System.Security.Cryptography;
using Kobian.UI.Wizard;

namespace Kobian.Tests.UI;

/// <summary>
/// Atrapa usługi kreatora (dane syntetyczne): reguły walidacji jak w tabeli D6.7 (bez nakładania, cron, retencja), sterowane wyniki probe,
/// losowy klucz odzyskiwania generowany w teście (nic stałego w repo — NFR-11). Rejestruje wywołania, by testy sprawdziły brak efektów ubocznych.
/// </summary>
public sealed class FakeWizardService : IWizardService
{
    public Queue<ProbeResult> ProbeResults { get; } = new();

    public List<string> Calls { get; } = [];

    public List<WizardDraft> Drafts { get; } = [];

    public bool IssueRecoveryKey { get; set; } = true;

    public string? RecoveryKey { get; private set; }

    public string? SaveCode { get; set; }

    public string? FirstBackupCode { get; set; }

    public int PasswordLengthSeen { get; private set; }

    public Task<IReadOnlyList<StepIssue>> ValidateStepAsync(WizardState wizardStep, WizardDraft draft, CancellationToken ct)
    {
        Calls.Add("validate:" + wizardStep);
        Drafts.Add(draft);
        List<StepIssue> issues = wizardStep switch
        {
            WizardState.S1Sources when draft.Database is null && draft.Sources.Count == 0 => [new("sources", "KB-0101")],
            WizardState.S1Sources when draft.Sources.Any(a => draft.Sources.Any(b => a != b && b.StartsWith(a.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) =>
                [new("sources", "KB-0102")],
            WizardState.S2Destination when string.IsNullOrWhiteSpace(draft.DestinationPath) => [new("destination", "KB-2106")],
            WizardState.S3Schedule when draft.Cron == WizardViewModel.InvalidCron => [new("cron", "KB-0201")],
            WizardState.S5Retention when draft.KeepDaily + draft.KeepWeekly + draft.KeepMonthly == 0 => [new("retention", "KB-0401")],
            _ => [],
        };
        return Task.FromResult<IReadOnlyList<StepIssue>>(issues);
    }

    /// <summary>Zapisane poświadczenia: nazwa → (login, długość hasła) — samego hasła atrapa nie przechowuje.</summary>
    public Dictionary<string, (string Login, int PasswordLength)> SavedCredentials { get; } = [];

    public Queue<ProbeResult> ServerProbeResults { get; } = new();

    public List<ServerSource> ProbedServers { get; } = [];

    public Task<string?> SaveCredentialAsync(string name, string login, string password, CancellationToken ct)
    {
        Calls.Add("credential:" + name);
        SavedCredentials[name] = (login, password.Length);
        return Task.FromResult<string?>(null);
    }

    public Task<ProbeResult> ProbeServerAsync(ServerSource server, CancellationToken ct)
    {
        Calls.Add("probe-server");
        ProbedServers.Add(server);
        return Task.FromResult(ServerProbeResults.Count > 0 ? ServerProbeResults.Dequeue() : new ProbeResult(ProbeOutcome.Ok));
    }

    public Task<ProbeResult> ProbeAsync(WizardDraft draft, CancellationToken ct)
    {
        Calls.Add("probe");
        Drafts.Add(draft);
        return Task.FromResult(ProbeResults.Count > 0 ? ProbeResults.Dequeue() : new ProbeResult(ProbeOutcome.Ok));
    }

    public Task<RepositoryResult> CreateRepositoryAsync(WizardDraft draft, string repositoryPassword, CancellationToken ct)
    {
        Calls.Add("init");
        PasswordLengthSeen = repositoryPassword.Length;
        if (repositoryPassword.Length < 12) return Task.FromResult(new RepositoryResult([new StepIssue("password", "KB-0301")], null));
        RecoveryKey = IssueRecoveryKey ? string.Join('-', Enumerable.Range(0, 8).Select(_ => RandomGroup())) : null;
        return Task.FromResult(new RepositoryResult([], RecoveryKey));
    }

    public Task<string?> SaveAsync(WizardDraft draft, CancellationToken ct)
    {
        Calls.Add("save");
        Drafts.Add(draft);
        return Task.FromResult(SaveCode);
    }

    public Task<string?> RunFirstBackupAsync(string jobName, CancellationToken ct)
    {
        Calls.Add("run:" + jobName);
        return Task.FromResult(FirstBackupCode);
    }

    public int PreviewCalls { get; private set; }

    /// <summary>null = usługa bez podglądu (zachowanie domyślne portu).</summary>
    public bool OfferPreview { get; set; } = true;

    /// <summary>Podgląd deterministyczny: dzienne + tygodniowe + miesięczne − 2 (nakładanie najnowszych), najstarsza = miesiące × 30 dni.</summary>
    public Task<RetentionPreview?> PreviewRetentionAsync(WizardDraft draft, CancellationToken ct)
    {
        PreviewCalls++;
        return Task.FromResult<RetentionPreview?>(OfferPreview
            ? new RetentionPreview(Math.Max(1, draft.KeepDaily + draft.KeepWeekly + draft.KeepMonthly - 2), Math.Max(draft.KeepDaily, draft.KeepMonthly * 30))
            : null);
    }

    /// <summary>S1-db: usługa obsługuje bazy (domyślnie nie — jak starszy Agent).</summary>
    public bool SupportsDatabases { get; set; }

    /// <summary>Wynik discovery (null = usługa bez obsługi baz); żądania zapisywane do sprawdzenia braku sekretów.</summary>
    public DatabaseDiscovery? Discovery { get; set; }

    public List<DatabaseServerRequest> DiscoveryRequests { get; } = [];

    public Task<DatabaseDiscovery?> DiscoverDatabasesAsync(DatabaseServerRequest request, CancellationToken ct)
    {
        Calls.Add("discover");
        DiscoveryRequests.Add(request);
        return Task.FromResult(Discovery);
    }

    /// <summary>Certyfikat serwera SQL do zatwierdzenia (null = usługa bez FetchSqlCertificate).</summary>
    public SqlCertificateCandidate? Certificate { get; set; }

    /// <summary>Odpowiedź zatwierdzenia; domyślnie sukces z proponowaną nazwą.</summary>
    public SqlCertificateApproval? Approval { get; set; }

    public List<(string Server, string Sha256, string FileName)> Approvals { get; } = [];

    public Task<SqlCertificateCandidate?> FetchSqlCertificateAsync(string server, CancellationToken ct)
    {
        Calls.Add("fetch-cert");
        return Task.FromResult(Certificate);
    }

    public Task<SqlCertificateApproval?> ApproveSqlCertificateAsync(string server, string sha256, string fileName, CancellationToken ct)
    {
        Calls.Add("approve-cert");
        Approvals.Add((server, sha256, fileName));
        return Task.FromResult<SqlCertificateApproval?>(Approval ?? new SqlCertificateApproval(null, fileName));
    }

    /// <summary>Certyfikat syntetyczny (samopodpisany, odcisk stały — nie pochodzi z żadnego serwera).</summary>
    public static SqlCertificateCandidate SampleCertificate() => new(null,
        "3F9A0C7E51B2D84466A1E0C9F27B53D18E6A4C0B92F1D7E3A5B8C6049D2E7F10", "CN=SRV01.firma.local", "CN=SRV01.firma.local",
        new DateTimeOffset(2027, 3, 31, 12, 0, 0, TimeSpan.Zero), SelfIssued: true, SystemTrusted: false, "SRV01-SQLEXPRESS-3f9a0c7e.cer");

    /// <summary>Serwer syntetyczny: Firma (pełna, z prawem kopii), Archiwum (TDE + log rośnie), Kadry (bez uprawnień — KB-3003), skrypt D5.7.</summary>
    public static DatabaseDiscovery SampleDiscovery() => new(null, 16, Express: true, Conditional: false,
    [
        new DatabaseItem("Firma", "FULL", 3_400_000_000, true, false, []),
        new DatabaseItem("Archiwum_2019", "FULL", 12_800_000_000, true, true, ["KB-3301", "KB-3105"]),
        new DatabaseItem("Kadry", "FULL", 820_000_000, false, false, ["KB-3003"]),
    ], Truncated: false, "USE [master];\nCREATE LOGIN [NT SERVICE\\KobianAgent] FROM WINDOWS;\nUSE [Kadry];\nALTER ROLE [db_backupoperator] ADD MEMBER [NT SERVICE\\KobianAgent];");

    /// <summary>Hasło testowe losowe (≥ 12 znaków), generowane w teście — nie trafia do repo ani logów.</summary>
    public static string RandomSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private static string RandomGroup()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, 5).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }
}
