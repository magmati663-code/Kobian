using System.Globalization;
using Kobian.Domain;

namespace Kobian.Providers.Sources.MsSql;

/// <summary>Serwer z D-01 (bez nazwy hosta/instancji w odpowiedzi — NFR-11; kreator zna je z własnego wejścia).</summary>
public sealed record MsSqlServerInfo(int Major, string Version, string Edition, bool Express, SqlSupport Support, bool Clustered, bool Hadr);

/// <summary>Baza z D-02 + uprawnienia z D-03 (per baza) + ostrzeżenia KB (D-04 TDE → KB-3301, FULL z LOG_BACKUP → KB-3105, tylko do odczytu).</summary>
public sealed record MsSqlDatabaseInfo(
    string Name,
    string RecoveryModel,
    long DataBytes,
    bool ReadOnly,
    bool Encrypted,
    int CompatibilityLevel,
    bool CanBackup,
    bool CanVerifyOnly,
    IReadOnlyList<string> Warnings);

/// <summary>Wynik discovery: serwer, bazy posortowane po nazwie, <c>Truncated</c> gdy baz było więcej niż limit, ostrzeżenia serwera.</summary>
public sealed record MsSqlDiscoveryResult(MsSqlServerInfo Server, IReadOnlyList<MsSqlDatabaseInfo> Databases, bool Truncated, IReadOnlyList<string> Warnings);

/// <summary>
/// Discovery MSSQL dla kreatora SCR-03 S1-db (BL-40 cz. 3, R2.5.2, D5.7/D5.8): wyłącznie zapytania odczytu D-01/D-02/D-03 — niczego nie zmienia na serwerze
/// (bez USE w tekście, bez CREATE/GRANT; skrypt uprawnień generuje osobno <see cref="PermissionScript"/> dla administratora SQL).
/// Limity (SEC-3.3/DoS): najwyżej <see cref="MaxDatabases"/> baz sprawdzanych per baza (D-03 to jedno zapytanie na bazę), nazwy spoza reguły sysname pomijane.
/// Brak dostępu do pojedynczej bazy (np. 916/4060 w kontekście bazy) = <c>CanBackup=false</c>, nie porażka całego discovery;
/// błędy połączenia/TLS/logowania przerywają discovery kodem KB-3xxx.
/// </summary>
public sealed class MsSqlDiscoveryService(IMsSqlSession sql)
{
    public const int MaxDatabases = 500;

    public async Task<MsSqlDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();

        // D-01: wersja (D5.8) — nieobsługiwana = porażka z KB-3401 (jak w PrepareAsync), warunkowa = ostrzeżenie.
        var s = SqlRows.Single(await Query(MsSqlQueries.D01Server, [], cancellationToken).ConfigureAwait(false));
        var major = SqlRows.ToInt(s, "major");
        var support = SqlVersionPolicy.Classify(major);
        if (support == SqlSupport.Unsupported)
            throw new KobianException(KbCode.SqlVersionUnsupported, ErrorClass.Permanent, "SQL Server version is not supported (D5.8).");
        if (support == SqlSupport.Conditional) warnings.Add(KbCode.SqlVersionUnsupported);
        var server = new MsSqlServerInfo(
            major,
            Text(SqlRows.Get(s, "version"), 32),
            Text(SqlRows.Get(s, "edition"), 128),
            SqlVersionPolicy.IsExpress(SqlRows.ToInt(s, "engine_edition")),
            support,
            SqlRows.ToInt(s, "is_clustered") == 1,
            SqlRows.ToInt(s, "is_hadr") == 1);

        // D-02: bazy użytkownika ONLINE; nazwa musi spełniać sysname (inaczej nie da się jej bezpiecznie użyć w BACKUP/QUOTENAME).
        var rows = await Query(MsSqlQueries.D02Databases, [], cancellationToken).ConfigureAwait(false);
        var named = new List<(string Name, IReadOnlyDictionary<string, object?> Row)>();
        foreach (var r in rows)
        {
            if (SqlRows.Get(r, "name") is not string n || !IsSysname(n)) continue;
            named.Add((n, r));
        }
        named.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        var truncated = named.Count > MaxDatabases;
        if (truncated) named.RemoveRange(MaxDatabases, named.Count - MaxDatabases);

