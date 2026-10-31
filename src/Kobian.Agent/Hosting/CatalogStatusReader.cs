using System.Globalization;
using Kobian.Agent.Jobs;
using Kobian.Api.V1;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Scheduler.Timing;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Odczyt stanu zadań i historii uruchomień z katalogu dla API (BL-60, Qb18/ADR-0008): pola pulpitu SCR-01 bez wartości zastępczych
/// (następne uruchomienie i cykl z włączonego wyzwalacza cron, ostatnia nieuszkodzona kopia i jej poziom weryfikacji, schemat celu, „suspect”).
/// Tylko odczyt; do odpowiedzi trafiają wyłącznie nazwy zadań, stany, kody KB, liczniki i znaczniki czasu — bez ścieżek, hostów i sekretów (NFR-11).
/// </summary>
public static class CatalogStatusReader
{
    public const int DefaultRunLimit = 200;
    public const int MaxRunLimit = 1000;

    public static GetStatusResponse ReadStatus(string catalogPath, string agentVersion, DateTimeOffset nowUtc)
    {
        var response = new GetStatusResponse { AgentVersion = agentVersion };
        var worst = OverallState.Ok;
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        // „Ostatni wynik” dotyczy WYŁĄCZNIE uruchomień kopii: udany check/forget/prune/restore (to też wiersze run_runs tego zadania) nie może zasłaniać nieudanej nocnej kopii,
        // a pominięte wystąpienie (Skipped, np. KB-4103) nie jest wynikiem. Rodzaj poznajemy po powodzie przejścia (kopie go nie mają).
        cmd.CommandText = """
            SELECT j.name, j.enabled, j.type, j.definition, j.time_zone,
                   (SELECT r.state FROM run_runs r WHERE r.job_id = j.job_id AND r.finished_at IS NOT NULL AND r.state <> 'Skipped' AND NOT EXISTS (SELECT 1 FROM run_transitions x WHERE x.run_id = r.run_id AND x.reason IN ('verify','check','forget','prune','restore')) ORDER BY r.finished_at DESC, r.rowid DESC LIMIT 1),
                   (SELECT r.finished_at FROM run_runs r WHERE r.job_id = j.job_id AND r.finished_at IS NOT NULL AND r.state <> 'Skipped' AND NOT EXISTS (SELECT 1 FROM run_transitions x WHERE x.run_id = r.run_id AND x.reason IN ('verify','check','forget','prune','restore')) ORDER BY r.finished_at DESC, r.rowid DESC LIMIT 1),
                   (SELECT r.kb_code FROM run_runs r WHERE r.job_id = j.job_id AND r.finished_at IS NOT NULL AND r.state <> 'Skipped' AND NOT EXISTS (SELECT 1 FROM run_transitions x WHERE x.run_id = r.run_id AND x.reason IN ('verify','check','forget','prune','restore')) ORDER BY r.finished_at DESC, r.rowid DESC LIMIT 1),
                   (SELECT s.time_utc FROM snap_snapshots s WHERE s.job_id = j.job_id AND s.forgotten_at IS NULL AND s.suspect = 0 ORDER BY s.time_utc DESC, s.rowid DESC LIMIT 1),
                   (SELECT s.verify_level FROM snap_snapshots s WHERE s.job_id = j.job_id AND s.forgotten_at IS NULL AND s.suspect = 0 ORDER BY s.time_utc DESC, s.rowid DESC LIMIT 1),
                   (SELECT s.suspect FROM snap_snapshots s WHERE s.job_id = j.job_id AND s.forgotten_at IS NULL ORDER BY s.time_utc DESC, s.rowid DESC LIMIT 1),
                   (SELECT t.spec FROM sched_triggers t WHERE t.job_id = j.job_id AND t.kind = 'cron' AND t.enabled = 1 ORDER BY t.trigger_id LIMIT 1),
                   (SELECT r.state FROM run_runs r WHERE r.job_id = j.job_id AND r.state IN ('Running','Paused','Queued')
                     ORDER BY CASE r.state WHEN 'Running' THEN 0 WHEN 'Paused' THEN 1 ELSE 2 END, r.queued_at, r.rowid LIMIT 1),
                   v.last_completed_at, v.last_kb_code, v.last_attempt_at,
                   (SELECT MIN(s.time_utc) FROM snap_snapshots s WHERE s.job_id = j.job_id AND s.forgotten_at IS NULL)
            FROM job_jobs j LEFT JOIN sched_verify_jobs v ON v.job_id = j.job_id ORDER BY j.name LIMIT 1000;
            """;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var enabled = rd.GetInt64(1) != 0;
            var result = MapState(rd.IsDBNull(5) ? "Succeeded" : rd.GetString(5));
            var job = new JobStatus { Name = rd.GetString(0), LastResult = result, Disabled = !enabled, DestinationScheme = SchemeOf(rd.GetString(2), (byte[])rd[3]) };
            if (TryTime(rd, 6, out var last)) job.LastRunUnixSeconds = last.ToUnixTimeSeconds();
            if (!rd.IsDBNull(7)) job.LastCode = rd.GetString(7);
            if (TryTime(rd, 8, out var success))
            {
                job.LastSuccessUnixSeconds = success.ToUnixTimeSeconds();
                job.LastSuccessVerifyLevel = (int)Math.Clamp(rd.GetInt64(9), 0, 3);
            }
            job.RepositorySuspect = !rd.IsDBNull(10) && rd.GetInt64(10) != 0;
            if (!rd.IsDBNull(12)) job.ActiveState = rd.GetString(12) == "Queued" ? "Queued" : "Running"; // Paused pokazujemy jako „w toku” (BL-76)
            if (enabled && !rd.IsDBNull(11) && Timing((byte[])rd[11], rd.GetString(4), nowUtc) is { } t)
            {
                job.NextRunUnixSeconds = t.Next.ToUnixTimeSeconds();
                if (t.Interval is { } i) job.IntervalSeconds = (long)i.TotalSeconds;
            }
            // BL-26: ostatni ukończony cykl V3 i termin następnego (polityka z definicji; ta sama funkcja co pętla weryfikacji).
            if (TryTime(rd, 13, out var verified))
            {
                job.LastVerifyUnixSeconds = verified.ToUnixTimeSeconds();
                if (!rd.IsDBNull(14)) job.LastVerifyCode = rd.GetString(14);
            }
            if (enabled && VerifyPolicyOf(rd.GetString(2), (byte[])rd[3]) is { } vp
                && CatalogVerifyScheduler.DueAt(vp, TryTime(rd, 16, out var first) ? first : null, TryTime(rd, 13, out var lc) ? lc : null, TryTime(rd, 15, out var la) ? la : null,
                    TimeSpan.FromHours(1)) is { } due)
                job.NextVerifyUnixSeconds = due.ToUnixTimeSeconds();
            response.Jobs.Add(job);
            if (enabled && result > worst) worst = result; // stan ogólny tylko z zadań włączonych
            if (enabled && job.RepositorySuspect && worst < OverallState.Failed) worst = OverallState.Failed;
        }
        if (ReadHold(c) is { } h)
        {
            response.RetentionHold = h; // SEC-17.4: retencja wstrzymana — wymaga uwagi Administratora
            if (worst < OverallState.Warning) worst = OverallState.Warning;
        }
        ReadAlerts(c, response.Alerts);
        response.State = worst;
        return response;
    }

    public const int MaxAlerts = 200;

    /// <summary>BL-82: nierozwiązane alerty (alert_alerts) — bezpieczeństwo, krytyczne, ostrzeżenia; w obrębie ważności najstarsze najpierw.</summary>
    internal static void ReadAlerts(Microsoft.Data.Sqlite.SqliteConnection c, Google.Protobuf.Collections.RepeatedField<AlertInfo> into)
    {
        using (var exists = c.CreateCommand())
        {
            // Katalog w trybie tylko do odczytu sprzed migracji „alert” (schemat innego modułu nowszy niż binarka) — brak tabeli = brak alertów.
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'alert_alerts';";
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return;
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT a.code, a.severity, a.state, a.scope, a.opened_at, a.reminders, a.escalated,
                   (SELECT j.name FROM job_jobs j WHERE a.scope = 'job:' || j.job_id), a.alert_id
            FROM alert_alerts a WHERE a.state <> 'resolved'
            ORDER BY CASE a.severity WHEN 'security' THEN 0 WHEN 'critical' THEN 1 ELSE 2 END, a.opened_at, a.alert_id LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", MaxAlerts);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var code = rd.GetString(0);
            var scope = Kobian.Notifications.Events.AlertEvent.ParseScope(rd.GetString(3));
            if (scope is null) continue; // wiersz spoza formatu (wstawiony poza Agentem) — pomijany
            var info = new AlertInfo
            {
                Code = code,
                Severity = rd.GetString(1),
                State = rd.GetString(2),
                ScopeKind = scope.Value.Kind,
                Subject = scope.Value.Kind == "job" ? (rd.IsDBNull(7) ? scope.Value.Id ?? "" : rd.GetString(7)) : scope.Value.Id ?? "",
                Reminders = (int)Math.Clamp(rd.GetInt64(5), 0, int.MaxValue),
                Escalated = rd.GetInt64(6) != 0,
                AlertId = rd.GetString(8),
            };
            if (TryTime(rd, 4, out var opened)) info.OpenedUnixSeconds = opened.ToUnixTimeSeconds();
            if (KbOf(code) is { } kb) info.Kb = kb;
            into.Add(info);
        }
    }

    private static readonly Lazy<Kobian.Notifications.Alerts.Rules.AlertRuleCatalog?> Rules = new(() =>
    {
        try
        {
            return Kobian.Notifications.Alerts.Rules.AlertRuleCatalog.LoadEmbedded();
        }
        catch (FormatException)
        {
            return null;
        }
    });

    private static string? KbOf(string code) =>
        Rules.Value is { } r && r.Rules.FirstOrDefault(x => x.Code == code) is { } rule ? rule.Kb : null;

    private static RetentionHoldInfo? ReadHold(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT held_at, kb_code, delta_sec FROM core_retention_hold WHERE id = 1;";
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        var info = new RetentionHoldInfo { Code = rd.GetString(1), ClockDeltaSeconds = rd.GetInt64(2) };
        if (TryTime(rd, 0, out var at)) info.HeldAtUnixSeconds = at.ToUnixTimeSeconds();
        return info;
    }

    /// <summary>Historia uruchomień (najnowsze pierwsze). <paramref name="limit"/> poza 1–1000 → przycięty; brak → 200.</summary>
    /// <summary>
    /// SCR-09: cele z definicji zadań (job_jobs.definition → JobDefinition.Parse) — schemat + korzeń, zadania (nazwy, porządek nazw).
    /// Id = ten sam skrót co w katalogu repozytoriów („dest-” + SHA-256(schemat:korzeń)[..16]). Nieczytelna definicja → licznik, nie wyjątek.
    /// </summary>
    public static ListDestinationsReply ReadDestinations(string catalogPath)
    {
        var reply = new ListDestinationsReply();
        var map = new SortedDictionary<string, DestinationInfo>(StringComparer.Ordinal);
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, type, definition FROM job_jobs ORDER BY name;";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            Jobs.JobDefinition def;
            try
            {
                def = Jobs.JobDefinition.Parse(rd.GetString(1), (byte[])rd[2]);
            }
            catch (KobianException)
            {
                reply.UnreadableJobs++;
                continue;
            }
            var id = def.DestinationId;
            if (!map.TryGetValue(id, out var info)) map[id] = info = new DestinationInfo { Id = id, Scheme = def.DestinationScheme, Root = def.DestinationRoot };
            info.Jobs.Add(rd.GetString(0));
        }
        reply.Destinations.Add(map.Values.OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase));
        return reply;
    }

    public static ListRunsResponse ReadRuns(string catalogPath, string? job, int? limit)
    {
        var n = Math.Clamp(limit ?? DefaultRunLimit, 1, MaxRunLimit);
        var response = new ListRunsResponse();
        if (job is not null && (job.Length == 0 || job.Length > ScheduleService.MaxJobNameLength)) return response;
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT r.run_id, COALESCE(j.name, r.job_id), r.cause, r.state, r.queued_at, r.started_at, r.finished_at, r.kb_code, r.snapshot_id, r.files, r.bytes_uploaded,
                   (SELECT t.reason FROM run_transitions t WHERE t.run_id = r.run_id ORDER BY t.seq LIMIT 1)
            FROM run_runs r LEFT JOIN job_jobs j ON j.job_id = r.job_id
            WHERE ($job IS NULL OR j.name = $job)
            ORDER BY COALESCE(r.finished_at, r.started_at, r.queued_at, r.scheduled_for_utc) DESC, r.rowid DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$job", (object?)job ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", n);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) response.Runs.Add(MapRun(rd));
        return response;
    }

    private const string RunColumns = """
        r.run_id, COALESCE(j.name, r.job_id), r.cause, r.state, r.queued_at, r.started_at, r.finished_at, r.kb_code, r.snapshot_id, r.files, r.bytes_uploaded,
        (SELECT t.reason FROM run_transitions t WHERE t.run_id = r.run_id ORDER BY t.seq LIMIT 1)
        """;

    // Stała składana z innych stałych (nie z danych) — SEC-5.1 nienaruszone; kolumna 12 = bytes_read.
    private const string ReadRunSql = $"SELECT {RunColumns}, COALESCE(r.bytes_read, 0) FROM run_runs r LEFT JOIN job_jobs j ON j.job_id = r.job_id WHERE r.run_id = $id;";

    private static RunInfo MapRun(Microsoft.Data.Sqlite.SqliteDataReader rd)
    {
        var state = rd.GetString(3);
        var run = new RunInfo
        {
            RunId = rd.GetString(0),
            Job = rd.GetString(1),
            Cause = rd.GetString(2),
            State = state,
            Result = MapState(state),
            Kind = KindOf(rd.IsDBNull(11) ? null : rd.GetString(11)),
        };
        if (TryTime(rd, 4, out var q)) run.QueuedUnixSeconds = q.ToUnixTimeSeconds();
        if (TryTime(rd, 5, out var s)) run.StartedUnixSeconds = s.ToUnixTimeSeconds();
        if (TryTime(rd, 6, out var f)) run.FinishedUnixSeconds = f.ToUnixTimeSeconds();
        if (!rd.IsDBNull(7)) run.Code = rd.GetString(7);
        if (!rd.IsDBNull(8)) run.SnapshotId = rd.GetString(8);
        if (!rd.IsDBNull(9)) run.Files = rd.GetInt64(9);
        if (!rd.IsDBNull(10)) run.BytesUploaded = rd.GetInt64(10);
        return run;
    }

    /// <summary>Stany końcowe maszyny uruchomień (D7.8) — po nich WatchRun kończy strumień.</summary>
    internal static bool IsTerminal(string state) =>
        state is "Succeeded" or "Warning" or "Failed" or "Skipped" or "Missed" or "Interrupted" or "Cancelled";

    /// <summary>Jeden bieg po identyfikatorze (it. 134, WatchRun). Identyfikator spoza [0-9A-Za-z_-]{1,64} → null (bez zapytania). Zwraca też bytes_read.</summary>
    public static (RunInfo Run, long BytesRead)? ReadRun(string catalogPath, string runId)
    {
        if (runId.Length is 0 or > 64 || !runId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return null;
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = ReadRunSql;
        cmd.Parameters.AddWithValue("$id", runId);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (MapRun(rd), rd.GetInt64(12)) : null;
    }

    /// <summary>Najbliższe wystąpienie i cykl (odstęp dwóch najbliższych). Uszkodzona specyfikacja lub strefa → brak (zadanie traktowane jak ręczne, bez wyjątku).</summary>
    internal static (DateTimeOffset Next, TimeSpan? Interval)? Timing(byte[] spec, string timeZone, DateTimeOffset nowUtc)
    {
        try
        {
            var schedule = CronSchedule.Parse(CatalogScheduler.ParseCron(spec), timeZone);
            var next = schedule.Preview(nowUtc, 1);
            if (next.Count == 0) return null;
            // Cykl = NAJDŁUŻSZY odstęp w najbliższych 8 dniach: dla „dni robocze” różnica dwóch najbliższych wystąpień w weekend dawała 24 h
            // i fałszywe A03 (1,5 × interwał) w niedzielę. Reguła S3 gwarantuje ≥ 5 min, więc okno ma ≤ ok. 2300 wystąpień.
            TimeSpan? interval = null;
            var prev = next[0];
            foreach (var o in schedule.Between(next[0], nowUtc.AddDays(8)))
            {
                if (interval is null || o - prev > interval) interval = o - prev;
                prev = o;
            }
            return (next[0], interval);
        }
        catch (KobianException)
        {
            return null;
        }
    }

    /// <summary>Schemat celu z definicji v1 (tylko nazwa schematu, nigdy ścieżka/host). Nieczytelna definicja → pusty.</summary>
    internal static string SchemeOf(string jobType, byte[] definition)
    {
        try
        {
            return JobDefinition.Parse(jobType, definition).DestinationScheme;
        }
        catch (KobianException)
        {
            return string.Empty;
        }
    }

    /// <summary>Polityka V3 z definicji; nieczytelna definicja → null.</summary>
    internal static VerifyPolicy? VerifyPolicyOf(string jobType, byte[] definition)
    {
        try
        {
            return JobDefinition.Parse(jobType, definition).EffectiveVerify;
        }
        catch (KobianException)
        {
            return null;
        }
    }

    internal static string KindOf(string? firstReason) => firstReason switch
    {
        "restore" => "restore",
        "check" or "verify" => "check", // BL-26: V3 z harmonogramu (cause 'schedule', powód 'verify') to też weryfikacja — UI/CLI bez nowego rodzaju
        "forget" => "forget",
        "prune" => "prune",
        _ => "backup",
    };

    internal static OverallState MapState(string state) => state switch
    {
        "Succeeded" or "Skipped" => OverallState.Ok,
        "Warning" or "Missed" or "Interrupted" or "Cancelled" => OverallState.Warning,
        "Failed" => OverallState.Failed,
        _ => OverallState.Ok,
    };

    private static bool TryTime(Microsoft.Data.Sqlite.SqliteDataReader rd, int i, out DateTimeOffset t)
    {
        t = default;
        return !rd.IsDBNull(i) && DateTimeOffset.TryParse(rd.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t);
    }
}
