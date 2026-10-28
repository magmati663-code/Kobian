using System.Security.Cryptography;
using System.Text;
using Kobian.Catalog.Sqlite;
using Kobian.Engine.Restore;
using Microsoft.Data.Sqlite;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Dziennik wznowień odtwarzania w katalogu (D8.8, R2.8.2; it. 107; PROWIZORYCZNIE Qb56): <c>restore_sessions</c> + <c>restore_files</c>.
/// Plan = SHA-256(snapshot ‖ cel ‖ wybór) — wznowienie TYLKO przy identycznym planie. Poprzednie niedokończone sesje tego planu (stan ≠ Done/Superseded)
/// dają zbiory „zweryfikowane” i „rozpoczęte”; po przejęciu dostają stan Superseded. Zapisy buforowane (co <see cref="FlushEvery"/> zmian i na końcu) —
/// po awarii ostatnia partia może zginąć: pliki docelowe o identycznej treści i tak są rozpoznawane (RestoreExecutor.SameContent), pliki robocze
/// z przyrostkiem są nasze. Ścieżki w dzienniku = względne w kopii (dane klienta — katalog z ACL, D2.6).
/// </summary>
public sealed class CatalogRestoreJournal : IRestoreJournal
{
    public const int FlushEvery = 256;

    private readonly CatalogWriter _writer;
    private readonly string _runId;
    private readonly HashSet<string> _restored;
    private readonly HashSet<string> _started;
    private readonly Dictionary<string, long> _fileNo = new(StringComparer.Ordinal);
    private readonly List<(string Sql, (string, object?)[] Args)> _pending = [];

    private CatalogRestoreJournal(CatalogWriter writer, string runId, HashSet<string> restored, HashSet<string> started)
    {
        _writer = writer;
        _runId = runId;
        _restored = restored;
        _started = started;
    }

    /// <summary>Pliki zweryfikowane w przejętych sesjach (do raportu).</summary>
    public int PreviouslyRestored => _restored.Count;

    /// <summary>Liczba przejętych sesji tego planu.</summary>
    public int ResumedSessions { get; private set; }

    public static byte[] PlanHash(string snapId, string target, string? filter, IReadOnlyCollection<string>? exact)
    {
        var sb = new StringBuilder();
        sb.Append(snapId).Append('\n').Append(Path.TrimEndingDirectorySeparator(Path.GetFullPath(target))).Append('\n');
        if (exact is not null) foreach (var p in exact.Order(StringComparer.Ordinal)) sb.Append("exact:").Append(p).Append('\n');
        else sb.Append("filter:").Append(filter ?? "").Append('\n');
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>Otwiera sesję dla <paramref name="runId"/> (wiersz run_runs już istnieje) i przejmuje niedokończone sesje tego samego planu.</summary>
    public static Task<CatalogRestoreJournal> OpenAsync(CatalogWriter writer, string runId, string snapId, string target, string callerSid, byte[] planHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return writer.ExecuteControlAsync((c, tx) =>
        {
            var restored = new HashSet<string>(StringComparer.Ordinal);
            var started = new HashSet<string>(StringComparer.Ordinal);
            var previous = new List<string>();
            using (var q = Cmd(c, tx, "SELECT run_id FROM restore_sessions WHERE plan_sha256 = $p AND state NOT IN ('Done','Superseded');", ("$p", planHash)))
            using (var r = q.ExecuteReader())
                while (r.Read()) previous.Add(r.GetString(0));
            foreach (var prev in previous)
            {
                using (var q = Cmd(c, tx, "SELECT dst, state FROM restore_files WHERE run_id = $r;", ("$r", prev)))
                using (var r = q.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var (dst, state) = (r.GetString(0), r.GetString(1));
                        if (state == "verified") restored.Add(dst);
                        else if (state is "pending" or "partial") started.Add(dst);
                    }
                }
                using var u = Cmd(c, tx, "UPDATE restore_sessions SET state='Superseded' WHERE run_id = $r;", ("$r", prev));
                u.ExecuteNonQuery();
            }
            started.ExceptWith(restored);
            using (var ins = Cmd(c, tx, "INSERT INTO restore_sessions(run_id,snap_id,target,caller_sid,conflict,restore_acl,plan_sha256,state) VALUES ($r,$s,$t,$c,'skip',0,$p,'Running');",
                ("$r", runId), ("$s", snapId), ("$t", target), ("$c", callerSid), ("$p", planHash)))
                ins.ExecuteNonQuery();
            return new CatalogRestoreJournal(writer, runId, restored, started) { ResumedSessions = previous.Count };
        }, ct);
    }

