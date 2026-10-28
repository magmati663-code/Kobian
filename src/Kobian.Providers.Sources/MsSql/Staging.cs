using System.Text.RegularExpressions;

namespace Kobian.Providers.Sources.MsSql;

/// <summary>
/// Cykl życia stagingu D5.10 (część niezależna od Windows): nazwa <c>staging\&lt;runId&gt;\&lt;guid&gt;.bak</c>, sprzątanie
/// katalogów starszych niż 1 h nienależących do aktywnego uruchomienia (RecoveryService), weryfikacja braku pliku po usunięciu (K16.1).
/// ACL katalogu (KobianAgent pełne, MSSQL$X zapis/odczyt, bez dziedziczenia) — port <see cref="IStagingAcl"/> (Platform.Windows).
/// </summary>
public static partial class Staging
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    [GeneratedRegex("^[0-9a-z]{8,40}$")]
    private static partial Regex RunIdPattern();

    public static bool IsValidRunId(string runId) => runId is not null && RunIdPattern().IsMatch(runId);

    /// <summary>Ścieżka pliku .bak w katalogu uruchomienia; nazwa pliku losowa (Guid) — nie ujawnia nazwy bazy.</summary>
    public static string FilePath(string stagingRoot, string runId, Guid fileId)
    {
        ArgumentException.ThrowIfNullOrEmpty(stagingRoot);
        if (!IsValidRunId(runId)) throw new ArgumentException("Invalid run id.", nameof(runId));
        return Path.Combine(stagingRoot, runId, fileId.ToString("N") + ".bak");
    }

    /// <summary>
    /// Usuwa przeterminowane katalogi uruchomień. Pomija: dowiązania (nie podąża), nazwy spoza wzorca runId, aktywne uruchomienia,
    /// katalogi młodsze niż <see cref="StaleAfter"/>. Zwraca usunięte runId.
    /// </summary>
    public static IReadOnlyList<string> CleanupStale(string stagingRoot, IReadOnlySet<string> activeRunIds, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(activeRunIds);
        var removed = new List<string>();
        var root = new DirectoryInfo(stagingRoot);
        if (!root.Exists || root.LinkTarget is not null) return removed;
        foreach (var dir in root.EnumerateDirectories())
        {
            if (dir.LinkTarget is not null || (dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (!IsValidRunId(dir.Name) || activeRunIds.Contains(dir.Name)) continue;
            if (nowUtc - dir.CreationTimeUtc < StaleAfter && nowUtc - dir.LastWriteTimeUtc < StaleAfter) continue;
            foreach (var f in dir.EnumerateFileSystemInfos())
            {
                if (f is DirectoryInfo || f.LinkTarget is not null) continue; // tylko pliki .bak w katalogu uruchomienia
                f.Delete();
            }
            if (!dir.EnumerateFileSystemInfos().Any())
            {
                dir.Delete();
                removed.Add(dir.Name);
            }
        }
        return removed;
    }

    /// <summary>Usunięcie pliku po przesłaniu strumienia i weryfikacja braku (K16.1: bez nadpisywania zerami).</summary>
    public static bool DeleteAndVerify(string filePath)
    {
        File.Delete(filePath);
        var dir = Path.GetDirectoryName(filePath)!;
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        return !File.Exists(filePath);
    }
}

/// <summary>Port ACL stagingu (D5.10) — implementacja Windows.</summary>
public interface IStagingAcl
{
    void Apply(string runDirectory, string sqlServiceSid);
}

/// <summary>Wynik oceny łańcucha (D5.2, P-03).</summary>
public enum ChainVerdict
{
    /// <summary>Brak pełnych nie-COPY_ONLY albo łańcuch Kobian aktualny.</summary>
    Clean,
    /// <summary>Ostatnia pełna nie-COPY_ONLY wykonana innym narzędziem — KB-3103 (MVP: kopia COPY_ONLY nie zmienia łańcucha, ostrzeżenie informacyjne).</summary>
    ForeignFullBackup,
}

public static class ChainEvaluator
{
    /// <summary>
    /// Reguła R2.5.3: łańcuch Kobian aktualny ⇔ differential_base_guid = backup_set_uuid ostatniej pełnej ∧ ta pełna wykonana przez Kobian.
    /// </summary>
    public static ChainVerdict Evaluate(Guid? lastFullBackupSetUuid, Guid? differentialBaseGuid, Guid? kobianFullBackupSetUuid)
    {
        if (lastFullBackupSetUuid is null) return ChainVerdict.Clean;
        var kobianOwnsLast = kobianFullBackupSetUuid is not null && kobianFullBackupSetUuid == lastFullBackupSetUuid;
        var baseMatches = differentialBaseGuid is not null && differentialBaseGuid == lastFullBackupSetUuid;
        return kobianOwnsLast && baseMatches ? ChainVerdict.Clean : ChainVerdict.ForeignFullBackup;
    }
}

/// <summary>Porównanie zapytań kontrolnych po odtworzeniu (D5.11, V4): range / exact / approx (±0,1%).</summary>
public static class ControlComparison
{
    public static bool Matches(string tolerance, long before, long after, long restored) => tolerance switch
    {
        "range" => restored >= before && restored <= Math.Max(before, after),
        "exact" => restored == before && before == after,
        "approx" => Math.Abs(restored - before) <= Math.Max(1, (long)Math.Ceiling(Math.Abs(before) * 0.001)),
        _ => throw new ArgumentException("Unknown tolerance.", nameof(tolerance)),
    };

    /// <summary>Daty: maksimum po odtworzeniu ≥ wartość wzorcowa z chwili backupu.</summary>
    public static bool DateMatches(DateTimeOffset? referenceMax, DateTimeOffset? restoredMax) =>
        referenceMax is null || (restoredMax is not null && restoredMax >= referenceMax);
}
