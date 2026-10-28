using System.Text;
using Kobian.Agent.Jobs;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Microsoft.Data.Sqlite;
using Xunit;
using E = Kobian.Agent.Jobs.ServerIdentityEvent;

namespace Kobian.Tests.Unit.Agent;

/// <summary>BL-35: migracja repo/0004 (DDL repo_dest_identities / repo_dest_identity_policy z CHECK) i katalog tożsamości (atomowe przejścia, przeniesienie pinów z definicji).</summary>
public sealed class ServerIdentityStoreTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-ident-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));
    private CatalogWriter _writer = null!;
    private const string Admin = "S-1-5-21-1000-2000-3000-500";
    private const string Dest = "dest-0123456789abcdef";

    private string Catalog => Path.Combine(_dir, "catalog.db");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
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

    private ServerIdentityStore Store() => new(Catalog, _writer, _time);

    internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan d) => _now += d;
    }

    private void Sql(string sql, params (string, object?)[] args)
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private const string Ins = "INSERT INTO repo_dest_identities(destination_id, fingerprint, kind, state, endpoint, display, first_seen, last_seen, approved_by_sid, approved_at) "
        + "VALUES ($d,$f,$k,$s,'sftp://h:22',NULL,'2026-10-17T08:00:00.000Z','2026-10-17T08:00:00.000Z',$by,$at);";

    [Fact, Trait("D", "D4.11"), Trait("RD", "RD-51")]
    public void Migration_creates_strict_tables_whose_checks_mirror_the_state_machine()
    {
        var schema = CatalogBootstrap.MigrationSchema("repo", 4);
        Assert.Contains("table:repo_dest_identities", schema.Keys);
        Assert.Contains("table:repo_dest_identity_policy", schema.Keys);
        Assert.All(schema.Values, sql => Assert.Contains("STRICT", sql, StringComparison.Ordinal));

        var fp = ServerIdentityMachineTests.Fp();
        Sql(Ins, ("$d", Dest), ("$f", fp), ("$k", "ssh-hostkey"), ("$s", "pending"), ("$by", null), ("$at", null));
        void Bad(string d, string f, string k, string s, string? by, string? at) =>
            Assert.Throws<SqliteException>(() => Sql(Ins, ("$d", d), ("$f", f), ("$k", k), ("$s", s), ("$by", by), ("$at", at)));
        Bad(Dest, fp, "ssh-hostkey", "pending", null, null);                                   // klucz główny (cel, odcisk)
        Bad("dest-XYZ", ServerIdentityMachineTests.Fp(), "ssh-hostkey", "pending", null, null);   // identyfikator celu
        Bad("../dest-0123456789abcdef", ServerIdentityMachineTests.Fp(), "ssh-hostkey", "pending", null, null);
        Bad(Dest, "SHA256:abc,SHA256:def", "ssh-hostkey", "pending", null, null);              // lista pinów nie jako jeden wiersz
        Bad(Dest, "SHA256:abc=", "ssh-hostkey", "pending", null, null);
        Bad(Dest, "MD5:aa", "ssh-hostkey", "pending", null, null);
        Bad(Dest, ServerIdentityMachineTests.Fp(), "ssh-dss", "pending", null, null);           // rodzaj
        Bad(Dest, ServerIdentityMachineTests.Fp(), "tls-spki", "trusted", Admin, "t");          // stan spoza listy
        Bad(Dest, ServerIdentityMachineTests.Fp(), "tls-spki", "pinned", null, null);           // pin bez decyzji (kto/kiedy)
        Bad(Dest, ServerIdentityMachineTests.Fp(), "tls-spki", "pending", Admin, "t");          // oczekujący z decyzją
        Assert.Throws<SqliteException>(() => Sql("INSERT INTO repo_dest_identity_policy VALUES ($d, 2, 't', 'S-1');", ("$d", Dest)));
        Assert.Throws<SqliteException>(() => Sql("INSERT INTO repo_dest_identity_policy VALUES ($d, 'yes', 't', 'S-1');", ("$d", Dest))); // STRICT
    }

    [Fact, Trait("D", "D4.11"), Trait("SEC", "SEC-14.5")]
    public async Task Transitions_are_persisted_listed_and_policy_toggles()
    {
        var store = Store();
        var k = ServerIdentityMachineTests.Fp();
        var t = await store.ApplyAsync(Dest, new E.Observed(ServerIdentityMachine.SshHostKey, k, "sftp://backup.example.test:22", "ssh-ed25519"), _ct);
        Assert.Equal(ServerIdentityNotice.NewPending, t.Notice);
        Assert.Equal(IdentityState.Pending, Assert.Single(store.Load(Dest)).State);
        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(KbCode.ServerIdentityNotPending, (await store.ApplyAsync(Dest, new E.Approve(ServerIdentityMachineTests.Fp(), false, Admin), _ct)).Code);
        Assert.Null((await store.ApplyAsync(Dest, new E.Approve(k, false, Admin), _ct)).Code);
        var row = Assert.Single(store.Load(Dest));
        Assert.Equal((IdentityState.Pinned, Admin, "sftp://backup.example.test:22", "ssh-ed25519"), (row.State, row.DecidedBy, row.Endpoint, row.Display));
        Assert.Equal(_time.GetUtcNow(), row.DecidedAt);

        var (list, requirePin) = store.List(null);
        Assert.Equal(Dest, Assert.Single(list).DestinationId);
        Assert.Empty(requirePin);
        Assert.False(store.RequiresPin(Dest));
        await store.SetRequirePinAsync(Dest, true, Admin, _ct);
        Assert.True(store.RequiresPin(Dest));
        Assert.Equal([Dest], store.List(Dest).RequirePin);
        await store.SetRequirePinAsync(Dest, false, Admin, _ct);
        Assert.False(store.RequiresPin(Dest));
        // Niepoprawny identyfikator celu — nic nie jest zapisywane ani czytane.
        Assert.Equal(KbCode.ServerIdentityNotPending, (await store.ApplyAsync("dest-' OR 1=1 --", new E.Observed(ServerIdentityMachine.SshHostKey, k, "x", null), _ct)).Code);
        Assert.Empty(store.Load("dest-' OR 1=1 --"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetRequirePinAsync("x", true, Admin, _ct));
    }

    [Fact, Trait("D", "D4.11")]
    public async Task Parallel_sessions_of_one_destination_do_not_lose_or_duplicate_transitions()
    {
        var store = Store();
        var k = ServerIdentityMachineTests.Fp();
        var seed = ServerIdentityMachineTests.Fp();
        var tasks = Enumerable.Range(0, 24).Select(i => i % 2 == 0
            ? store.ApplyAsync(Dest, new E.SeedFromDefinition(ServerIdentityMachine.SshHostKey, seed, "sftp://h:22"), _ct)
            : store.ApplyAsync(Dest, new E.Observed(ServerIdentityMachine.SshHostKey, k, "sftp://h:22", null), _ct));
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r.Notice == ServerIdentityNotice.Seeded));
        Assert.Equal(1, results.Count(r => r.Notice == ServerIdentityNotice.NewPending));
        var rows = store.Load(Dest);
        Assert.Equal(2, rows.Count);
        Assert.Equal([seed], ServerIdentityMachine.EffectivePins(rows));
    }

    [Fact, Trait("D", "D4.11"), Trait("SEC", "SEC-3.3")]
    public async Task Probing_many_destinations_cannot_grow_the_pending_set_beyond_the_global_limit()
    {
        using (var c = CatalogBootstrap.Open(Catalog))
        using (var tx = c.BeginTransaction())
        {
            for (var i = 0; i < ServerIdentityStore.MaxPendingTotal; i++)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = Ins;
                cmd.Parameters.AddWithValue("$d", "dest-" + i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$f", ServerIdentityMachineTests.Fp());
                cmd.Parameters.AddWithValue("$k", "ssh-hostkey");
                cmd.Parameters.AddWithValue("$s", "pending");
                cmd.Parameters.AddWithValue("$by", DBNull.Value);
                cmd.Parameters.AddWithValue("$at", DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var t = await Store().ApplyAsync(Dest, new E.Observed(ServerIdentityMachine.SshHostKey, ServerIdentityMachineTests.Fp(), "sftp://h:22", null), _ct);
        Assert.Equal(ServerIdentityNotice.Ignored, t.Notice);
        Assert.Empty(Store().Load(Dest));
        // Decyzje i przeniesienie pinów z definicji działają mimo limitu.
        Assert.Equal(ServerIdentityNotice.Seeded, (await Store().ApplyAsync(Dest, new E.SeedFromDefinition(ServerIdentityMachine.SshHostKey, ServerIdentityMachineTests.Fp(), "sftp://h:22"), _ct)).Notice);
    }

    [Fact, Trait("D", "D4.11")]
    public async Task Unparseable_time_written_outside_the_application_fails_closed_as_expired()
    {
        var k = ServerIdentityMachineTests.Fp();
        Sql(Ins.Replace("'2026-10-17T08:00:00.000Z','2026-10-17T08:00:00.000Z'", "'garbage','garbage'", StringComparison.Ordinal),
            ("$d", Dest), ("$f", k), ("$k", "tls-spki"), ("$s", "pending"), ("$by", null), ("$at", null));
        var t = await Store().ApplyAsync(Dest, new E.Approve(k, false, Admin), _ct);
        Assert.Equal(KbCode.ServerIdentityApprovalExpired, t.Code);
    }

    private void AddJob(string name, string destination)
    {
        var src = OperatingSystem.IsWindows() ? @"C:\\Dane" : "/dane";
        var key = OperatingSystem.IsWindows() ? @"C:\\k.key" : "/k.key";
        var json = Encoding.UTF8.GetBytes("{\"version\":1,\"sources\":[\"" + src + "\"],\"destination\":" + destination + ",\"key\":{\"file\":\"" + key + "\"}}");
        Sql("INSERT INTO job_jobs(job_id, name, type, definition, definition_sha256, time_zone, misfire, created_at, updated_at) VALUES ($i,$n,'files',$d,$h,'UTC','skip','t','t');",
            ("$i", "job-" + name), ("$n", name), ("$d", json), ("$h", System.Security.Cryptography.SHA256.HashData(json)));
    }

    [Fact, Trait("D", "D4.11"), Trait("AC", "AC-04.1")]
    public async Task Startup_migrates_host_keys_from_existing_job_definitions_once()
    {
        var sshPin = ServerIdentityMachineTests.Fp();
        var spki = ServerIdentityMachineTests.Fp();
        AddJob("sftp1", $$"""{"scheme":"sftp","root":"/srv/kopie","host":"backup.example.test","hostKey":"{{sshPin}}=","secretRef":"file:s1"}""");
        AddJob("sftp2", $$"""{"scheme":"sftp","root":"/srv/kopie","host":"backup.example.test","hostKey":"{{sshPin}}","secretRef":"file:s1"}"""); // ten sam cel
        AddJob("ftps1", $$"""{"scheme":"ftps","root":"/kopie","host":"nas.example.test","port":990,"hostKey":"{{spki}}","secretRef":"file:f1"}""");
        AddJob("ftps-ca", """{"scheme":"ftps","root":"/inne","host":"nas.example.test","secretRef":"file:f1"}"""); // bez pinu — nic do przeniesienia
        Sql("INSERT INTO job_jobs(job_id, name, type, definition, definition_sha256, time_zone, misfire, created_at, updated_at) VALUES ('j-bad','zly','files',X'7B7D',X'00','UTC','skip','t','t');");

        var store = Store();
        Assert.Equal(2, await store.MigrateJobDefinitionsAsync(_ct));
        Assert.Equal(0, await store.MigrateJobDefinitionsAsync(_ct)); // ponowny start — bez zmian
        var (list, _) = store.List(null);
        Assert.Equal(2, list.Count);
        Assert.All(list, e => Assert.Equal((IdentityState.Pinned, ServerIdentityMachine.DefinitionActor), (e.Row.State, e.Row.DecidedBy)));
        var sftpId = JobDestination.Id("sftp", "/srv/kopie", new Dictionary<string, string> { ["host"] = "backup.example.test" });
        var sftp = Assert.Single(list, e => e.DestinationId == sftpId);
        Assert.Equal((sshPin, ServerIdentityMachine.SshHostKey, "sftp://backup.example.test:22"), (sftp.Row.Fingerprint, sftp.Row.Kind, sftp.Row.Endpoint)); // „=” znormalizowane
        Assert.Contains(list, e => e.Row.Fingerprint == spki && e.Row.Kind == ServerIdentityMachine.TlsSpki && e.Row.Endpoint == "ftps://nas.example.test:990");
    }
}
