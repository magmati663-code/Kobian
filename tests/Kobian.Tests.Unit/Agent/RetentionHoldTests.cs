using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.V1;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Engine.Retention;
using Kobian.Providers.Storage.Local;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-25 cz. 5 (it. 69): retencja automatyczna po kopii (Qb28) i wstrzymanie po skoku zegara (SEC-17.4, A17, D9.3) — scheduler ustawia, retencja respektuje,
/// status pokazuje, Administrator zwalnia. Dane syntetyczne, zegar wirtualny.
/// </summary>
public sealed class RetentionHoldTests : IAsyncLifetime
{
    private sealed class VirtualTime(DateTimeOffset start) : TimeProvider
    {
        private long _mono;

        public DateTimeOffset Wall { get; private set; } = start;

        public override DateTimeOffset GetUtcNow() => Wall;

        public override long GetTimestamp() => _mono;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan d)
        {
            Wall += d;
            _mono += d.Ticks;
        }

        /// <summary>Ręczna zmiana zegara ściennego (monotoniczny bez zmian).</summary>
        public void Jump(TimeSpan d) => Wall += d;
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-hold-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualTime _time = new(T0);
    private CatalogWriter _writer = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.Combine(_dir, n);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
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

    private void AddJob(string id, string name, string? retention)
    {
        var def = $$"""{"version":1,"sources":[{{J(P("src"))}}],"destination":{"scheme":"local","root":{{J(P("repo"))}}},"key":{"file":{{J(P("wrapped.key"))}},"passwordFile":{{J(P("pw.txt"))}},"repoHeader":{{J(P("kobian.repo"))}}},"packTargetSize":1048576{{(retention is null ? "" : ",\"retention\":" + retention)}}}""";
        Exec("INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ($i,$n,'files',$d,x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');",
            ("$i", id), ("$n", name), ("$d", Encoding.UTF8.GetBytes(def)));
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private List<object?[]> Query(string sql, int cols)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (rd.Read())
        {
            var row = new object?[cols];
            for (var i = 0; i < cols; i++) row[i] = rd.IsDBNull(i) ? null : rd.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private RetentionHoldService Hold() => new(Catalog, _writer, _time);

    [Theory, Trait("SEC", "SEC-17.4"), Trait("Plan", "A17")]
    [InlineData(25 * 3600, true)]
    [InlineData(23 * 3600, false)]
    [InlineData(-3 * 60, true)]
    [InlineData(-60, false)]
    [InlineData(0, false)]
    public void Threat_threshold_comes_from_rule_A17_data(int deltaSeconds, bool threat) =>
        Assert.Equal(threat, Hold().IsThreat(TimeSpan.FromSeconds(deltaSeconds)));

    [Fact, Trait("SEC", "SEC-17.4"), Trait("BL", "BL-25")]
    public async Task First_hold_wins_is_audited_and_release_is_idempotent()
    {
        var h = Hold();
        Assert.Null(h.Current());
        Assert.False(await h.ReportClockJumpAsync(TimeSpan.FromHours(2), _ct)); // poniżej progu A17 — tylko diagnostyka
        Assert.Null(h.Current());

        Assert.True(await h.ReportClockJumpAsync(TimeSpan.FromHours(30), _ct));
        Assert.False(await h.ReportClockJumpAsync(TimeSpan.FromMinutes(-10), _ct)); // kolejny skok nie nadpisuje przyczyny
        var cur = h.Current()!;
        Assert.Equal((KbCode.AlertClockJump, TimeSpan.FromHours(30), "scheduler", T0), (cur.KbCode, cur.ClockDelta, cur.Source, cur.HeldAtUtc));

        Assert.True(await h.ReleaseAsync(_ct));
        Assert.Null(h.Current());
        Assert.False(await h.ReleaseAsync(_ct));

        var events = Query("SELECT type, aggregate, CAST(payload AS TEXT) FROM core_events ORDER BY seq;", 3);
        Assert.Equal(new[] { RetentionHoldService.HeldEventType, RetentionHoldService.ReleasedEventType }, events.Select(e => (string)e[0]!).ToArray());
        Assert.All(events, e => Assert.Equal("host:retention", e[1]));
        Assert.Contains("\"deltaSeconds\":108000", (string)events[0][2]!, StringComparison.Ordinal);
        Assert.DoesNotContain(_dir, string.Concat(events.Select(e => (string)e[2]!)), StringComparison.Ordinal); // bez ścieżek (SEC-9.6)
    }

    [Fact, Trait("SEC", "SEC-17.4"), Trait("Plan", "D7.9")]
    public void Scheduler_forward_jump_over_24h_holds_retention_backward_small_does_not()
    {
        using var sched = new CatalogScheduler(Catalog, _writer, new CatalogJobService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory>(), new byte[16], _time), _time)
        { RetentionHold = Hold() };
        sched.Reload();
        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Jump(TimeSpan.FromSeconds(-90)); // NTP: korekta w tył < 2 min — bez KB-4201 (próg D7.9 120 s) i bez wstrzymania
        sched.Tick(_ct);
        Assert.Null(Hold().Current());

        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Jump(TimeSpan.FromHours(26));
        sched.Tick(_ct);
        Assert.NotNull(Hold().Current());
        var codes = sched.Diagnostics.Select(d => d.KbCode).ToList();
        Assert.Contains(KbCode.ClockJump, codes);
        Assert.Contains(KbCode.AlertClockJump, codes);

        // skok, który nie spełnia A17 (+3 h): tylko KB-4201, wstrzymanie bez zmian
        while (sched.Diagnostics.TryDequeue(out _)) { }
        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Jump(TimeSpan.FromHours(3));
        sched.Tick(_ct);
        Assert.Equal(new[] { KbCode.ClockJump }, sched.Diagnostics.Select(d => d.KbCode).ToArray());
    }

    [Fact, Trait("SEC", "SEC-17.4"), Trait("AC", "AC-10.1")]
    public async Task Held_retention_keeps_everything_records_skipped_run_and_resumes_after_release()
    {
        Exec("INSERT INTO repo_repositories(repo_id,destination_id,format_version,header_sha256,state) VALUES ('rp','d1',1,x'00','ok');");
        AddJob("j1", "Dokumenty", """{"keepLast":2}""");
        for (var i = 0; i < 6; i++)
            Exec("INSERT INTO snap_snapshots(snap_id,repo_id,job_id,run_id,host_id,time_utc,mode,root_tree) VALUES ($s,'rp','j1','r','h',$t,1,x'00');",
                ("$s", $"a{i:D2}"), ("$t", T0.AddDays(-i).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)));
        var hold = Hold();
        await hold.ReportClockJumpAsync(TimeSpan.FromDays(400), _ct);
        var svc = new CatalogRetentionService(Catalog, _writer, _time, hold);

        var preview = await svc.ApplyAsync("Dokumenty", dryRun: true, _ct);
        Assert.True(preview.Held);
        Assert.Equal(KbCode.AlertClockJump, preview.Code);
        Assert.Empty(preview.Plan!.Forget);
        Assert.All(preview.Plan.Keep.Values, r => Assert.True(r.HasFlag(KeepReason.Paused)));

        var applied = await svc.ApplyAsync("Dokumenty", dryRun: false, _ct);
        Assert.Equal((KbCode.AlertClockJump, 0, true), (applied.Code, applied.Forgotten, applied.Held));
        Assert.Empty(Query("SELECT 1 FROM snap_snapshots WHERE forgotten_at IS NOT NULL;", 1));
        var run = Assert.Single(CatalogStatusReader.ReadRuns(Catalog, "Dokumenty", null).Runs);
        Assert.Equal(("forget", "Skipped", KbCode.AlertClockJump), (run.Kind, run.State, run.Code));

        var backend = new CatalogAgentBackend(Catalog, "1", retention: svc, hold: hold);
        var reply = await backend.ApplyRetentionAsync(new ApplyRetentionArgs { Job = "Dokumenty" }, _ct);
        Assert.Equal((OverallState.Warning, true, 0), (reply.Result, reply.Held, reply.Forgotten));
        var status = await backend.GetStatusAsync(_ct);
        Assert.Equal(KbCode.AlertClockJump, status.RetentionHold.Code);
        Assert.Equal(400L * 86400, status.RetentionHold.ClockDeltaSeconds);
        Assert.True(status.State >= OverallState.Warning);

        var released = await backend.ReleaseRetentionHoldAsync(_ct);
        Assert.Equal((OverallState.Ok, true), (released.Result, released.Released));
        Assert.Null((await backend.GetStatusAsync(_ct)).RetentionHold);
        var after = await svc.ApplyAsync("Dokumenty", dryRun: false, _ct);
        Assert.Equal((null, 4, false), (after.Code, after.Forgotten, after.Held));
    }

