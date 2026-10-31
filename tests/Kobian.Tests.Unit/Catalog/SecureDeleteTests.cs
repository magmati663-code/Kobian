using System.Text;
using Kobian.Catalog.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Catalog;

/// <summary>RD-74 (it. 117): usunięte wiersze katalogu nie zostają w pliku (PRAGMA secure_delete) — kontrola: to samo bez pragmy zostawia ślad. Dane syntetyczne.</summary>
public sealed class SecureDeleteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-sd-" + Guid.NewGuid().ToString("N"));

    public SecureDeleteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Wstawia wiersze ze znacznikiem, usuwa je i wymusza zapis do pliku głównego; zwraca, czy znacznik został w pliku.</summary>
    private bool MarkerSurvives(bool hardened)
    {
        var path = Path.Combine(_dir, (hardened ? "on" : "off") + ".db");
        CatalogBootstrap.Bootstrap(path, TimeProvider.System);
        var marker = "Kadry/umowa-" + Guid.NewGuid().ToString("N") + ".pdf";
        using (var c = hardened ? CatalogBootstrap.Open(path) : new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            if (!hardened)
            {
                c.Open();
                Exec(c, "PRAGMA secure_delete = OFF;");
            }
            using (var ins = c.CreateCommand())
            {
                ins.CommandText = "INSERT INTO core_outbox(channel,payload,attempts,next_attempt_at,state) VALUES ('webhook',$p,1,'2026-01-01T00:00:00.000Z','sent');";
                ins.Parameters.AddWithValue("$p", Encoding.UTF8.GetBytes(marker + new string('x', 400)));
                for (var i = 0; i < 50; i++) ins.ExecuteNonQuery();
            }
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            Exec(c, "DELETE FROM core_outbox;");
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(path);
        var wal = path + "-wal";
        if (File.Exists(wal)) bytes = [.. bytes, .. File.ReadAllBytes(wal)];
        return bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0;
    }

    [Fact, Trait("RD", "RD-74"), Trait("RD", "RD-69")]
    public void Deleted_rows_do_not_remain_in_the_catalog_file()
    {
        Assert.True(MarkerSurvives(hardened: false)); // kontrola: bez pragmy treść zostaje w wolnych stronach
        Assert.False(MarkerSurvives(hardened: true));
    }

    [Fact, Trait("RD", "RD-74")]
    public void Writable_connection_has_secure_delete_enabled()
    {
        var path = Path.Combine(_dir, "p.db");
        CatalogBootstrap.Bootstrap(path, TimeProvider.System);
        using var c = CatalogBootstrap.Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA secure_delete;";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }
}
