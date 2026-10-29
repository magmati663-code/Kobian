using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.V1;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Providers.Storage.Local;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>BL-15/BL-42/BL-22: RunJob z katalogu → RunExecutor → BackupPipeline na dostawcy Local; wiersze run_runs i snap_snapshots (dane syntetyczne).</summary>
public sealed class CatalogJobServiceTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-jobs-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private CatalogJobService _svc = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.Combine(_dir, n);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), RandomNumberGenerator.GetBytes(3 * 1024 * 1024));
        File.WriteAllText(Path.Combine(P("src"), "b.txt"), "syntetyczne dane");
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = Kobian.Crypto.RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(P("wrapped.key"), prov.KeyFileJson);
        File.WriteAllText(P("kobian.repo"), prov.HeaderJson);
        File.WriteAllText(P("pw.txt"), pw + "\n");
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        _svc = Service();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private CatalogJobService Service(Func<int, CancellationToken, Task>? delay = null) =>
        new(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, new byte[16], TimeProvider.System, delay ?? ((_, _) => Task.CompletedTask));

    private string Definition(string? source = null) =>
        $$"""{"version":1,"sources":[{{J(source ?? P("src"))}}],"destination":{"scheme":"local","root":{{J(P("repo"))}}},"key":{"file":{{J(P("wrapped.key"))}},"passwordFile":{{J(P("pw.txt"))}},"repoHeader":{{J(P("kobian.repo"))}}},"packTargetSize":1048576}""";

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private void AddJob(string id, string name, string def, bool enabled = true, string type = "files")
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ($i,$n,$t,$d,x'00',$e,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$d", Encoding.UTF8.GetBytes(def));
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
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

    [Fact, Trait("AC", "AC-08.1"), Trait("AC", "AC-08.4")]
    public async Task Run_creates_full_then_incremental_snapshot_and_run_rows()
    {
        AddJob("j1", "Pliki", Definition());
        var first = await _svc.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, first.Result);
        Assert.NotNull(first.SnapshotId);

        File.WriteAllText(Path.Combine(P("src"), "c.txt"), "nowy plik");
        var second = await _svc.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, second.Result);

        var snaps = Query("SELECT snap_id, mode, parent, run_id, files, bytes_new FROM snap_snapshots ORDER BY time_utc;", 6);
        Assert.Equal(2, snaps.Count);
        Assert.Equal(0L, snaps[0][1]);
        Assert.Equal(1L, snaps[1][1]); // przyrost
        Assert.Equal(first.SnapshotId, (string)snaps[0][0]!);
        Assert.Equal(first.SnapshotId, (string)snaps[1][2]!); // parent = poprzedni
        Assert.True((long)snaps[1][5]! < 1024 * 1024, "dedup: duży plik nie jest ponownie wysyłany");

        var runs = Query("SELECT run_id, state, cause, snapshot_id, finished_at FROM run_runs ORDER BY started_at;", 5);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => { Assert.Equal("Succeeded", r[1]); Assert.Equal("manual", r[2]); Assert.NotNull(r[4]); });
        Assert.Equal((string)snaps[0][3]!, (string)runs[0][0]!); // run_id snapshotu = run_id z katalogu
        Assert.Equal(4, Query("SELECT 1 FROM run_transitions;", 1).Count); // Running + stan końcowy dla każdego z 2 uruchomień
        Assert.Single(Query("SELECT 1 FROM repo_repositories;", 1));
    }

    [Fact]
    public async Task Missing_job_disabled_job_and_bad_definition_report_distinct_codes_without_paths()
    {
        AddJob("j2", "Wylaczone", Definition(), enabled: false);
        AddJob("j3", "Zepsute", """{"version":2}""");
        AddJob("j4", "ZlyTyp", Definition(), type: "verify");

        Assert.Equal(KbCode.JobNotFound, (await _svc.RunAsync("brak", _ct)).Code);
        var disabled = await _svc.RunAsync("Wylaczone", _ct);
        Assert.Equal((JobResult.Warning, KbCode.JobDisabled), (disabled.Result, disabled.Code));
        Assert.Equal("Skipped", Query("SELECT state FROM run_runs;", 1)[0][0]);
        var bad = await _svc.RunAsync("Zepsute", _ct);
        Assert.Equal((JobResult.Failed, KbCode.JobDefinitionInvalid), (bad.Result, bad.Code));
        Assert.Equal(KbCode.JobDefinitionInvalid, (await _svc.RunAsync("ZlyTyp", _ct)).Code);
        Assert.Empty(Query("SELECT 1 FROM snap_snapshots;", 1));
    }

    [Fact]
    public async Task Missing_source_fails_permanently_without_snapshot_and_without_retry_storm()
    {
        AddJob("j5", "BrakZrodla", Definition(P("nie-ma")));
        var r = await _svc.RunAsync("BrakZrodla", _ct);
        Assert.Equal((JobResult.Failed, KbCode.SourcesNotReadable), (r.Result, r.Code));
        var runs = Query("SELECT state, kb_code FROM run_runs;", 2);
        Assert.Single(runs); // błąd trwały: bez ponowień
        Assert.Equal("Failed", runs[0][0]);
        Assert.Empty(Query("SELECT 1 FROM snap_snapshots;", 1));
    }

    [Fact]
    public async Task Second_concurrent_run_of_same_job_is_rejected_with_previous_run_active()
    {
        AddJob("j6", "Rownolegle", Definition());
        var gate = new TaskCompletionSource();
        var slow = new CatalogJobService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new GatedFactory(gate.Task) }, new byte[16], TimeProvider.System);
        var running = slow.RunAsync("Rownolegle", _ct);
        await Task.Delay(300, _ct);
        var second = await slow.RunAsync("Rownolegle", _ct);
        Assert.Equal(KbCode.PreviousRunActive, second.Code);
        gate.SetResult();
        Assert.Equal(JobResult.Succeeded, (await running).Result);
    }

    [Fact]
    public async Task Interrupted_rows_are_recovered_at_start()
    {
        AddJob("j7", "Awaria", Definition());
        using (var c = CatalogBootstrap.Open(Catalog))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO run_runs(run_id,job_id,cause,state) VALUES ('rx','j7','manual','Running');";
            cmd.ExecuteNonQuery();
        }
        await _svc.RecoverInterruptedAsync(_ct);
        Assert.Equal("Interrupted", Query("SELECT state FROM run_runs WHERE run_id='rx';", 1)[0][0]);
    }

    [Fact]
    public async Task Backend_RunJob_maps_outcome_and_without_service_reports_unavailable()
    {
        AddJob("j8", "Backend", Definition());
        var ok = await new CatalogAgentBackend(Catalog, "t", _svc).RunJobAsync("Backend", wait: true, _ct);
        Assert.Equal(OverallState.Ok, ok.Result);
        Assert.False(string.IsNullOrEmpty(ok.SnapshotId));
        var none = await new CatalogAgentBackend(Catalog, "t").RunJobAsync("Backend", wait: true, _ct);
        Assert.Equal(KbCode.OperationNotAvailable, none.Code);
        var missing = await new CatalogAgentBackend(Catalog, "t", _svc).RunJobAsync("brak", wait: true, _ct);
        Assert.Equal((OverallState.Failed, KbCode.JobNotFound), (missing.Result, missing.Code));
    }

    [Theory]
    [InlineData("""{"version":1,"sources":[],"destination":{"scheme":"local","root":"/r"},"key":{"file":"/k"}}""")]
    [InlineData("""{"version":1,"sources":["/a",{"scheme":"smb","root":"\\\\nas\\k","secretRef":"file:x"}],"destination":{"scheme":"local","root":"/r"},"key":{"file":"/k"}}""")]
    [InlineData("""{"version":1,"sources":["rel"],"destination":{"scheme":"local","root":"/r"},"key":{"file":"/k"}}""")]
    [InlineData("""{"version":1,"sources":["/a"],"destination":{"scheme":"sftp","root":"/r"},"key":{"file":"/k"}}""")]
    [InlineData("""{"version":1,"sources":["/a"],"destination":{"scheme":"local","root":"/r"},"key":{"file":"/k"},"packTargetSize":5}""")]
    [InlineData("not json")]
    public void Definition_parser_rejects_unsupported_or_malformed_input(string json)
    {
        foreach (var t in new[] { "/a", "/b", "/r", "/k" }) json = json.Replace($"\"{t}\"", J(Path.GetFullPath(t)), StringComparison.Ordinal); // ścieżki bezwzględne także na Windows
        var ex = Assert.Throws<KobianException>(() => JobDefinition.Parse("files", Encoding.UTF8.GetBytes(json)));
        Assert.Equal(KbCode.JobDefinitionInvalid, ex.Code);
        Assert.DoesNotContain(Path.GetFullPath("/a"), ex.Message, StringComparison.Ordinal); // NFR-11: bez ścieżek w komunikacie
    }

    [Fact]
    public void Definition_parser_accepts_valid_v1_on_this_platform()
    {
        var d = JobDefinition.Parse("files", Encoding.UTF8.GetBytes(Definition()));
        Assert.Equal(P("src"), d.Source);
        Assert.Equal("local", d.DestinationScheme);
        Assert.Equal(1048576, d.PackTargetSize);
    }

    [Fact, Trait("AC", "AC-09.1"), Trait("V", "V2"), Trait("BL", "BL-26")]
    public async Task Every_successful_backup_is_V2_verified_and_recorded_in_catalog()
    {
        AddJob("j10", "Pliki", Definition());
        Assert.Equal(JobResult.Succeeded, (await _svc.RunAsync("Pliki", _ct)).Result);
        File.WriteAllText(Path.Combine(P("src"), "c.txt"), "zmiana");
        Assert.Equal(JobResult.Succeeded, (await _svc.RunAsync("Pliki", _ct)).Result);
        var snaps = Query("SELECT verify_level, suspect FROM snap_snapshots;", 2);
        Assert.Equal(2, snaps.Count);
        Assert.All(snaps, r => { Assert.Equal(2L, r[0]); Assert.Equal(0L, r[1]); });
    }

    [Fact, Trait("AC", "AC-09.1"), Trait("V", "V2"), Trait("PM", "PM-15")]
    public async Task Failed_V2_after_commit_fails_the_run_marks_snapshot_suspect_and_forces_next_full()
    {
        AddJob("j11", "Pliki", Definition());
        Assert.Equal(JobResult.Succeeded, (await _svc.RunAsync("Pliki", _ct)).Result);
        File.WriteAllText(Path.Combine(P("src"), "c.txt"), "zmiana");

        // cel „gubi” bajty packów po commit (PM-15: ENOSPC/awaria w locie widoczna dopiero przy odczycie) — Stat packa po zapisie snapshotu zwraca inny rozmiar
        var lying = new CatalogJobService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LyingPackSizeFactory() }, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        var bad = await lying.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Failed, bad.Result);
        Assert.Equal(KbCode.CheckPackSizeMismatch, bad.Code);
        Assert.False(bad.Transient);
        Assert.NotNull(bad.SnapshotId);

        var row = Query($"SELECT suspect, verify_level FROM snap_snapshots WHERE snap_id='{bad.SnapshotId}';", 2).Single();
        Assert.Equal(1L, row[0]);
        Assert.Equal(1L, row[1]);
        var runs = Query("SELECT state, kb_code FROM run_runs ORDER BY started_at;", 2);
        Assert.Equal(2, runs.Count); // błąd trwały: bez ponowień
        Assert.Equal("Failed", runs[1][0]);
        Assert.Equal(KbCode.CheckPackSizeMismatch, runs[1][1]);

        // następna kopia: pełna (nie przyrost od podejrzanego ani od starszego snapshotu) i znów V2 OK
        var next = await _svc.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, next.Result);
        var last = Query($"SELECT mode, parent, verify_level, suspect FROM snap_snapshots WHERE snap_id='{next.SnapshotId}';", 4).Single();
        Assert.Equal(0L, last[0]);
        Assert.Null(last[1]);
        Assert.Equal(2L, last[2]);
        Assert.Equal(0L, last[3]);
    }

    [Fact, Trait("AC", "AC-09.1"), Trait("V", "V2"), Trait("BL", "BL-26")]
    public async Task Check_marks_damaged_snapshots_suspect_so_the_next_backup_is_full()
    {
        AddJob("j12", "Pliki", Definition());
        var first = await _svc.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, first.Result);
        var pack = Directory.EnumerateFiles(Path.Combine(P("repo"), "data"), "*.pack", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
        File.Delete(pack);

        var check = new CatalogCheckService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, TimeProvider.System);
        var r = await check.CheckAsync("Pliki", 0, null, _ct);
        Assert.Equal(Kobian.Engine.Repo.CheckStatus.Failed, r.Status);
        Assert.Equal(KbCode.CheckPackMissing, r.Code);
        Assert.Equal(1L, Query($"SELECT suspect FROM snap_snapshots WHERE snap_id='{first.SnapshotId}';", 1).Single()[0]);

        var next = await _svc.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, next.Result); // pełna kopia wysyła brakujące bloki ponownie; V2 nowego snapshotu przechodzi
        Assert.Equal(0L, Query($"SELECT mode FROM snap_snapshots WHERE snap_id='{next.SnapshotId}';", 1).Single()[0]);
    }

    /// <summary>Sesja Local, która po rozpoczęciu zapisu snapshotu zgłasza dla packów rozmiar o 1 B większy (symulacja utraty danych po V1).</summary>
    private sealed class LyingPackSizeFactory : IStorageProviderFactory
    {
        public string Scheme => "local";

        public async ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct) =>
            new LyingSession(await new LocalStorageProviderFactory().OpenAsync(config, role, ct).ConfigureAwait(false));

        private sealed class LyingSession(IStorageSession inner) : IStorageSession
        {
            private bool _armed;

            public CredentialRole Role => inner.Role;

            public ProviderCapabilities Capabilities => inner.Capabilities;

            public ServerIdentity? PresentedIdentity => inner.PresentedIdentity;

            public async ValueTask<StatResult?> StatAsync(string path, CancellationToken ct)
            {
                var r = await inner.StatAsync(path, ct).ConfigureAwait(false);
                return _armed && r is not null && path.StartsWith("data/", StringComparison.Ordinal) ? r with { Length = r.Length + 1 } : r;
            }

            public IAsyncEnumerable<StatResult> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);

            public ValueTask<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct) => inner.OpenReadAsync(path, offset, length, ct);

            public ValueTask<IObjectWriter> CreateAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct)
            {
                if (path.StartsWith("snapshots/", StringComparison.Ordinal)) _armed = true;
                return inner.CreateAsync(path, expectedLength, mode, resumeFrom, ct);
            }

            public ValueTask<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);

            public ValueTask DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

            public ValueTask<byte[]?> ServerHashAsync(string path, HashAlgorithmName algorithm, CancellationToken ct) => inner.ServerHashAsync(path, algorithm, ct);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    /// <summary>Fabryka, której OpenAsync czeka na bramkę — pozwala utrzymać pierwsze uruchomienie „w toku”.</summary>
    private sealed class GatedFactory(Task gate) : IStorageProviderFactory
    {
        public string Scheme => "local";

        public async ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
        {
            await gate.ConfigureAwait(false);
            return await new LocalStorageProviderFactory().OpenAsync(config, role, ct).ConfigureAwait(false);
        }
    }
}