    [Fact, Trait("SEC", "SEC-17.4"), Trait("Plan", "D2.9")]
    public async Task Release_requires_administrator_and_backend_without_service_is_unavailable()
    {
        Assert.Equal(new MethodPolicy.Entry(Kobian.Agent.Hosting.AgentRole.Administrator, true), MethodPolicy.Find("/kobian.v1.Agent/ReleaseRetentionHold"));
        var r = await new CatalogAgentBackend(Catalog, "1").ReleaseRetentionHoldAsync(_ct);
        Assert.Equal((OverallState.Failed, KbCode.OperationNotAvailable), (r.Result, r.Code));
        Assert.Null((await new CatalogAgentBackend(Catalog, "1").GetStatusAsync(_ct)).RetentionHold);
    }

    [Fact, Trait("BL", "BL-25"), Trait("Qb", "Qb28")]
    public async Task Successful_backup_applies_policy_automatically_unless_held()
    {
        var hold = Hold();
        var retention = new CatalogRetentionService(Catalog, _writer, _time, hold);
        var jobs = new CatalogJobService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, new byte[16], _time, (_, _) => Task.CompletedTask)
        { AutoRetention = retention };
        AddJob("j1", "Pliki", """{"keepLast":1}""");
        AddJob("j2", "Bez-polityki", null);

