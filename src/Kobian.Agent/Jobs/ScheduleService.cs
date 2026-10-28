using Kobian.Catalog.Sqlite;
using Kobian.Domain;

namespace Kobian.Agent.Jobs;

/// <summary>Wyzwalacz cron zadania w widoku API (RD-27).</summary>
public sealed record ScheduleEntry(string Job, string TriggerId, string Cron, string TimeZone, bool Enabled);

/// <summary>Wynik zmiany harmonogramu: <c>Code</c> = kod KB przy porażce, inaczej <c>TriggerId</c>.</summary>
public sealed record ScheduleResult(string? Code, string? TriggerId);

/// <summary>
/// Zarządzanie harmonogramem zadań przez API Agenta (RD-27, US-03, AC-03.3): jeden wyzwalacz cron na zadanie (PROWIZORYCZNIE — wiele wyzwalaczy na zadanie, okna czasowe i wyzwalacze
/// zdarzeniowe później). Id wyzwalacza = <c>cron-&lt;job_id&gt;</c>, więc ponowne ustawienie zastępuje wyrażenie w miejscu. Walidacja (składnia, strefa zadania, ≥ 5 min między wystąpieniami)
/// następuje przed zapisem; działający <see cref="CatalogScheduler"/> podchwytuje zmianę w kolejnym obrocie pętli.
/// </summary>
public sealed class ScheduleService(string catalogPath, CatalogWriter writer)
{
    public const int MaxCronLength = 256;
    public const int MaxJobNameLength = 200;

    public static string TriggerIdFor(string jobId) => "cron-" + jobId;

    /// <summary>Ustawia (lub zastępuje) harmonogram cron zadania i włącza wyzwalacz. Błędy → kod KB (KB-0104 brak zadania, KB-0201 zły harmonogram).</summary>
    public async Task<ScheduleResult> SetAsync(string job, string cron, CancellationToken ct)
    {
        if (!ValidJobName(job)) return new ScheduleResult(KbCode.JobNotFound, null);
        if (string.IsNullOrWhiteSpace(cron) || cron.Length > MaxCronLength) return new ScheduleResult(KbCode.ScheduleInvalid, null);
        var found = FindJob(job);
        if (found is null) return new ScheduleResult(KbCode.JobNotFound, null);
        var triggerId = TriggerIdFor(found.Value.JobId);
        try
        {
            await CatalogSchedulerStore.UpsertCronTriggerAsync(writer, triggerId, found.Value.JobId, cron.Trim(), found.Value.TimeZone, ct).ConfigureAwait(false);
        }
        catch (KobianException ex)
        {
            return new ScheduleResult(ex.Code, null);
        }
        return new ScheduleResult(null, triggerId);
    }

    /// <summary>Wyłącza harmonogram zadania (idempotentnie; zadanie nadal można uruchomić ręcznie). Brak zadania → KB-0104.</summary>
    public async Task<ScheduleResult> ClearAsync(string job, CancellationToken ct)
    {
        if (!ValidJobName(job)) return new ScheduleResult(KbCode.JobNotFound, null);
        var found = FindJob(job);
        if (found is null) return new ScheduleResult(KbCode.JobNotFound, null);
        var triggerId = TriggerIdFor(found.Value.JobId);
        await CatalogSchedulerStore.DisableTriggerAsync(writer, triggerId, ct).ConfigureAwait(false);
        return new ScheduleResult(null, triggerId);
    }

    /// <summary>Włącza zadanie (zapis kreatora — UpsertJob). Brak zadania → KB-0104.</summary>
    public async Task<string?> EnableJobAsync(string job, CancellationToken ct)
    {
        if (!ValidJobName(job)) return KbCode.JobNotFound;
        var n = await writer.ExecuteControlAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE job_jobs SET enabled = 1, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE name = $n;";
            cmd.Parameters.AddWithValue("$n", job);
            return cmd.ExecuteNonQuery();
        }, ct).ConfigureAwait(false);
        return n == 0 ? KbCode.JobNotFound : null;
    }

    /// <summary>
    /// Zapis polityki retencji w definicji v1 istniejącego zadania (S5 kreatora po utworzeniu repozytorium): odczyt → walidacja całej definicji → zastąpienie sekcji
    /// „retention” → zapis z nowym skrótem, w jednej transakcji. Brak zadania → KB-0104; definicja nieczytelna → KB-0103.
    /// </summary>
    public async Task<string?> SetRetentionAsync(string job, Kobian.Application.Jobs.RetentionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!ValidJobName(job)) return KbCode.JobNotFound;
        return await writer.ExecuteControlAsync<string?>((c, tx) =>
        {
            byte[] def;
            string type;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT type, definition FROM job_jobs WHERE name = $n;";
                q.Parameters.AddWithValue("$n", job);
                using var rd = q.ExecuteReader();
                if (!rd.Read()) return KbCode.JobNotFound;
                type = rd.GetString(0);
                def = (byte[])rd[1];
            }
            byte[] updated;
            try
            {
                _ = JobDefinition.Parse(type, def); // definicja musi być poprawna przed i po zmianie
                updated = JobDefinition.WithRetention(def, policy);
                _ = JobDefinition.Parse(type, updated);
            }
            catch (KobianException ex)
            {
                return ex.Code;
            }
            using var u = c.CreateCommand();
            u.Transaction = tx;
            u.CommandText = "UPDATE job_jobs SET definition = $d, definition_sha256 = $h, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE name = $n;";
            u.Parameters.AddWithValue("$d", updated);
            u.Parameters.AddWithValue("$h", System.Security.Cryptography.SHA256.HashData(updated));
            u.Parameters.AddWithValue("$n", job);
            u.ExecuteNonQuery();
            return null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Lista wyzwalaczy cron (opcjonalnie jednego zadania), także wyłączonych; wyzwalacz z uszkodzoną specyfikacją pokazuje wyrażenie „?”.</summary>
    public static IReadOnlyList<ScheduleEntry> List(string catalogPath, string? job)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        var list = new List<ScheduleEntry>();
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT j.name, t.trigger_id, t.spec, j.time_zone, t.enabled
            FROM sched_triggers t JOIN job_jobs j ON j.job_id = t.job_id
            WHERE t.kind = 'cron' AND ($job IS NULL OR j.name = $job)
            ORDER BY j.name, t.trigger_id LIMIT 1000;
            """;
        cmd.Parameters.AddWithValue("$job", (object?)job ?? DBNull.Value);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string cron;
            try
            {
                cron = CatalogScheduler.ParseCron((byte[])rd[2]);
            }
            catch (KobianException)
            {
                cron = "?";
            }
            list.Add(new ScheduleEntry(rd.GetString(0), rd.GetString(1), cron, rd.GetString(3), rd.GetInt64(4) != 0));
        }
        return list;
    }

    private static bool ValidJobName(string job) => !string.IsNullOrWhiteSpace(job) && job.Length <= MaxJobNameLength;

    private (string JobId, string TimeZone)? FindJob(string name)
    {
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT job_id, time_zone FROM job_jobs WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.GetString(0), rd.GetString(1)) : null;
    }
}
