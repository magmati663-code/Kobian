namespace Kobian.Cli.Commands;

public enum OverallState
{
    Ok = 0,
    Warning = 1,
    Failed = 2,
}

/// <summary>Aktywny alert (BL-45, D9.7) z GetStatus: <c>Id</c> = losowy identyfikator do ack/silence; bez ścieżek i nazw plików (NFR-11).</summary>
public sealed record AlertItem(string Id, string Code, string Severity, string State, string ScopeKind, string Subject, DateTimeOffset? OpenedUtc, string? Kb, int Reminders, bool Escalated);

/// <summary>Lista aktywnych alertów.</summary>
public sealed record AlertListResult(OverallState Result, string? Code, IReadOnlyList<AlertItem> Alerts);

/// <summary>Wynik potwierdzenia/wyciszenia alertu: kod KB przy porażce, nowy stan („acknowledged”/„silenced”) przy powodzeniu.</summary>
public sealed record AlertActionResult(OverallState Result, string? Code, string? State);

public sealed record JobStatus(string Name, OverallState LastResult, DateTimeOffset? LastRunUtc, string? LastCode);

/// <summary>Wstrzymanie retencji hosta po skoku zegara (SEC-17.4): kod KB-6117, od kiedy, przesunięcie zegara (dodatnie = w przód).</summary>
public sealed record RetentionHoldStatus(string Code, DateTimeOffset? HeldAtUtc, long ClockDeltaSeconds);

/// <summary>Liczby alertów z GetStatus (BL-45): <c>Active</c> = open/notified/acknowledged, <c>Unacknowledged</c> = open/notified, <c>Critical</c> = aktywne critical/security, <c>Silenced</c> = wyciszone. Bez nazw zadań i ścieżek (NFR-11).</summary>
public sealed record AlertSummary(int Active, int Unacknowledged, int Critical, int Silenced)
{
    public static AlertSummary From(IEnumerable<AlertItem> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        int active = 0, unack = 0, crit = 0, silenced = 0;
        foreach (var a in alerts)
        {
            if (string.Equals(a.State, "silenced", StringComparison.Ordinal)) { silenced++; continue; }
            active++;
            if (string.Equals(a.State, "open", StringComparison.Ordinal) || string.Equals(a.State, "notified", StringComparison.Ordinal)) unack++;
            if (string.Equals(a.Severity, "critical", StringComparison.Ordinal) || string.Equals(a.Severity, "security", StringComparison.Ordinal)) crit++;
        }
        return new AlertSummary(active, unack, crit, silenced);
    }
}

public sealed record AgentStatus(OverallState State, string AgentVersion, IReadOnlyList<JobStatus> Jobs, RetentionHoldStatus? RetentionHold = null, string? RuntimeCode = null, string? RuntimeVersion = null, AlertSummary? Alerts = null);

/// <summary>Wynik zwolnienia wstrzymania retencji: <c>Released</c> = false, gdy nic nie było wstrzymane.</summary>
public sealed record RetentionReleaseResult(OverallState Result, string? Code, bool Released);

/// <summary>Weryfikacja łańcucha audytu (RD-42): liczba wpisów, pierwszy niezgodny seq, hash ostatniego wpisu (hex).</summary>
/// <summary>Odtworzenie bazy (BL-68): nazwa nowej bazy.</summary>
public sealed record DatabaseRestoreResult(OverallState Result, string? Code, string TargetDatabase, string SnapshotId);

public sealed record AuditVerifyResult(OverallState Result, string? Code, long Entries, long? BrokenSeq, string LastHash, int AnchorsChecked = 0, string? LastAnchor = null, string? LastAnchorAtUtc = null);

public sealed record RunOutcome(OverallState Result, string? Code, string? SnapshotId, string? RunId = null);

/// <summary>Podgląd odtworzenia (it. 98, R2.8.1): liczby, konflikty (do 20 ścieżek względnych w kopii), wolne miejsce, kod blokady.</summary>
public sealed record RestorePlanResult(string? Code, string SnapshotId, long Files, long Bytes, long NewDirectories, long Conflicts, long ConflictBytes,
    IReadOnlyList<string> ConflictSample, long InvalidNames, long Links, long MissingBlocks, int Packs, long ReadBytes, long? FreeBytes, string? BlockingCode, long Resumable = 0);

