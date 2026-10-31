using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Jobs;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Engine.Restore;
using Kobian.Providers.Storage.Local;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// it. 107 — D8.8/R2.8.2 dziennik wznowień odtwarzania: przerwana próba (stan dziennika jak po awarii) → ta sama operacja wznawia bez KB-5105 dla plików
/// już zweryfikowanych, plik roboczy z przyrostkiem jest nasz (zastąpiony), plik docelowy o identycznej treści po utracie ostatniej partii dziennika = odtworzony,
/// obcy plik o innej treści NIGDY nie jest nadpisany; zakończona sesja nie jest wznawiana; inny plan = brak wznowienia. Dane syntetyczne.
/// </summary>
public sealed class RestoreJournalTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-rj-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private CatalogRestoreService _restore = null!;
    private string _snapId = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.Combine(_dir, n);
    private string Out(string n) => Path.Combine(_dir, "out", n);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        Directory.CreateDirectory(P("out"));
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), RandomNumberGenerator.GetBytes(300_000));
        File.WriteAllText(Path.Combine(P("src"), "b.txt"), "plik b");
        File.WriteAllText(Path.Combine(P("src"), "c.txt"), "plik c");
        File.WriteAllText(Path.Combine(P("src"), "d.txt"), "plik d");
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = Kobian.Crypto.RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(P("wrapped.key"), prov.KeyFileJson);
        File.WriteAllText(P("kobian.repo"), prov.HeaderJson);
        File.WriteAllText(P("pw.txt"), pw + "\n");
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        var providers = new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() };
        var jobs = new CatalogJobService(Catalog, _writer, providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        var def = "{\"version\":1,\"sources\":[" + J(P("src")) + "],\"destination\":{\"scheme\":\"local\",\"root\":" + J(P("repo")) + "},\"key\":{\"file\":" + J(P("wrapped.key")) + ",\"passwordFile\":" + J(P("pw.txt")) + ",\"repoHeader\":" + J(P("kobian.repo")) + "}}";
        using (var c = CatalogBootstrap.Open(Catalog))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ('j1','Pliki','files',$d,x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');";
            cmd.Parameters.AddWithValue("$d", Encoding.UTF8.GetBytes(def));
            cmd.ExecuteNonQuery();
        }
        var backup = await jobs.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, backup.Result);
        _snapId = backup.SnapshotId!;
        _restore = new CatalogRestoreService(Catalog, _writer, providers, TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    /// <summary>Stan „po awarii”: niedokończona sesja tego planu z wpisami plików (wiersz run_runs dla FK).</summary>
    private async Task CrashedSessionAsync(byte[] plan, params (string Dst, string State)[] files)
    {
        await _writer.FlushAsync(_ct);
        using var c = CatalogBootstrap.Open(Catalog);
        void X(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$p", plan);
            cmd.ExecuteNonQuery();
        }
        X("INSERT INTO run_runs(run_id,job_id,cause,state) VALUES ('crash','j1','manual','Interrupted');");
        X($"INSERT INTO restore_sessions(run_id,snap_id,target,caller_sid,conflict,restore_acl,plan_sha256,state) VALUES ('crash','{_snapId}','{P("out")}','agent','skip',0,$p,'Running');");
        var no = 1;
        foreach (var (dst, state) in files) X($"INSERT INTO restore_files(run_id,file_no,dst,size,chunks_total,state) VALUES ('crash',{no++},'{dst}',1,1,'{state}');");
    }

    private string SessionState(string runId)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT state FROM restore_sessions WHERE run_id=$r;";
        cmd.Parameters.AddWithValue("$r", runId);
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact, Trait("Plan", "D8.8"), Trait("AC", "AC-07.1")]
    public async Task Interrupted_restore_resumes_without_conflicts_and_replaces_only_our_partial_file()
    {
        var plan = CatalogRestoreJournal.PlanHash(_snapId, P("out"), null, null);
        File.Copy(Path.Combine(P("src"), "a.bin"), Out("a.bin"));         // zweryfikowany przed awarią
        File.WriteAllText(Out("b.txt") + RestoreExecutor.PartialSuffix, "pół"); // przerwany w trakcie zapisu
        File.Copy(Path.Combine(P("src"), "c.txt"), Out("c.txt"));         // zapisany tuż przed awarią — partia dziennika zginęła (stan pending)
        await CrashedSessionAsync(plan, ("a.bin", "verified"), ("b.txt", "pending"), ("c.txt", "pending"));

        var r = await _restore.RestoreAsync(_snapId, null, P("out"), _ct);
        Assert.Equal((JobResult.Succeeded, (string?)null), (r.Result, r.Code)); // bez KB-5105
        Assert.Equal(2, _restore.LastResumed); // a.bin (dziennik) + c.txt (identyczna treść)
        Assert.Equal("plik b", File.ReadAllText(Out("b.txt")));
        Assert.False(File.Exists(Out("b.txt") + RestoreExecutor.PartialSuffix));
        Assert.Equal("plik d", File.ReadAllText(Out("d.txt")));
        Assert.Equal("Superseded", SessionState("crash"));
        Assert.Empty(Directory.GetFiles(P("out"), "*" + RestoreExecutor.PartialSuffix));
    }

    [Fact, Trait("Plan", "D8.8"), Trait("RD", "RD-68")]
    public async Task Plan_shows_files_from_the_interrupted_attempt_as_resumable_not_conflicts()
    {
        var plan = CatalogRestoreJournal.PlanHash(_snapId, P("out"), null, null);
        File.Copy(Path.Combine(P("src"), "a.bin"), Out("a.bin"));
        File.Copy(Path.Combine(P("src"), "c.txt"), Out("c.txt"));
        File.WriteAllText(Out("d.txt"), "obcy");
        await CrashedSessionAsync(plan, ("a.bin", "verified"), ("c.txt", "pending"), ("d.txt", "pending"));
        var svc = new CatalogRestoreService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, TimeProvider.System)
        {
            Planners = r => new RestorePlanner(r) { FreeBytes = _ => long.MaxValue },
        };
        var p = (await svc.PlanAsync(_snapId, null, null, P("out"), _ct)).Plan!;
        Assert.Equal((2L, 1L), (p.Resumable, p.Conflicts)); // a.bin + c.txt do pominięcia; d.txt (inna treść) — konflikt
        Assert.Equal(["d.txt"], p.ConflictSample);
        Assert.Equal(p.Bytes - p.ResumableBytes - p.ConflictBytes, p.WriteBytes);
        Assert.Equal("Running", SessionState("crash")); // podgląd NIE przejmuje sesji
    }

    [Fact, Trait("Plan", "D8.8"), Trait("SEC", "D8.9")]
    public async Task Foreign_file_with_different_content_is_never_overwritten_even_if_journal_says_started()
    {
        var plan = CatalogRestoreJournal.PlanHash(_snapId, P("out"), null, null);
        File.WriteAllText(Out("c.txt"), "dokument użytkownika");
        await CrashedSessionAsync(plan, ("c.txt", "pending"));
        var r = await _restore.RestoreAsync(_snapId, null, P("out"), _ct);
        Assert.Equal((JobResult.Warning, KbCode.RestoreTargetExists), (r.Result, r.Code));
        Assert.Equal("dokument użytkownika", File.ReadAllText(Out("c.txt")));
        Assert.Equal(0, _restore.LastResumed);
    }

    [Fact, Trait("Plan", "D8.8")]
    public async Task Finished_session_or_other_plan_does_not_resume()
    {
        var first = await _restore.RestoreAsync(_snapId, null, P("out"), _ct);
        Assert.Equal(JobResult.Succeeded, first.Result);
        var again = await _restore.RestoreAsync(_snapId, null, P("out"), _ct); // sesja Done — powtórka to zwykłe konflikty
        Assert.Equal((JobResult.Warning, KbCode.RestoreTargetExists), (again.Result, again.Code));
        Assert.Equal(0, _restore.LastResumed);

        Directory.CreateDirectory(P("out2"));
        var plan = CatalogRestoreJournal.PlanHash(_snapId, P("out"), null, null);
        File.Copy(Path.Combine(P("src"), "a.bin"), Path.Combine(P("out2"), "a.bin"));
        await CrashedSessionAsync(plan, ("a.bin", "verified")); // sesja dla INNEGO folderu docelowego
        var other = await _restore.RestoreAsync(_snapId, null, P("out2"), _ct);
        Assert.Equal((JobResult.Warning, KbCode.RestoreTargetExists), (other.Result, other.Code)); // nie przejmuje cudzego planu
        Assert.NotEqual(plan, CatalogRestoreJournal.PlanHash(_snapId, P("out"), null, ["a.bin"]));
        Assert.Equal(CatalogRestoreJournal.PlanHash(_snapId, P("out"), null, ["b", "a"]), CatalogRestoreJournal.PlanHash(_snapId, P("out") + Path.DirectorySeparatorChar, null, ["a", "b"]));
    }

    [Fact, Trait("Plan", "D8.8"), Trait("AC", "AC-07.3")]
    public async Task Journal_records_every_file_and_session_totals()
    {
        var r = await _restore.RestoreAsync(_snapId, null, P("out"), _ct);
        Assert.Equal(JobResult.Succeeded, r.Result);
        await _writer.FlushAsync(_ct);
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT s.state, s.files_total, COUNT(f.file_no), SUM(f.state='verified') FROM restore_sessions s JOIN restore_files f ON f.run_id=s.run_id GROUP BY s.run_id;";
        using var rd = cmd.ExecuteReader();
        Assert.True(rd.Read());
        Assert.Equal(("Done", 4L, 4L, 4L), (rd.GetString(0), rd.GetInt64(1), rd.GetInt64(2), rd.GetInt64(3)));
        Assert.False(rd.Read());
    }
}
