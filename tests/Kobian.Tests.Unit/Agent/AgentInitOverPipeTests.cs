using System.Security.Cryptography;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Cli.Commands;
using Kobian.Domain;
using Kobian.Engine.Restore;
using Kobian.Providers.Storage.Local;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>BL-24 / BL-14 / BL-15: pełna ścieżka przez pipe — CLI `init` → `job run --wait` → `snapshots` → kobian-restore. Dane syntetyczne, hasło losowe w teście.</summary>
public sealed class AgentInitOverPipeTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-initpipe-" + Guid.NewGuid().ToString("N"));
    private readonly string _pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    private readonly List<WebApplication> _hosts = [];
    private CatalogWriter _writer = null!;
    private string P(string n) => Path.Combine(_dir, n);

    private static Dictionary<string, IStorageProviderFactory> Providers => new() { ["local"] = new LocalStorageProviderFactory() };

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), RandomNumberGenerator.GetBytes(300_000));
        File.WriteAllText(P("pw.txt"), _pw + "\n");
        CatalogBootstrap.Bootstrap(P("catalog.db"), TimeProvider.System);
        _writer = new CatalogWriter(P("catalog.db"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private async Task<string> StartAgent(AgentRole role)
    {
        var pipe = "kobian-test-" + Guid.NewGuid().ToString("N");
        var jobs = new CatalogJobService(P("catalog.db"), _writer, Providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        var init = new RepoInitService(P("catalog.db"), _writer, Providers, P("state"), TimeProvider.System);
        var app = AgentHost.Create(new AgentHostOptions(pipe), new CatalogAgentBackend(P("catalog.db"), "9.9.9", jobs, init,
            check: new CatalogCheckService(P("catalog.db"), _writer, Providers, TimeProvider.System)), new FixedRoleResolver(role));
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

    [Fact, Trait("BL", "BL-24"), Trait("BL", "BL-14"), Trait("Category", "Integration")]
    public async Task Cli_init_then_run_then_snapshots_then_offline_restore_without_secrets_in_output()
    {
        var pipe = await StartAgent(AgentRole.Operator);
        var init = await Cli(pipe, "init", "Pliki", "--source", P("src"), "--dest", P("repo"), "--password-file", P("pw.txt"), "--json");
        Assert.True(init.Code == 0, init.Out + init.Err);
        Assert.DoesNotContain(_pw, init.Out + init.Err, StringComparison.Ordinal); // NFR-11
        Assert.True(File.Exists(P("repo/kobian.repo")));

        var run = await Cli(pipe, "job", "run", "Pliki", "--wait", "--json");
        Assert.True(run.Code == 0, run.Out + run.Err);
        var snaps = await Cli(pipe, "snapshots", "--job", "Pliki", "--json");
        Assert.Equal(0, snaps.Code);
        Assert.Contains("Pliki", snaps.Out, StringComparison.Ordinal);

        var keyFile = Directory.GetFiles(P("repo/keys"), "*.key").Single();
        Directory.CreateDirectory(P("dst"));
        var e = new StringWriter();
        var code = await OfflineRestore.RunAsync(["restore", "--repo", P("repo"), "--key-file", keyFile, "--password-file", P("pw.txt"), "--to", P("dst")], new LocalStorageProviderFactory(), new StringWriter(), e, _ct);
        Assert.True(code == 0, e.ToString());
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(P("src/a.bin"))), SHA256.HashData(File.ReadAllBytes(P("dst/a.bin"))));

        // BL-26: kobian-restore check — V2 + pełne V3 na tym samym repo, potem walidacja argumentów
        string[] baseArgs = ["--repo", P("repo"), "--key-file", keyFile, "--password-file", P("pw.txt")];
        var co = new StringWriter();
        var chk = await OfflineRestore.RunAsync(["check", .. baseArgs, "--sample", "100"], new LocalStorageProviderFactory(), co, e, _ct);
        Assert.True(chk == 0, co + e.ToString());
        Assert.StartsWith("Succeeded: V3, 1 snapshots", co.ToString(), StringComparison.Ordinal);
        Assert.Equal(64, await OfflineRestore.RunAsync(["check", .. baseArgs, "--sample", "101"], new LocalStorageProviderFactory(), new StringWriter(), new StringWriter(), _ct));
        Assert.Equal(64, await OfflineRestore.RunAsync(["check", .. baseArgs, "--sample", "-5"], new LocalStorageProviderFactory(), new StringWriter(), new StringWriter(), _ct));
        Assert.Equal(64, await OfflineRestore.RunAsync(["check", .. baseArgs, "--rotation", "x"], new LocalStorageProviderFactory(), new StringWriter(), new StringWriter(), _ct));
        // ta sama weryfikacja przez Agenta (pipe): V3 100%, potem brak zadania
        var agentCheck = await Cli(pipe, "check", "Pliki", "--sample", "100", "--json");
        Assert.True(agentCheck.Code == 0, agentCheck.Out + agentCheck.Err);
        Assert.Contains("\"level\":3", agentCheck.Out, StringComparison.Ordinal);
        Assert.Contains("\"damagedSnapshots\":[]", agentCheck.Out, StringComparison.Ordinal);
        var noJob = await Cli(pipe, "check", "Nie-ma");
        Assert.Equal(CliApp.ExitFailed, noJob.Code);
        Assert.Contains(KbCode.JobNotFound, noJob.Out, StringComparison.Ordinal);
        foreach (var pack in Directory.GetFiles(P("repo/data"), "*.pack", SearchOption.AllDirectories)) File.Delete(pack);
        var broken = await Cli(pipe, "check", "Pliki");
        Assert.Equal(CliApp.ExitFailed, broken.Code);
        Assert.Contains("KB-1501: pack:", broken.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(_pw, broken.Out + broken.Err + agentCheck.Out, StringComparison.Ordinal);
        using (var c = new SqliteConnection($"Data Source={P("catalog.db")};Mode=ReadOnly;Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT r.state, r.kb_code FROM run_runs r JOIN run_transitions t ON t.run_id = r.run_id AND t.seq = 1 WHERE t.reason = 'check' ORDER BY r.queued_at, r.finished_at;";
            using var rd = cmd.ExecuteReader();
            var rows = new List<(string, string?)>();
            while (rd.Read()) rows.Add((rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1)));
            Assert.Equal([("Succeeded", null), ("Failed", KbCode.CheckPackMissing)], rows);
        }
        var ce = new StringWriter();
        co = new StringWriter();
        Assert.Equal(2, await OfflineRestore.RunAsync(["check", .. baseArgs], new LocalStorageProviderFactory(), co, ce, _ct));
        Assert.Contains("KB-1501: pack:", ce.ToString(), StringComparison.Ordinal);
        Assert.Contains("damaged snapshot ", co.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("a.bin", co.ToString() + ce, StringComparison.Ordinal);
    }

    [Fact, Trait("BL", "BL-24"), Trait("Category", "Integration")]
    public async Task Init_reports_domain_codes_and_refuses_reader_role()
    {
        var op = await StartAgent(AgentRole.Operator);
        var missingPw = await Cli(op, "init", "A", "--source", P("src"), "--dest", P("repo"), "--password-file", P("brak.txt"));
        Assert.Equal(CliApp.ExitFailed, missingPw.Code);
        Assert.Contains("KB-", missingPw.Out, StringComparison.Ordinal);
        var badSource = await Cli(op, "init", "B", "--source", P("src") + "\0x", "--dest", P("repo"), "--password-file", P("pw.txt"));
        Assert.Equal(CliApp.ExitUsage, badSource.Code); // NUL w ścieżce odrzucony po stronie CLI
        var again = await Cli(op, "init", "C", "--source", P("src"), "--dest", P("repo"), "--password-file", P("pw.txt"));
        Assert.Equal(CliApp.ExitOk, again.Code);
        var dupRepo = await Cli(op, "init", "D", "--source", P("src"), "--dest", P("repo"), "--password-file", P("pw.txt"));
        Assert.Equal(CliApp.ExitFailed, dupRepo.Code);
        Assert.Contains(KbCode.ObjectAlreadyExists, dupRepo.Out, StringComparison.Ordinal);

        var reader = await StartAgent(AgentRole.Reader);
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(reader, "init", "E", "--source", P("src"), "--dest", P("repo"), "--password-file", P("pw.txt"))).Code);
    }
}
