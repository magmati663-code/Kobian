using System.Globalization;
using System.Security.Cryptography;
using Kobian.Catalog.Sqlite;
using Kobian.Notifications.Events;
using Microsoft.Data.Sqlite;

namespace Kobian.Agent.Notifications;

/// <summary>Termin raportu dziennego (D9.11: 07:00 czasu lokalnego hosta, po kotwicy audytu 06:55). Zaległy po przerwie = jeden, nie wiele.</summary>
public static class DailyReportSchedule
{
    public static readonly TimeSpan LocalTime = new(7, 0, 0);

    public static DateTimeOffset LastSlotUtc(DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var day = local.TimeOfDay >= LocalTime ? local.Date : local.Date.AddDays(-1);
        var slotLocal = day + LocalTime;
        if (zone.IsInvalidTime(slotLocal)) slotLocal = slotLocal.AddHours(1);
        return new DateTimeOffset(slotLocal, zone.GetUtcOffset(slotLocal)).ToUniversalTime();
    }
}

/// <summary>Treść raportu: okno [od, do), liczniki wyników, zadania z błędem (≤ 20 identyfikatorów), martwe powiadomienia, ostatnia kotwica audytu (RD-61).</summary>
public sealed record DailyReportData(DateTimeOffset FromUtc, DateTimeOffset ToUtc, IReadOnlyDictionary<string, long> Results, IReadOnlyList<string> FailedJobIds,
    long DeadNotifications, Kobian.Agent.Audit.AuditAnchor? Anchor);

/// <summary>
/// Raport dzienny (D9.11 + RD-61; it. 112; PROWIZORYCZNIE Qb59): zdarzenie <c>pl.kobian.report.ready.v1</c> z podsumowaniem doby i ostatnią kotwicą łańcucha
/// audytu („seq:sha256hex”) — kopia kotwicy POZA katalogiem (u odbiorcy webhooka), wejście dla <c>kobian audit verify --anchor</c>. Zapis znacznika w
/// <c>core_events</c> i wpisu outboxa w JEDNEJ transakcji (idempotencja: drugi przebieg w tym samym terminie nic nie dodaje). Bez nazw plików i ścieżek (SEC-9.6);
/// tryb minimalny bez nazw zadań — raport niesie tylko identyfikatory.
/// </summary>
public sealed class DailyReportService(string catalogPath, CatalogWriter writer, string hostIdHex, TimeProvider time, TimeZoneInfo zone)
{
    public const string EventType = "pl.kobian.report.ready.v1";
    public const string Aggregate = "report:daily";
    public const int MaxFailedJobs = 20;
    /// <summary>BL-45: kanały, do których trafia raport (jedno zdarzenie, wpis outboxa na kanał). Domyślnie sam webhook.</summary>
    public IReadOnlyList<string> Channels { get; init; } = [OutboxDispatcher.WebhookChannel];

    private static readonly string[] Counted = ["Succeeded", "Warning", "Failed", "Cancelled", "Skipped", "Interrupted"];

    /// <summary>Jeden krok: raport, gdy należny (brak raportu od ostatniego terminu 07:00). Zwraca dane raportu albo null.</summary>
    public Task<DailyReportData?> TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var slot = DailyReportSchedule.LastSlotUtc(now, zone);
        return writer.ExecuteControlAsync<DailyReportData?>((c, tx) =>
        {
            using (var q = Cmd(c, tx, "SELECT COUNT(*) FROM core_events WHERE type=$t AND aggregate=$a AND at >= $s;", ("$t", EventType), ("$a", Aggregate), ("$s", OutboxDispatcher.Iso(slot))))
                if (Convert.ToInt64(q.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return null;
            var data = Collect(c, tx, slot.AddDays(-1), slot);
            var payload = Build(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), hostIdHex, now, data);
            using (var ins = Cmd(c, tx, "INSERT INTO core_events(at, type, aggregate, payload) VALUES ($at, $t, $a, $p);",
                ("$at", OutboxDispatcher.Iso(now)), ("$t", EventType), ("$a", Aggregate), ("$p", payload)))
                ins.ExecuteNonQuery();
            foreach (var ch in Channels) OutboxDispatcher.Enqueue(c, tx, ch, payload, now);
            return data;
        }, ct);
    }

