namespace Kobian.UI.Dashboard;

/// <summary>Stan ochrony (6.3). Kolejność = waga: wyższy jest gorszy (stan globalny = najgorszy z zadań).</summary>
public enum ProtectionState
{
    Protected = 0,
    Paused = 1,
    Warning = 2,
    AtRisk = 3,
}

/// <summary>Wynik ostatniego uruchomienia zadania widziany przez UI (spójny z run_runs i CLI — AC-37.1).</summary>
public enum LastRunResult
{
    None = 0,
    Succeeded = 1,
    Warning = 2,
    Failed = 3,
}

/// <summary>Powód stanu — klucz komunikatu (Strings) i ewentualny kod KB; jeden powód = jedna główna akcja (6.3).</summary>
public enum ProtectionReason
{
    Ok = 0,
    Disabled,
    NeverSucceeded,
    LastRunFailed,
    Overdue,
    RepositoryDamaged,
    HostKeyMismatch,
    LastRunWarning,
    NotVerified,
    LowSpace,
    NotDeleteProtected,
    V3Overdue,
}

/// <summary>
/// Stan zadania z usługi (GetStatus/ListJobs — SCR-01). Wyłącznie metadane: nazwa zadania nadana przez użytkownika, czasy, kody — bez ścieżek i treści (NFR-11).
/// <paramref name="Interval"/> = oczekiwany cykl z harmonogramu (null = zadanie ręczne, bez kontroli zaległości).
/// </summary>
public sealed record JobStatusInfo(
    string JobId,
    string Name,
    bool Enabled,
    LastRunResult LastResult,
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? NextRunUtc,
    TimeSpan? Interval,
    int LastSuccessVerifyLevel = 0,
    string? LastCode = null,
    string? DestinationName = null,
    double? DestinationFreePercent = null,
    bool? DestinationDeleteProtected = null,
    bool RepositoryDamaged = false,
    bool HostKeyMismatch = false,
    bool V3Overdue = false,
    int SkippedFiles = 0,
    ActiveRun Activity = ActiveRun.None);

/// <summary>BL-76: uruchomienie w toku z Agenta (JobStatus.active_state) — informacja na SCR-02, nie zmienia stanu ochrony 6.3.</summary>
public enum ActiveRun
{
    None,
    Queued,
    Running,
}

public sealed record JobProtection(ProtectionState State, ProtectionReason Reason);

/// <summary>
/// Czysta funkcja stanu ochrony (6.3) — bez zegara systemowego i bez UI, żeby dało się ją testować tabelą przypadków.
/// Kolejność reguł: Wstrzymane → Zagrożone (najpoważniejszy powód pierwszy) → Uwaga → Chronione.
/// </summary>
public static class ProtectionEvaluator
{
    /// <summary>Próg „zbliżający się brak miejsca” (6.3: &lt; 15%).</summary>
    public const double LowSpacePercent = 15.0;

    /// <summary>Tolerancja zaległości: max(1 h, 25% cyklu) ponad jeden cykl (6.3 „&gt; 1 cyklu (+ tolerancja)”). PROWIZORYCZNIE Qb17.</summary>
    public static TimeSpan Tolerance(TimeSpan interval) => TimeSpan.FromTicks(Math.Max(TimeSpan.FromHours(1).Ticks, interval.Ticks / 4));

    public static JobProtection Evaluate(JobStatusInfo job, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.Enabled) return new(ProtectionState.Paused, ProtectionReason.Disabled);
        if (job.RepositoryDamaged) return new(ProtectionState.AtRisk, ProtectionReason.RepositoryDamaged);
        if (job.HostKeyMismatch) return new(ProtectionState.AtRisk, ProtectionReason.HostKeyMismatch);
        if (job.LastResult == LastRunResult.Failed) return new(ProtectionState.AtRisk, ProtectionReason.LastRunFailed);
        if (job.LastSuccessUtc is not { } ok)
            return job.LastResult == LastRunResult.None && job.NextRunUtc is not null
                ? new(ProtectionState.Warning, ProtectionReason.NeverSucceeded) // nowe zadanie czeka na pierwszą kopię
                : new(ProtectionState.AtRisk, ProtectionReason.NeverSucceeded);
        if (job.Interval is { } every && nowUtc - ok > every + Tolerance(every)) return new(ProtectionState.AtRisk, ProtectionReason.Overdue);
        if (job.LastResult == LastRunResult.Warning) return new(ProtectionState.Warning, ProtectionReason.LastRunWarning);
        if (job.LastSuccessVerifyLevel < 2) return new(ProtectionState.Warning, ProtectionReason.NotVerified);
        if (job.DestinationFreePercent is { } free && free < LowSpacePercent) return new(ProtectionState.Warning, ProtectionReason.LowSpace);
        if (job.DestinationDeleteProtected == false) return new(ProtectionState.Warning, ProtectionReason.NotDeleteProtected);
        if (job.V3Overdue) return new(ProtectionState.Warning, ProtectionReason.V3Overdue);
        return new(ProtectionState.Protected, ProtectionReason.Ok);
    }

    /// <summary>Stan globalny = najgorszy z zadań; brak zadań = null (pulpit pokazuje „utwórz pierwsze zadanie”).</summary>
    public static ProtectionState? Overall(IEnumerable<JobProtection> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ProtectionState? worst = null;
        foreach (var j in jobs)
            if (worst is null || j.State > worst) worst = j.State;
        return worst;
    }
}
