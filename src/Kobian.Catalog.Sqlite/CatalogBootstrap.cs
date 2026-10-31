using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Kobian.Catalog.Sqlite;

/// <summary>Tryby pracy katalogu (D2.14).</summary>
public enum CatalogMode
{
    Normal,
    ReadOnly,
    Emergency,
}

/// <summary>
/// Otwieranie i migracje katalogu (D2.11, D2.14): utwardzenie połączenia, kontrola wersji biblioteki SQLite (≥ 3.53.2 — CVE-2026-11822),
/// <c>quick_check</c>, migracje „tylko do przodu” w jednej transakcji, kopia <c>VACUUM INTO</c> przed migracją istniejącej bazy,
/// schemat nowszy niż binarka → tryb tylko do odczytu.
/// </summary>
public static partial class CatalogBootstrap
{
    public static readonly Version MinSqliteVersion = new(3, 53, 2);

    /// <summary>Kolejność modułów wynika z kluczy obcych.</summary>
    public static readonly string[] ModuleOrder = ["core", "job", "sched", "run", "repo", "snap", "alert"];

    private static int _initialized;

    public static void InitializeProvider()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0) SQLitePCL.Batteries_V2.Init();
    }

    /// <summary>Otwiera połączenie z utwardzeniem. Ścieżka musi wskazywać plik katalogu tej instalacji (D2.14 pkt 3).</summary>
    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        InitializeProvider();
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();
        var c = new SqliteConnection(cs);
        try
        {
            c.Open();
            // SQLITE_DBCONFIG_DEFENSIVE (1010) — blokuje m.in. writable_schema i bezpośrednie uszkadzanie pliku przez SQL.
            var rc = SQLitePCL.raw.sqlite3_db_config(c.Handle, 1010, 1, out int _);
            if (rc != SQLitePCL.raw.SQLITE_OK) throw new InvalidOperationException("Cannot enable SQLITE_DBCONFIG_DEFENSIVE.");
            Exec(c, "PRAGMA trusted_schema = OFF;");
            Exec(c, "PRAGMA cell_size_check = ON;");
            if (!readOnly)
            {
                Exec(c, "PRAGMA journal_mode = WAL;");
                Exec(c, "PRAGMA synchronous = FULL;");
                // RD-74 (it. 117): usunięte wiersze (nazwy plików z dziennika odtwarzania, treść powiadomień — RD-69) nadpisywane zerami, a nie zostawiane w wolnych stronach.
                Exec(c, "PRAGMA secure_delete = ON;");
            }
        }
        catch
        {
            c.Dispose(); // plik nie-bazy / uszkodzony: bez zwolnienia uchwytu Windows nie pozwoli go przenieść (odtworzenie z kopii)
            throw;
        }
        return c;
    }

    public static Version SqliteVersion(SqliteConnection c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Version.Parse(Scalar<string>(c, "SELECT sqlite_version();"));
    }

    /// <summary>Plik automatycznej kopii katalogu obok bazy (<c>catalog.db.auto.bak</c>) — wystarczy do odtworzenia po uszkodzeniu pliku bazy.</summary>
    public static string AutoBackupPath(string catalogPath) => catalogPath + ".auto.bak";

    /// <summary>
    /// Kopia spójna (<c>VACUUM INTO</c>) do pliku tymczasowego, kontrola <c>quick_check</c>, potem atomowa podmiana <see cref="AutoBackupPath"/>.
    /// Nie blokuje zapisów (czyta w trybie tylko do odczytu). Zwraca false, gdy baza źródłowa jest uszkodzona (kopia NIE zastępuje dobrej kopii uszkodzoną).
    /// </summary>
    public static bool TryBackup(string path)
    {
        var tmp = AutoBackupPath(path) + ".tmp";
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var c = Open(path, readOnly: true))
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "VACUUM INTO $p;";
                cmd.Parameters.AddWithValue("$p", tmp);
                cmd.ExecuteNonQuery();
            }
            if (!IsHealthy(tmp)) return false;
            File.Move(tmp, AutoBackupPath(path), overwrite: true);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { /* następny przebieg posprząta */ }
        }
    }

    /// <summary>Czy plik jest czytelną bazą SQLite z poprawnym <c>quick_check</c> (każdy błąd otwarcia/odczytu = nie).</summary>
    public static bool IsHealthy(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
            using var c = Open(path, readOnly: true);
            return string.Equals(Scalar<string>(c, "PRAGMA quick_check;"), "ok", StringComparison.Ordinal);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// Uszkodzony katalog + zdrowa kopia automatyczna → uszkodzony plik zostaje obok (<c>.corrupt-&lt;czas&gt;</c>, do analizy), a kopia staje się katalogiem.
    /// Utracone są tylko wpisy od czasu kopii (zwykle &lt; doba); migawki i dane są w repozytorium, a następna kopia planuje się od ostatniej znanej migawki.
    /// </summary>
    private static bool TryRestoreFromBackup(string path, TimeProvider time)
    {
        var backup = AutoBackupPath(path);
        if (!IsHealthy(backup)) return false;
        SqliteConnection.ClearAllPools();
        var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        if (File.Exists(path)) File.Move(path, path + ".corrupt-" + stamp, overwrite: true);
        foreach (var side in new[] { "-wal", "-shm" })
            if (File.Exists(path + side)) File.Move(path + side, path + ".corrupt-" + stamp + side, overwrite: true);
        File.Copy(backup, path, overwrite: false);
        return true;
    }

    /// <summary>Start katalogu: zwraca tryb pracy. Wersja SQLite poniżej minimum → wyjątek (usługa nie startuje — D2.14 pkt 2).</summary>
    public static CatalogMode Bootstrap(string path, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var mode = BootstrapCore(path, time);
        if (mode != CatalogMode.Emergency || !TryRestoreFromBackup(path, time)) return mode;
        return BootstrapCore(path, time); // jedna próba na kopii; jeśli i ona zawiedzie — tryb awaryjny jak dotąd
    }

    private static CatalogMode BootstrapCore(string path, TimeProvider time)
    {
        var existed = File.Exists(path) && new FileInfo(path).Length > 0;
        SqliteConnection c;
        try
        {
            c = Open(path);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) // SQLITE_CORRUPT / SQLITE_NOTADB — plik nie jest czytelną bazą
        {
            return CatalogMode.Emergency;
        }
        using var _ = c;
        if (SqliteVersion(c) < MinSqliteVersion)
            throw new InvalidOperationException($"SQLite library {SqliteVersion(c)} is below required {MinSqliteVersion}.");
        try
        {
            if (!string.Equals(Scalar<string>(c, "PRAGMA quick_check;"), "ok", StringComparison.Ordinal)) return CatalogMode.Emergency;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            return CatalogMode.Emergency;
        }

        Exec(c, "CREATE TABLE IF NOT EXISTS schema_versions (module TEXT PRIMARY KEY, version INTEGER NOT NULL, applied_at TEXT NOT NULL) STRICT;");
        var current = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT module, version FROM schema_versions;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) current[r.GetString(0)] = r.GetInt32(1);
        }
        var known = KnownMigrations();
        foreach (var (module, ver) in current)
        {
            var max = known.Where(m => m.Module == module).Select(m => m.Number).DefaultIfEmpty(0).Max();
            if (!ModuleOrder.Contains(module) || ver > max) return CatalogMode.ReadOnly; // schemat nowszy niż binarka (A14)
        }
        var pending = known.Where(m => m.Number > current.GetValueOrDefault(m.Module))
            .OrderBy(m => Array.IndexOf(ModuleOrder, m.Module)).ThenBy(m => m.Number).ToList();
        if (pending.Count == 0) return CatalogMode.Normal;

        if (existed && current.Count > 0)
        {
            var backup = path + ".pre-" + string.Join('-', current.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + k.Value.ToString(CultureInfo.InvariantCulture))) + ".db";
            if (!File.Exists(backup))
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "VACUUM INTO $p;";
                cmd.Parameters.AddWithValue("$p", backup);
                cmd.ExecuteNonQuery();
            }
        }
        using var tx = c.BeginTransaction();
        foreach (var m in pending)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = m.Sql;
                cmd.ExecuteNonQuery();
            }
            using (var up = c.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = "INSERT INTO schema_versions(module, version, applied_at) VALUES ($m, $v, $t) ON CONFLICT(module) DO UPDATE SET version = excluded.version, applied_at = excluded.applied_at;";
                up.Parameters.AddWithValue("$m", m.Module);
                up.Parameters.AddWithValue("$v", m.Number);
                up.Parameters.AddWithValue("$t", time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                up.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return CatalogMode.Normal;
    }

    internal sealed record Migration(string Module, int Number, string Sql);

    [GeneratedRegex(@"\.Migrations\.(?<m>[a-z]+)\.(?<n>\d{4})_[a-z0-9_]+\.sql$")]
    private static partial Regex ResourceName();

    internal static IReadOnlyList<Migration> KnownMigrations()
    {
        var asm = Assembly.GetExecutingAssembly();
        var list = new List<Migration>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            var m = ResourceName().Match(name);
            if (!m.Success) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var rd = new StreamReader(s);
            list.Add(new Migration(m.Groups["m"].Value, int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture), rd.ReadToEnd()));
        }
        return list;
    }

    /// <summary>
    /// Schemat oczekiwany po jednej migracji (RD-51): migracja wykonana w pustej bazie w pamięci → (typ, nazwa) → SQL z sqlite_schema.
    /// Porównanie z katalogiem wykrywa odbudowaną tabelę, usunięty/dodany wyzwalacz albo zdjęte CHECK. Obiekty bez SQL (autoindeksy) pominięte.
    /// </summary>
    public static IReadOnlyDictionary<string, string> MigrationSchema(string module, int number)
    {
        var m = KnownMigrations().SingleOrDefault(x => x.Module == module && x.Number == number)
            ?? throw new ArgumentException("Unknown migration.", nameof(module));
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        Exec(c, m.Sql);
        return ReadSchema(c, null);
    }

    /// <summary>Obiekty schematu (klucz „typ:nazwa”, wartość = SQL); <paramref name="tables"/> = filtr po tbl_name (null = wszystkie).</summary>
    public static IReadOnlyDictionary<string, string> ReadSchema(SqliteConnection c, IReadOnlyCollection<string>? tables)
    {
        ArgumentNullException.ThrowIfNull(c);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE sql IS NOT NULL;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (tables is not null && !tables.Contains(r.GetString(2))) continue;
            result[r.GetString(0) + ":" + r.GetString(1)] = r.GetString(3);
        }
        return result;
    }

    internal static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static T Scalar<T>(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
}