        var result = new List<MsSqlDatabaseInfo>(named.Count);
        foreach (var (name, r) in named)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (canBackup, canVerify) = await PermissionsAsync(name, cancellationToken).ConfigureAwait(false);
            var recovery = Text(SqlRows.Get(r, "recovery_model_desc"), 60);
            var encrypted = SqlRows.ToInt(r, "is_encrypted") == 1;
            var dbWarnings = new List<string>();
            if (encrypted) dbWarnings.Add(KbCode.SqlTdeCertificateMissing);
            if (string.Equals(recovery, "FULL", StringComparison.OrdinalIgnoreCase)
                && string.Equals(SqlRows.Get(r, "log_reuse_wait_desc") as string, "LOG_BACKUP", StringComparison.OrdinalIgnoreCase))
                dbWarnings.Add(KbCode.SqlLogGrowing);
            if (!canBackup) dbWarnings.Add(KbCode.SqlBackupPermissionMissing);
            result.Add(new MsSqlDatabaseInfo(
                name,
                recovery,
                Math.Max(0, SqlRows.ToLong(r, "data_bytes")),
                SqlRows.ToInt(r, "is_read_only") == 1,
                encrypted,
                SqlRows.ToInt(r, "compatibility_level"),
                canBackup,
                canVerify,
                dbWarnings));
        }

        return new MsSqlDiscoveryResult(server, result, truncated, warnings);
    }

    /// <summary>D-03 w kontekście bazy (QUOTENAME po stronie serwera). Brak dostępu do bazy = brak uprawnień, nie porażka.</summary>
    private async Task<(bool CanBackup, bool CanVerify)> PermissionsAsync(string db, CancellationToken ct)
    {
        try
        {
            var rows = await sql.QueryAsync(
                new SqlCommandSpec(MsSqlQueries.D03Permissions, new Dictionary<string, object>(StringComparer.Ordinal) { ["@db"] = db }), ct).ConfigureAwait(false);
            if (rows.Count == 0) return (false, false);
            return (SqlRows.ToInt(rows[0], "can_backup_db") == 1, SqlRows.ToInt(rows[0], "can_verifyonly") == 1);
        }
        catch (MsSqlServerException e)
        {
            var (code, cls) = SqlErrorMap.MapMany(e.ErrorNumbers);
            // Połączenie zerwane / TLS: przerwij całe discovery (wynik byłby fałszywie „brak uprawnień”).
            if (cls is ErrorClass.Transient or ErrorClass.Security) throw new KobianException(code, cls, "SQL Server reported an error (" + code + ").", e);
            return (false, false);
        }
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Query(string text, Dictionary<string, object> parameters, CancellationToken ct)
    {
        try
        {
            return await sql.QueryAsync(new SqlCommandSpec(text, parameters), ct).ConfigureAwait(false);
        }
        catch (MsSqlServerException e)
        {
            var (code, cls) = SqlErrorMap.MapMany(e.ErrorNumbers);
            throw new KobianException(code, cls, "SQL Server reported an error (" + code + ").", e);
        }
    }

    private static bool IsSysname(string name)
    {
        try
        {
            TSql.RequireSysname(name, nameof(name));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Tekst z serwera przycięty i bez znaków sterujących (wartość trafia do UI/JSON).</summary>
    private static string Text(object? value, int max)
    {
        var t = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        t = new string(t.Where(c => !char.IsControl(c)).ToArray());
        return t.Length > max ? t[..max] : t;
    }
}

/// <summary>Odczyt wartości z wierszy <see cref="IMsSqlSession"/> (kolumny bez rozróżniania wielkości liter, NULL = 0/null).</summary>
internal static class SqlRows
{
    public static IReadOnlyDictionary<string, object?> Single(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        rows.Count > 0 ? rows[0] : throw new KobianException(KbCode.Internal, ErrorClass.Permanent, "SQL query returned no rows.");

    public static object? Get(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (row.TryGetValue(column, out var v)) return v;
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, column, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        }
        return null;
    }

    public static int ToInt(IReadOnlyDictionary<string, object?> row, string column)
    {
        var v = ToLong(row, column);
        return v is > int.MaxValue or < int.MinValue ? 0 : (int)v;
    }

    public static long ToLong(IReadOnlyDictionary<string, object?> row, string column)
    {
        try
        {
            return Get(row, column) switch
            {
                null => 0,
                bool b => b ? 1 : 0,
                string => 0,
                IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
                _ => 0,
            };
        }
        catch (OverflowException)
        {
            return 0;
        }
    }
}