/// <summary>Żądanie inicjalizacji repozytorium i zadania „files”. Hasło wyłącznie przez <c>PasswordFile</c> (ścieżka bezwzględna) — nigdy w argumentach ani w wiadomości.
/// Cel zdalny (BL-73 cz. 2): <c>SecretRef</c> = odwołanie do magazynu sekretów Agenta („file:&lt;nazwa&gt;”), dla SFTP także host/port/odcisk klucza hosta.</summary>
public sealed record InitRequest(string JobName, string Source, string DestinationScheme, string DestinationRoot, string PasswordFile, long? PackTargetSize = null,
    string? SecretRef = null, string? Host = null, int? Port = null, string? HostKey = null);

/// <summary>Wynik inicjalizacji: identyfikatory bez materiału tajnego; <c>Code</c> przy porażce.</summary>
public sealed record InitResult(OverallState Result, string? Code, string? JobId, string? RepoId, string? KeyId);

/// <summary>Wyzwalacz cron zadania (widok CLI). <c>Cron</c> = „?” przy uszkodzonej specyfikacji.</summary>
public sealed record ScheduleItem(string Job, string TriggerId, string Cron, string TimeZone, bool Enabled);

/// <summary>Wynik zmiany harmonogramu: kod KB przy porażce, identyfikator wyzwalacza przy powodzeniu.</summary>
public sealed record ScheduleSetResult(OverallState Result, string? Code, string? TriggerId);

/// <summary>Wynik <c>check</c> (BL-26): poziom 2 (V2) lub 3 (V2+V3), znaleziska (kod, „rodzaj:id”) — bez nazw plików (NFR-11).</summary>
public sealed record CheckResult(OverallState Result, string? Code, int Level, int Snapshots, int Packs, int PacksRead, long BlocksVerified,
    IReadOnlyList<string> DamagedSnapshots, int Issues, IReadOnlyList<(string Code, string Target)> Findings, IReadOnlyList<DamageItem>? Damage = null,
    int RecoveredPacks = 0, int UnreadablePacks = 0);

/// <summary>Szkody snapshotu po <c>check --repair</c> (D8.10, it. 109): stan intact|partial|unrecoverable i liczby.</summary>
public sealed record DamageItem(string SnapshotId, string State, long DamagedFiles, long DamagedDirectories, long IntactFiles, bool IndexRecovered = false);

/// <remarks>it. 115 (RD-70): <c>Damage</c> = null | "partial" | "unrecoverable" (z `check --repair`).</remarks>
public sealed record SnapshotInfo(string Id, string Job, DateTimeOffset TimeUtc, string Mode, long Files, long BytesRaw, string? Damage = null, long DamagedFiles = 0);

/// <summary>Wynik retencji (BL-25): zachowane/zapomniane (przy podglądzie — do zapomnienia); <c>Forget</c> = ID snapshotów do zapomnienia.</summary>
public sealed record RetentionResult(OverallState Result, string? Code, bool HasPolicy, bool DryRun, int Kept, int Forgotten, IReadOnlyList<string> Forget, bool Held = false);

/// <summary>Plan prune (BL-25 cz. 3) — tylko podgląd: obiekty żywe i do usunięcia (liczby i bajty), karencja; <c>BlockedObject</c> = „rodzaj:hex” przy KB-1601.</summary>
public sealed record PrunePlanResult(OverallState Result, string? Code, string? BlockedObject, int LiveSnapshots, int LivePacks, long LiveBytes,
    int DeadSnapshots, int DeadIndexes, int DeadPacks, long DeadBytes, long GraceSeconds);

/// <summary>Wynik prune (BL-25 cz. 4): liczby obiektów; <c>Pending</c> = nieżywe czekające na koniec karencji.</summary>
public sealed record PruneRunResult(OverallState Result, string? Code, string? StopObject, int Marked, int Unmarked, int DeletedSnapshots, int DeletedIndexes, int DeletedPacks,
    long DeletedBytes, int Pending, long GraceSeconds, int LocksCleaned, int IndexesConsolidated = 0, int IndexesWritten = 0, int ActiveIndexes = 0);

