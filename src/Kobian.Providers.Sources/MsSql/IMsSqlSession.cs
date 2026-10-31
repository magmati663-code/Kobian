namespace Kobian.Providers.Sources.MsSql;

/// <summary>
/// Port połączenia z SQL Server (R2.5.1). Adapter produkcyjny: <c>Microsoft.Data.SqlClient</c> 7.1.1 (BL-40 cz. 2,
/// <c>CommandTimeout = 0</c> dla BACKUP/RESTORE, anulowanie z tokenu, <c>ApplicationName = "Kobian Backup"</c>).
/// Testy: sesja zastępcza w pamięci (S5c = bez kontenerów). Polecenia wyłącznie jako tekst-szablon + parametry (SEC-5.1).
/// </summary>
public interface IMsSqlSession
{
    /// <summary>Zapytanie zwracające wiersze (nazwy kolumn bez rozróżniania wielkości liter, NULL = <see langword="null"/>).
    /// Błąd serwera → <see cref="MsSqlServerException"/> z numerami błędów.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(SqlCommandSpec command, CancellationToken cancellationToken);

    /// <summary>Polecenie bez wyniku (BACKUP, RESTORE VERIFYONLY) — bez limitu czasu po stronie klienta, przerwanie tokenem.</summary>
    Task ExecuteAsync(SqlCommandSpec command, CancellationToken cancellationToken);
}

/// <summary>Błąd zgłoszony przez SQL Server — wyłącznie numery błędów (tekst komunikatu może zawierać nazwy obiektów klienta, NFR-11).</summary>
public sealed class MsSqlServerException : Exception
{
    public MsSqlServerException(IReadOnlyList<int> errorNumbers)
        : base("SQL Server error " + string.Join(",", errorNumbers ?? []))
    {
        ArgumentNullException.ThrowIfNull(errorNumbers);
        ErrorNumbers = errorNumbers;
    }

    public MsSqlServerException()
        : this([])
    {
    }

    public MsSqlServerException(string message)
        : base(message) => ErrorNumbers = [];

    public MsSqlServerException(string message, Exception innerException)
        : base(message, innerException) => ErrorNumbers = [];

    public IReadOnlyList<int> ErrorNumbers { get; }
}

/// <summary>Port wolnego miejsca na wolumenie stagingu (P-02; Windows: GetDiskFreeSpaceEx). <see langword="null"/> = nieznane.</summary>
public interface IStagingVolume
{
    long? FreeBytes(string stagingRoot);
}

/// <summary>Implementacja wieloplatformowa przez <see cref="DriveInfo"/> (wystarczająca dla lokalnego NTFS i testów).</summary>
public sealed class DriveInfoStagingVolume : IStagingVolume
{
    public long? FreeBytes(string stagingRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(stagingRoot));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
