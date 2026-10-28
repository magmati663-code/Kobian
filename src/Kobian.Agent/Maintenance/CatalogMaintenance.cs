using System.Globalization;
using Kobian.Catalog.Sqlite;
using Microsoft.Data.Sqlite;

namespace Kobian.Agent.Maintenance;

/// <summary>Wynik jednego przebiegu sprzątania (liczby usuniętych wierszy).</summary>
public sealed record MaintenanceResult(int RestoreSessions, int RestoreFiles, int OutboxEntries);

/// <summary>
/// Sprzątanie katalogu (RD-69, RODO — minimalizacja przechowywania; it. 116; PROWIZORYCZNIE Qb61): dziennik wznowień odtwarzania <c>restore_*</c>
/// (ścieżki docelowe i nazwy plików klienta) usuwany po 30 dniach od zakończenia uruchomienia; uruchomienia w toku (Queued/Running/Paused) — nigdy.
/// Wysłane i martwe wpisy <c>core_outbox</c> (treść powiadomień) — po 30 dniach. Historia uruchomień (<c>run_runs</c>, bez nazw plików) i łańcuch audytu
/// zostają. Partie ≤ 200 sesji na transakcję pisarza (bez długiej blokady katalogu). Pętla co godzinę, przebieg raz na dobę.
/// </summary>
public sealed class CatalogMaintenance(string catalogPath, CatalogWriter writer, TimeProvider time)
{
    public static readonly TimeSpan RestoreJournalRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan OutboxRetention = TimeSpan.FromDays(30);
    public const int BatchSessions = 200;

    private DateTimeOffset? _lastRun;

    /// <summary>Jeden pełny przebieg (wszystkie partie).</summary>
    public async Task<MaintenanceResult> RunOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var restoreCutoff = Iso(now - RestoreJournalRetention);
        int sessions = 0, files = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (s, f) = await writer.ExecuteControlAsync((c, tx) => DeleteRestoreBatch(c, tx, restoreCutoff), ct).ConfigureAwait(false);
            sessions += s;
            files += f;
            if (s < BatchSessions) break;
        }
        var outboxCutoff = Iso(now - OutboxRetention);
        var outbox = await writer.ExecuteControlAsync((c, tx) =>
        {
            using var cmd = Cmd(c, tx, "DELETE FROM core_outbox WHERE state IN ('sent','dead') AND next_attempt_at < $t;", ("$t", outboxCutoff));
            return cmd.ExecuteNonQuery();
        }, ct).ConfigureAwait(false);
        _lastRun = now;
        return new MaintenanceResult(sessions, files, outbox);
    }

    private static (int Sessions, int Files) DeleteRestoreBatch(SqliteConnection c, SqliteTransaction tx, string cutoff)
    {
        var ids = new List<string>();
        using (var q = Cmd(c, tx, """
            SELECT s.run_id FROM restore_sessions s JOIN run_runs r ON r.run_id = s.run_id
            WHERE r.state NOT IN ('Queued','Running','Paused') AND COALESCE(r.finished_at, r.started_at, r.queued_at) < $t
            ORDER BY s.run_id LIMIT $n;
            """, ("$t", cutoff), ("$n", BatchSessions)))
        using (var r = q.ExecuteReader())
            while (r.Read()) ids.Add(r.GetString(0));
        var files = 0;
        foreach (var id in ids)
        {
            using (var d = Cmd(c, tx, "DELETE FROM restore_done_packs WHERE run_id=$r;", ("$r", id))) d.ExecuteNonQuery();
            using (var d = Cmd(c, tx, "DELETE FROM restore_files WHERE run_id=$r;", ("$r", id))) files += d.ExecuteNonQuery();
            using (var d = Cmd(c, tx, "DELETE FROM restore_sessions WHERE run_id=$r;", ("$r", id))) d.ExecuteNonQuery();
        }
        return (ids.Count, files);
    }

    /// <summary>Podgląd (tylko odczyt): ile sesji dziennika odtwarzania kwalifikuje się do usunięcia teraz.</summary>
    public long DueRestoreSessions()
    {
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM restore_sessions s JOIN run_runs r ON r.run_id = s.run_id
            WHERE r.state NOT IN ('Queued','Running','Paused') AND COALESCE(r.finished_at, r.started_at, r.queued_at) < $t;
            """;
        cmd.Parameters.AddWithValue("$t", Iso(time.GetUtcNow() - RestoreJournalRetention));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Krok pętli: przebieg, gdy od ostatniego minęła doba (pierwszy — przy starcie).</summary>
    public async Task<MaintenanceResult?> TickAsync(CancellationToken ct)
    {
        if (_lastRun is { } last && time.GetUtcNow() - last < TimeSpan.FromDays(1) && time.GetUtcNow() >= last) return null;
        return await RunOnceAsync(ct).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        try
        {
            do
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
                {
                    // katalog chwilowo niedostępny — następna próba za godzinę
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }
}
