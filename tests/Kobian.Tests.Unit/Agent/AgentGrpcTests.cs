using System.Diagnostics;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.Contracts;
using Kobian.Catalog.Sqlite;
using Kobian.Cli.Commands;
using Kobian.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>BL-14/BL-15: CLI → gRPC po named pipe → host Agenta → katalog SQLite (dane syntetyczne, katalog tymczasowy).</summary>
public sealed class AgentGrpcTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-agent-" + Guid.NewGuid().ToString("N"));
    private string CatalogPath => Path.Combine(_dir, "catalog.db");
    private readonly List<WebApplication> _hosts = [];
    private CatalogWriter? _writer;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(CatalogPath, TimeProvider.System);
        using var c = CatalogBootstrap.Open(CatalogPath);
        Exec(c, "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,time_zone,misfire,created_at,updated_at) VALUES ('j1','ERP-noc','files',x'00',x'00','UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');");
        Exec(c, "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,time_zone,misfire,created_at,updated_at) VALUES ('j2','Pliki-dzienne','files',x'00',x'00','UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');");
        Exec(c, "INSERT INTO run_runs(run_id,job_id,cause,state,finished_at,kb_code) VALUES ('r1','j1','manual','Failed','2026-10-13T02:00:00Z','KB-1402');");
        Exec(c, "INSERT INTO run_runs(run_id,job_id,cause,state,finished_at) VALUES ('r2','j2','manual','Succeeded','2026-10-13T03:00:00Z');");
        Exec(c, "INSERT INTO repo_repositories(repo_id,destination_id,format_version,header_sha256,state) VALUES ('rp','d1',1,x'00','ok');");
        Exec(c, "INSERT INTO snap_snapshots(snap_id,repo_id,job_id,run_id,host_id,time_utc,mode,root_tree,files,bytes_raw) VALUES ('00112233445566778899aabbccddeeff','rp','j1','r1','h','2026-10-13T01:00:00Z',0,x'00',3,100);");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
        if (_writer is not null) await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private async Task<string> StartAgent(AgentRole role, int minRevision = ApiConventions.CurrentRevision, bool schedules = false)
    {
        var pipe = "kobian-test-" + Guid.NewGuid().ToString("N");
        ScheduleService? svc = null;
        if (schedules)
        {
            _writer = new CatalogWriter(CatalogPath);
            svc = new ScheduleService(CatalogPath, _writer);
        }
        var app = AgentHost.Create(new AgentHostOptions(pipe, minRevision), new CatalogAgentBackend(CatalogPath, "9.9.9", schedules: svc), new FixedRoleResolver(role));
        _hosts.Add(app);
        await app.StartAsync(_ct);
        return pipe;
    }

    private async Task<(int Code, string Out, string Err)> Cli(string pipe, params string[] args)
    {
        using var client = new GrpcAgentClient(pipe, TimeSpan.FromSeconds(2));
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(args, client, o, e, _ct);
        return (code, o.ToString(), e.ToString());
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Status_over_pipe_reads_catalog_and_reflects_worst_job_result()
    {
        var pipe = await StartAgent(AgentRole.Reader);
        var r = await Cli(pipe, "status");
        Assert.Equal(CliApp.ExitFailed, r.Code); // ERP-noc: ostatni run Failed
        Assert.Contains("Kobian Backup 9.9.9", r.Out, StringComparison.Ordinal);
        Assert.Contains("ERP-noc", r.Out, StringComparison.Ordinal);
        Assert.Contains("KB-1402", r.Out, StringComparison.Ordinal);
        Assert.Contains("Pliki-dzienne", r.Out, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Snapshots_over_pipe_with_job_filter()
    {
        var pipe = await StartAgent(AgentRole.Reader);
        var all = await Cli(pipe, "snapshots", "--json");
        Assert.Equal(CliApp.ExitOk, all.Code);
        Assert.Contains("00112233445566778899aabbccddeeff", all.Out, StringComparison.Ordinal);
        var none = await Cli(pipe, "snapshots", "--job", "Pliki-dzienne", "--json");
        Assert.Equal(CliApp.ExitOk, none.Code);
        Assert.DoesNotContain("00112233445566778899aabbccddeeff", none.Out, StringComparison.Ordinal);
    }

    [Fact, Trait("BL", "BL-60"), Trait("Category", "Integration")]
    public async Task Runs_over_pipe_show_failed_runs_with_code_for_reader()
    {
        var pipe = await StartAgent(AgentRole.Reader);
        var r = await Cli(pipe, "runs", "--json");
        Assert.Equal(CliApp.ExitOk, r.Code);
        Assert.StartsWith("{\"schema\":\"kobian.cli.runs/", r.Out, StringComparison.Ordinal);
        Assert.True(r.Out.IndexOf("\"r2\"", StringComparison.Ordinal) < r.Out.IndexOf("\"r1\"", StringComparison.Ordinal)); // najnowsze pierwsze
        Assert.Contains("\"state\":\"Failed\"", r.Out, StringComparison.Ordinal);
        Assert.Contains("KB-1402", r.Out, StringComparison.Ordinal);
        var one = await Cli(pipe, "runs", "--job", "Pliki-dzienne", "--limit", "5");
        Assert.Equal(CliApp.ExitOk, one.Code);
        Assert.Contains("Pliki-dzienne  backup  Succeeded", one.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("ERP-noc", one.Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitUsage, (await Cli(pipe, "runs", "--limit", "0")).Code);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Run_job_is_honest_about_missing_scheduler_KB7012()
    {
        var pipe = await StartAgent(AgentRole.Operator);
        var r = await Cli(pipe, "job", "run", "ERP-noc");
        Assert.Equal(CliApp.ExitFailed, r.Code);
        Assert.Contains(KbCode.OperationNotAvailable, r.Out, StringComparison.Ordinal);
    }

    [Fact, Trait("BL", "RD-27"), Trait("AC", "AC-03.3"), Trait("Category", "Integration")]
    public async Task Schedule_set_list_off_over_pipe_with_validation_codes()
    {
        var pipe = await StartAgent(AgentRole.Operator, schedules: true);
        var set = await Cli(pipe, "schedule", "set", "ERP-noc", "--cron", "30 2 * * 1-5", "--json");
        Assert.Equal(CliApp.ExitOk, set.Code);
        Assert.Contains("cron-j1", set.Out, StringComparison.Ordinal);

        var list = await Cli(pipe, "schedule", "list", "--json");
        Assert.Equal(CliApp.ExitOk, list.Code);
        Assert.Contains("30 2 * * 1-5", list.Out, StringComparison.Ordinal);
        Assert.Contains("\"enabled\":true", list.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Pliki-dzienne", (await Cli(pipe, "schedule", "list", "--job", "ERP-noc", "--json")).Out, StringComparison.Ordinal);

        var bad = await Cli(pipe, "schedule", "set", "ERP-noc", "--cron", "to nie cron");
        Assert.Equal(CliApp.ExitFailed, bad.Code);
        Assert.Contains(KbCode.ScheduleInvalid, bad.Out, StringComparison.Ordinal);
        var tooOften = await Cli(pipe, "schedule", "set", "ERP-noc", "--cron", "* * * * *");
        Assert.Contains(KbCode.ScheduleInvalid, tooOften.Out, StringComparison.Ordinal);
        Assert.Contains("30 2 * * 1-5", (await Cli(pipe, "schedule", "list", "--json")).Out, StringComparison.Ordinal); // odrzucone żądanie nie zmieniło niczego

        var missing = await Cli(pipe, "schedule", "set", "Nie-ma", "--cron", "0 2 * * *");
        Assert.Equal(CliApp.ExitFailed, missing.Code);
        Assert.Contains(KbCode.JobNotFound, missing.Out, StringComparison.Ordinal);

        Assert.Equal(CliApp.ExitOk, (await Cli(pipe, "schedule", "off", "ERP-noc")).Code);
        Assert.Equal(CliApp.ExitOk, (await Cli(pipe, "schedule", "off", "ERP-noc")).Code); // idempotentne
        Assert.Contains("\"enabled\":false", (await Cli(pipe, "schedule", "list", "--json")).Out, StringComparison.Ordinal);
    }

    [Fact, Trait("BL", "RD-27"), Trait("Category", "Integration")]
    public async Task Schedule_changes_need_operator_and_are_honest_without_service()
    {
        var reader = await StartAgent(AgentRole.Reader, schedules: true);
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(reader, "schedule", "set", "ERP-noc", "--cron", "0 2 * * *")).Code); // Reader nie zmienia harmonogramu
        Assert.Equal(CliApp.ExitOk, (await Cli(reader, "schedule", "list")).Code); // odczyt dozwolony
        var noService = await StartAgent(AgentRole.Operator);
        var r = await Cli(noService, "schedule", "off", "ERP-noc");
        Assert.Equal(CliApp.ExitFailed, r.Code);
        Assert.Contains(KbCode.OperationNotAvailable, r.Out, StringComparison.Ordinal);
    }

    [Fact, Trait("AC", "AC-37.2"), Trait("Category", "Integration")]
    public async Task Old_client_revision_blocks_modifying_commands_but_not_reads()
    {
        var pipe = await StartAgent(AgentRole.Operator, minRevision: ApiConventions.CurrentRevision + 1);
        var run = await Cli(pipe, "job", "run", "ERP-noc");
        Assert.Equal(CliApp.ExitClientTooOld, run.Code);
        Assert.Contains(KbCode.ClientRevisionTooOld, run.Err, StringComparison.Ordinal);
        var restore = await Cli(pipe, "restore", "--snapshot", "latest", "--to", Path.Combine(_dir, "out"));
        Assert.Equal(CliApp.ExitClientTooOld, restore.Code);
        Assert.NotEqual(CliApp.ExitUnavailable, (await Cli(pipe, "snapshots")).Code);
        Assert.Equal(CliApp.ExitOk, (await Cli(pipe, "snapshots")).Code);
    }

    [Fact, Trait("AC", "AC-37.1"), Trait("Category", "Integration")]
    public async Task Insufficient_role_is_denied_and_reported_as_unavailable()
    {
        var none = await StartAgent(AgentRole.None);
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(none, "status")).Code);
        var reader = await StartAgent(AgentRole.Reader);
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(reader, "job", "run", "ERP-noc")).Code); // Reader nie może modyfikować
        Assert.Equal(CliApp.ExitFailed, (await Cli(reader, "status")).Code);
    }

    [Fact, Trait("AC", "AC-37.1"), Trait("Category", "Integration")]
    public async Task Missing_agent_gives_exit_3_within_five_seconds()
    {
        var sw = Stopwatch.StartNew();
        using var client = new GrpcAgentClient("kobian-test-nonexistent-" + Guid.NewGuid().ToString("N"));
        var code = await CliApp.RunAsync(["status"], client, new StringWriter(), new StringWriter(), _ct);
        Assert.Equal(CliApp.ExitUnavailable, code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Every_service_method_has_a_role_policy_and_nothing_else_is_allowed()
    {
        var methods = Api.V1.Agent.Descriptor.Methods.Select(m => $"/{Api.V1.Agent.Descriptor.FullName}/{m.Name}").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(methods, MethodPolicy.Methods.Order(StringComparer.Ordinal).ToArray());
        Assert.Null(MethodPolicy.Find("/kobian.v1.Agent/Unknown"));
        Assert.All(["RunJob", "Restore", "InitJob", "SetSchedule"], m => Assert.True(MethodPolicy.Find($"/kobian.v1.Agent/{m}")!.Modifying));
        Assert.All(["GetStatus", "ListSnapshots", "ListSchedules"], m => Assert.False(MethodPolicy.Find($"/kobian.v1.Agent/{m}")!.Modifying));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void Pipe_name_must_be_plain(string name)
    {
        Assert.Throws<ArgumentException>(() => AgentHost.Create(new AgentHostOptions(name), new CatalogAgentBackend(CatalogPath, "1"), new FixedRoleResolver(AgentRole.Reader)));
    }
}
