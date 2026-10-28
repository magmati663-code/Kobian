using Grpc.Core;
using Kobian.Api.V1;
using Kobian.UI.Wizard;

namespace Kobian.UI.Services;

/// <summary>
/// Adapter kreatora SCR-03 → Agent (BL-61, Qb20/Qb24): ValidateStep → UpsertJob(validate_only), Probe → ProbeDestination, CreateRepository → InitRepository
/// (hasło w wiadomości po uwierzytelnionym pipe, klucz odzyskiwania jednorazowo), Save → UpsertJob (włączenie + harmonogram), pierwsza kopia → RunJob(wait).
/// Reguły walidacji żyją w Agencie — UI pokazuje tylko kody KB per pole. Brak połączenia → <see cref="IOException"/> (jak pulpit).
/// </summary>
public sealed class GrpcWizardService(Agent.AgentClient client, Metadata headers) : IWizardService
{
    /// <summary>Pola walidowane w danym kroku (reszta szkicu jeszcze niewypełniona).</summary>
    internal static IReadOnlySet<string> FieldsFor(WizardState step) => step switch
    {
        WizardState.S1Sources => new HashSet<string>(StringComparer.Ordinal) { "name", "sources", "database" },
        WizardState.S2Destination or WizardState.S2Probe or WizardState.S2Identity => new HashSet<string>(StringComparer.Ordinal) { "destination" },
        WizardState.S3Schedule => new HashSet<string>(StringComparer.Ordinal) { "schedule" },
        WizardState.S5Retention => new HashSet<string>(StringComparer.Ordinal) { "retention" },
        WizardState.S6Summary => new HashSet<string>(StringComparer.Ordinal) { "name", "sources", "destination", "schedule", "retention", "database" },
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    public async Task<IReadOnlyList<StepIssue>> ValidateStepAsync(WizardState wizardStep, WizardDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var fields = FieldsFor(wizardStep);
        if (fields.Count == 0) return [];
        var args = new UpsertJobArgs { JobName = draft.JobName.Trim(), Destination = Destination(draft), ValidateOnly = true, Retention = Retention(draft) };
        args.Sources.Add(draft.Sources);
        if (draft.Cron is not null) args.Cron = draft.Cron;
        if (Database(draft) is { } db) args.Database = db;
        var r = await Call(() => client.UpsertJobAsync(args, headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        return [.. r.Issues.Where(i => fields.Contains(i.Field)).Select(i => new StepIssue(i.Field, i.Code))];
    }

    public async Task<ProbeResult> ProbeAsync(WizardDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var r = await Call(() => client.ProbeDestinationAsync(new ProbeDestinationArgs { Destination = Destination(draft) }, headers, Deadline(TimeSpan.FromSeconds(60)), ct).ResponseAsync).ConfigureAwait(false);
        return Map(r);
    }

    internal static ProbeResult Map(ProbeDestinationReply r) => r.Status switch
    {
        ProbeStatus.Ok => new ProbeResult(ProbeOutcome.Ok),
        ProbeStatus.UnknownIdentity when r.HasIdentityFingerprint => new ProbeResult(ProbeOutcome.UnknownIdentity, null, r.IdentityFingerprint),
        _ => new ProbeResult(ProbeOutcome.PermanentError, r.HasCode ? r.Code : GrpcDashboardSource.KbUnknown, r.HasIdentityFingerprint ? r.IdentityFingerprint : null),
    };

    public async Task<RepositoryResult> CreateRepositoryAsync(WizardDraft draft, string repositoryPassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var args = new InitRepositoryArgs { JobName = draft.JobName.Trim(), Destination = Destination(draft), Password = repositoryPassword, CreateRecoveryKey = true, Retention = Retention(draft) };
        args.Sources.Add(draft.Sources);
        if (draft.Cron is not null) args.Cron = draft.Cron;
        if (Database(draft) is { } db) args.Database = db;
        // Init (PBKDF2 600 000 + zapis na cel) bywa długi — termin 5 min.
        var r = await Call(() => client.InitRepositoryAsync(args, headers, Deadline(TimeSpan.FromMinutes(5)), ct).ResponseAsync).ConfigureAwait(false);
        return Map(r);
    }

    internal static RepositoryResult Map(InitRepositoryReply r)
    {
        if (r.Result != OverallState.Failed) return new RepositoryResult([], r.HasRecoveryKey ? r.RecoveryKey : null);
        IReadOnlyList<StepIssue> issues = r.Issues.Count > 0
            ? [.. r.Issues.Select(i => new StepIssue(i.Field, i.Code))]
            : [new StepIssue("job", r.HasCode ? r.Code : GrpcDashboardSource.KbUnknown)];
        return new RepositoryResult(issues, null);
    }

    public async Task<string?> SaveAsync(WizardDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        // Retencja z S5 (po utworzeniu repozytorium w S4) zapisywana w definicji zadania przy zapisie.
        var args = new UpsertJobArgs { JobName = draft.JobName.Trim(), Destination = Destination(draft), ValidateOnly = false, Retention = Retention(draft) };
        args.Sources.Add(draft.Sources);
        if (draft.Cron is not null) args.Cron = draft.Cron;
        if (Database(draft) is { } db) args.Database = db;
        var r = await Call(() => client.UpsertJobAsync(args, headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        return r.Result == OverallState.Failed ? (r.HasCode ? r.Code : GrpcDashboardSource.KbUnknown) : null;
    }

    public async Task<string?> RunFirstBackupAsync(string jobName, CancellationToken ct)
    {
        // Pierwsza kopia może trwać długo — bez terminu (anulowanie przez ct, jak restore w CLI).
        var r = await Call(() => client.RunJobAsync(new RunJobRequest { Job = jobName, Wait = true }, headers, null, ct).ResponseAsync).ConfigureAwait(false);
        // Warning (np. pominięte zablokowane pliki) to kopia, która POWSTAŁA — kreator nie może jej pokazywać jako niepowodzenia (wcześniej każdy wynik ≠ Ok → „nie udało się”, a przy Warning bez kodu „KB-0001”).
        return r.Result == OverallState.Failed ? (r.HasCode ? r.Code : GrpcDashboardSource.KbUnknown) : null;
    }

    /// <summary>
    /// Cel bez sekretów (AC-01.3). PROWIZORYCZNIE Qb24: poświadczenie SFTP = odwołanie <c>file:&lt;użytkownik&gt;</c> do katalogu sekretów Agenta (ADR-0007) —
    /// plik sekretu zakłada administrator; przypięty odcisk z zatwierdzonej tożsamości (D4.11).
    /// </summary>
    internal static DestinationSpec Destination(WizardDraft d)
    {
        if (d.Destination == DestinationKind.Local) return new DestinationSpec { Scheme = "local", Root = d.DestinationPath.Trim() };
        if (d.Destination == DestinationKind.Smb)
        {
            // BL-73 cz. 3: UNC bez zmian (walidacja D8.9 po stronie usługi — jedno źródło reguł); brak wpisu → usługa odpowie KB-2102.
            var smb = new DestinationSpec { Scheme = "smb", Root = d.DestinationPath.Trim() };
            if (!string.IsNullOrWhiteSpace(d.SmbSecret)) smb.SecretRef = "file:" + d.SmbSecret.Trim();
            return smb;
        }
        if (d.Destination == DestinationKind.Ftps)
        {
            // BL-34: port zawsze jawnie — Agent wybiera tryb TLS z portu (PROWIZORYCZNIE Qb68: 990 = Implicit); pin SPKI tylko po zatwierdzeniu w S2Identity (D4.11).
            var f = new DestinationSpec { Scheme = "ftps", Root = d.DestinationPath.Trim(), Port = d.FtpsPort };
            if (!string.IsNullOrWhiteSpace(d.FtpsHost)) f.Host = d.FtpsHost.Trim();
            if (!string.IsNullOrWhiteSpace(d.FtpsSecret)) f.SecretRef = "file:" + d.FtpsSecret.Trim();
            if (!string.IsNullOrEmpty(d.ApprovedHostKey)) f.HostKey = d.ApprovedHostKey;
            return f;
        }
        if (d.Destination == DestinationKind.WebDav)
        {
            // HTTPS zawsze; pin SPKI tylko po zatwierdzeniu w S2Identity (D4.11) — jak FTPS.
            var w = new DestinationSpec { Scheme = "webdav", Root = d.DestinationPath.Trim(), Port = d.FtpsPort };
            if (!string.IsNullOrWhiteSpace(d.FtpsHost)) w.Host = d.FtpsHost.Trim();
            if (!string.IsNullOrWhiteSpace(d.FtpsSecret)) w.SecretRef = "file:" + d.FtpsSecret.Trim();
            if (!string.IsNullOrEmpty(d.ApprovedHostKey)) w.HostKey = d.ApprovedHostKey;
            return w;
        }
        var s = new DestinationSpec { Scheme = "sftp", Root = d.DestinationPath.Trim(), Port = d.SftpPort };
        if (!string.IsNullOrWhiteSpace(d.SftpHost)) s.Host = d.SftpHost.Trim();
        if (!string.IsNullOrWhiteSpace(d.SftpUser)) s.SecretRef = "file:" + d.SftpUser.Trim();
        if (!string.IsNullOrEmpty(d.ApprovedHostKey)) s.HostKey = d.ApprovedHostKey;
        return s;
    }

    /// <summary>Agent z obsługą zadań „database” (DiscoverDatabases + pole database w UpsertJob/InitRepository, it. 72–73).</summary>
    public bool SupportsDatabases => true;

    /// <summary>Źródło bazodanowe bez sekretów (AC-01.3): secret_ref i nazwa pliku certyfikatu jak w discovery (Qb30).</summary>
    internal static DatabaseSourceSpec? Database(WizardDraft d)
    {
        if (d.Database is not { } db) return null;
        var spec = new DatabaseSourceSpec { Server = db.Server.Trim(), Name = db.Database, Auth = db.SqlLogin ? "sql" : "integrated" };
        if (!string.IsNullOrEmpty(db.SecretRef)) spec.SecretRef = db.SecretRef;
        if (!string.IsNullOrEmpty(db.Certificate)) spec.Certificate = db.Certificate;
        return spec;
    }

    /// <summary>PreviewRetention (Reader, bez skutków ubocznych): symulacja Keep() dla polityki S5 i harmonogramu S3.</summary>
    public async Task<RetentionPreview?> PreviewRetentionAsync(WizardDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var args = new PreviewRetentionArgs { Retention = Retention(draft) };
        if (draft.Cron is not null) args.Cron = draft.Cron;
        var r = await Call(() => client.PreviewRetentionAsync(args, headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        return r.Result == OverallState.Failed ? null : new RetentionPreview(r.Kept, r.OldestDays);
    }

    /// <summary>DiscoverDatabases (Operator, tylko odczyt; BL-40 cz. 3): lista baz serwera SQL dla S1-db. Termin 2 min (D-03 per baza).</summary>
    public async Task<DatabaseDiscovery?> DiscoverDatabasesAsync(DatabaseServerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var args = new DiscoverDatabasesArgs { Server = request.Server.Trim(), Auth = request.SqlLogin ? "sql" : "integrated", AllowLocalhostUntrusted = request.AllowLocalhostUntrusted };
        if (!string.IsNullOrEmpty(request.SecretRef)) args.SecretRef = request.SecretRef;
        if (!string.IsNullOrEmpty(request.Certificate)) args.Certificate = request.Certificate;
        var r = await Call(() => client.DiscoverDatabasesAsync(args, headers, Deadline(TimeSpan.FromMinutes(2)), ct).ResponseAsync).ConfigureAwait(false);
        return Map(r);
    }

    /// <summary>FetchSqlCertificate (Operator, bez zapisu; BL-40 cz. 5): handshake TLS bez logowania, termin 30 s.</summary>
    public async Task<SqlCertificateCandidate?> FetchSqlCertificateAsync(string server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        var r = await Call(() => client.FetchSqlCertificateAsync(new FetchSqlCertificateArgs { Server = server.Trim() }, headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        return r.Result == OverallState.Failed
            ? new SqlCertificateCandidate(r.HasCode ? r.Code : GrpcDashboardSource.KbUnknown, "", "", "", default, false, false, "")
            : new SqlCertificateCandidate(null, r.Sha256, r.Subject, r.Issuer, DateTimeOffset.FromUnixTimeSeconds(r.NotAfterUnixSeconds), r.SelfIssued, r.SystemTrusted, r.SuggestedName);
    }

    /// <summary>ApproveSqlCertificate (Operator, modyfikujące): Agent porównuje odcisk z ponownie pobranym certyfikatem i zapisuje pin.</summary>
    public async Task<SqlCertificateApproval?> ApproveSqlCertificateAsync(string server, string sha256, string fileName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        var args = new ApproveSqlCertificateArgs { Server = server.Trim(), Sha256 = sha256 ?? "", FileName = fileName ?? "" };
        var r = await Call(() => client.ApproveSqlCertificateAsync(args, headers, Deadline(), ct).ResponseAsync).ConfigureAwait(false);
        return r.Result == OverallState.Failed ? new SqlCertificateApproval(r.HasCode ? r.Code : GrpcDashboardSource.KbUnknown, null) : new SqlCertificateApproval(null, r.FileName);
    }

    internal static DatabaseDiscovery Map(DiscoverDatabasesReply r)
    {
        if (r.Result == OverallState.Failed) return new DatabaseDiscovery(r.HasCode ? r.Code : GrpcDashboardSource.KbUnknown, 0, false, false, [], false, null);
        return new DatabaseDiscovery(null, r.ServerMajor, r.Express, r.Support == "conditional",
            [.. r.Databases.Select(d => new DatabaseItem(d.Name, d.RecoveryModel, d.DataBytes, d.CanBackup, d.Encrypted, [.. d.Warnings]))],
            r.Truncated, r.HasPermissionScript ? r.PermissionScript : null);
    }

    /// <summary>Retencja z S5 (BL-25): dzienne/tygodniowe/miesięczne — wartości domyślne przy InitRepository (S4), ostateczne przy zapisie (UpsertJob, it. 66).</summary>
    internal static RetentionSpec Retention(WizardDraft d) => new() { KeepDaily = d.KeepDaily, KeepWeekly = d.KeepWeekly, KeepMonthly = d.KeepMonthly };

    private static DateTime Deadline(TimeSpan? t = null) => DateTime.UtcNow + (t ?? TimeSpan.FromSeconds(30));

    private static Task<T> Call<T>(Func<Task<T>> f) => GrpcDashboardSource.CallShared(f);
}