/// <summary>Uruchomienie z historii (BL-60): rodzaj backup/restore/check, stan maszyny uruchomień, kod KB — bez ścieżek i danych klienta (NFR-11).</summary>
public sealed record RunItem(string RunId, string Job, string Kind, string Cause, string State, OverallState Result, DateTimeOffset? StartedUtc, DateTimeOffset? FinishedUtc,
    string? Code, string? SnapshotId, long? Files, long? BytesUploaded);

/// <summary>Jedna zmiana uruchomienia ze strumienia WatchRun (it. 134, SCR-07): stan, liczniki, bajty odczytane; <c>Final</c> = ostatnia wiadomość.</summary>
public sealed record RunProgressItem(RunItem Run, long BytesRead, bool Final);

/// <summary>Tożsamość serwera celu (BL-35, D4.11): stan pending|pinned|rejected|retired|expired; <c>TrustedByCa</c> = SPKI zapisany informacyjnie (ważny łańcuch CA).</summary>
public sealed record ServerIdentityItem(string DestinationId, string Kind, string Fingerprint, string State, string Endpoint, string? Display,
    DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc, string? DecidedBy, DateTimeOffset? DecidedAtUtc, bool TrustedByCa);

/// <summary>Lista tożsamości i cele „wysokiego ryzyka” (SEC-14.5).</summary>
public sealed record ServerIdentityList(OverallState Result, string? Code, IReadOnlyList<ServerIdentityItem> Identities, IReadOnlyList<string> RequirePinDestinations);

/// <summary>Decyzja Administratora o odcisku: add | replace | reject.</summary>
public enum IdentityDecision
{
    Add,
    Replace,
    Reject,
}

/// <summary>Wynik decyzji: nowy stan odcisku albo kod KB (KB-2141/2142/2143).</summary>
public sealed record IdentityDecisionResult(OverallState Result, string? Code, string? State);

/// <summary>Problem pliku zadań (US-29): ścieżka JSON, kod KB, stały powód.</summary>
public sealed record TransferIssue(string Path, string Code, string Reason);

/// <summary>Wynik eksportu zadań: dokument kobian-export v1 (bez sekretów) albo kod KB z problemami.</summary>
public sealed record JobsExportResult(OverallState Result, string? Code, byte[]? Document, int Jobs, IReadOnlyList<TransferIssue> Issues);

/// <summary>Zmiana z planu importu: kind new|unchanged|conflict, action create|none|skip|overwrite|rename|blocked.</summary>
public sealed record JobImportChange(string Job, string Kind, string Action, string? NewName, bool NeedsCredentials, bool MissingKeyMaterial);

/// <summary>Wynik importu zadań; <c>Applied</c> = zapisano w katalogu (zadania wyłączone).</summary>
public sealed record JobsImportResult(OverallState Result, string? Code, IReadOnlyList<TransferIssue> Issues, IReadOnlyList<JobImportChange> Changes, bool Applied);

/// <summary>Rozwiązanie konfliktu nazw przy imporcie (--on-conflict); <c>Fail</c> = bez opcji.</summary>
public enum ImportConflict
{
    Fail = 0,
    Skip = 1,
    Overwrite = 2,
    Rename = 3,
}

/// <summary>Usługa niedostępna (pipe nie istnieje, odmowa, przekroczony czas ≤ 5 s — AC-37.1) → kod wyjścia 3.</summary>
public sealed class AgentUnavailableException : Exception
{
    public AgentUnavailableException() : base("Agent unavailable.") { }

    public AgentUnavailableException(string message) : base(message) { }

    public AgentUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Usługa odpowiedziała, ale jej obsługa metody zawiodła (nieobsłużony wyjątek w Agencie, np. niedostępny katalog stanu — status Unknown/NotFound/…): kod wyjścia 2, KB-0001.
/// Wcześniej taki status wychodził z CLI jako nieobsłużony wyjątek ze stosem wywołań.
/// </summary>
public sealed class AgentInternalException : Exception
{
    public AgentInternalException() : base("Agent internal error.") { }

