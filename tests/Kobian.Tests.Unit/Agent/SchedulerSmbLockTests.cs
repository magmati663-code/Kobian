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
/// BL-73 cz. 3 (it. 94, D7.4/D7.10, scenariusz E6): kolejka Agenta serializuje uruchomienia do jednego serwera SMB z różnymi kontami
/// (zasób <c>smb:&lt;serwer&gt;</c>), zamiast pozwolić drugiemu skończyć się błędem 1219 (KB-2101). Udział = katalog tymczasowy, dane syntetyczne.
/// </summary>
public sealed class SchedulerSmbLockTests : IAsyncLifetime
{
    private const string R1 = "00000000000000000000000000000011";
    private const string R2 = "00000000000000000000000000000012";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-smblock-" + Guid.NewGuid().ToString("N"));
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

    private (CatalogScheduler Scheduler, SharedSmbConnector Shared) Sut()
    {
        var shared = new SharedSmbConnector(new SlowConnector(_dir));
        var providers = new Dictionary<string, IStorageProviderFactory> { ["smb"] = new SmbStorageProviderFactory(shared, new PerRefCredentials()) };
        var jobs = new CatalogJobService(Catalog, _writer, providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        return (new CatalogScheduler(Catalog, _writer, jobs, TimeProvider.System, 2, (min, _) => min), shared);
    }

    [Fact, Trait("Decision", "D7.4")]
    public async Task Two_accounts_to_one_smb_server_run_one_after_another_instead_of_failing_with_1219()
    {
        AddJob("j1", "Biuro", SmbDefinition("a", "file:k1"));
        AddJob("j2", "Ksiegowosc", SmbDefinition("b", "file:k2"));
        Seed(R1, "j1", "2026-10-13T02:00:00.000Z");
        Seed(R2, "j2", "2026-10-13T02:00:00.000Z");
        var (sched, shared) = Sut();
        using var s = sched;
        using var sh = shared;
        sched.Reload();

        sched.Tick(_ct); // limit globalny 2, ale zasób smb:nas trzyma R1 (konto k1) → R2 czeka w kolejce
        Assert.Equal("Queued/", State(R2));
        await sched.DrainAsync();
        Assert.Equal("Succeeded/", State(R1));

        sched.Tick(_ct);
        await sched.DrainAsync();
        Assert.Equal("Succeeded/", State(R2)); // bez kolejki: Failed/KB-2101
        Assert.Equal(0, shared.ActiveConnections);
        Assert.Equal(2, sched.Locks.GlobalFree);
    }

    [Fact, Trait("Decision", "D7.4")]
    public async Task Same_account_to_one_server_runs_in_parallel()
    {
        AddJob("j1", "Biuro", SmbDefinition("a", "file:k1"));
        AddJob("j2", "Ksiegowosc", SmbDefinition("b", "file:k1"));
        Seed(R1, "j1", "2026-10-13T02:00:00.000Z");
        Seed(R2, "j2", "2026-10-13T02:00:00.000Z");
        var (sched, shared) = Sut();
        using var s = sched;
        using var sh = shared;
        sched.Reload();
        // Obce uruchomienie trzyma już smb:nas kontem k1 — zgodne, więc oba startują w tym samym obrocie.
        Assert.True(sched.Locks.TryAcquireAll("obce", [new LockRequest("smb:nas", LockMode.Shared, "file:k1")]));
        Assert.Equal(1, sched.Locks.GlobalFree);
        sched.Locks.Release("obce");

        sched.Tick(_ct); // jeden obrót pętli: oba uruchomienia przyjęte razem (bez drugiego Tick)
        await sched.DrainAsync();
        Assert.Equal(("Succeeded/", "Succeeded/"), (State(R1), State(R2)));
        Assert.Equal(2, sched.Locks.GlobalFree);
    }

    [Fact, Trait("Decision", "D7.4")]
    public async Task Run_waits_while_another_account_holds_the_server_and_starts_after_release()
    {
        AddJob("j1", "Biuro", SmbDefinition("a", "file:k1"));
        Seed(R1, "j1", "2026-10-13T02:00:00.000Z");
        var (sched, shared) = Sut();
        using var s = sched;
        using var sh = shared;
        sched.Reload();
        Assert.True(sched.Locks.TryAcquireAll("obce", [new LockRequest("smb:nas", LockMode.Shared, "file:inne")]));

        sched.Tick(_ct);
        sched.Tick(_ct);
        await sched.DrainAsync();
        Assert.Equal("Queued/", State(R1));

        sched.Locks.Release("obce");
        sched.Tick(_ct);
        await sched.DrainAsync();
        Assert.Equal("Succeeded/", State(R1));
    }

    [Theory]
    [InlineData(@"\\NAS\kopie\biuro", "file:k1", "smb:nas", "file:k1")]
    [InlineData(@"\\nas.firma.local\kopie", "file:k2", "smb:nas.firma.local", "file:k2")]
    public void Smb_destination_yields_a_server_lock_with_the_secret_reference_as_account(string root, string secretRef, string key, string account)
    {
        var def = new JobDefinition("/src", "smb", root, "/k", "/p", "/h", null, Remote: new RemoteDestination(secretRef));
        var l = Assert.Single(RunResourceLocks.For(def));
        Assert.Equal((key, account, LockMode.Shared), (l.Key, l.Account, l.Mode));
    }

    [Fact]
    public void Local_sftp_and_unparsable_definitions_need_no_resource_locks()
    {
        Assert.Empty(RunResourceLocks.For(new JobDefinition("/src", "local", "/repo", "/k", "/p", "/h", null)));
        Assert.Empty(RunResourceLocks.For(new JobDefinition("/src", "sftp", "/repo", "/k", "/p", "/h", null, Remote: new RemoteDestination("file:x", "host", 22))));
        Assert.Empty(RunResourceLocks.For(new JobDefinition("/src", "smb", "/nie-unc", "/k", "/p", "/h", null, Remote: new RemoteDestination("file:x"))));
        Assert.Empty(RunResourceLocks.For("files", Encoding.UTF8.GetBytes("nie json")));
        Assert.Empty(RunResourceLocks.For("inny", Encoding.UTF8.GetBytes("{}")));
        Assert.Empty(RunResourceLocks.For(null, null));
    }

    [Fact]
    public void Different_secret_references_conflict_and_equal_ones_do_not()
    {
        var a = new LockRequest("smb:nas", LockMode.Shared, "file:k1");
        Assert.False(LockManager.Compatible(a, a with { Account = "file:k2" }));
        Assert.True(LockManager.Compatible(a, a with { Account = "FILE:K1" })); // konwencja Windows: bez wielkości liter
    }
}
