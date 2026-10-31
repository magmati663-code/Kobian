using Kobian.Application.Jobs;
using Kobian.Domain;

namespace Kobian.Engine.Backup;

/// <summary>Mapowanie wyniku potoku na <see cref="JobOutcome"/> (D3.14 pkt 1): Repo-Write = przejściowy (ponowienie wg D7.8), Integrity/brak źródła = trwały.</summary>
public static class BackupOutcome
{
    public static JobOutcome ToJobOutcome(this BackupResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Status switch
        {
            BackupStatus.Succeeded => new JobOutcome(JobResult.Succeeded, null, false, Hex(r.SnapId)),
            BackupStatus.Warning => new JobOutcome(JobResult.Warning, r.Code, false, Hex(r.SnapId)),
            _ => new JobOutcome(JobResult.Failed, r.Code ?? KbCode.Internal, r.Code == KbCode.RepoWriteFailed, null),
        };
    }

    private static string? Hex(byte[]? id) => id is null ? null : Convert.ToHexStringLower(id);
}
