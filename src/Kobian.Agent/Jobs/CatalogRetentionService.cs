using System.Globalization;
using System.Security.Cryptography;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Engine.Retention;

namespace Kobian.Agent.Jobs;

/// <summary>Wynik retencji: kod KB przy błędzie, plan (powody zachowania), liczba zapomnianych. <c>DryRun</c> = nic nie zapisano. <c>Held</c> = retencja wstrzymana (SEC-17.4).</summary>
public sealed record RetentionOutcome(string? Code, RetentionPlan? Plan, int Forgotten, bool DryRun, bool HasPolicy, bool Held = false);

/// <summary>
/// Retencja zadania w Agencie (BL-25, D3.12, AC-10.1): polityka z definicji v1 → <see cref="RetentionPlanner"/> nad niezapomnianymi snapshotami zadania (strefa zadania)
/// → <b>logiczne</b> zapomnienie (<c>snap_snapshots.forgotten_at</c>) w jednej transakcji z wierszem run_runs (reason „forget”). Dane na celu NIE są usuwane —
/// to robi prune dwufazowy z poświadczeniem Maintenance (D3.7, D4.1). Brak polityki = nic nie jest zapominane.
/// Wstrzymanie po skoku zegara (<see cref="RetentionHoldService"/>, SEC-17.4): plan z <c>paused</c> (wszystko zachowane), zapis = wiersz run_runs „forget”
/// w stanie Skipped z KB-6117 i nic nie jest zapominane. Retencja automatyczna po udanej kopii (<see cref="ApplyAfterBackupAsync"/>, PROWIZORYCZNIE Qb28).
/// </summary>
public sealed class CatalogRetentionService(string catalogPath, CatalogWriter writer, TimeProvider time, RetentionHoldService? hold = null)
{
    /// <summary>
    /// Retencja automatyczna po udanej kopii zadania (PROWIZORYCZNIE Qb28; D3.12 + SEC-17.4): bez polityki nic się nie dzieje (bez wiersza run_runs);
    /// wiersz „forget” ma przyczynę <c>dependency</c> (następstwo kopii, nie osobne wystąpienie harmonogramu). Wynik kopii nie zależy od wyniku retencji.
    /// </summary>
    public Task<RetentionOutcome> ApplyAfterBackupAsync(string job, CancellationToken ct) => ApplyCoreAsync(job, dryRun: false, "dependency", ct);

    public Task<RetentionOutcome> ApplyAsync(string job, bool dryRun, CancellationToken ct) => ApplyCoreAsync(job, dryRun, "manual", ct);

    private async Task<RetentionOutcome> ApplyCoreAsync(string job, bool dryRun, string cause, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job) || job.Length > ScheduleService.MaxJobNameLength) return new RetentionOutcome(KbCode.JobNotFound, null, 0, dryRun, false);
        var row = FindJob(job);
        if (row is null) return new RetentionOutcome(KbCode.JobNotFound, null, 0, dryRun, false);
        JobDefinition def;
        TimeZoneInfo zone;
        try
        {
            def = JobDefinition.Parse(row.Value.Type, row.Value.Definition);
            zone = TimeZoneInfo.FindSystemTimeZoneById(row.Value.TimeZone);
        }
        catch (KobianException ex)
        {
            return new RetentionOutcome(ex.Code, null, 0, dryRun, false);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new RetentionOutcome(KbCode.ScheduleInvalid, null, 0, dryRun, false);
        }
        if (def.Retention is null) return new RetentionOutcome(null, null, 0, dryRun, false);

        var held = hold?.Current() is not null;
        var plan = RetentionPlanner.Plan(Snapshots(row.Value.JobId), def.Retention, time.GetUtcNow(), zone, paused: held);
        if (dryRun) return new RetentionOutcome(held ? KbCode.AlertClockJump : null, plan, 0, true, true, held);

        var now = Iso(time.GetUtcNow());
        var runId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        if (held)
        {
            // SEC-17.4: wstrzymane — ślad w historii (SCR-10 pokaże „porządkowanie kopii — pominięte, KB-6117”), zero zapomnień.
            await writer.ExecuteControlAsync((c, tx) =>
            {
                Exec(c, tx, "INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at,finished_at,kb_code) VALUES ($i,$j,$c,'Skipped',0,$t,$t,$k);",
                    ("$i", runId), ("$j", row.Value.JobId), ("$c", cause), ("$t", now), ("$k", KbCode.AlertClockJump));
                Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason,kb_code) VALUES ($i,0,NULL,'Skipped',$t,'forget',$k);",
                    ("$i", runId), ("$t", now), ("$k", KbCode.AlertClockJump));
                return 0;
            }, ct).ConfigureAwait(false);
            return new RetentionOutcome(KbCode.AlertClockJump, plan, 0, false, true, true);
        }
        var forgotten = await writer.ExecuteControlAsync((c, tx) =>
        {
            Exec(c, tx, "INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at,started_at) VALUES ($i,$j,$c,'Running',0,$t,$t);", ("$i", runId), ("$j", row.Value.JobId), ("$c", cause), ("$t", now));
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason) VALUES ($i,0,NULL,'Running',$t,'forget');", ("$i", runId), ("$t", now));
            var n = 0;
            foreach (var id in plan.Forget)
                n += Exec(c, tx, "UPDATE snap_snapshots SET forgotten_at=$t WHERE snap_id=$s AND job_id=$j AND forgotten_at IS NULL;", ("$t", now), ("$s", id), ("$j", row.Value.JobId));
            Exec(c, tx, "UPDATE run_runs SET state='Succeeded', finished_at=$t WHERE run_id=$i;", ("$t", now), ("$i", runId));
            Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason) VALUES ($i,1,'Running','Succeeded',$t,'forget');", ("$i", runId), ("$t", now));
            return n;
        }, ct).ConfigureAwait(false);
        return new RetentionOutcome(null, plan, forgotten, false, true);
    }

    private List<RetentionSnapshot> Snapshots(string jobId)
    {
        var list = new List<RetentionSnapshot>();
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT snap_id, time_utc, mode, base_full, suspect FROM snap_snapshots WHERE job_id = $j AND forgotten_at IS NULL;";
        cmd.Parameters.AddWithValue("$j", jobId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            if (!DateTimeOffset.TryParse(rd.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) continue; // nieczytelny czas = nie zapominamy (fail-safe)
            list.Add(new RetentionSnapshot(rd.GetString(0), t, rd.GetInt64(2) == 2, rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetInt64(4) != 0));
        }
        return list;
    }

    private (string JobId, string Type, byte[] Definition, string TimeZone)? FindJob(string name)
    {
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT job_id, type, definition, time_zone FROM job_jobs WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.GetString(0), rd.GetString(1), (byte[])rd[2], rd.GetString(3)) : null;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static int Exec(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }
}