    public AgentInternalException(string message) : base(message) { }

    public AgentInternalException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Usługa wymaga nowszej rewizji klienta (D2.16) — tylko polecenia modyfikujące; kod wyjścia 4 (AC-37.2).</summary>
public sealed class ClientTooOldException : Exception
{
    public ClientTooOldException() : base("Client revision too old.") { }

    public ClientTooOldException(string message) : base(message) { }

    public ClientTooOldException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Port klienta API Agenta (gRPC po named pipe — D2.4; implementacja po BL-14). CLI nie zna silnika (test architektury).</summary>
public interface IAgentClient
{
    Task<AgentStatus> GetStatusAsync(CancellationToken ct);

    Task<RunOutcome> RunJobAsync(string job, bool wait, CancellationToken ct);

    Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string? job, CancellationToken ct);

    Task<RunOutcome> RestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct);

    /// <summary>RD-81: odtworzenie w tle — Agent zwraca od razu <c>RunId</c> (Ok = przyjęto), postęp przez <see cref="WatchRunAsync"/>. Domyślnie niedostępne (fałszywe klienty).</summary>
    Task<RunOutcome> RestoreDetachedAsync(string snapshot, string? pathFilter, string target, CancellationToken ct) => throw new AgentUnavailableException();

    Task<InitResult> InitJobAsync(InitRequest request, CancellationToken ct);

    Task<IReadOnlyList<ScheduleItem>> ListSchedulesAsync(string? job, CancellationToken ct);

    /// <summary>Ustawia harmonogram cron zadania; <c>cron == null</c> wyłącza go.</summary>
    Task<ScheduleSetResult> SetScheduleAsync(string job, string? cron, CancellationToken ct);

    /// <summary>Weryfikacja repozytorium zadania; <c>samplePercent == null</c> = tylko V2, <c>rotation == null</c> = tydzień ISO po stronie Agenta.</summary>
    Task<CheckResult> CheckAsync(string job, int? samplePercent, long? rotation, CancellationToken ct);

    /// <summary>check z oceną szkód (D8.10, rola Administrator). Domyślnie: bez naprawy niedostępne.</summary>
    Task<CheckResult> CheckAsync(string job, int? samplePercent, long? rotation, bool repair, CancellationToken ct) =>
        repair ? throw new AgentUnavailableException() : CheckAsync(job, samplePercent, rotation, ct);

    /// <summary>Historia uruchomień, najnowsze pierwsze; <c>limit == null</c> = domyślny limit Agenta (200).</summary>
    Task<IReadOnlyList<RunItem>> ListRunsAsync(string? job, int? limit, CancellationToken ct);

