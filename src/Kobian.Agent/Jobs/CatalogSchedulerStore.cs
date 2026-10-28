using System.Globalization;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Scheduler.Runs;
using Kobian.Scheduler.Timing;
using Microsoft.Data.Sqlite;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Magazyn schedulera na katalogu SQLite (D7.7, D7.9): <see cref="ISchedulerStore"/> dla <see cref="SchedulerCore"/>.
/// „Ostatnio obsłużone” = MAX(scheduled_for_utc) z run_runs wyzwalacza (źródło prawdy po restarcie — brak osobnego licznika, który mógłby się rozjechać);
/// duplikat wystąpienia odrzuca unikalny indeks run_occurrence (KB-4901). Zapisy przez <see cref="CatalogWriter"/> (D2.14 pkt 1).
/// Metody portu są synchroniczne (SchedulerCore bez I/O async) — wołać z wątku pętli, nigdy z kontekstu synchronizacji.
/// </summary>
public sealed class CatalogSchedulerStore : ISchedulerStore
{
    private readonly string _catalogPath;
    private readonly CatalogWriter _writer;
    private readonly IReadOnlyDictionary<string, string> _jobByTrigger;

    public CatalogSchedulerStore(string catalogPath, CatalogWriter writer, IReadOnlyDictionary<string, string> jobByTrigger)
    {
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _jobByTrigger = jobByTrigger ?? throw new ArgumentNullException(nameof(jobByTrigger));
    }

    public DateTimeOffset? LastHandled(string triggerId)
    {
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(scheduled_for_utc) FROM run_runs WHERE trigger_id=$t AND cause IN ('schedule','misfire');";
        cmd.Parameters.AddWithValue("$t", triggerId);
        return cmd.ExecuteScalar() is string s ? ParseIso(s) : null;
    }

    public bool TryInsertRun(ScheduledRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!_jobByTrigger.TryGetValue(run.TriggerId, out var jobId)) return false;
        var runId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var queued = Iso(run.CreatedAtUtc);
        var finished = run.State == RunState.Missed ? queued : null;
        return _writer.ExecuteControlAsync((c, tx) =>
        {
            try
            {
                Exec(c, tx, "INSERT INTO run_runs(run_id,job_id,trigger_id,scheduled_for_utc,cause,state,attempt,queued_at,finished_at,kb_code) VALUES ($i,$j,$t,$sf,$c,$s,0,$q,$f,NULL);",
                    ("$i", runId), ("$j", jobId), ("$t", run.TriggerId), ("$sf", run.ScheduledForUtc is { } d ? Iso(d) : null), ("$c", run.Cause),
                    ("$s", run.State.ToString()), ("$q", queued), ("$f", finished));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return false; // run_occurrence: to samo wystąpienie już istnieje (KB-4901)
            }
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc) VALUES ($i,0,NULL,$s,$t);", ("$i", runId), ("$s", run.State.ToString()), ("$t", queued));
            return true;
        }).GetAwaiter().GetResult();
    }

    public void SetLastHandled(string triggerId, DateTimeOffset utc)
    {
        // Celowo puste: LastHandled wynika z run_runs (wiersz wstawiony w TryInsertRun), więc nie ma drugiego miejsca prawdy.
    }

    /// <summary>Dodaje lub zmienia wyzwalacz cron zadania (spec: <c>{"cron":"…"}</c>; strefa i misfire są w job_jobs). Walidacja wyrażenia przed zapisem (AC-03.3).</summary>
    public static Task<int> UpsertCronTriggerAsync(CatalogWriter writer, string triggerId, string jobId, string cron, string timeZone, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var schedule = CronSchedule.Parse(cron, timeZone); // KB-0201 przy błędzie
        if (!schedule.RespectsMinimumInterval(DateTimeOffset.UtcNow)) throw new KobianException(KbCode.ScheduleInvalid, ErrorClass.Permanent, "Schedule fires more often than every 5 minutes.");
        var spec = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["cron"] = cron });
        return writer.ExecuteControlAsync((c, tx) =>
        {
            Exec(c, tx, "INSERT INTO sched_triggers(trigger_id,job_id,kind,spec,enabled) VALUES ($t,$j,'cron',$s,1) ON CONFLICT(trigger_id) DO UPDATE SET job_id=$j, spec=$s, enabled=1;",
                ("$t", triggerId), ("$j", jobId), ("$s", spec));
            return 0;
        }, ct);
    }

    /// <summary>Wyłącza wyzwalacz (enabled=0; wiersz zostaje — historia run_runs nie traci odwołania). Idempotentne; zwraca liczbę zmienionych wierszy.</summary>
    public static Task<int> DisableTriggerAsync(CatalogWriter writer, string triggerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return writer.ExecuteControlAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE sched_triggers SET enabled=0 WHERE trigger_id=$t AND enabled<>0;";
            cmd.Parameters.AddWithValue("$t", triggerId);
            return cmd.ExecuteNonQuery();
        }, ct);
    }

    internal static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseIso(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
