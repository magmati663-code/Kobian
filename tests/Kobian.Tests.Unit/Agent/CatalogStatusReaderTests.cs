using System.Text;
using System.Text.Json;
using Kobian.Agent.Hosting;
using Kobian.Api.V1;
using Kobian.Catalog.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>BL-60 (Qb18/ADR-0008): stan zadań dla pulpitu bez wartości zastępczych i historia uruchomień (ListRuns) — katalog tymczasowy, dane syntetyczne.</summary>
public sealed class CatalogStatusReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-status-" + Guid.NewGuid().ToString("N"));
    private string CatalogPath => Path.Combine(_dir, "catalog.db");

    public CatalogStatusReaderTests()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(CatalogPath, TimeProvider.System);
        Exec("INSERT INTO repo_repositories(repo_id,destination_id,format_version,header_sha256,state) VALUES ('rp','d1',1,x'00','ok');");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private void Exec(string sql, params (string Name, object Value)[] p)
    {
        using var c = CatalogBootstrap.Open(CatalogPath);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private byte[] Definition() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        sources = new[] { Path.Combine(_dir, "src") },
        destination = new { scheme = "local", root = Path.Combine(_dir, "dst") },
        key = new { file = Path.Combine(_dir, "dst", "a.key") },
    });

    private void Job(string id, string name, bool enabled = true, byte[]? definition = null) =>
        Exec("INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ($i,$n,'files',$d,x'00',$e,'UTC','skip','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');",
            ("$i", id), ("$n", name), ("$d", definition ?? Definition()), ("$e", enabled ? 1 : 0));

    private void Cron(string jobId, string spec, bool enabled = true) =>
        Exec("INSERT INTO sched_triggers(trigger_id,job_id,kind,spec,enabled) VALUES ($t,$j,'cron',$s,$e);",
            ("$t", "cron-" + jobId), ("$j", jobId), ("$s", Encoding.UTF8.GetBytes(spec)), ("$e", enabled ? 1 : 0));

    private void Snap(string id, string jobId, string time, int verify, bool suspect = false) =>
        Exec("INSERT INTO snap_snapshots(snap_id,repo_id,job_id,run_id,host_id,time_utc,mode,root_tree,files,bytes_raw,verify_level,suspect) VALUES ($i,'rp',$j,'r','h',$t,0,x'00',1,1,$v,$s);",
            ("$i", id), ("$j", jobId), ("$t", time), ("$v", verify), ("$s", suspect ? 1 : 0));

    private void Run(string id, string jobId, string state, string? finished, string? code = null, string? reason = null, string? snapshot = null, string cause = "manual")
    {
        Exec("INSERT INTO run_runs(run_id,job_id,cause,state,started_at,finished_at,kb_code,snapshot_id,files,bytes_uploaded) VALUES ($i,$j,$c,$s,$st,$f,$k,$sn,5,500);",
            ("$i", id), ("$j", jobId), ("$c", cause), ("$s", state), ("$st", (object?)finished ?? "2026-10-14T08:59:00Z"), ("$f", (object?)finished ?? DBNull.Value),
            ("$k", (object?)code ?? DBNull.Value), ("$sn", (object?)snapshot ?? DBNull.Value));
        Exec("INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason) VALUES ($i,0,NULL,'Running','2026-10-14T00:00:00Z',$r);", ("$i", id), ("$r", (object?)reason ?? DBNull.Value));
    }

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "R2.6.1")]
    public void Status_has_next_run_cycle_verify_level_and_destination_scheme()
    {
        Job("j1", "Dokumenty");
        Cron("j1", """{"cron":"0 22 * * *"}""");
        Snap("s-old", "j1", "2026-10-12T22:00:00Z", 1);
        Snap("s-new", "j1", "2026-10-13T22:00:00Z", 2);
        Run("r1", "j1", "Succeeded", "2026-10-13T22:05:00Z", snapshot: "s-new", cause: "schedule");

        var s = CatalogStatusReader.ReadStatus(CatalogPath, "1.2.3", Now);
        var j = Assert.Single(s.Jobs);
        Assert.Equal(OverallState.Ok, s.State);
        Assert.False(j.Disabled);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), j.NextRunUnixSeconds);
        Assert.Equal(86_400, j.IntervalSeconds);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), j.LastSuccessUnixSeconds);
        Assert.Equal(2, j.LastSuccessVerifyLevel);
        Assert.Equal("local", j.DestinationScheme);
        Assert.False(j.RepositorySuspect);
        Assert.DoesNotContain(_dir, s.ToString(), StringComparison.Ordinal); // NFR-11: żadnych ścieżek z definicji w odpowiedzi
    }

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "3.7")]
    public void Suspect_latest_snapshot_marks_repository_and_last_success_skips_it()
    {
        Job("j1", "ERP");
        Snap("s-good", "j1", "2026-10-12T22:00:00Z", 2);
        Snap("s-bad", "j1", "2026-10-13T22:00:00Z", 2, suspect: true);
        Run("r1", "j1", "Succeeded", "2026-10-13T22:05:00Z");

        var s = CatalogStatusReader.ReadStatus(CatalogPath, "1", Now);
        var j = Assert.Single(s.Jobs);
        Assert.True(j.RepositorySuspect);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), j.LastSuccessUnixSeconds);
        Assert.Equal(OverallState.Failed, s.State); // uszkodzone repozytorium = stan ogólny czerwony (6.3), nawet przy udanym ostatnim uruchomieniu
        Assert.False(j.HasNextRunUnixSeconds); // brak harmonogramu = zadanie ręczne
        Assert.False(j.HasIntervalSeconds);
    }

    [Fact, Trait("BL", "BL-60")]
    public void Disabled_job_is_listed_without_next_run_and_does_not_drive_overall_state()
    {
        Job("j1", "Aktywne");
        Run("r1", "j1", "Succeeded", "2026-10-13T22:05:00Z");
        Job("j2", "Stare", enabled: false);
        Cron("j2", """{"cron":"0 22 * * *"}""");
        Run("r2", "j2", "Failed", "2026-10-10T22:05:00Z", code: "KB-1402");

        var s = CatalogStatusReader.ReadStatus(CatalogPath, "1", Now);
        Assert.Equal(2, s.Jobs.Count);
        var old = s.Jobs.Single(x => x.Name == "Stare");
        Assert.True(old.Disabled);
        Assert.False(old.HasNextRunUnixSeconds);
        Assert.Equal("KB-1402", old.LastCode);
        Assert.Equal(OverallState.Ok, s.State);
    }

    [Fact, Trait("Backlog", "BL-76"), Trait("Decision", "D7.8")]
    public void Active_state_is_running_before_queued_and_empty_when_nothing_is_in_progress()
    {
        Job("j1", "Czeka");
        Run("r1", "j1", "Succeeded", "2026-10-13T22:05:00Z");
        Exec("INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at) VALUES ('q1','j1','manual','Queued',0,'2026-10-14T08:58:00Z');");
        Job("j2", "Biegnie");
        Exec("INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at) VALUES ('q2','j2','manual','Queued',0,'2026-10-14T08:57:00Z');");
        Run("r2", "j2", "Running", null);
        Job("j3", "Spokojne");
        Run("r3", "j3", "Succeeded", "2026-10-13T22:05:00Z");

        var s = CatalogStatusReader.ReadStatus(CatalogPath, "1", Now);
        Assert.Equal("Queued", s.Jobs.Single(x => x.Name == "Czeka").ActiveState);
        Assert.Equal(OverallState.Ok, s.Jobs.Single(x => x.Name == "Czeka").LastResult); // kolejka nie zmienia wyniku ostatniego zakończonego uruchomienia
        Assert.Equal("Running", s.Jobs.Single(x => x.Name == "Biegnie").ActiveState);
        Assert.Equal(string.Empty, s.Jobs.Single(x => x.Name == "Spokojne").ActiveState);
    }

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "SEC-3.3")]
    public void Broken_definition_or_cron_spec_degrades_to_empty_fields_without_exception()
    {
        Job("j1", "Zepsute", definition: Encoding.UTF8.GetBytes("{nie json"));
        Cron("j1", "{\"cron\":\"to nie cron\"}");
        Job("j2", "Bez-crona", definition: Encoding.UTF8.GetBytes("{\"version\":2}"));
        Cron("j2", "[]");

        var s = CatalogStatusReader.ReadStatus(CatalogPath, "1", Now);
        Assert.All(s.Jobs, j =>
        {
            Assert.Equal(string.Empty, j.DestinationScheme);
            Assert.False(j.HasNextRunUnixSeconds);
            Assert.Equal(0, j.LastSuccessVerifyLevel);
        });
    }

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "R2.6.1 SCR-10")]
    public void Runs_newest_first_with_kind_codes_and_job_filter()
    {
        Job("j1", "Dokumenty");
        Job("j2", "ERP");
        Run("r1", "j1", "Succeeded", "2026-10-12T22:05:00Z", snapshot: "s1", cause: "schedule");
        Run("r2", "j1", "Failed", "2026-10-13T22:05:00Z", code: "KB-1402", cause: "schedule");
        Run("r3", "j1", "Succeeded", "2026-10-14T07:00:00Z", reason: "restore");
        Run("r4", "j2", "Warning", "2026-10-14T08:00:00Z", code: "KB-1502", reason: "check");
        Run("r5", "j2", "Running", null);

        var all = CatalogStatusReader.ReadRuns(CatalogPath, null, null);
        Assert.Equal(["r5", "r4", "r3", "r2", "r1"], all.Runs.Select(r => r.RunId));
        var failed = all.Runs.Single(r => r.RunId == "r2");
        Assert.Equal(("Dokumenty", "backup", "schedule", "Failed", OverallState.Failed, "KB-1402"), (failed.Job, failed.Kind, failed.Cause, failed.State, failed.Result, failed.Code));
        Assert.False(failed.HasSnapshotId);
        Assert.Equal("restore", all.Runs.Single(r => r.RunId == "r3").Kind);
        Assert.Equal(("check", OverallState.Warning), (all.Runs.Single(r => r.RunId == "r4").Kind, all.Runs.Single(r => r.RunId == "r4").Result));
        var running = all.Runs.Single(r => r.RunId == "r5");
        Assert.False(running.HasFinishedUnixSeconds);
        Assert.True(running.HasStartedUnixSeconds);
        Assert.Equal("s1", all.Runs.Single(r => r.RunId == "r1").SnapshotId);
        Assert.Equal(500, all.Runs.Single(r => r.RunId == "r1").BytesUploaded);

        Assert.Equal(["r3", "r2", "r1"], CatalogStatusReader.ReadRuns(CatalogPath, "Dokumenty", null).Runs.Select(r => r.RunId));
        Assert.Equal(["r5", "r4"], CatalogStatusReader.ReadRuns(CatalogPath, null, 2).Runs.Select(r => r.RunId));
        Assert.Single(CatalogStatusReader.ReadRuns(CatalogPath, null, -5).Runs); // limit przycięty do 1
        Assert.Empty(CatalogStatusReader.ReadRuns(CatalogPath, "Nie-ma", null).Runs);
        Assert.Empty(CatalogStatusReader.ReadRuns(CatalogPath, new string('x', 201), null).Runs);
        Assert.Empty(CatalogStatusReader.ReadRuns(CatalogPath, "", null).Runs);
    }

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "D2.9")]
    public void ListRuns_is_a_reader_method_and_role_maps_to_contract()
    {
        var p = MethodPolicy.Find("/kobian.v1.Agent/ListRuns");
        Assert.NotNull(p);
        Assert.Equal(AgentRole.Reader, p.Required);
        Assert.False(p.Modifying);
        Assert.Equal(CallerRole.Reader, AgentGrpcService.ToProto(AgentRole.Reader));
        Assert.Equal(CallerRole.Operator, AgentGrpcService.ToProto(AgentRole.Operator));
        Assert.Equal(CallerRole.Administrator, AgentGrpcService.ToProto(AgentRole.Administrator));
        Assert.Equal(CallerRole.Unspecified, AgentGrpcService.ToProto(AgentRole.None));
    }

    [Fact, Trait("RD", "RD-70")]
    public async Task ListSnapshots_carries_damage_from_check_repair()
    {
        Job("j1", "Faktury");
        Snap("aa", "j1", "2026-10-14T08:00:00.000Z", 1);
        Snap("bb", "j1", "2026-10-14T07:00:00.000Z", 1);
        Snap("cc", "j1", "2026-10-14T06:00:00.000Z", 1);
        Exec("UPDATE snap_snapshots SET damage='partial', damaged_files=4 WHERE snap_id='bb';");
        Exec("UPDATE snap_snapshots SET damage='unrecoverable' WHERE snap_id='cc';");
        var r = await new CatalogAgentBackend(CatalogPath, "test").ListSnapshotsAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal([("aa", "", 0L), ("bb", "partial", 4L), ("cc", "unrecoverable", 0L)], r.Snapshots.Select(x => (x.Id, x.Damage, x.DamagedFiles)));
    }
}