    /// <summary>Strumień zmian jednego uruchomienia do stanu końcowego (it. 134). Domyślnie (fałszywe klienty, stary Agent) = usługa niedostępna.</summary>
    IAsyncEnumerable<RunProgressItem> WatchRunAsync(string runId, CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Retencja zadania: <c>dryRun</c> = podgląd; inaczej logiczne zapomnienie (dane na celu zostają do prune).</summary>
    Task<RetentionResult> ApplyRetentionAsync(string job, bool dryRun, CancellationToken ct);

    /// <summary>Plan prune repozytorium zadania (tylko podgląd). Domyślnie: brak usługi.</summary>
    Task<PrunePlanResult> PlanPruneAsync(string job, CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Prune dwufazowy repozytorium zadania (oznaczenie + usunięcie po karencji). Domyślnie: brak usługi.</summary>
    Task<PruneRunResult> PruneAsync(string job, CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Zwolnienie wstrzymania retencji po skoku zegara (rola Administrator). Domyślnie: brak usługi.</summary>
    Task<RetentionReleaseResult> ReleaseRetentionHoldAsync(CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Weryfikacja łańcucha audytu Agenta (SEC-19.1). Domyślnie: brak usługi.</summary>
    Task<AuditVerifyResult> VerifyAuditAsync(CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Weryfikacja z kotwicą zewnętrzną „seq:sha256hex” (it. 99, D9.9); null = bez kotwicy.</summary>
    Task<AuditVerifyResult> VerifyAuditAsync(string? anchor, CancellationToken ct) => anchor is null ? VerifyAuditAsync(ct) : throw new AgentUnavailableException();

    /// <summary>Odtworzenie bazy MSSQL z kopii do NOWEJ bazy (BL-68). Domyślnie: brak usługi.</summary>
    Task<DatabaseRestoreResult> RestoreDatabaseAsync(string snapshot, string? targetDatabase, CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Tożsamości serwerów celów (BL-35). Domyślnie: brak usługi.</summary>
    Task<ServerIdentityList> ListServerIdentitiesAsync(string? destinationId, CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Zatwierdzenie/odrzucenie oczekującego odcisku (Administrator). Domyślnie: brak usługi.</summary>
    Task<IdentityDecisionResult> DecideServerIdentityAsync(string destinationId, string fingerprint, IdentityDecision decision, CancellationToken ct) =>
        throw new AgentUnavailableException();

    /// <summary>„Wysokie ryzyko” celu — pin mimo ważnego CA (Administrator). Domyślnie: brak usługi.</summary>
    Task<IdentityDecisionResult> SetServerIdentityPolicyAsync(string destinationId, bool requirePin, CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Aktywne alerty (BL-45). Domyślnie: brak usługi.</summary>
    Task<AlertListResult> ListAlertsAsync(CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Potwierdzenie alertu (Operator+). Domyślnie: brak usługi.</summary>
    Task<AlertActionResult> AcknowledgeAlertAsync(string alertId, CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Wyciszenie alertu na <paramref name="minutes"/> minut (Administrator). Domyślnie: brak usługi.</summary>
    Task<AlertActionResult> SilenceAlertAsync(string alertId, int minutes, CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Podgląd odtworzenia (it. 98): `kobian restore … --dry-run`.</summary>
    Task<RestorePlanResult> PlanRestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Eksport zadań (US-29, Administrator); pusta lista = wszystkie. Domyślnie: brak usługi.</summary>
    Task<JobsExportResult> ExportJobsAsync(IReadOnlyList<string> jobs, CancellationToken ct) => throw new AgentUnavailableException();

    /// <summary>Import zadań (US-29, Administrator); <c>dryRun</c> = tylko plan zmian. Domyślnie: brak usługi.</summary>
    Task<JobsImportResult> ImportJobsAsync(byte[] document, bool dryRun, ImportConflict onConflict, CancellationToken ct) => throw new AgentUnavailableException();
}

/// <summary>Do czasu BL-14 (kontrakt gRPC) — zawsze „brak usługi”. Uczciwie: CLI nie udaje działającej usługi.</summary>
public sealed class UnavailableAgentClient : IAgentClient
{
    public Task<AgentStatus> GetStatusAsync(CancellationToken ct) => throw new AgentUnavailableException();

    public Task<RunOutcome> RunJobAsync(string job, bool wait, CancellationToken ct) => throw new AgentUnavailableException();

    public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string? job, CancellationToken ct) => throw new AgentUnavailableException();

    public Task<RunOutcome> RestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct) => throw new AgentUnavailableException();

    public Task<InitResult> InitJobAsync(InitRequest request, CancellationToken ct) => throw new AgentUnavailableException();

    public Task<IReadOnlyList<ScheduleItem>> ListSchedulesAsync(string? job, CancellationToken ct) => throw new AgentUnavailableException();

    public Task<ScheduleSetResult> SetScheduleAsync(string job, string? cron, CancellationToken ct) => throw new AgentUnavailableException();

    public Task<CheckResult> CheckAsync(string job, int? samplePercent, long? rotation, CancellationToken ct) => throw new AgentUnavailableException();

    public Task<IReadOnlyList<RunItem>> ListRunsAsync(string? job, int? limit, CancellationToken ct) => throw new AgentUnavailableException();

    public Task<RetentionResult> ApplyRetentionAsync(string job, bool dryRun, CancellationToken ct) => throw new AgentUnavailableException();
}