    /// <summary>Podgląd raportu dla dowolnego okna — tylko odczyt (bez znacznika i outboxa).</summary>
    public DailyReportData Preview(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        return Collect(c, null, fromUtc, toUtc);
    }

    internal static DailyReportData Collect(SqliteConnection c, SqliteTransaction? tx, DateTimeOffset from, DateTimeOffset to)
    {
        var (f, t) = (OutboxDispatcher.Iso(from), OutboxDispatcher.Iso(to));
        var results = Counted.ToDictionary(s => s.ToLowerInvariant(), _ => 0L, StringComparer.Ordinal);
        using (var q = Cmd(c, tx, "SELECT state, COUNT(*) FROM run_runs WHERE finished_at >= $f AND finished_at < $t GROUP BY state;", ("$f", f), ("$t", t)))
        using (var r = q.ExecuteReader())
            while (r.Read())
                if (Counted.Contains(r.GetString(0))) results[r.GetString(0).ToLowerInvariant()] = r.GetInt64(1);
        var failed = new List<string>();
        using (var q = Cmd(c, tx, "SELECT DISTINCT job_id FROM run_runs WHERE state='Failed' AND finished_at >= $f AND finished_at < $t ORDER BY job_id LIMIT $n;",
            ("$f", f), ("$t", t), ("$n", MaxFailedJobs)))
        using (var r = q.ExecuteReader())
            while (r.Read())
                if (IsReportableJobId(r.GetString(0))) failed.Add(r.GetString(0)); // it. 120: identyfikator spoza formatu (wiersz wstawiony poza Agentem) pomijany — nie zatrzymuje raportu
        long dead;
        using (var q = Cmd(c, tx, "SELECT COUNT(*) FROM core_outbox WHERE state='dead' AND next_attempt_at >= $f AND next_attempt_at < $t;", ("$f", f), ("$t", t)))
            dead = Convert.ToInt64(q.ExecuteScalar(), CultureInfo.InvariantCulture);
        Kobian.Agent.Audit.AuditAnchor? anchor = null;
        using (var q = Cmd(c, tx, "SELECT anchor_seq, hash, at_utc FROM audit_anchors ORDER BY anchor_seq DESC LIMIT 1;"))
        using (var r = q.ExecuteReader())
            if (r.Read()) anchor = new Kobian.Agent.Audit.AuditAnchor(r.GetInt64(0), (byte[])r[1], r.GetString(2));
        return new DailyReportData(from, to, results, failed, dead, anchor);
    }

    /// <summary>Koperta CloudEvents; identyfikatory zadań sprawdzane jak w run.finished (ASCII, cyfry, '-').</summary>
    public static byte[] Build(string eventId, string hostIdHex, DateTimeOffset now, DailyReportData d)
    {
        ArgumentNullException.ThrowIfNull(d);
        foreach (var id in d.FailedJobIds)
            if (!IsReportableJobId(id)) throw new ArgumentException("Invalid job id.", nameof(d));
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "daily",
            ["fromUtc"] = d.FromUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["toUtc"] = d.ToUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["results"] = d.Results.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal),
            ["failedJobIds"] = string.Join(',', d.FailedJobIds),
            ["deadNotifications"] = d.DeadNotifications,
            ["auditAnchor"] = d.Anchor is null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["text"] = d.Anchor.Text, ["atUtc"] = d.Anchor.AtUtc,
            },
        };
        return CloudEvent.Serialize(eventId, hostIdHex, EventType, now, Aggregate, data, minimal: true);
    }

    /// <summary>Identyfikator zadania do raportu: 1–64 znaki ASCII (litery, cyfry, '-') — jak w run.finished.</summary>
    public static bool IsReportableJobId(string id) =>
        id is { Length: > 0 and <= 64 } && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

    /// <summary>Pętla co minutę do zatrzymania hosta; błąd katalogu nie zatrzymuje usługi.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        try
        {
            do
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException or ArgumentException)
                {
                    // następna próba za minutę (it. 120: także ArgumentException z danych katalogu — pętla raportu nie może zginąć po cichu)
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }
}
