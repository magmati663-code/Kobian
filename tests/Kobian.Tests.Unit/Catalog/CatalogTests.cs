using System.Security.Cryptography;
using Kobian.Catalog.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Catalog;

/// <summary>D2.11, D2.14, D7.7 — katalog SQLite w katalogu tymczasowym.</summary>
public sealed class CatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-cat-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private string Db => Path.Combine(_dir, "catalog.db");

    public CatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact, Trait("D", "D2.14")]
    public void Sqlite_library_meets_minimum_version()
    {
        using var c = CatalogBootstrap.Open(Db);
        Assert.True(CatalogBootstrap.SqliteVersion(c) >= CatalogBootstrap.MinSqliteVersion, $"SQLite {CatalogBootstrap.SqliteVersion(c)}");
    }

    [Fact, Trait("D", "D2.14")]
    public void Connection_is_hardened()
    {
        using var c = CatalogBootstrap.Open(Db);
        Assert.Equal(0L, CatalogBootstrap.Scalar<long>(c, "PRAGMA trusted_schema;"));
        Assert.Equal(1L, CatalogBootstrap.Scalar<long>(c, "PRAGMA cell_size_check;"));
        Assert.Equal("wal", CatalogBootstrap.Scalar<string>(c, "PRAGMA journal_mode;"));
        Assert.Equal(1L, CatalogBootstrap.Scalar<long>(c, "PRAGMA foreign_keys;"));
        // DEFENSIVE: writable_schema nie pozwala modyfikować sqlite_schema
        CatalogBootstrap.Exec(c, "PRAGMA writable_schema = ON;");
        Assert.ThrowsAny<SqliteException>(() => CatalogBootstrap.Exec(c, "UPDATE sqlite_schema SET sql = 'x' WHERE 0;"));
        // load_extension wyłączone domyślnie
        Assert.ThrowsAny<SqliteException>(() => CatalogBootstrap.Exec(c, "SELECT load_extension('x');"));
    }

    [Fact, Trait("D", "D2.11")]
    public void Migrations_apply_once_and_restart_is_idempotent()
    {
        Assert.Equal(CatalogMode.Normal, CatalogBootstrap.Bootstrap(Db, TimeProvider.System));
        Assert.Equal(CatalogMode.Normal, CatalogBootstrap.Bootstrap(Db, TimeProvider.System));
        using var c = CatalogBootstrap.Open(Db);
        Assert.Equal(CatalogBootstrap.ModuleOrder.Length, CatalogBootstrap.Scalar<long>(c, "SELECT COUNT(*) FROM schema_versions;"));
        foreach (var t in new[] { "core_events", "job_jobs", "sched_next_fire", "run_runs", "repo_packs", "snap_snapshots" })
            Assert.Equal(1L, CatalogBootstrap.Scalar<long>(c, $"SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='{t}';"));
        Assert.False(File.Exists(Db + ".pre-.db")); // pierwsza instalacja — bez kopii
    }

    [Fact, Trait("D", "D2.11")]
    public void Newer_schema_than_binary_starts_read_only()
    {
        CatalogBootstrap.Bootstrap(Db, TimeProvider.System);
        using (var c = CatalogBootstrap.Open(Db))
            CatalogBootstrap.Exec(c, "UPDATE schema_versions SET version = 99 WHERE module = 'run';");
        Assert.Equal(CatalogMode.ReadOnly, CatalogBootstrap.Bootstrap(Db, TimeProvider.System));
    }

    [Fact, Trait("D", "D2.11")]
    public void Pending_migration_on_existing_catalog_makes_backup_first()
    {
        CatalogBootstrap.Bootstrap(Db, TimeProvider.System);
        using (var c = CatalogBootstrap.Open(Db))
        {
            CatalogBootstrap.Exec(c, "DROP TABLE snap_name_index; DROP TABLE snap_paths; DROP TABLE snap_snapshots;");
            CatalogBootstrap.Exec(c, "DELETE FROM schema_versions WHERE module = 'snap';");
        }
        Assert.Equal(CatalogMode.Normal, CatalogBootstrap.Bootstrap(Db, TimeProvider.System));
        Assert.Single(Directory.GetFiles(_dir, "catalog.db.pre-*.db"));
    }

    [Fact, Trait("D", "D7.7"), Trait("E", "E5")]
    public void Occurrence_uniqueness_is_enforced_by_index()
    {
        CatalogBootstrap.Bootstrap(Db, TimeProvider.System);
        using var c = CatalogBootstrap.Open(Db);
        CatalogBootstrap.Exec(c, "INSERT INTO job_jobs VALUES ('j1','ERP','files',x'7b7d',x'00',1,0,1,'Europe/Warsaw','skip',NULL,'t','t',0);");
        const string ins = "INSERT INTO run_runs(run_id, job_id, trigger_id, scheduled_for_utc, cause, state) VALUES ($r,'j1','t1','2026-10-12T20:00:00Z',$c,'Queued');";
        void Insert(string run, string cause)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = ins;
            cmd.Parameters.AddWithValue("$r", run);
            cmd.Parameters.AddWithValue("$c", cause);
            cmd.ExecuteNonQuery();
        }
        Insert("r1", "schedule");
        Assert.ThrowsAny<SqliteException>(() => Insert("r2", "misfire"));
        Insert("r3", "manual"); // poza indeksem częściowym
    }

    [Fact, Trait("NFR", "NFR-17")]
    public async Task Writer_gives_control_queue_priority_and_flushes_bulk()
    {
        CatalogBootstrap.Bootstrap(Db, TimeProvider.System);
        await using var w = new CatalogWriter(Db);
        for (var i = 0; i < 20_000; i++)
        {
            var n = i;
            await w.EnqueueBulkAsync((c, tx) =>
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO snap_paths(path_id, parent_id, name) VALUES ($i, NULL, $n);";
                cmd.Parameters.AddWithValue("$i", n);
                cmd.Parameters.AddWithValue("$n", "plik" + n);
                cmd.ExecuteNonQuery();
            }, TestContext.Current.CancellationToken);
        }
        var count = await w.ExecuteControlAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO core_events(at, type, aggregate, payload) VALUES ('t','RunStateChanged','run:1',x'7b7d'); SELECT COUNT(*) FROM snap_paths;";
            return (long)cmd.ExecuteScalar()!;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(20_000, count); // flush: wcześniej zlecone wiersze masowe są widoczne
        Assert.True(w.BulkTransactions >= 1);
        Assert.Equal(1, w.ControlTransactions);
    }

    [Fact]
    public async Task Failed_control_operation_rolls_back_and_reports()
    {
        CatalogBootstrap.Bootstrap(Db, TimeProvider.System);
        await using var w = new CatalogWriter(Db);
        await Assert.ThrowsAnyAsync<SqliteException>(() => w.ExecuteControlAsync<int>((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO core_events(at, type, aggregate, payload) VALUES ('t','X','a',x'00'); INSERT INTO run_runs(run_id, job_id, cause, state) VALUES ('r','brak-zadania','manual','Queued');";
            return cmd.ExecuteNonQuery();
        }, TestContext.Current.CancellationToken));
        var n = await w.ExecuteControlAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM core_events;";
            return (long)cmd.ExecuteScalar()!;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(0, n); // FK naruszony → cała transakcja wycofana
    }
}
