using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Scheduler.Timing;

namespace Kobian.Agent.Jobs;

/// <summary>Szkic zadania z kreatora (bez sekretów). <c>Cron</c> = null → zadanie ręczne. <c>Database</c> ≠ null → zadanie „database” (bez <c>Sources</c>).</summary>
public sealed record JobDraft(string Name, IReadOnlyList<string> Sources, DestinationRequest Destination, string? Cron, DatabaseDraft? Database = null);

/// <summary>Źródło bazodanowe z kreatora S1-db (bez sekretów): jak <see cref="DatabaseDiscoveryRequest"/> + nazwa bazy.</summary>
public sealed record DatabaseDraft(DatabaseDiscoveryRequest Server, string Name);

/// <summary>Problem walidacji: pole („name”, „sources”, „destination”, „schedule”) + kod KB — bez wartości pola (NFR-11).</summary>
public sealed record FieldProblem(string Field, string Code);

/// <summary>
/// Walidacja szkicu zadania po stronie Agenta (UpsertJob validate_only — BL-60, D6.7): reguły z jednego miejsca dla UI i CLI.
/// Sprawdza tylko to, co Agent widzi na swoim koncie (istnienie i czytelność źródła), bez łączenia z celem (to robi ProbeDestination).
/// </summary>
public sealed class JobDraftValidator(string catalogPath, DestinationProbeService destinations, TimeProvider time, DatabaseDiscoveryService? databases = null)
{
    public const int MaxNameLength = ScheduleService.MaxJobNameLength;

    /// <summary>
    /// PROWIZORYCZNIE Qb23: strefa nowego zadania = strefa systemu Agenta (harmonogram „21:00” z kreatora oznacza czas lokalny serwera).
    /// Alternatywa: strefa przekazana z UI. Koszt zmiany: mały.
    /// </summary>
    public static string DefaultTimeZone => TimeZoneInfo.Local.Id;

    public IReadOnlyList<FieldProblem> Validate(JobDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var problems = new List<FieldProblem>();
        if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Length > MaxNameLength || draft.Name.Any(char.IsControl) || draft.Name.Trim() != draft.Name)
            problems.Add(new("name", KbCode.JobDefinitionInvalid));
        else if (JobExists(draft.Name))
            problems.Add(new("name", KbCode.JobNameTaken));

        string? source = null;
        if (draft.Database is not null)
        {
            if (draft.Sources is { Count: > 0 }) problems.Add(new("sources", KbCode.JobDefinitionInvalid)); // zadanie „database” nie ma „sources” (ADR-0018)
            if (ResolveDatabase(draft.Database).Code is { } dbc) problems.Add(new("database", dbc));
        }
        else if (draft.Sources is null || draft.Sources.Count == 0)
            problems.Add(new("sources", KbCode.SourcesNotReadable));
        else if (draft.Sources.Count > 1)
            problems.Add(new("sources", KbCode.JobDefinitionInvalid)); // definicja v1: dokładnie jedno źródło (ADR-0005)
        else if (!ReadableDirectory(draft.Sources[0]))
            problems.Add(new("sources", KbCode.SourcesNotReadable));
        else
            source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(draft.Sources[0]));

        if (destinations.ValidateShape(draft.Destination) is { } dc)
            problems.Add(new("destination", dc));
        else if (JobDestination.Validate(draft.Destination) is { } jc)
            problems.Add(new("destination", jc)); // reguły definicji zadania (BL-73 cz. 2, ADR-0026): zdalny cel wymaga poświadczenia, SFTP — przypiętego klucza
        else if (draft.Destination.Scheme == "local" && source is not null && Overlaps(source, Path.TrimEndingDirectorySeparator(Path.GetFullPath(draft.Destination.Root))))
            problems.Add(new("destination", KbCode.SourcesOverlap)); // kopia do wnętrza źródła (lub odwrotnie) = rekurencja i utrata sensu kopii

        if (draft.Cron is not null && !ValidCron(draft.Cron))
            problems.Add(new("schedule", KbCode.ScheduleInvalid));
        return problems;
    }

    /// <summary>
    /// Kształt źródła bazodanowego (bez łączenia z serwerem): opcje połączenia jak w discovery (pin = plik w &lt;stan&gt;/certs, KB-0103) + nazwa bazy (sysname).
    /// Agent bez obsługi baz → KB-7012.
    /// </summary>
    public (string? Code, Kobian.Providers.Sources.MsSql.SqlClient.MsSqlConnectionOptions? Options) ResolveDatabase(DatabaseDraft db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (databases is null) return (KbCode.OperationNotAvailable, null);
        var (code, options) = databases.Validate(db.Server);
        if (code is not null) return (code, null);
        try
        {
            Kobian.Providers.Sources.MsSql.TSql.RequireSysname(db.Name, "name");
        }
        catch (ArgumentException)
        {
            return (KbCode.JobDefinitionInvalid, null);
        }
        return (null, options);
    }

    public bool ValidCron(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron) || cron.Length > ScheduleService.MaxCronLength) return false;
        try
        {
            return CronSchedule.Parse(cron.Trim(), DefaultTimeZone).RespectsMinimumInterval(time.GetUtcNow());
        }
        catch (KobianException)
        {
            return false;
        }
    }

    private bool JobExists(string name)
    {
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM job_jobs WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool ReadableDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > DestinationProbeService.MaxRootLength || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) return false;
            using var e = info.EnumerateFileSystemInfos().GetEnumerator();
            _ = e.MoveNext(); // próba odczytu na koncie Agenta
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool Overlaps(string a, string b)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string Dir(string p) => p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
        return Dir(a).StartsWith(Dir(b), cmp) || Dir(b).StartsWith(Dir(a), cmp);
    }
}