    /// <summary>
    /// RD-68 (it. 108): podgląd niedokończonych sesji planu BEZ ich przejmowania (PlanRestore) — dziennik tylko do odczytu.
    /// </summary>
    public static IRestoreJournal Peek(string catalogPath, byte[] planHash)
    {
        var restored = new HashSet<string>(StringComparer.Ordinal);
        var started = new HashSet<string>(StringComparer.Ordinal);
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT f.dst, f.state FROM restore_files f JOIN restore_sessions s ON s.run_id = f.run_id
            WHERE s.plan_sha256 = $p AND s.state NOT IN ('Done','Superseded');
            """;
        q.Parameters.AddWithValue("$p", planHash);
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            if (r.GetString(1) == "verified") restored.Add(r.GetString(0));
            else if (r.GetString(1) is "pending" or "partial") started.Add(r.GetString(0));
        }
        started.ExceptWith(restored);
        return new ReadOnlyJournal(restored, started);
    }

    private sealed class ReadOnlyJournal(HashSet<string> restored, HashSet<string> started) : IRestoreJournal
    {
        public bool IsRestored(string rel) => restored.Contains(rel);

        public bool WasStarted(string rel) => started.Contains(rel);

        public ValueTask StartedAsync(string rel, long size, int chunks) => throw new InvalidOperationException("Read-only journal.");

        public ValueTask FinishedAsync(string rel, RestoreFileState state) => throw new InvalidOperationException("Read-only journal.");
    }

    public bool IsRestored(string rel) => _restored.Contains(rel);

    public bool WasStarted(string rel) => _started.Contains(rel);

    public ValueTask StartedAsync(string rel, long size, int chunks)
    {
        var no = NextNo(rel, out var existed);
        _pending.Add(existed
            ? ("UPDATE restore_files SET state='pending', size=$z, chunks_total=$k WHERE run_id=$r AND file_no=$n;", [("$z", size), ("$k", chunks), ("$r", _runId), ("$n", no)])
            : ("INSERT INTO restore_files(run_id,file_no,dst,size,chunks_total,state) VALUES ($r,$n,$d,$z,$k,'pending');", [("$r", _runId), ("$n", no), ("$d", rel), ("$z", size), ("$k", chunks)]));
        return MaybeFlushAsync();
    }

    public ValueTask FinishedAsync(string rel, RestoreFileState state)
    {
        var s = state switch { RestoreFileState.Verified => "verified", RestoreFileState.Quarantined => "quarantined", RestoreFileState.Skipped => "skipped", _ => "pending" };
        var no = NextNo(rel, out var existed);
        _pending.Add(existed
            ? ("UPDATE restore_files SET state=$s, chunks_done=CASE WHEN $s='verified' THEN chunks_total ELSE chunks_done END WHERE run_id=$r AND file_no=$n;", [("$s", s), ("$r", _runId), ("$n", no)])
            : ("INSERT INTO restore_files(run_id,file_no,dst,size,chunks_total,state) VALUES ($r,$n,$d,0,0,$s);", [("$r", _runId), ("$n", no), ("$d", rel), ("$s", s)]));
        return MaybeFlushAsync();
    }

    /// <summary>Zapis zaległych zmian i stan sesji (Done przy sukcesie/ostrzeżeniu; inaczej Failed — wznawialna).</summary>
    public async Task CompleteAsync(bool done, long files, long bytes, CancellationToken ct)
    {
        await FlushAsync(ct).ConfigureAwait(false);
        await _writer.ExecuteControlAsync((c, tx) =>
        {
            using var u = Cmd(c, tx, "UPDATE restore_sessions SET state=$s, files_total=$f, bytes_total=$b WHERE run_id=$r;",
                ("$s", done ? "Done" : "Failed"), ("$f", files), ("$b", bytes), ("$r", _runId));
            return u.ExecuteNonQuery();
        }, ct).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (_pending.Count == 0) return;
        var batch = _pending.ToArray();
        _pending.Clear();
        await _writer.ExecuteControlAsync((c, tx) =>
        {
            foreach (var (sql, args) in batch)
            {
                using var cmd = Cmd(c, tx, sql, args);
                cmd.ExecuteNonQuery();
            }
            return 0;
        }, ct).ConfigureAwait(false);
    }

    private ValueTask MaybeFlushAsync() => _pending.Count >= FlushEvery ? new ValueTask(FlushAsync(CancellationToken.None)) : ValueTask.CompletedTask;

    private long NextNo(string rel, out bool existed)
    {
        existed = _fileNo.TryGetValue(rel, out var no);
        if (!existed)
        {
            no = _fileNo.Count + 1;
            _fileNo[rel] = no;
        }
        return no;
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }
}
