using Kobian.Agent.Hosting;
using Kobian.Api.Contracts;
using Kobian.Catalog.Sqlite;
using Kobian.Cli.Commands;
using Kobian.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>it. 134 (SCR-07): strumień postępu uruchomienia WatchRun — backend na katalogu, autoryzacja strumienia (interceptor) i CLI. Dane syntetyczne, katalog tymczasowy.</summary>
public sealed class WatchRunTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-watch-" + Guid.NewGuid().ToString("N"));
    private string CatalogPath => Path.Combine(_dir, "catalog.db");
    private readonly List<WebApplication> _hosts = [];

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(CatalogPath, TimeProvider.System);
        Exec("INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,time_zone,misfire,created_at,updated_at) VALUES ('j1','ERP-noc','files',x'00',x'00','UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');");
        Exec("INSERT INTO run_runs(run_id,job_id,cause,state,started_at) VALUES ('rr1','j1','manual','Running','2026-10-18T08:00:00Z');");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
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

    private CatalogAgentBackend Backend() => new(CatalogPath, "9.9.9") { WatchInterval = TimeSpan.FromMilliseconds(20) };

    private async Task<string> StartAgent(AgentRole role)
    {
        var pipe = "kobian-test-" + Guid.NewGuid().ToString("N");
        var app = AgentHost.Create(new AgentHostOptions(pipe, ApiConventions.CurrentRevision), Backend(), new FixedRoleResolver(role));
        _hosts.Add(app);
        await app.StartAsync(_ct);
        return pipe;
    }

    [Fact, Trait("BL", "BL-64"), Trait("Plan", "R2.6.1")]
    public async Task Backend_streams_changes_only_then_final_state()
    {
        var backend = Backend();
        var seen = new List<Kobian.Api.V1.RunProgress>();
        var reader = Task.Run(async () =>
        {
            await foreach (var p in backend.WatchRunAsync("rr1", _ct)) lock (seen) seen.Add(p);
        }, _ct);
        // Każdy krok dopiero po odebraniu poprzedniego (stałe pauzy zawodziły na obciążonym runnerze CI).
        async Task Until(Func<bool> seenIt)
        {
            for (var i = 0; i < 500 && !seenIt(); i++) await Task.Delay(10, _ct);
        }
        bool Saw(long files) { lock (seen) return seen.Any(p => p.Run.Files == files); }
        await Until(() => { lock (seen) return seen.Count > 0; });
        Exec("UPDATE run_runs SET files=10, bytes_read=1000 WHERE run_id='rr1';");
        await Until(() => Saw(10));
        Exec("UPDATE run_runs SET files=25, bytes_read=2500 WHERE run_id='rr1';");
        await Until(() => Saw(25));
        Exec("UPDATE run_runs SET state='Succeeded', finished_at='2026-10-18T08:05:00Z' WHERE run_id='rr1';");
        await reader.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        Assert.True(seen.Count >= 4, "stan początkowy + 2 zmiany liczników + stan końcowy");
        Assert.Equal("Running", seen[0].Run.State);
        Assert.False(seen[0].Final);
        Assert.Contains(seen, p => p.Run.Files == 25 && p.BytesRead == 2500 && !p.Final);
        // zmiany, nie powtórzenia: sąsiednie wiadomości nigdy nie są identyczne
        for (var i = 1; i < seen.Count; i++) Assert.NotEqual(seen[i - 1].ToString(), seen[i].ToString());
        Assert.True(seen[^1].Final);
        Assert.Equal("Succeeded", seen[^1].Run.State);
        Assert.Equal(Kobian.Api.V1.OverallState.Ok, seen[^1].Run.Result);
        Assert.Single(seen, p => p.Final);
    }

    [Theory, Trait("BL", "BL-64")]
    [InlineData("nieistniejacy")]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("x'; DROP TABLE run_runs;--")]
    public async Task Backend_unknown_or_malformed_id_yields_single_KB0107_final(string id)
    {
        var all = new List<Kobian.Api.V1.RunProgress>();
        await foreach (var p in Backend().WatchRunAsync(id, _ct)) all.Add(p);
        var only = Assert.Single(all);
        Assert.True(only.Final);
        Assert.Equal(KbCode.RunNotFound, only.Run.Code);
        Exec("SELECT 1 FROM run_runs;"); // tabela nietknięta
    }

    [Fact, Trait("BL", "BL-64")]
    public async Task Backend_stops_when_caller_cancels()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var count = 0;
        var task = Task.Run(async () =>
        {
            await foreach (var _ in Backend().WatchRunAsync("rr1", cts.Token)) count++;
        }, _ct);
        await Task.Delay(150, _ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10), _ct));
        Assert.Equal(1, count);
    }

    [Fact, Trait("BL", "BL-64")]
    public async Task Backend_gives_up_after_max_duration()
    {
        var backend = new CatalogAgentBackend(CatalogPath, "9.9.9") { WatchInterval = TimeSpan.FromMilliseconds(10), WatchMaxDuration = TimeSpan.FromMilliseconds(100) };
        var n = 0;
        await foreach (var _ in backend.WatchRunAsync("rr1", _ct)) n++;
        Assert.Equal(1, n); // bieg wciąż Running — strumień kończy się bez final, klient może wznowić
    }

    [Fact, Trait("Category", "Integration"), Trait("BL", "BL-64"), Trait("SEC", "SEC-3.1")]
    public async Task Reader_watches_run_over_pipe_and_cli_exit_code_follows_result()
    {
        var pipe = await StartAgent(AgentRole.Reader);
        Exec("UPDATE run_runs SET state='Failed', kb_code='KB-1402', finished_at='2026-10-18T08:05:00Z', files=7 WHERE run_id='rr1';");
        using var client = new GrpcAgentClient(pipe, TimeSpan.FromSeconds(2));
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(["watch", "rr1"], client, o, e, _ct);
        Assert.Equal(CliApp.ExitFailed, code);
        Assert.Contains("Failed", o.ToString());
        Assert.Contains("files 7", o.ToString());
        Assert.Contains("KB-1402", o.ToString());
    }

    [Fact, Trait("Category", "Integration"), Trait("BL", "BL-64")]
    public async Task Watch_of_unknown_run_over_pipe_is_failed_with_KB0107()
    {
        var pipe = await StartAgent(AgentRole.Reader);
        using var client = new GrpcAgentClient(pipe, TimeSpan.FromSeconds(2));
        var o = new StringWriter();
        var code = await CliApp.RunAsync(["watch", "brak"], client, o, new StringWriter(), _ct);
        Assert.Equal(CliApp.ExitFailed, code);
        Assert.Contains(KbCode.RunNotFound, o.ToString());
    }

    [Fact, Trait("Category", "Integration"), Trait("BL", "BL-64"), Trait("SEC", "SEC-3.1")]
    public async Task Streaming_method_is_denied_without_role_instead_of_bypassing_the_interceptor()
    {
        var pipe = await StartAgent(AgentRole.None);
        using var client = new GrpcAgentClient(pipe, TimeSpan.FromSeconds(2));
        // odmowa roli → AgentUnavailableException (kod 3), a nie dane biegu
        // bieg rr1 jest Running, więc bez bramki strumień nie skończyłby się → limit czasu zamiast zawieszenia testu
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var o = new StringWriter();
        var code = await CliApp.RunAsync(["watch", "rr1"], client, o, new StringWriter(), cts.Token);
        Assert.Equal(CliApp.ExitUnavailable, code);
        Assert.Empty(o.ToString());
    }

    [Fact, Trait("BL", "BL-64")]
    public void Watch_is_listed_in_the_method_policy_as_reader_and_non_modifying()
    {
        var entry = MethodPolicy.Find("/kobian.v1.Agent/WatchRun");
        Assert.NotNull(entry);
        Assert.Equal(AgentRole.Reader, entry.Required);
        Assert.False(entry.Modifying);
        // żadna metoda strumieniowa nie może być modyfikująca (interceptor takich nie obsługuje)
        foreach (var m in Kobian.Api.V1.Agent.Descriptor.Methods.Where(x => x.IsServerStreaming))
            Assert.False(MethodPolicy.Find("/" + Kobian.Api.V1.Agent.Descriptor.FullName + "/" + m.Name)!.Modifying, m.Name);
    }
}
