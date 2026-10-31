using Kobian.Agent.Maintenance;
using Kobian.Catalog.Sqlite;
using Kobian.Tests.Unit.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Maintenance;

/// <summary>RD-69 (it. 116): dziennik restore_* i wysłane powiadomienia usuwane po 30 dniach; uruchomienia w toku nietknięte; partie. Dane syntetyczne.</summary>
public sealed class CatalogMaintenanceTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-mnt-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private string Catalog => Path.Combine(_dir, "catalog.db");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static void Session(SqliteConnection c, string run, string runState, DateTimeOffset at, string sessionState, int files)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO run_runs(run_id,job_id,cause,state,started_at,finished_at) VALUES ($r,'j1','manual',$rs,$t,CASE WHEN $rs IN ('Running','Queued') THEN NULL ELSE $t END);
            INSERT INTO restore_sessions(run_id,snap_id,target,caller_sid,conflict,restore_acl,plan_sha256,state) VALUES ($r,'s1','D:\Odtw\Kadry','agent','skip',0,x'00',$ss);
            INSERT INTO restore_done_packs(run_id,pack_id) VALUES ($r, x'01');
            """;
        cmd.Parameters.AddWithValue("$r", run);
        cmd.Parameters.AddWithValue("$rs", runState);
        cmd.Parameters.AddWithValue("$t", Iso(at));
        cmd.Parameters.AddWithValue("$ss", sessionState);
        cmd.ExecuteNonQuery();
        for (var i = 0; i < files; i++)
        {
            using var f = c.CreateCommand();
            f.CommandText = "INSERT INTO restore_files(run_id,file_no,dst,size,chunks_total,chunks_done,state) VALUES ($r,$n,$d,1,1,1,'renamed');";
            f.Parameters.AddWithValue("$r", run);
            f.Parameters.AddWithValue("$n", i);
            f.Parameters.AddWithValue("$d", $"Kadry/umowa-{i}.pdf");
            f.ExecuteNonQuery();
        }
    }

    private long Count(string sql)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// D5.10 / SEC-5.2: po awarii w stagingu zostaje pełny .bak. Sprzątanie usuwa przeterminowane katalogi kopii (<c>runId</c>) i odtworzeń (<c>restore-runId</c>),
    /// zostawia aktywne uruchomienia, młode katalogi i cudze nazwy.
    /// </summary>
    [Fact, Trait("BL", "BL-40"), Trait("Plan", "D5.10")]
    public async Task Staging_sweep_removes_stale_backup_and_restore_directories_but_not_active_young_or_foreign_ones()
    {
        var staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(staging);
        string Dir(string name, DateTime ageUtc, bool withBak = true)
        {
            var d = Path.Combine(staging, name);
            Directory.CreateDirectory(d);
            if (withBak) File.WriteAllBytes(Path.Combine(d, new string('e', 32) + ".bak"), [1, 2, 3]);
            Directory.SetCreationTimeUtc(d, ageUtc);
            Directory.SetLastWriteTimeUtc(d, ageUtc);
            return d;
        }
        var old = DateTime.UtcNow.AddDays(-2);
        var stale = Dir("aaaaaaaa111111110000000000000000", old);
        var staleRestore = Dir("restore-bbbbbbbb222222220000000000000000", old);
        var active = Dir("cccccccc333333330000000000000000", old);
        var young = Dir("dddddddd444444440000000000000000", DateTime.UtcNow.AddMinutes(-5));
        var foreign = Dir("Moje Dokumenty", old);
        using (var c = CatalogBootstrap.Open(Catalog))
        {
            using var job = c.CreateCommand();
            static string R(string n) => System.Text.Json.JsonSerializer.Serialize(OperatingSystem.IsWindows() ? @"C:\" + n : "/" + n); // ścieżka bezwzględna na tej platformie
            var def = $$$"""{"version":1,"database":{"server":"srv01\\SQLEXPRESS","name":"Db","staging":{{{System.Text.Json.JsonSerializer.Serialize(staging)}}},"sqlStaging":"C:\\ProgramData\\Kobian\\staging"},"destination":{"scheme":"local","root":{{{R("x")}}}},"key":{"file":{{{R("k")}}},"passwordFile":{{{R("p")}}},"repoHeader":{{{R("h")}}}}}""";
            job.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ('j1','Erp','database',$d,x'00',1,'UTC','skip','2026-10-14T00:00:00Z','2026-10-14T00:00:00Z');";
            job.Parameters.AddWithValue("$d", System.Text.Encoding.UTF8.GetBytes(def));
            job.ExecuteNonQuery();
            using var run = c.CreateCommand();
            run.CommandText = "INSERT INTO run_runs(run_id,job_id,cause,state,started_at) VALUES ('cccccccc333333330000000000000000','j1','manual','Running','2026-10-17T11:00:00.000Z');";
            run.ExecuteNonQuery();
        }

        var removed = await new CatalogMaintenance(Catalog, _writer, TimeProvider.System).SweepStagingAsync(_ct);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(stale));
        Assert.False(Directory.Exists(staleRestore));
        Assert.True(Directory.Exists(active)); // uruchomienie w toku
        Assert.True(Directory.Exists(young)); // młodszy niż godzina
        Assert.True(Directory.Exists(foreign)); // nazwa spoza wzorca runId
    }

    [Fact, Trait("RD", "RD-69"), Trait("Plan", "D8.8")]
    public async Task Old_restore_journal_and_sent_notifications_are_removed_in_batches_active_runs_kept()
    {
        using (var c = CatalogBootstrap.Open(Catalog))
        {
            using (var j = c.CreateCommand())
            {
                j.CommandText = """
                    INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ('j1','Kadry','files',x'7B7D',x'00',1,'UTC','skip','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
                    INSERT INTO core_outbox(channel,payload,attempts,next_attempt_at,state) VALUES
                      ('webhook',x'7B7D',1,'2026-09-01T00:00:00.000Z','sent'),('webhook',x'7B7D',6,'2026-09-01T00:00:00.000Z','dead'),
                      ('webhook',x'7B7D',1,'2026-10-16T00:00:00.000Z','sent'),('webhook',x'7B7D',0,'2026-09-01T00:00:00.000Z','pending');
                    """;
                j.ExecuteNonQuery();
            }
            using var tx = c.BeginTransaction();
            Session(c, "old-done", "Succeeded", Now.AddDays(-40), "Done", 3);
            Session(c, "old-failed", "Failed", Now.AddDays(-31), "Failed", 1);
            Session(c, "new-done", "Succeeded", Now.AddDays(-10), "Done", 2);
            Session(c, "old-running", "Running", Now.AddDays(-40), "Running", 2); // uruchomienie w toku — nietknięte mimo wieku
            for (var i = 0; i < 450; i++) Session(c, $"bulk-{i:D3}", "Succeeded", Now.AddDays(-60), "Superseded", 1);
            tx.Commit();
        }
        var m = new CatalogMaintenance(Catalog, _writer, new ManualTime(Now));
        Assert.Equal(452, m.DueRestoreSessions());
        var r = await m.RunOnceAsync(_ct);
        Assert.Equal(452, r.RestoreSessions); // 3 partie po ≤ 200
        Assert.Equal(3 + 1 + 450, r.RestoreFiles);
        Assert.Equal(2, r.OutboxEntries);
        Assert.Equal(2, Count("SELECT COUNT(*) FROM restore_sessions;"));
        Assert.Equal(4, Count("SELECT COUNT(*) FROM restore_files;"));
        Assert.Equal(2, Count("SELECT COUNT(*) FROM restore_done_packs;"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM restore_files WHERE run_id LIKE 'old-done' OR run_id LIKE 'bulk-%';"));
        Assert.Equal(454, Count("SELECT COUNT(*) FROM run_runs;")); // historia uruchomień zostaje
        Assert.Equal(2, Count("SELECT COUNT(*) FROM core_outbox;")); // świeży sent + pending
        Assert.Equal(0, m.DueRestoreSessions());
        Assert.Null(await m.TickAsync(_ct)); // tej samej doby — bez przebiegu
    }
}
