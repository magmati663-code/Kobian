namespace Kobian.Providers.Sources.MsSql;

/// <summary>Profil wpływu backupu (5.7, NFR-07).</summary>
public enum BackupImpactProfile
{
    /// <summary>Wartości domyślne SQL Server (brak opcji).</summary>
    Default = 0,
    /// <summary>„Niski wpływ”: BUFFERCOUNT = 8, MAXTRANSFERSIZE = 1 MiB.</summary>
    LowImpact = 1,
}

/// <summary>Polecenie gotowe do wykonania: tekst T-SQL + parametry (nazwy → wartości). Nic nie jest sklejane z danymi.</summary>
public sealed record SqlCommandSpec(string Text, IReadOnlyDictionary<string, object> Parameters);

/// <summary>
/// Szablon BACKUP z R2.5.3: nazwa bazy jako parametr <c>sysname</c> i QUOTENAME po stronie serwera; ścieżka stagingu
/// jako parametr <c>@path</c> w sp_executesql. MVP (S1=B): tylko pełny COPY_ONLY (US-02, D5.2).
/// </summary>
public static class MsSqlBackupCommand
{
    public const int LowImpactBufferCount = 8;
    public const int LowImpactMaxTransfer = 1_048_576;

    public const string TemplateText = """
        DECLARE @sql nvarchar(max) = N'BACKUP DATABASE ' + QUOTENAME(@db) +
          N' TO DISK = @path WITH COPY_ONLY, CHECKSUM, FORMAT, INIT, STATS = 5' +
          CASE WHEN @bufferCount IS NOT NULL THEN N', BUFFERCOUNT = ' + CAST(@bufferCount AS nvarchar(10)) ELSE N'' END +
          CASE WHEN @maxTransfer IS NOT NULL THEN N', MAXTRANSFERSIZE = ' + CAST(@maxTransfer AS nvarchar(10)) ELSE N'' END;
        EXEC sp_executesql @sql, N'@path nvarchar(4000)', @path = @stagingPath;
        """;

    public static SqlCommandSpec FullCopyOnly(string database, string stagingPath, BackupImpactProfile profile)
    {
        TSql.RequireSysname(database, nameof(database));
        ArgumentException.ThrowIfNullOrEmpty(stagingPath);
        if (stagingPath.Length > 4000 || stagingPath.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("Invalid staging path.", nameof(stagingPath));
        // Ścieżka po stronie hosta SQL Server (Windows albo Linux), niezależnie od systemu, na którym budujemy polecenie.
        if (!SqlServerPath.IsAbsolute(stagingPath))
            throw new ArgumentException("Staging path must be an absolute path on the SQL Server host.", nameof(stagingPath));
        var p = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["@db"] = database,
            ["@stagingPath"] = stagingPath,
            ["@bufferCount"] = profile == BackupImpactProfile.LowImpact ? LowImpactBufferCount : DBNullValue.Instance,
            ["@maxTransfer"] = profile == BackupImpactProfile.LowImpact ? LowImpactMaxTransfer : DBNullValue.Instance,
        };
        return new SqlCommandSpec(TemplateText, p);
    }

    /// <summary>D5.7: weryfikacja kopii — tylko gdy login ma CREATE DATABASE w master (wariant „z weryfikacją”).</summary>
    public const string VerifyOnlyText = "RESTORE VERIFYONLY FROM DISK = @stagingPath WITH CHECKSUM;";

    public static SqlCommandSpec VerifyOnly(string stagingPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(stagingPath);
        if (stagingPath.Length > 4000 || stagingPath.Contains('\0', StringComparison.Ordinal) || !SqlServerPath.IsAbsolute(stagingPath))
            throw new ArgumentException("Invalid staging path.", nameof(stagingPath));
        return new SqlCommandSpec(VerifyOnlyText, new Dictionary<string, object>(StringComparer.Ordinal) { ["@stagingPath"] = stagingPath });
    }

    public static bool IsWindowsAbsolute(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        || (path.Length >= 5 && path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal));
}

/// <summary>
/// Ścieżki widziane przez SQL Server: Windows (<c>C:\…</c>, <c>\\serwer\udział\…</c>) albo Linux (<c>/var/opt/mssql/…</c> — SQL Server 2017+ na Linuksie
/// i w kontenerach). Styl wynika z korzenia, a nie z systemu Agenta — Agent na Linuksie może kopiować SQL Server na Windows i odwrotnie.
/// </summary>
public static class SqlServerPath
{
    public static bool IsPosixAbsolute(string path) =>
        path.Length >= 2 && path[0] == '/' && path[1] != '/' && !path.Contains('\\', StringComparison.Ordinal);

    public static bool IsAbsolute(string path) => MsSqlBackupCommand.IsWindowsAbsolute(path) || IsPosixAbsolute(path);

    /// <summary>Łączy korzeń z segmentami separatorem stylu korzenia (segmenty: nazwy plików/katalogów tworzone przez Kobian, bez separatorów).</summary>
    public static string Join(string root, params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(segments);
        var sep = MsSqlBackupCommand.IsWindowsAbsolute(root) ? '\\' : '/';
        var result = root.TrimEnd(sep);
        foreach (var s in segments)
        {
            if (s.Length == 0 || s.IndexOfAny(['/', '\\', '\0']) >= 0 || s is "." or "..") throw new ArgumentException("Invalid path segment.", nameof(segments));
            result += sep + s;
        }
        return result;
    }
}

/// <summary>Neutralny znacznik NULL (bez zależności od System.Data w kontrakcie).</summary>
public sealed class DBNullValue
{
    public static readonly DBNullValue Instance = new();

    private DBNullValue()
    {
    }
}
