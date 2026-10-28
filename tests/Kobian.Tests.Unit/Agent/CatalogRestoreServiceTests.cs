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

/// <summary>BL-43: restore w Agencie — backup (CatalogJobService) → RestoreAsync z katalogu; dane syntetyczne w katalogu tymczasowym.</summary>
public sealed class CatalogRestoreServiceTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-rst-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private CatalogJobService _jobs = null!;
    private CatalogRestoreService _restore = null!;
    private byte[] _big = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.Combine(_dir, n);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        Directory.CreateDirectory(P("out"));
        _big = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 123);
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), _big);
        Directory.CreateDirectory(Path.Combine(P("src"), "sub"));
        File.WriteAllText(Path.Combine(P("src"), "sub", "b.txt"), "syntetyczne dane");
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = Kobian.Crypto.RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(P("wrapped.key"), prov.KeyFileJson);
        File.WriteAllText(P("kobian.repo"), prov.HeaderJson);
        File.WriteAllText(P("pw.txt"), pw + "\n");
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        var providers = new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() };
        _jobs = new CatalogJobService(Catalog, _writer, providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        _restore = new CatalogRestoreService(Catalog, _writer, providers, TimeProvider.System);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private void AddJob()
    {
        var def = $$"""{"version":1,"sources":[{{J(P("src"))}}],"destination":{"scheme":"local","root":{{J(P("repo"))}}},"key":{"file":{{J(P("wrapped.key"))}},"passwordFile":{{J(P("pw.txt"))}},"repoHeader":{{J(P("kobian.repo"))}}},"packTargetSize":1048576}""";
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ('j1','Pliki','files',$d,x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');";
        cmd.Parameters.AddWithValue("$d", Encoding.UTF8.GetBytes(def));
        cmd.ExecuteNonQuery();
    }

    private long Query1(string sql)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private object?[] OneRun(string where)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT state, kb_code, snapshot_id, files, bytes_read FROM run_runs WHERE {where};";
        using var rd = cmd.ExecuteReader();
        Assert.True(rd.Read());
        var row = new object?[5];
        for (var i = 0; i < 5; i++) row[i] = rd.IsDBNull(i) ? null : rd.GetValue(i);
        return row;
    }

    [Fact, Trait("AC", "AC-07.1")]
    public async Task Restore_latest_by_id_and_by_prefix_gives_byte_identical_files_and_logs_the_run()
    {
        AddJob();
        var backup = await _jobs.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, backup.Result);

        var r = await _restore.RestoreAsync("latest", null, P("out"), _ct);
        Assert.Equal(JobResult.Succeeded, r.Result);
        Assert.Null(r.Code);
        Assert.Equal(backup.SnapshotId, r.SnapshotId);
        Assert.Equal(_big, await File.ReadAllBytesAsync(Path.Combine(P("out"), "a.bin"), _ct));
        Assert.Equal("syntetyczne dane", await File.ReadAllTextAsync(Path.Combine(P("out"), "sub", "b.txt"), _ct));

        var row = OneRun("cause='manual' AND snapshot_id IS NOT NULL AND run_id IN (SELECT run_id FROM run_transitions WHERE reason='restore')");
        Assert.Equal("Succeeded", row[0]);
        Assert.Equal(backup.SnapshotId, (string)row[2]!);
        Assert.Equal(2L, row[3]);
        Assert.Equal(_big.Length + "syntetyczne dane".Length, (long)row[4]!);

        Directory.CreateDirectory(P("out2"));
        var byPrefix = await _restore.RestoreAsync(backup.SnapshotId![..12].ToUpperInvariant(), "sub/*", P("out2"), _ct);
        Assert.Equal(JobResult.Succeeded, byPrefix.Result);
        Assert.False(File.Exists(Path.Combine(P("out2"), "a.bin")));
        Assert.True(File.Exists(Path.Combine(P("out2"), "sub", "b.txt")));
    }

    [Fact, Trait("RD", "RD-82")]
    public async Task Detached_restore_started_after_shutdown_is_cancelled_and_not_executed()
    {
        AddJob();
        var backup = await _jobs.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, backup.Result);

        _restore.CancelDetached();
        var (runId, failure) = await _restore.StartRestoreAsync("latest", null, null, P("out"), _ct);
        Assert.Null(failure);
        await _restore.WhenDetachedIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), _ct);

        Assert.Equal("Cancelled", OneRun($"run_id='{runId}'")[0]);
        Assert.False(File.Exists(Path.Combine(P("out"), "a.bin"))); // nic nie zostało odtworzone
    }

    [Fact, Trait("RD", "RD-81"), Trait("AC", "AC-07.1")]
    public async Task Detached_restore_returns_run_id_at_once_and_finishes_in_the_catalog()
    {
        AddJob();
        var backup = await _jobs.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, backup.Result);

        var (runId, failure) = await _restore.StartRestoreAsync("latest", null, null, P("out"), _ct);
        Assert.Null(failure);
        Assert.NotNull(runId);
        Assert.Equal(32, runId.Length);
        Assert.Equal(1L, Query1($"SELECT COUNT(*) FROM run_runs WHERE run_id='{runId}'")); // wiersz istnieje od razu (Running albo już gotowy)

        await _restore.WhenDetachedIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), _ct);
        var row = OneRun($"run_id='{runId}'");
        Assert.Equal("Succeeded", row[0]);
        Assert.Equal(backup.SnapshotId, (string)row[2]!);
        Assert.Equal(_big, await File.ReadAllBytesAsync(Path.Combine(P("out"), "a.bin"), _ct));
    }

    [Fact, Trait("RD", "RD-81")]
    public async Task Detached_restore_with_bad_target_or_snapshot_fails_synchronously_without_a_run_row()
    {
        AddJob();
        _ = await _jobs.RunAsync("Pliki", _ct);
        var before = Query1("SELECT COUNT(*) FROM run_runs");

        var (id1, f1) = await _restore.StartRestoreAsync("latest", null, null, "wzgledna/sciezka", _ct);
        Assert.Null(id1);
        Assert.Equal(KbCode.RestoreTargetAccessDenied, f1!.Code);
        var (id2, f2) = await _restore.StartRestoreAsync("deadbeefdeadbeef", null, null, P("out"), _ct);
        Assert.Null(id2);
        Assert.Equal(KbCode.SnapshotNotFound, f2!.Code);
        Assert.Equal(before, Query1("SELECT COUNT(*) FROM run_runs"));
    }

    [Fact, Trait("RD", "RD-81")]
    public async Task Backend_detached_restore_maps_to_run_id_and_failure_codes()
    {
        AddJob();
        _ = await _jobs.RunAsync("Pliki", _ct);
        var backend = new CatalogAgentBackend(Catalog, "test", jobs: _jobs, restore: _restore);

        var ok = await backend.RestoreDetachedAsync("latest", null, [], P("out"), _ct);
        Assert.Equal(OverallState.Ok, ok.Result);
        Assert.True(ok.HasRunId);
        await _restore.WhenDetachedIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), _ct);
        Assert.Equal("Succeeded", OneRun($"run_id='{ok.RunId}'")[0]);

        var bad = await backend.RestoreDetachedAsync("latest", null, [], "relative", _ct);
        Assert.Equal(OverallState.Failed, bad.Result);
        Assert.False(bad.HasRunId);
        Assert.Equal(KbCode.RestoreTargetAccessDenied, bad.Code);

        var none = await new CatalogAgentBackend(Catalog, "test").RestoreDetachedAsync("latest", null, [], P("out"), _ct);
        Assert.Equal(KbCode.OperationNotAvailable, none.Code);
    }

    [Fact, Trait("BL", "BL-70"), Trait("AC", "AC-37.1")]
    public async Task Latest_of_job_picks_that_jobs_newest_snapshot_not_the_global_newest()
    {
        AddJob();
        var first = await _jobs.RunAsync("Pliki", _ct);
        Directory.CreateDirectory(P("src2"));
        Directory.CreateDirectory(P("repo2"));
        File.WriteAllText(Path.Combine(P("src2"), "inne.txt"), "drugie zadanie");
        var def2 = $$"""{"version":1,"sources":[{{J(P("src2"))}}],"destination":{"scheme":"local","root":{{J(P("repo2"))}}},"key":{"file":{{J(P("wrapped.key"))}},"passwordFile":{{J(P("pw.txt"))}},"repoHeader":{{J(P("kobian.repo"))}}},"packTargetSize":1048576}""";
        using (var c = CatalogBootstrap.Open(Catalog))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ('j2','Druga',  'files',$d,x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');";
            cmd.Parameters.AddWithValue("$d", Encoding.UTF8.GetBytes(def2));
            cmd.ExecuteNonQuery();
        }
        var second = await _jobs.RunAsync("Druga", _ct);
        Assert.Equal((JobResult.Succeeded, JobResult.Succeeded), (first.Result, second.Result));

        // samo „latest” = najnowsza ze wszystkich zadań (zachowanie udokumentowane), „latest:<zadanie>” = najnowsza TEGO zadania
        Assert.Equal(second.SnapshotId, (await _restore.BrowseAsync("latest", "", _ct)).SnapshotId);
        Assert.Equal(first.SnapshotId, (await _restore.BrowseAsync("latest:Pliki", "", _ct)).SnapshotId);
        Assert.Equal(first.SnapshotId, (await _restore.BrowseAsync("LATEST:Pliki", "", _ct)).SnapshotId);
        Directory.CreateDirectory(P("out-j1"));
        var r = await _restore.RestoreAsync("latest:Pliki", null, P("out-j1"), _ct);
        Assert.Equal((JobResult.Succeeded, first.SnapshotId), (r.Result, r.SnapshotId));
        Assert.True(File.Exists(Path.Combine(P("out-j1"), "a.bin")));
        Assert.False(File.Exists(Path.Combine(P("out-j1"), "inne.txt")));

        // nieznane zadanie, pusta nazwa, nazwa zbyt długa, nazwa zadania nie jest wzorcem LIKE → brak kopii (KB-5002), bez run_runs
        var before = Query1("SELECT COUNT(*) FROM run_runs;");
        foreach (var bad in new[] { "latest:Brak", "latest:", "latest:" + new string('x', CatalogRestoreService.MaxJobNameLength + 1), "latest:%", "latest:Plik_" })
            Assert.Equal(KbCode.SnapshotNotFound, (await _restore.RestoreAsync(bad, null, P("out"), _ct)).Code);
        Assert.Equal(before, Query1("SELECT COUNT(*) FROM run_runs;"));
    }

    [Fact, Trait("AC", "AC-07.2")]
    public async Task Restore_never_overwrites_existing_files_and_reports_warning()
    {
        AddJob();
        await _jobs.RunAsync("Pliki", _ct);
        await File.WriteAllTextAsync(Path.Combine(P("out"), "a.bin"), "moje", _ct);
        var r = await _restore.RestoreAsync("latest", null, P("out"), _ct);
        Assert.Equal((JobResult.Warning, KbCode.RestoreTargetExists), (r.Result, r.Code));
        Assert.Equal("moje", await File.ReadAllTextAsync(Path.Combine(P("out"), "a.bin"), _ct));
        Assert.True(File.Exists(Path.Combine(P("out"), "sub", "b.txt")));
        Assert.Equal("Warning", OneRun("kb_code='KB-5105'")[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("zzzzzzzzzz")]
    [InlineData("abc")]
    [InlineData("0000000000000000")]
    [InlineData("100%")]
    public async Task Unknown_or_malformed_snapshot_reference_is_rejected_without_a_run_row(string reference)
    {
        AddJob();
        await _jobs.RunAsync("Pliki", _ct);
        var r = await _restore.RestoreAsync(reference, null, P("out"), _ct);
        Assert.Equal((JobResult.Failed, KbCode.SnapshotNotFound), (r.Result, r.Code));
        Assert.Empty(Directory.GetFileSystemEntries(P("out")));
    }

    [Fact]
    public async Task Latest_without_any_snapshot_and_relative_or_missing_target_fail_with_distinct_codes()
    {
        Assert.Equal(KbCode.SnapshotNotFound, (await _restore.RestoreAsync("latest", null, P("out"), _ct)).Code);
        AddJob();
        await _jobs.RunAsync("Pliki", _ct);
        Assert.Equal(KbCode.RestoreTargetAccessDenied, (await _restore.RestoreAsync("latest", null, "wzgledna/sciezka", _ct)).Code);
        var missing = await _restore.RestoreAsync("latest", null, P("nie-ma-takiego"), _ct);
        Assert.Equal((JobResult.Failed, KbCode.RestoreTargetAccessDenied), (missing.Result, missing.Code));
        Assert.False(Directory.Exists(P("nie-ma-takiego"))); // restore nie tworzy celu
    }

    [Fact, Trait("AC", "AC-37.1")]
    public async Task Backend_Restore_maps_outcome_and_without_service_reports_unavailable()
    {
        AddJob();
        await _jobs.RunAsync("Pliki", _ct);
        var ok = await new CatalogAgentBackend(Catalog, "t", _jobs, null, _restore).RestoreAsync("latest", null, P("out"), _ct);
        Assert.Equal(OverallState.Ok, ok.Result);
        Assert.False(string.IsNullOrEmpty(ok.SnapshotId));
        var none = await new CatalogAgentBackend(Catalog, "t", _jobs).RestoreAsync("latest", null, P("out"), _ct);
        Assert.Equal(KbCode.OperationNotAvailable, none.Code);
    }

    [Fact, Trait("AC", "AC-07.3")]
    public async Task Tampered_pack_fails_restore_with_integrity_code_and_logs_failed_run()
    {
        AddJob();
        await _jobs.RunAsync("Pliki", _ct);
        var pack = Directory.EnumerateFiles(P("repo"), "*.pack", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).Length).First();
        var bytes = await File.ReadAllBytesAsync(pack, _ct);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(pack, bytes, _ct);
        var r = await _restore.RestoreAsync("latest", null, P("out"), _ct);
        Assert.Equal(JobResult.Failed, r.Result);
        Assert.Contains(r.Code, new[] { KbCode.RestoreHashMismatch, KbCode.RestorePackMissing });
        Assert.Equal(1, Query1("SELECT COUNT(*) FROM run_runs WHERE state='Failed' AND snapshot_id IS NOT NULL;"));
    }

    [Fact, Trait("AC", "AC-07.1"), Trait("BL", "BL-63")]
    public async Task Browse_lists_one_folder_dirs_first_without_reading_file_data_or_logging_a_run()
    {
        AddJob();
        var backup = await _jobs.RunAsync("Pliki", _ct);
        var runsBefore = Query1("SELECT COUNT(*) FROM run_runs;");

        var root = await _restore.BrowseAsync("latest", "", _ct);
        Assert.Null(root.Code);
        Assert.Equal(backup.SnapshotId, root.SnapshotId);
        var l = Assert.IsType<Kobian.Engine.Restore.BrowseListing>(root.Listing);
        Assert.Equal("", l.Path);
        Assert.False(l.Truncated);
        Assert.Equal(["sub", "a.bin"], l.Entries.Select(e => e.Name));
        Assert.Equal(Kobian.Engine.Restore.BrowseEntryKind.Directory, l.Entries[0].Kind);
        Assert.Equal(0, l.Entries[0].Size);
        Assert.Equal((Kobian.Engine.Restore.BrowseEntryKind.File, (long)_big.Length), (l.Entries[1].Kind, l.Entries[1].Size));
        Assert.NotNull(l.Entries[1].ModifiedUtc);

        foreach (var path in new[] { "sub", "/sub/", "sub\\" })
        {
            var sub = await _restore.BrowseAsync(backup.SnapshotId![..10], path, _ct);
            Assert.Null(sub.Code);
            Assert.Equal("sub", sub.Listing!.Path);
            var e = Assert.Single(sub.Listing.Entries);
            Assert.Equal(("b.txt", (long)"syntetyczne dane".Length), (e.Name, e.Size));
        }
        Assert.Equal(runsBefore, Query1("SELECT COUNT(*) FROM run_runs;")); // przeglądanie nie jest uruchomieniem
    }

    [Theory, Trait("SEC", "D8.9"), Trait("BL", "BL-63")]
    [InlineData("../repo")]
    [InlineData("sub/..")]
    [InlineData("sub/./x")]
    [InlineData("a.bin")]
    [InlineData("brak")]
    [InlineData("sub/b.txt")]
    [InlineData("SUB")]
    [InlineData("sub\0x")]
    public async Task Browse_rejects_paths_that_are_not_folders_of_the_snapshot(string path)
    {
        AddJob();
        await _jobs.RunAsync("Pliki", _ct);
        var r = await _restore.BrowseAsync("latest", path.Replace("\\0", "\0", StringComparison.Ordinal), _ct);
        Assert.Null(r.Listing);
        Assert.Equal(KbCode.SnapshotPathNotFound, r.Code);
    }

    [Fact, Trait("BL", "BL-63")]
    public async Task Browse_unknown_snapshot_and_too_deep_paths_fail_with_codes()
    {
        AddJob();
        await _jobs.RunAsync("Pliki", _ct);
        Assert.Equal(KbCode.SnapshotNotFound, (await _restore.BrowseAsync("ffffffffffff", "", _ct)).Code);
        var deep = string.Join('/', Enumerable.Repeat("a", Kobian.Engine.Restore.SnapshotBrowser.MaxDepth + 1));
        Assert.Equal(KbCode.SnapshotPathNotFound, (await _restore.BrowseAsync("latest", deep, _ct)).Code);
        Assert.Equal(KbCode.SnapshotPathNotFound, (await _restore.BrowseAsync("latest", new string('a', 5000), _ct)).Code);
    }

    [Fact, Trait("BL", "BL-63"), Trait("SEC", "D2.9")]
    public async Task BrowseTree_rpc_maps_listing_and_is_a_reader_method()
    {
        AddJob();
        var backup = await _jobs.RunAsync("Pliki", _ct);
        var backend = new CatalogAgentBackend(Catalog, "test", restore: _restore);
        var reply = await backend.BrowseTreeAsync(new BrowseTreeArgs { Snapshot = "latest" }, _ct);
        Assert.Equal(OverallState.Ok, reply.Result);
        Assert.False(reply.HasCode);
        Assert.Equal(backup.SnapshotId, reply.SnapshotId);
        Assert.Equal([Api.V1.BrowseEntryKind.Directory, Api.V1.BrowseEntryKind.File], reply.Entries.Select(e => e.Kind));
        Assert.True(reply.Entries[1].HasModifiedUnixMs);

        var bad = await backend.BrowseTreeAsync(new BrowseTreeArgs { Snapshot = "latest", Path = "brak" }, _ct);
        Assert.Equal((OverallState.Failed, KbCode.SnapshotPathNotFound), (bad.Result, bad.Code));
        var none = await new CatalogAgentBackend(Catalog, "test").BrowseTreeAsync(new BrowseTreeArgs { Snapshot = "latest" }, _ct);
        Assert.Equal(KbCode.OperationNotAvailable, none.Code);

        var p = MethodPolicy.Find("/kobian.v1.Agent/BrowseTree");
        Assert.NotNull(p);
        Assert.Equal((AgentRole.Reader, false), (p!.Required, p.Modifying));
    }

    [Fact, Trait("AC", "AC-07.1"), Trait("BL", "BL-64")]
    public async Task Single_file_filter_from_SCR07_restores_only_that_file_with_its_path()
    {
        AddJob();
        await _jobs.RunAsync("Pliki", _ct);
        var r = await _restore.RestoreAsync("latest", "sub/b.txt", P("out"), _ct);
        Assert.Equal(JobResult.Succeeded, r.Result);
        Assert.Equal("syntetyczne dane", await File.ReadAllTextAsync(Path.Combine(P("out"), "sub", "b.txt"), _ct));
        Assert.False(File.Exists(Path.Combine(P("out"), "a.bin")));
        Assert.Equal(1L, (long)OneRun("run_id IN (SELECT run_id FROM run_transitions WHERE reason='restore')")[3]!);
    }

    [Fact, Trait("AC", "AC-07.1"), Trait("BL", "BL-64"), Trait("SEC", "Qb34")]
    public async Task Exact_paths_restore_only_the_selection_without_wildcards()
    {
        AddJob();
        var wild = !OperatingSystem.IsWindows(); // nazwa z „*” możliwa tylko poza Windows
        if (wild)
        {
            File.WriteAllText(Path.Combine(P("src"), "x*y.txt"), "gwiazdka");
            File.WriteAllText(Path.Combine(P("src"), "xay.txt"), "sasiad");
        }
        await _jobs.RunAsync("Pliki", _ct);

        var dir = await _restore.RestoreExactAsync("latest", ["sub"], P("out"), _ct);
        Assert.Equal(JobResult.Succeeded, dir.Result);
        Assert.True(File.Exists(Path.Combine(P("out"), "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(P("out"), "a.bin")));

        if (wild)
        {
            Directory.CreateDirectory(P("out2"));
            Assert.Equal(JobResult.Succeeded, (await _restore.RestoreExactAsync("latest", ["x*y.txt"], P("out2"), _ct)).Result);
            Assert.Equal(["x*y.txt"], Directory.GetFiles(P("out2")).Select(Path.GetFileName));
        }

        Directory.CreateDirectory(P("out3"));
        foreach (var bad in new[] { "brak", "../repo", "sub/..", "" })
        {
            var r = await _restore.RestoreExactAsync("latest", [bad], P("out3"), _ct);
            Assert.Equal((JobResult.Failed, (string?)KbCode.SnapshotPathNotFound), (r.Result, r.Code));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(P("out3")));
    }
}
