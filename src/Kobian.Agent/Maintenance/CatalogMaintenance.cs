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

    /// <summary>
    /// D5.10 / SEC-5.2: sprzątanie stagingu zadań „database” po awarii. Zabity Agent (albo SQL Server z blokadą pliku) zostawia w katalogu stagingu pełny, nieszyfrowany .bak
    /// (kopia: <c>&lt;runId&gt;</c>, odtworzenie: <c>restore-&lt;runId&gt;</c>); <see cref="Staging.CleanupStale"/> nie była nigdzie wywoływana. Przebieg: katalogi stagingu ze wszystkich zadań
    /// „database” (bez powtórzeń), aktywne uruchomienia (Queued/Running/Paused) pominięte, młodsze niż godzina pominięte. Błąd jednego katalogu nie przerywa pozostałych.
    /// </summary>
    /// <returns>Liczba usuniętych katalogów uruchomień.</returns>
    public Task<int> SweepStagingAsync(CancellationToken ct)
    {
        var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        using (var c = CatalogBootstrap.Open(catalogPath, readOnly: true))
        {
            using (var jobs = c.CreateCommand())
            {
                jobs.CommandText = "SELECT type, definition FROM job_jobs WHERE type = 'database';";
                using var rd = jobs.ExecuteReader();
                while (rd.Read())
                {
                    try
                    {
                        var def = Kobian.Agent.Jobs.JobDefinition.Parse(rd.GetString(0), (byte[])rd[1]);
                        if (def.MsSql is { StagingRoot.Length: > 0 } db && Path.IsPathFullyQualified(db.StagingRoot)) roots.Add(Path.GetFullPath(db.StagingRoot));
                    }
                    catch (Kobian.Domain.KobianException)
                    {
                        // definicja nieczytelna dla tej wersji — jej stagingu nie ruszamy
                    }
                }
            }
            using var runs = c.CreateCommand();
            runs.CommandText = "SELECT run_id FROM run_runs WHERE state IN ('Queued','Running','Paused');";
            using var rr = runs.ExecuteReader();
            while (rr.Read()) active.Add(rr.GetString(0));
        }
        var removed = 0;
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                removed += Kobian.Providers.Sources.MsSql.Staging.CleanupStale(root, active, time.GetUtcNow()).Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // plik .bak zablokowany (SQL Server jeszcze pisze) albo brak uprawnień — następny przebieg za godzinę
            }
        }
        return Task.FromResult(removed);
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

    /// <summary>Kopia katalogu (<see cref="CatalogBootstrap.TryBackup"/>) nie rzadziej niż raz na dobę, liczona z czasu pliku kopii (przetrwa restarty usługi).</summary>
    internal void CatalogBackupIfDue()
    {
        try
        {
            var bak = CatalogBootstrap.AutoBackupPath(catalogPath);
            if (File.Exists(bak) && time.GetUtcNow() - new DateTimeOffset(File.GetLastWriteTimeUtc(bak), TimeSpan.Zero) < TimeSpan.FromDays(1)) return;
            CatalogBootstrap.TryBackup(catalogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // brak miejsca / uprawnień: kopia katalogu to zabezpieczenie dodatkowe — następna próba za godzinę
        }
    }

    /// <summary>Krok pętli: przebieg, gdy od ostatniego minęła doba (pierwszy — przy starcie).</summary>
    public async Task<MaintenanceResult?> TickAsync(CancellationToken ct)
    {
        CatalogBackupIfDue();
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
                    await SweepStagingAsync(ct).ConfigureAwait(false); // co godzinę (pierwszy przebieg przy starcie: po awarii zostają pliki .bak)
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
