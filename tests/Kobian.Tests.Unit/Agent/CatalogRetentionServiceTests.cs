using System.Text;
using System.Text.Json;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.V1;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Engine.Retention;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>BL-25 (it. 65): retencja w Agencie — polityka z definicji v1, podgląd, logiczne zapomnienie w katalogu, wiersz run_runs „forget”. Dane syntetyczne.</summary>
public sealed class CatalogRetentionServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-ret-" + Guid.NewGuid().ToString("N"));
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private CatalogWriter _writer = null!;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        Exec("INSERT INTO repo_repositories(repo_id,destination_id,format_version,header_sha256,state) VALUES ('rp','d1',1,x'00','ok');");
        Job("j1", "Dokumenty", """{"keepLast":2,"keepDaily":5,"lockDays":3}""");
        Job("j2", "Bez-retencji", null);
        for (var i = 0; i < 20; i++)
        {
            Exec("INSERT INTO snap_snapshots(snap_id,repo_id,job_id,run_id,host_id,time_utc,mode,root_tree) VALUES ($s,'rp','j1','r','h',$t,1,x'00');",
                ("$s", $"a{i:D2}"), ("$t", Now.AddDays(-i).AddHours(-1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)));
            Exec("INSERT INTO snap_snapshots(snap_id,repo_id,job_id,run_id,host_id,time_utc,mode,root_tree) VALUES ($s,'rp','j2','r','h',$t,1,x'00');",
                ("$s", $"b{i:D2}"), ("$t", Now.AddDays(-i).AddHours(-1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)));
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private void Job(string id, string name, string? retention)
    {
        var def = $$$"""{"version":1,"sources":[{{{JsonSerializer.Serialize(Path.Combine(_dir, "src"))}}}],"destination":{"scheme":"local","root":{{{JsonSerializer.Serialize(Path.Combine(_dir, "dst"))}}}},"key":{"file":{{{JsonSerializer.Serialize(Path.Combine(_dir, "k.key"))}}}}{{{(retention is null ? "" : ",\"retention\":" + retention)}}}}""";
        Exec("INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,time_zone,misfire,created_at,updated_at) VALUES ($i,$n,'files',$d,x'00','Europe/Warsaw','skip','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');",
            ("$i", id), ("$n", name), ("$d", Encoding.UTF8.GetBytes(def)));
    }

    private void Exec(string sql, params (string Name, object Value)[] p)
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private CatalogRetentionService Svc() => new(Catalog, _writer, new FixedTime(Now));

    [Fact, Trait("BL", "BL-25"), Trait("AC", "AC-10.1")]
    public async Task Dry_run_changes_nothing_and_apply_forgets_logically_once()
    {
        var preview = await Svc().ApplyAsync("Dokumenty", dryRun: true, _ct);
        Assert.Null(preview.Code);
        Assert.True(preview.HasPolicy);
        Assert.Equal(5, preview.Plan!.Keep.Count); // 5 dni (obejmuje last 2 i lock 3)
        Assert.Equal(15, preview.Plan.Forget.Count);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM snap_snapshots WHERE forgotten_at IS NOT NULL;"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM run_runs;"));

        var applied = await Svc().ApplyAsync("Dokumenty", dryRun: false, _ct);
        Assert.Equal(15, applied.Forgotten);
        Assert.Equal(15, Scalar("SELECT COUNT(*) FROM snap_snapshots WHERE job_id='j1' AND forgotten_at IS NOT NULL;"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM snap_snapshots WHERE job_id='j2' AND forgotten_at IS NOT NULL;")); // inne zadanie nietknięte
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM snap_snapshots WHERE snap_id IN ('a00','a01','a02','a03','a04') AND forgotten_at IS NOT NULL;"));
        var run = Assert.Single(CatalogStatusReader.ReadRuns(Catalog, "Dokumenty", null).Runs);
        Assert.Equal(("forget", "Succeeded"), (run.Kind, run.State));

        var again = await Svc().ApplyAsync("Dokumenty", dryRun: false, _ct);
        Assert.Equal(0, again.Forgotten); // idempotentne
        Assert.Equal(5, again.Plan!.Keep.Count);
    }

    [Fact, Trait("BL", "BL-25")]
    public async Task No_policy_or_unknown_job_forgets_nothing()
    {
        var none = await Svc().ApplyAsync("Bez-retencji", dryRun: false, _ct);
        Assert.Equal((null, false, 0), (none.Code, none.HasPolicy, none.Forgotten));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM run_runs;"));
        Assert.Equal(KbCode.JobNotFound, (await Svc().ApplyAsync("Nie-ma", dryRun: true, _ct)).Code);
        Assert.Equal(KbCode.JobNotFound, (await Svc().ApplyAsync("", dryRun: true, _ct)).Code);
    }

    [Fact, Trait("BL", "BL-25")]
    public async Task Backend_reply_lists_items_with_reasons_and_forget_count_in_preview()
    {
        var backend = new CatalogAgentBackend(Catalog, "1", retention: Svc());
        var r = await backend.ApplyRetentionAsync(new ApplyRetentionArgs { Job = "Dokumenty", DryRun = true }, _ct);
        Assert.Equal((OverallState.Ok, true, 5, 15, true), (r.Result, r.HasPolicy, r.Kept, r.Forgotten, r.DryRun));
        Assert.Equal((uint)(KeepReason.Last | KeepReason.Daily | KeepReason.Locked | KeepReason.Newest), r.Items.Single(i => i.SnapshotId == "a00").Reasons);
        Assert.False(r.Items.Single(i => i.SnapshotId == "a19").Keep);
        Assert.Equal(KbCode.OperationNotAvailable, (await new CatalogAgentBackend(Catalog, "1").ApplyRetentionAsync(new ApplyRetentionArgs { Job = "Dokumenty" }, _ct)).Code);
        Assert.Equal(new MethodPolicy.Entry(Kobian.Agent.Hosting.AgentRole.Operator, true), MethodPolicy.Find("/kobian.v1.Agent/ApplyRetention"));
    }

    [Fact, Trait("BL", "BL-25"), Trait("Plan", "D3.12")]
    public async Task Preview_simulates_schedule_history_without_catalog()
    {
        var backend = new CatalogAgentBackend(Path.Combine(_dir, "nie-ma.db"), "1", time: new FixedTime(Now)); // bez katalogu — czysta symulacja
        var daily = await backend.PreviewRetentionAsync(new PreviewRetentionArgs { Retention = new RetentionSpec { KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 12 } }, _ct);
        Assert.Equal(OverallState.Ok, daily.Result);
        Assert.InRange(daily.Kept, 19, 22); // 7 dni + tygodnie poza nimi + miesiące poza nimi (nakładanie najnowszych)
        Assert.InRange(daily.OldestDays, 300, 340); // najnowsza kopia miesiąca sprzed 12 miesięcy = koniec listopada poprzedniego roku
        var often = await backend.PreviewRetentionAsync(new PreviewRetentionArgs { Retention = new RetentionSpec { KeepLast = 3, KeepDaily = 2 }, Cron = "*/5 * * * *" }, _ct);
        Assert.Equal((OverallState.Ok, 4), (often.Result, often.Kept)); // 3 ostatnie (dziś) + najnowsza wczoraj
        Assert.Equal(KbCode.RetentionInvalid, (await backend.PreviewRetentionAsync(new PreviewRetentionArgs { Retention = new RetentionSpec() }, _ct)).Code);
        Assert.Equal(KbCode.RetentionInvalid, (await backend.PreviewRetentionAsync(new PreviewRetentionArgs(), _ct)).Code);
        Assert.Equal(KbCode.RetentionInvalid, (await backend.PreviewRetentionAsync(new PreviewRetentionArgs { Retention = new RetentionSpec { KeepDaily = 20_000 } }, _ct)).Code);
        Assert.Equal(KbCode.ScheduleInvalid, (await backend.PreviewRetentionAsync(new PreviewRetentionArgs { Retention = new RetentionSpec { KeepDaily = 1 }, Cron = "nie cron" }, _ct)).Code);
        Assert.Equal(new MethodPolicy.Entry(Kobian.Agent.Hosting.AgentRole.Reader, false), MethodPolicy.Find("/kobian.v1.Agent/PreviewRetention"));
    }

    [Fact, Trait("BL", "BL-25")]
    public async Task Save_writes_retention_into_existing_definition_keeping_other_fields()
    {
        var svc = new ScheduleService(Catalog, _writer);
        var before = Definition("Bez-retencji", checkSha: false); // wiersz z fixture ma skrót zastępczy
        Assert.Null(await svc.SetRetentionAsync("Bez-retencji", new Kobian.Application.Jobs.RetentionPolicy(KeepDaily: 10, RetentionLock: TimeSpan.FromDays(5)), _ct));
        var after = Definition("Bez-retencji");
        Assert.Equal(new Kobian.Application.Jobs.RetentionPolicy(KeepDaily: 10, RetentionLock: TimeSpan.FromDays(5)), after.Retention);
        Assert.Equal(before with { Retention = after.Retention }, after);
        Assert.Equal(KbCode.JobNotFound, await svc.SetRetentionAsync("Nie-ma", new Kobian.Application.Jobs.RetentionPolicy(KeepDaily: 1), _ct));

        var backend = new CatalogAgentBackend(Catalog, "1", schedules: svc, validator: new JobDraftValidator(Catalog, new DestinationProbeService(new Dictionary<string, Kobian.Application.Storage.IStorageProviderFactory>()), new FixedTime(Now)));
        var bad = await backend.UpsertJobAsync(new UpsertJobArgs { JobName = "Dokumenty", Retention = new RetentionSpec { KeepDaily = 2, LockDays = 9 } }, _ct);
        Assert.Equal(("retention", KbCode.RetentionInvalid), (Assert.Single(bad.Issues).Field, bad.Code));
        var ok = await backend.UpsertJobAsync(new UpsertJobArgs { JobName = "Dokumenty", Retention = new RetentionSpec { KeepMonthly = 6 } }, _ct);
        Assert.Equal(OverallState.Ok, ok.Result);
        Assert.Equal(new Kobian.Application.Jobs.RetentionPolicy(KeepMonthly: 6), Definition("Dokumenty").Retention);
    }

    private JobDefinition Definition(string name, bool checkSha = true)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT type, definition, definition_sha256 FROM job_jobs WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        using var rd = cmd.ExecuteReader();
        Assert.True(rd.Read());
        var def = (byte[])rd[1];
        if (checkSha) Assert.Equal(System.Security.Cryptography.SHA256.HashData(def), (byte[])rd[2]); // skrót zgodny z treścią po zapisie
        return JobDefinition.Parse(rd.GetString(0), def);
    }

    [Theory, Trait("BL", "BL-25"), Trait("Plan", "SEC-3.3")]
    [InlineData("""{"keepDaily":7}""", true)]
    [InlineData("""{"keepDaily":7,"lockDays":7}""", true)]
    [InlineData("""{}""", false)]                              // brak reguł
    [InlineData("""{"keepDaily":7,"lockDays":8}""", false)]    // lock dłuższy niż najkrótszy okres (KB-0401)
    [InlineData("""{"keepDaily":-1}""", false)]
    [InlineData("""{"keepDaily":10001}""", false)]
    [InlineData("""{"keepDaily":1.5}""", false)]
    [InlineData("""{"keepDaily":"7"}""", false)]
    [InlineData("""{"keepDaily":7,"evil":1}""", false)]
    [InlineData("""[7]""", false)]
    public void Retention_section_is_strictly_validated(string json, bool ok)
    {
        using var doc = JsonDocument.Parse(json);
        if (ok) Assert.True(JobDefinition.ParseRetention(doc.RootElement).HasAnyRule);
        else Assert.Equal(KbCode.RetentionInvalid, Assert.Throws<KobianException>(() => JobDefinition.ParseRetention(doc.RootElement)).Code);
    }
}
