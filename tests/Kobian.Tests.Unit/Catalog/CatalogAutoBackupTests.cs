using System.Text;
using Kobian.Catalog.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Catalog;

/// <summary>Runda 13: uszkodzony plik katalogu nie zatrzymuje usługi, jeśli jest zdrowa kopia automatyczna (VACUUM INTO + kontrola).</summary>
public sealed class CatalogAutoBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-cab-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "catalog.db");

    public CatalogAutoBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private void Seed(string jobName)
    {
        using var c = CatalogBootstrap.Open(Db);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ($i,$n,'files',x'7b7d',x'00',1,'UTC','skip','2026-10-31T00:00:00Z','2026-10-31T00:00:00Z');";
        cmd.Parameters.AddWithValue("$i", "j-" + jobName);
        cmd.Parameters.AddWithValue("$n", jobName);
        cmd.ExecuteNonQuery();
    }

    private List<string> Jobs()
    {
        using var c = CatalogBootstrap.Open(Db, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM job_jobs ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static void Corrupt(string path, bool header)
    {
        SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(path);
        var at = header ? 0 : bytes.Length / 2;
        for (var i = 0; i < 4096 && at + i < bytes.Length; i++) bytes[at + i] = (byte)(0xA5 ^ i);
        File.WriteAllBytes(path, bytes);
    }

    [Theory]
    [InlineData(true)]   // nagłówek: „file is not a database”
    [InlineData(false)]  // środek pliku: błąd kontroli spójności
    public void Corrupted_catalog_is_restored_from_the_automatic_backup_and_the_damaged_file_is_kept(bool header)
    {
        Assert.Equal(CatalogMode.Normal, CatalogBootstrap.Bootstrap(Db, TimeProvider.System));
        for (var i = 0; i < 40; i++) Seed("zadanie-" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)); // kilka stron danych
        Assert.True(CatalogBootstrap.TryBackup(Db));
        Seed("po-kopii"); // wpis późniejszy niż kopia — zostanie utracony (tylko dane katalogu, nie repozytorium)
        Corrupt(Db, header);

        Assert.Equal(CatalogMode.Normal, CatalogBootstrap.Bootstrap(Db, TimeProvider.System));
        var jobs = Jobs();
        Assert.Equal(40, jobs.Count);
        Assert.DoesNotContain("po-kopii", jobs);
        Assert.Single(Directory.GetFiles(_dir, "catalog.db.corrupt-*")); // uszkodzony plik zachowany do analizy
    }

    [Fact]
    public void Without_a_healthy_backup_the_emergency_mode_is_unchanged_and_nothing_is_moved()
    {
        Assert.Equal(CatalogMode.Normal, CatalogBootstrap.Bootstrap(Db, TimeProvider.System));
        for (var i = 0; i < 40; i++) Seed("z" + i);
        Corrupt(Db, header: false);
        Assert.Equal(CatalogMode.Emergency, CatalogBootstrap.Bootstrap(Db, TimeProvider.System)); // brak kopii

        File.WriteAllText(CatalogBootstrap.AutoBackupPath(Db), "to nie jest baza"); // uszkodzona kopia
        Assert.Equal(CatalogMode.Emergency, CatalogBootstrap.Bootstrap(Db, TimeProvider.System));
        Assert.Empty(Directory.GetFiles(_dir, "catalog.db.corrupt-*")); // niczego nie ruszono
    }

    [Fact]
    public void Backup_of_a_damaged_catalog_never_replaces_a_good_backup()
    {
        Assert.Equal(CatalogMode.Normal, CatalogBootstrap.Bootstrap(Db, TimeProvider.System));
        for (var i = 0; i < 40; i++) Seed("z" + i);
        Assert.True(CatalogBootstrap.TryBackup(Db));
        var good = File.ReadAllBytes(CatalogBootstrap.AutoBackupPath(Db));
        Corrupt(Db, header: false);
        Assert.False(CatalogBootstrap.TryBackup(Db));
        Assert.Equal(good, File.ReadAllBytes(CatalogBootstrap.AutoBackupPath(Db)));
        Assert.False(File.Exists(CatalogBootstrap.AutoBackupPath(Db) + ".tmp"));
    }
}
