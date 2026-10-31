using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Providers.Storage.Smb;
using Kobian.Scheduler.Locks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-74 (it. 96, D7.4/D7.10): uruchomienie ręczne (RunJob) czeka w tej samej kolejce zasobów co harmonogram — wspólny <see cref="LockManager"/>
/// w <see cref="CatalogJobService.Locks"/> i <see cref="CatalogScheduler"/>; po limicie czasu Skipped z KB-4102; anulowanie w kolejce nie blokuje zadania.
/// Udział SMB = katalog tymczasowy, dane syntetyczne.
/// </summary>
public sealed class ManualRunQueueTests : IAsyncLifetime
{
    private const string R2 = "00000000000000000000000000000012";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-manualq-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.Combine(_dir, n);

    /// <summary>Konektor testowy: <c>\\serwer\udział</c> → katalog; połączenie trwa chwilę (jak sieć), żeby równoległe uruchomienia na siebie trafiały.</summary>
    private sealed class SlowConnector(string baseDir) : ISmbConnector
    {
        public int Connects;

        public async ValueTask<ISmbConnection> ConnectAsync(SmbShare share, SmbCredential credential, CancellationToken ct)
        {
            Interlocked.Increment(ref Connects);
            await Task.Delay(30, ct);
            return new Conn(Path.Combine(baseDir, share.Server, share.Share));
        }

        private sealed class Conn(string root) : ISmbConnection
        {
            public string LocalRoot { get; } = root;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Każdy wpis sekretu = inne konto (k1 → FIRMA\k1); to samo konto dla backup i maintenance.</summary>
    private sealed class PerRefCredentials : ISmbCredentialProvider
    {
        public ValueTask<SmbCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct) =>
            ValueTask.FromResult(new SmbCredential(@"FIRMA\" + secretRef["file:".Length..], Convert.ToHexString(RandomNumberGenerator.GetBytes(8))));
    }

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(Path.Combine(_dir, "nas", "a"));
        Directory.CreateDirectory(Path.Combine(_dir, "nas", "b"));
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), RandomNumberGenerator.GetBytes(200 * 1024));
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = Kobian.Crypto.RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(P("wrapped.key"), prov.KeyFileJson);
        File.WriteAllText(P("kobian.repo"), prov.HeaderJson);
        File.WriteAllText(P("pw.txt"), pw + "\n");
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _shared) s.Dispose();
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private string SmbDefinition(string share, string secretRef) =>
        $$"""{"version":1,"sources":[{{J(P("src"))}}],"destination":{"scheme":"smb","root":{{J(@"\\nas\" + share)}},"secretRef":{{J(secretRef)}}},"key":{"file":{{J(P("wrapped.key"))}},"passwordFile":{{J(P("pw.txt"))}},"repoHeader":{{J(P("kobian.repo"))}}},"packTargetSize":1048576}""";

    private void AddJob(string id, string name, string definition) =>
        Exec("INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,misfire_within_sec,created_at,updated_at) VALUES ($i,$n,'files',$d,x'00',1,'UTC','skip',NULL,'2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');",
            ("$i", id), ("$n", name), ("$d", Encoding.UTF8.GetBytes(definition)));

    private void Seed(string runId, string jobId, string at)
    {
        Exec("INSERT INTO run_runs(run_id,job_id,trigger_id,scheduled_for_utc,cause,state,attempt,queued_at) VALUES ($i,$j,$t,$s,'schedule','Queued',0,$s);", ("$t", "t-" + jobId), ("$i", runId), ("$j", jobId), ("$s", at));
        Exec("INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc) VALUES ($i,0,NULL,'Queued',$s);", ("$i", runId), ("$s", at));
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private string State(string runId)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT state || '/' || COALESCE(kb_code,'') FROM run_runs WHERE run_id=$i;";
        cmd.Parameters.AddWithValue("$i", runId);
        return (string)cmd.ExecuteScalar()!;
    }

    private CatalogJobService Jobs(LockManager locks, TimeSpan? timeout = null)
    {
        var shared = new SharedSmbConnector(new SlowConnector(_dir));
        _shared.Add(shared);
        var providers = new Dictionary<string, IStorageProviderFactory> { ["smb"] = new SmbStorageProviderFactory(shared, new PerRefCredentials()) };
        return new CatalogJobService(Catalog, _writer, providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask)
        {
            Locks = locks,
            ManualQueuePoll = TimeSpan.FromMilliseconds(20),
            ManualQueueTimeout = timeout ?? TimeSpan.FromSeconds(30),
        };
    }

    private readonly List<SharedSmbConnector> _shared = [];

