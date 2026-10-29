using Kobian.Api.V1;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Co audytować dla wywołania API (RD-42, D9.2): każda metoda modyfikująca i każda odmowa. Cel i parametry tylko dla zmian zaufania
/// i konfiguracji (pin certyfikatu SQL, klucz hosta SFTP, nazwa zadania) — bez haseł i treści plików (SEC-11.5).
/// </summary>
public static class AgentCallAudit
{
    /// <summary>Aktor w trybie konsoli (pipe CurrentUserOnly). W usłudze aktor = SID klienta z tokenu pipe (BL-17, TokenCallerResolver; W-17).</summary>
    public const string LocalActor = "local-pipe";

    public static string Operation(string fullMethod) => fullMethod.StartsWith("/kobian.v1.", StringComparison.Ordinal) ? fullMethod[11..] : fullMethod;

    public static (string? Target, IReadOnlyDictionary<string, string>? Params) Describe(object? request) => request switch
    {
        ApproveSqlCertificateArgs a => (a.FileName, new Dictionary<string, string> { ["server"] = a.Server, ["sha256"] = a.Sha256, ["strict"] = a.Strict ? "true" : "false" }),
        // BL-35 (D4.11): zmiana zaufania do serwera celu — cel, odcisk, decyzja (ADD/REPLACE/REJECT).
        ApproveServerIdentityArgs s => (s.DestinationId, new Dictionary<string, string> { ["fingerprint"] = s.Fingerprint, ["decision"] = s.Decision.ToString() }),
        SetServerIdentityPolicyArgs p => (p.DestinationId, new Dictionary<string, string> { ["require_pin"] = p.RequirePin ? "true" : "false" }),
        // BL-45: alert wskazany losowym identyfikatorem (bez danych klienta); wyciszenie z czasem trwania.
        AckAlertArgs a => (a.AlertId, null),
        SilenceAlertArgs s => (s.AlertId, new Dictionary<string, string> { ["minutes"] = s.DurationMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
        InitRepositoryArgs i => (i.JobName, HostKey(i.Destination)),
        SaveCredentialArgs c => (c.Name, null), // tylko nazwa wpisu — bez loginu i hasła (NFR-11, SEC-11.5)
        UpsertJobArgs u => (u.JobName, HostKey(u.Destination)),
        SetScheduleArgs s => (s.Job, s.HasCron ? new Dictionary<string, string> { ["cron"] = s.Cron } : null),
        RunJobRequest r => (r.Job, null),
        RestoreRequest r => (r.Snapshot, null),
        RestoreDatabaseArgs d => (d.Snapshot, d.HasTargetDatabase ? new Dictionary<string, string> { ["target_database"] = d.TargetDatabase } : null),
        PruneArgs p => (p.Job, null),
        ApplyRetentionArgs a => (a.Job, null),
        CheckArgs c => (c.Job, null),
        // US-29: bez treści pliku — skrót SHA-256 dokumentu, rozmiar, tryb (SEC-11.5).
        ImportJobsArgs i => (null, new Dictionary<string, string>
        {
            ["sha256"] = Kobian.Agent.Jobs.JobTransferService.DocumentHash(i.Document.Span),
            ["bytes"] = i.Document.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["validate_only"] = i.ValidateOnly ? "true" : "false",
            ["on_conflict"] = i.OnConflict.ToString(),
        }),
        ExportJobsArgs e => (e.Jobs.Count == 0 ? "*" : string.Join(",", e.Jobs.Take(20)), null),
        _ => (null, null),
    };

    private static Dictionary<string, string>? HostKey(DestinationSpec? d) =>
        d is not null && d.HasHostKey ? new Dictionary<string, string> { ["scheme"] = d.Scheme, ["host_key"] = d.HostKey } : null;
}
