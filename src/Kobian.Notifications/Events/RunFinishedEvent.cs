using System.Globalization;

namespace Kobian.Notifications.Events;

/// <summary>Dane zakończonego uruchomienia — wyłącznie identyfikatory, liczniki i kody (SEC-9.6: bez ścieżek, nazw baz, serwerów i sekretów).</summary>
public sealed record RunFinished(
    string RunId,
    string JobId,
    string JobName,
    string Status,
    string? KbCode,
    int Attempt,
    string Cause,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    long Files,
    long BytesRaw,
    long BytesNew,
    long Skipped,
    string? SnapshotId);

/// <summary>
/// Zdarzenie <c>pl.kobian.run.finished.v1</c> (D9.6). Pola danych z zamkniętej listy; identyfikatory sprawdzane (hex/ULID-podobne),
/// status ∈ {succeeded, warning, failed, cancelled, skipped}, kod KB w formacie KB-dddd. Tryb minimalny pomija <c>job.name</c>.
/// </summary>
public static class RunFinishedEvent
{
    public const string Type = "pl.kobian.run.finished.v1";
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "succeeded", "warning", "failed", "cancelled", "skipped" };
    private static readonly HashSet<string> Causes = new(StringComparer.Ordinal) { "schedule", "manual", "misfire", "retry", "resume", "event" };

    public static byte[] Build(string eventId, string hostId, RunFinished r, bool minimal)
    {
        ArgumentNullException.ThrowIfNull(r);
        Id(r.RunId, nameof(r.RunId));
        Id(r.JobId, nameof(r.JobId));
        if (r.SnapshotId is not null) Id(r.SnapshotId, nameof(r.SnapshotId));
        if (!Statuses.Contains(r.Status)) throw new ArgumentException("Unknown status.", nameof(r));
        if (!Causes.Contains(r.Cause)) throw new ArgumentException("Unknown cause.", nameof(r));
        if (r.KbCode is not null && !IsKb(r.KbCode)) throw new ArgumentException("Invalid KB code.", nameof(r));
        if (r.FinishedUtc < r.StartedUtc || r.Files < 0 || r.BytesRaw < 0 || r.BytesNew < 0 || r.Skipped < 0)
            throw new ArgumentException("Inconsistent run data.", nameof(r));
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["runId"] = r.RunId,
            ["status"] = r.Status,
            ["kb"] = r.KbCode,
            ["attempt"] = r.Attempt,
            ["cause"] = r.Cause,
            ["job"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = r.JobId, ["name"] = r.JobName },
            ["startedUtc"] = r.StartedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["durationSeconds"] = (long)(r.FinishedUtc - r.StartedUtc).TotalSeconds,
            ["stats"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["files"] = r.Files, ["bytesRaw"] = r.BytesRaw, ["bytesNew"] = r.BytesNew, ["skipped"] = r.Skipped,
            },
            ["snapshotId"] = r.SnapshotId,
        };
        return CloudEvent.Serialize(eventId, hostId, Type, r.FinishedUtc, "job:" + r.JobId, data, minimal);
    }

    private static void Id(string v, string name)
    {
        if (string.IsNullOrEmpty(v) || v.Length > 64 || !v.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("Identifier must be ASCII letters, digits or '-'.", name);
    }

    private static bool IsKb(string c) => c.Length == 7 && c.StartsWith("KB-", StringComparison.Ordinal) && c[3..].All(char.IsAsciiDigit);
}
