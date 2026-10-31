using System.Security.Cryptography;
using Kobian.Catalog.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Catalog;

/// <summary>D8.8 — schemat dziennika wznowień restore (migracja run/0002). Niezweryfikowane kompilatorem (iteracja 23);
/// ten sam zestaw niezmienników sprawdza tools/verify-restore-ddl.py.</summary>
public sealed class RestoreJournalSchemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-rj-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private string Db => Path.Combine(_dir, "catalog.db");

    public RestoreJournalSchemaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact, Trait("D", "D8.8"), Trait("AC", "AC-07.3")]
    public void Restore_journal_enforces_state_fk_and_pack_idempotency()
    {
        CatalogBootstrap.Bootstrap(Db, TimeProvider.System);
        using var c = CatalogBootstrap.Open(Db);
        CatalogBootstrap.Exec(c, "INSERT INTO job_jobs VALUES ('j1','ERP','files',x'7b7d',x'00',1,0,1,'Europe/Warsaw','skip',NULL,'t','t',0);");
        CatalogBootstrap.Exec(c, "INSERT INTO run_runs(run_id, job_id, cause, state) VALUES ('r1','j1','manual','Running');");
        CatalogBootstrap.Exec(c, "INSERT INTO restore_sessions VALUES ('r1','s1','C:\\t','S-1-5-x','rename',0,x'00',2,100,'Running');");
        CatalogBootstrap.Exec(c, "INSERT INTO restore_files(run_id,file_no,dst,size,chunks_total,state) VALUES ('r1',1,'a.txt',10,1,'pending');");
        Assert.ThrowsAny<SqliteException>(() => CatalogBootstrap.Exec(c, "INSERT INTO restore_files(run_id,file_no,dst,size,chunks_total,state) VALUES ('r1',2,'b',1,1,'bogus');"));
        Assert.ThrowsAny<SqliteException>(() => CatalogBootstrap.Exec(c, "INSERT INTO restore_files(run_id,file_no,dst,size,chunks_total,state) VALUES ('zz',1,'b',1,1,'pending');"));
        CatalogBootstrap.Exec(c, "INSERT INTO restore_done_packs VALUES ('r1', x'01');");
        Assert.ThrowsAny<SqliteException>(() => CatalogBootstrap.Exec(c, "INSERT INTO restore_done_packs VALUES ('r1', x'01');"));
        CatalogBootstrap.Exec(c, "INSERT OR IGNORE INTO restore_done_packs VALUES ('r1', x'01');");
        Assert.Equal(1L, CatalogBootstrap.Scalar<long>(c, "SELECT COUNT(*) FROM restore_done_packs;"));
    }
}
