using System.Globalization;
using System.Text.Json;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Notifications.Alerts.Rules;
using Microsoft.Data.Sqlite;

namespace Kobian.Agent.Jobs;

/// <summary>Aktywne wstrzymanie retencji (SEC-17.4): kiedy, kod KB (KB-6117), obserwowany skok zegara i źródło.</summary>
public sealed record RetentionHold(DateTimeOffset HeldAtUtc, string KbCode, TimeSpan ClockDelta, string Source);

/// <summary>
/// Wstrzymanie retencji po skoku zegara (BL-25 cz. 5, SEC-17.4, A17, D9.3). Stan całego hosta w <c>core_retention_hold</c> (jeden wiersz):
/// dopóki istnieje, <see cref="CatalogRetentionService"/> nic nie zapomina (ręcznie ani automatycznie po kopii), a podgląd zachowuje wszystko (powód „paused”).
/// O wstrzymaniu decyduje reguła A17 z danych (<c>alert-rules.json</c>: &gt; 24 h w przód lub &gt; 2 min w tył) — progi nie są powielane w kodzie.
/// Ustawienie i zwolnienie zostawiają zdarzenie w <c>core_events</c> (ślad audytu; bez danych klienta — tylko czas, kod i przesunięcie).
/// Zwolnienie wyłącznie przez Administratora (RPC ReleaseRetentionHold, MethodPolicy).
/// </summary>
public sealed class RetentionHoldService
{
    public const string HeldEventType = "pl.kobian.retention.held.v1";
    public const string ReleasedEventType = "pl.kobian.retention.released.v1";

    private readonly string _catalogPath;
    private readonly CatalogWriter _writer;
    private readonly TimeProvider _time;
    private readonly AlertRule _a17;

    public RetentionHoldService(string catalogPath, CatalogWriter writer, TimeProvider time, AlertRuleCatalog? rules = null)
    {
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _a17 = (rules ?? AlertRuleCatalog.LoadEmbedded()).Get("A17");
    }

    /// <summary>Czy dany skok zegara (obserwowany − oczekiwany upływ) spełnia warunek A17, czyli wstrzymuje retencję.</summary>
    public bool IsThreat(TimeSpan clockDelta) => _a17.Condition(new AlertFacts { ClockDelta = clockDelta }, _time.GetUtcNow());

    /// <summary>Bieżące wstrzymanie albo null. Nieczytelny wiersz = traktowany jako wstrzymanie (fail-safe: lepiej nie zapominać).</summary>
    public RetentionHold? Current()
    {
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT held_at, kb_code, delta_sec, source FROM core_retention_hold WHERE id = 1;";
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        var at = DateTimeOffset.TryParse(rd.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;
        return new RetentionHold(at, rd.GetString(1), TimeSpan.FromSeconds(rd.GetInt64(2)), rd.GetString(3));
    }

    /// <summary>
    /// Zgłoszenie skoku zegara (scheduler, D7.9). Gdy spełnia A17 — wstrzymuje retencję (pierwsze wstrzymanie wygrywa) i zapisuje zdarzenie.
    /// Zwraca true, gdy wstrzymanie zostało właśnie ustawione.
    /// </summary>
    public async Task<bool> ReportClockJumpAsync(TimeSpan clockDelta, CancellationToken ct)
    {
        if (!IsThreat(clockDelta)) return false;
        var now = _time.GetUtcNow();
        var delta = (long)Math.Clamp(clockDelta.TotalSeconds, -100L * 365 * 86400, 100L * 365 * 86400);
        return await _writer.ExecuteControlAsync((c, tx) =>
        {
            var n = Exec(c, tx, "INSERT OR IGNORE INTO core_retention_hold(id,held_at,kb_code,delta_sec,source) VALUES (1,$t,$k,$d,'scheduler');",
                ("$t", Iso(now)), ("$k", KbCode.AlertClockJump), ("$d", delta));
            if (n == 1) AddEvent(c, tx, HeldEventType, now, new Dictionary<string, object?> { ["kb"] = KbCode.AlertClockJump, ["deltaSeconds"] = delta, ["source"] = "scheduler" });
            return n == 1;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Zwolnienie wstrzymania (potwierdzenie Administratora). Zwraca false, gdy nic nie było wstrzymane (idempotentne).</summary>
    public async Task<bool> ReleaseAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        return await _writer.ExecuteControlAsync((c, tx) =>
        {
            long delta = 0;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT delta_sec FROM core_retention_hold WHERE id = 1;";
                if (q.ExecuteScalar() is long d) delta = d;
                else return false;
            }
            Exec(c, tx, "DELETE FROM core_retention_hold WHERE id = 1;");
            AddEvent(c, tx, ReleasedEventType, now, new Dictionary<string, object?> { ["kb"] = KbCode.AlertClockJump, ["deltaSeconds"] = delta });
            return true;
        }, ct).ConfigureAwait(false);
    }

    private static void AddEvent(SqliteConnection c, SqliteTransaction tx, string type, DateTimeOffset at, Dictionary<string, object?> data) =>
        Exec(c, tx, "INSERT INTO core_events(at,type,aggregate,payload) VALUES ($a,$y,'host:retention',$p);",
            ("$a", Iso(at)), ("$y", type), ("$p", JsonSerializer.SerializeToUtf8Bytes(data)));

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static int Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }
}