    private string LastManual(string jobId)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT state || '/' || COALESCE(kb_code,'') FROM run_runs WHERE job_id=$j AND cause='manual' ORDER BY rowid DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$j", jobId);
        return cmd.ExecuteScalar() as string ?? "-";
    }

    private int ManualRows(string jobId)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM run_runs WHERE job_id=$j AND cause='manual';";
        cmd.Parameters.AddWithValue("$j", jobId);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Przejścia ostatniego uruchomienia ręcznego zadania, np. „Queued&gt;Running&gt;Succeeded”.</summary>
    private string Transitions(string jobId)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT group_concat(to_state, '>') FROM (SELECT t.to_state FROM run_transitions t
            WHERE t.run_id = (SELECT run_id FROM run_runs WHERE job_id=$j AND cause='manual' ORDER BY rowid DESC LIMIT 1) ORDER BY t.seq);
            """;
        cmd.Parameters.AddWithValue("$j", jobId);
        return cmd.ExecuteScalar() as string ?? "-";
    }

    private static async Task WaitUntil(Func<bool> cond, CancellationToken ct)
    {
        for (var i = 0; i < 500 && !cond(); i++) await Task.Delay(10, ct);
        Assert.True(cond());
    }

    [Fact, Trait("Decision", "D7.10"), Trait("Backlog", "BL-74")]
    public async Task Manual_run_waits_while_another_account_holds_the_smb_server_and_succeeds_after_release()
    {
        AddJob("j1", "Biuro", SmbDefinition("a", "file:k1"));
        var locks = new LockManager(2);
        var jobs = Jobs(locks);
        Assert.True(locks.TryAcquireAll("harmonogram", [new LockRequest("smb:nas", LockMode.Shared, "file:inne")]));

        var run = jobs.RunAsync("Biuro", _ct);
        await WaitUntil(() => jobs.ManualWaiting == 1, _ct);
        await Task.Delay(100, _ct);
        Assert.False(run.IsCompleted); // bez kolejki: start od razu i Failed/KB-2101 (1219)
        Assert.Equal("Queued/", LastManual("j1")); // BL-76: czekanie widoczne w run_runs

        locks.Release("harmonogram");
        var outcome = await run;
        Assert.Equal(Kobian.Application.Jobs.JobResult.Succeeded, outcome.Result);
        Assert.Equal("Succeeded/", LastManual("j1"));
        Assert.Equal(1, ManualRows("j1")); // to samo uruchomienie przeszło Queued → Running → Succeeded (bez drugiego wiersza)
        Assert.Equal("Queued>Running>Succeeded", Transitions("j1"));
        Assert.Equal(2, locks.GlobalFree); // blokady uruchomienia ręcznego zwolnione
        Assert.Equal(0, jobs.ManualWaiting);
    }

    [Fact, Trait("Decision", "D7.10"), Trait("Backlog", "BL-74")]
    public async Task Manual_run_waits_for_a_free_global_slot()
    {
        AddJob("j1", "Biuro", SmbDefinition("a", "file:k1"));
        var locks = new LockManager(1);
        var jobs = Jobs(locks);
        Assert.True(locks.TryAcquireAll("inne-zadanie", [])); // jedyny slot globalny zajęty

        var run = jobs.RunAsync("Biuro", _ct);
        await WaitUntil(() => jobs.ManualWaiting == 1, _ct);
        Assert.False(run.IsCompleted);
        locks.Release("inne-zadanie");
        Assert.Equal(Kobian.Application.Jobs.JobResult.Succeeded, (await run).Result);
        Assert.Equal(1, locks.GlobalFree);
    }

    [Fact, Trait("Decision", "D7.10"), Trait("Backlog", "BL-74")]
    public async Task Manual_run_that_waits_too_long_is_skipped_with_KB_4102_and_the_job_stays_runnable()
    {
        AddJob("j1", "Biuro", SmbDefinition("a", "file:k1"));
        var locks = new LockManager(2);
        var jobs = Jobs(locks, TimeSpan.FromMilliseconds(150));
        Assert.True(locks.TryAcquireAll("harmonogram", [new LockRequest("smb:nas", LockMode.Shared, "file:inne")]));

        var outcome = await jobs.RunAsync("Biuro", _ct);
        Assert.Equal((Kobian.Application.Jobs.JobResult.Warning, "KB-4102"), (outcome.Result, outcome.Code));
        Assert.Equal("Skipped/KB-4102", LastManual("j1"));
        Assert.Equal(1, ManualRows("j1"));
        Assert.Equal("Queued>Skipped", Transitions("j1"));

        locks.Release("harmonogram");
        Assert.Equal(Kobian.Application.Jobs.JobResult.Succeeded, (await jobs.RunAsync("Biuro", _ct)).Result); // brak „zawieszonego” KB-4103
    }

    [Fact, Trait("Backlog", "BL-74")]
    public async Task Cancelling_a_waiting_manual_run_frees_the_job_and_takes_no_locks()
    {
        AddJob("j1", "Biuro", SmbDefinition("a", "file:k1"));
        var locks = new LockManager(2);
        var jobs = Jobs(locks);
        Assert.True(locks.TryAcquireAll("harmonogram", [new LockRequest("smb:nas", LockMode.Shared, "file:inne")]));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);

        var run = jobs.RunAsync("Biuro", cts.Token);
        await WaitUntil(() => jobs.ManualWaiting == 1, _ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, jobs.ManualWaiting);
        Assert.Equal(1, locks.GlobalFree); // tylko „harmonogram”
        Assert.Equal("Cancelled/", LastManual("j1")); // BL-76: anulowane w kolejce nie zostaje Queued

        locks.Release("harmonogram");
        Assert.Equal(Kobian.Application.Jobs.JobResult.Succeeded, (await jobs.RunAsync("Biuro", _ct)).Result);
    }

    [Fact, Trait("Backlog", "BL-74")]
    public async Task Scheduler_shares_the_lock_manager_of_the_job_service_so_a_manual_run_blocks_a_scheduled_one()
    {
        AddJob("j1", "Biuro", SmbDefinition("a", "file:k1"));
        AddJob("j2", "Ksiegowosc", SmbDefinition("b", "file:k2"));
        Seed(R2, "j2", "2026-10-13T02:00:00.000Z");
        var locks = new LockManager(2);
        var jobs = Jobs(locks);
        using var sched = new CatalogScheduler(Catalog, _writer, jobs, TimeProvider.System, 2, (min, _) => min);
        Assert.Same(locks, sched.Locks);
        sched.Reload();

        // Uruchomienie ręczne j1 (konto k1) trzyma smb:nas — zaplanowane j2 (konto k2) musi czekać w kolejce.
        Assert.True(locks.TryAcquireAll("manual:test", [new LockRequest("smb:nas", LockMode.Shared, "file:k1")]));
        sched.Tick(_ct);
        await sched.DrainAsync();
        Assert.Equal("Queued/", State(R2));

        locks.Release("manual:test");
        sched.Tick(_ct);
        await sched.DrainAsync();
        Assert.Equal("Succeeded/", State(R2));
    }

    [Fact, Trait("Backlog", "BL-74")]
    public async Task Without_a_lock_manager_manual_run_starts_immediately_as_before()
    {
        AddJob("j1", "Biuro", SmbDefinition("a", "file:k1"));
        var shared = new SharedSmbConnector(new SlowConnector(_dir));
        _shared.Add(shared);
        var providers = new Dictionary<string, IStorageProviderFactory> { ["smb"] = new SmbStorageProviderFactory(shared, new PerRefCredentials()) };
        var jobs = new CatalogJobService(Catalog, _writer, providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        Assert.Null(jobs.Locks);
        Assert.Equal(Kobian.Application.Jobs.JobResult.Succeeded, (await jobs.RunAsync("Biuro", _ct)).Result);
    }

    [Fact, Trait("Backlog", "BL-76"), Trait("Decision", "D7.8")]
    public async Task Waiting_manual_run_is_not_taken_by_the_scheduler_and_becomes_Interrupted_after_a_crash()
    {
        AddJob("j1", "Biuro", SmbDefinition("a", "file:k1"));
        var locks = new LockManager(2);
        var jobs = Jobs(locks);
        using var sched = new CatalogScheduler(Catalog, _writer, jobs, TimeProvider.System, 2, (min, _) => min);
        sched.Reload();
        Assert.True(locks.TryAcquireAll("harmonogram", [new LockRequest("smb:nas", LockMode.Shared, "file:inne")]));
        var run = jobs.RunAsync("Biuro", _ct);
        await WaitUntil(() => jobs.ManualWaiting == 1, _ct);
        locks.Release("harmonogram");
        sched.Tick(_ct); // wiersz manual należy do czekającego RunAsync, nie do schedulera
        await sched.DrainAsync();
        var outcome = await run; // czekający sam bierze blokady w następnym obrocie
        Assert.Equal(Kobian.Application.Jobs.JobResult.Succeeded, outcome.Result);
        Assert.Equal(1, ManualRows("j1"));

        // Awaria w trakcie czekania: wiersz Queued (manual) bez właściciela; zasoby wolne, a scheduler go nie uruchamia → Interrupted przy starcie Agenta.
        Exec("INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at) VALUES ('00000000000000000000000000000077','j1','manual','Queued',0,'2026-10-15T04:00:00.000Z');");
        sched.Tick(_ct);
        await sched.DrainAsync();
        Assert.Equal("Queued/", State("00000000000000000000000000000077"));
        await jobs.RecoverInterruptedAsync(_ct);
        Assert.Equal("Interrupted/", State("00000000000000000000000000000077"));
    }

    [Fact, Trait("Backlog", "BL-76")]
    public async Task Manual_run_with_free_resources_starts_without_a_queued_step()
    {
        AddJob("j1", "Biuro", SmbDefinition("a", "file:k1"));
        var jobs = Jobs(new LockManager(2));
        Assert.Equal(Kobian.Application.Jobs.JobResult.Succeeded, (await jobs.RunAsync("Biuro", _ct)).Result);
        Assert.Equal("Running>Succeeded", Transitions("j1"));
    }
}
