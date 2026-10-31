namespace Kobian.Application.Jobs;

public enum JobResult
{
    Succeeded = 0,
    Warning = 1,
    Failed = 2,
}

/// <summary>Wynik wykonania zadania (port między silnikiem a schedulerem). <see cref="Transient"/> decyduje o ponowieniu (D7.8 RetryDue).</summary>
public sealed record JobOutcome(JobResult Result, string? Code, bool Transient, string? SnapshotId);

/// <summary>Port wykonawcy zadania (JobRunner — D7.8 akcja StartJobRunner). Implementacja w Agencie składa potok silnika.</summary>
public interface IJobRunner
{
    Task<JobOutcome> RunAsync(string jobId, int attempt, CancellationToken ct);
}