        Assert.Equal(JobResult.Succeeded, (await jobs.RunAsync("Pliki", _ct)).Result);
        Assert.Equal(0, jobs.LastAutoRetention!.Forgotten);
        _time.Advance(TimeSpan.FromHours(1));
        File.WriteAllText(Path.Combine(P("src"), "b.txt"), "zmiana");
        var second = await jobs.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, second.Result);
        Assert.Equal(1, jobs.LastAutoRetention!.Forgotten);
        var live = Query("SELECT snap_id FROM snap_snapshots WHERE job_id='j1' AND forgotten_at IS NULL;", 1);
        Assert.Equal(second.SnapshotId, (string)Assert.Single(live)[0]!);
        var forgetRuns = Query("SELECT r.cause, r.state FROM run_runs r JOIN run_transitions t ON t.run_id=r.run_id AND t.seq=0 WHERE t.reason='forget';", 2);
        Assert.All(forgetRuns, r => Assert.Equal(("dependency", "Succeeded"), ((string)r[0]!, (string)r[1]!)));
        Assert.Equal(2, forgetRuns.Count);

        // wstrzymanie: kopia nadal udana, nic nie zapomniane, ślad Skipped KB-6117
        await hold.ReportClockJumpAsync(TimeSpan.FromDays(3), _ct);
        _time.Advance(TimeSpan.FromHours(1));
        File.WriteAllText(Path.Combine(P("src"), "c.txt"), "kolejna zmiana");
        var third = await jobs.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, third.Result);
        Assert.True(jobs.LastAutoRetention!.Held);
        Assert.Equal(2, Query("SELECT 1 FROM snap_snapshots WHERE job_id='j1' AND forgotten_at IS NULL;", 1).Count);
        Assert.Equal(KbCode.AlertClockJump, CatalogStatusReader.ReadRuns(Catalog, "Pliki", 1).Runs.Single().Code);

        // zadanie bez polityki: żadnego wiersza „forget”
        var before = Query("SELECT 1 FROM run_runs;", 1).Count;
        Assert.Equal(JobResult.Succeeded, (await jobs.RunAsync("Bez-polityki", _ct)).Result);
        Assert.Equal(before + 1, Query("SELECT 1 FROM run_runs;", 1).Count);
    }
}
