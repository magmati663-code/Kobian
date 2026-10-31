using System.Text.Json;
using Kobian.Providers.Sources.MsSql;
using Kobian.Providers.Sources.MsSql.SqlClient;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Sekcja <c>"database"</c> definicji zadania typu „database” (job_jobs.type, DDL planu) z silnikiem MSSQL (BL-40 cz. 2; PROWIZORYCZNIE Qb29, ADR-0018):
/// <code>
/// {"engine":"mssql","server":"srv\\SQLEXPRESS","name":"Firma","auth":"integrated","secretRef":"file:erp-sql","certificate":"C:\\ProgramData\\Kobian\\certs\\srv.cer",
///  "strict":false,"allowLocalhostUntrusted":false,"staging":"D:\\Kobian\\staging","sqlStaging":null,"lowImpact":true,"conflictWaitMinutes":30}
/// </code>
/// Bez sekretów: login i hasło SQL (auth = "sql") rozwiązywane z <c>secretRef</c> w chwili połączenia (magazyn sekretów Agenta).
/// Walidacja połączenia — <see cref="MsSqlConnectionFactory"/> (KB-0103); nazwa bazy — reguła sysname (<see cref="TSql"/>).
/// </summary>
public sealed record MsSqlSourceDefinition(
    MsSqlConnectionOptions Connection,
    string Database,
    string? SecretRef,
    string StagingRoot,
    string? SqlStagingRoot,
    BackupImpactProfile Profile,
    TimeSpan ConflictWait)
{
    public const int MaxConflictWaitMinutes = 24 * 60;

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "engine", "server", "name", "auth", "secretRef", "certificate", "strict", "allowLocalhostUntrusted", "staging", "sqlStaging", "lowImpact", "conflictWaitMinutes",
    };

    public static MsSqlSourceDefinition Parse(JsonElement e)
    {
        foreach (var p in e.EnumerateObject())
            if (!Known.Contains(p.Name)) throw JobDefinition.Invalid("Unknown field in 'database'.");
        // 1.0: tylko MSSQL (S1 = B); MySQL/PostgreSQL/SQLite = 1.1 (osobne rekordy źródeł).
        if (e.TryGetProperty("engine", out var engine) && (engine.ValueKind != JsonValueKind.String || engine.GetString() != "mssql"))
            throw JobDefinition.Invalid("Only the 'mssql' database engine is supported.");
        var server = JobDefinition.Str(e, "server");
        var name = JobDefinition.Str(e, "name");
        try
        {
            TSql.RequireSysname(name, "name");
        }
        catch (ArgumentException)
        {
            throw JobDefinition.Invalid("Invalid database name.");
        }

        var auth = e.TryGetProperty("auth", out var a) ? (a.ValueKind == JsonValueKind.String ? a.GetString() : null) : "integrated";
        var mode = auth switch
        {
            "integrated" => MsSqlAuthentication.Integrated,
            "sql" => MsSqlAuthentication.SqlLogin,
            _ => throw JobDefinition.Invalid("Field 'auth' must be 'integrated' or 'sql'."),
        };
        string? secretRef = null;
        if (e.TryGetProperty("secretRef", out var sr) && sr.ValueKind != JsonValueKind.Null)
            secretRef = sr.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sr.GetString()) ? sr.GetString() : throw JobDefinition.Invalid("Invalid 'secretRef'.");
        if (mode == MsSqlAuthentication.SqlLogin && secretRef is null) throw JobDefinition.Invalid("SQL authentication requires 'secretRef'.");
        if (mode == MsSqlAuthentication.Integrated && secretRef is not null) throw JobDefinition.Invalid("Integrated authentication takes no 'secretRef'.");

        var options = new MsSqlConnectionOptions
        {
            Server = server,
            Authentication = mode,
            // Login z magazynu sekretów; tu tylko znacznik do walidacji kształtu (MsSqlConnectionFactory wymaga nazwy dla SqlLogin).
            UserName = mode == MsSqlAuthentication.SqlLogin ? "placeholder" : null,
            ServerCertificatePath = JobDefinition.OptionalPath(e, "certificate"),
            Strict = Bool(e, "strict"),
            AllowLocalhostUntrusted = Bool(e, "allowLocalhostUntrusted"),
        };
        MsSqlConnectionFactory.Build(options); // KB-0103 przy niepoprawnym serwerze/pinie/zgodzie
        options = options with { UserName = null };

        var staging = JobDefinition.AbsolutePath(JobDefinition.Prop(e, "staging"), "staging");
        string? sqlStaging = null;
        if (e.TryGetProperty("sqlStaging", out var ss) && ss.ValueKind != JsonValueKind.Null)
        {
            sqlStaging = ss.ValueKind == JsonValueKind.String ? ss.GetString() : null;
            if (sqlStaging is null || !SqlServerPath.IsAbsolute(sqlStaging) || sqlStaging.Contains('\0', StringComparison.Ordinal))
                throw JobDefinition.Invalid("Field 'sqlStaging' must be an absolute path on the SQL Server host.");
        }

        // Bez „sqlStaging” SQL Server widzi staging Agenta pod tą samą ścieżką (Agent na hoście SQL — D5.10; Windows albo Linux).
        if (sqlStaging is null && !SqlServerPath.IsAbsolute(staging))
            throw JobDefinition.Invalid("Field 'staging' must be an absolute path unless 'sqlStaging' is given.");

        var wait = TimeSpan.FromMinutes(30);
        if (e.TryGetProperty("conflictWaitMinutes", out var w))
        {
            if (w.ValueKind != JsonValueKind.Number || !w.TryGetInt32(out var m) || m is < 0 or > MaxConflictWaitMinutes)
                throw JobDefinition.Invalid("Field 'conflictWaitMinutes' out of range.");
            wait = TimeSpan.FromMinutes(m);
        }

        return new MsSqlSourceDefinition(options, name, secretRef, staging, sqlStaging,
            Bool(e, "lowImpact") ? BackupImpactProfile.LowImpact : BackupImpactProfile.Default, wait);
    }

    /// <summary>Bez nazw serwera/bazy/ścieżek (NFR-11 — rekord trafia do logów diagnostycznych).</summary>
    public override string ToString() => $"MsSqlSourceDefinition({Connection}, profile={Profile})";

    private static bool Bool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw JobDefinition.Invalid($"Field '{name}' must be a boolean."),
        };
    }
}
