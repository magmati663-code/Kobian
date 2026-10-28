using System.Globalization;

namespace Kobian.Providers.Sources.MsSql;

public enum MsSqlBackupKind
{
    Full,
    Differential,
    Log,
}

/// <summary>Wynik <c>BACKUP</c> odczytany z <c>msdb.dbo.backupset</c> (R2.5.3) — bez sekretów.</summary>
public sealed record MsSqlBackupInfo(
    string ServerName,
    string DatabaseName,
    MsSqlBackupKind Kind,
    bool CopyOnly,
    bool HasChecksum,
    Guid BackupSetUuid,
    Guid? DifferentialBaseGuid,
    decimal FirstLsn,
    decimal LastLsn,
    decimal DatabaseBackupLsn,
    DateTimeOffset FinishedUtc,
    string? ErpProfile);

/// <summary>
/// Klucz 14 snapshotu (<c>dbMeta</c>, 5.1/R2.3.5) — płaska mapa tekstowa, wartości niezmienne kulturowo.
/// Snapshot jest szyfrowany, ale i tak nie zapisujemy ścieżek stagingu ani poświadczeń (NFR-11).
/// </summary>
public static class DbSnapshotMeta
{
    public const string EntryName = "database.bak";

    public static IReadOnlyDictionary<string, string> Build(MsSqlBackupInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.HasChecksum) throw new InvalidOperationException("BACKUP must be taken WITH CHECKSUM (D5.x).");
        var d = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["engine"] = "mssql",
            ["server"] = info.ServerName,
            ["database"] = info.DatabaseName,
            ["kind"] = info.Kind.ToString().ToLowerInvariant(),
            ["copyOnly"] = info.CopyOnly ? "1" : "0",
            ["checksum"] = "1",
            ["backupSetUuid"] = info.BackupSetUuid.ToString("D"),
            ["firstLsn"] = info.FirstLsn.ToString(CultureInfo.InvariantCulture),
            ["lastLsn"] = info.LastLsn.ToString(CultureInfo.InvariantCulture),
            ["databaseBackupLsn"] = info.DatabaseBackupLsn.ToString(CultureInfo.InvariantCulture),
            ["finishedUtc"] = info.FinishedUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        };
        if (info.DifferentialBaseGuid is { } g) d["differentialBaseGuid"] = g.ToString("D");
        if (info.ErpProfile is { Length: > 0 } p) d["erpProfile"] = p;
        return d;
    }

    /// <summary>Odczyt identyfikatora łańcucha z dbMeta (do D5.2 / ChainEvaluator).</summary>
    public static Guid? BackupSetUuid(IReadOnlyDictionary<string, string>? meta) =>
        meta is not null && meta.TryGetValue("backupSetUuid", out var s) && Guid.TryParse(s, out var g) ? g : null;
}
