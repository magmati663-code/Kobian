using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.V1;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Engine.Restore;
using Kobian.Providers.Storage.Local;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// it. 98, BL-64 / R2.8.1: PlanRestore (dry-run) — liczby zgodne z późniejszym odtworzeniem, konflikty, wybór dokładnych ścieżek,
/// blokady KB-5001/5003/5106, brak zapisu danych i brak wiersza run_runs. Dane syntetyczne w katalogu tymczasowym.
/// </summary>
public sealed class RestorePlanTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-plan-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private CatalogJobService _jobs = null!;
    private Dictionary<string, IStorageProviderFactory> _providers = null!;
    private int _bigLen;
    private const string Small = "syntetyczne dane";
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.Combine(_dir, n);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        Directory.CreateDirectory(P("out"));
        var big = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 123);
        _bigLen = big.Length;
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), big);
        File.WriteAllBytes(Path.Combine(P("src"), "a-kopia.bin"), big); // te same bloki — czytane raz
        Directory.CreateDirectory(Path.Combine(P("src"), "sub"));
        File.WriteAllText(Path.Combine(P("src"), "sub", "b.txt"), Small);
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = Kobian.Crypto.RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(P("wrapped.key"), prov.KeyFileJson);
        File.WriteAllText(P("kobian.repo"), prov.HeaderJson);
        File.WriteAllText(P("pw.txt"), pw + "\n");
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        _providers = new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() };
        _jobs = new CatalogJobService(Catalog, _writer, _providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        AddJob();
        Assert.Equal(JobResult.Succeeded, (await _jobs.RunAsync("Pliki", _ct)).Result);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private CatalogRestoreService Service(Func<string, long?>? free = null, Func<string, bool>? canWrite = null) =>
        new(Catalog, _writer, _providers, TimeProvider.System)
        {
            Planners = r => new RestorePlanner(r) { FreeBytes = free ?? (_ => long.MaxValue), CanWrite = canWrite ?? RestorePlanner.ProbeWritable },
        };

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

    private long Runs()
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM run_runs;";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact, Trait("BL", "BL-64"), Trait("Plan", "R2.8.1")]
    public async Task Plan_of_whole_snapshot_counts_files_bytes_and_shared_blocks_once_without_writing_anything()
    {
        var runsBefore = Runs();
        var o = await Service().PlanAsync("latest", null, null, P("out"), _ct);
        Assert.Null(o.Code);
        var p = Assert.IsType<RestorePlan>(o.Plan);
        Assert.Equal(3, p.Files);
        Assert.Equal(2L * _bigLen + Small.Length, p.Bytes);
        Assert.Equal(1, p.NewDirectories); // sub
        Assert.Equal(0, p.Conflicts);
        Assert.Null(p.BlockingCode);
        Assert.True(p.Packs >= 1);
        Assert.InRange(p.ReadBytes, _bigLen, 2L * _bigLen - 1); // duplikat pliku nie podwaja odczytu (dedup)
        Assert.Empty(Directory.EnumerateFileSystemEntries(P("out"))); // dry-run: żadnego folderu ani pliku próbnego
        Assert.Equal(runsBefore, Runs()); // bez wiersza run_runs
        Assert.False(string.IsNullOrEmpty(o.SnapshotId));
    }

    [Fact, Trait("BL", "BL-64"), Trait("AC", "AC-07.1")]
    public async Task Conflicts_are_listed_and_match_what_restore_skips()
    {
        Directory.CreateDirectory(Path.Combine(P("out"), "sub"));
        File.WriteAllText(Path.Combine(P("out"), "sub", "b.txt"), "istniejacy");
        var svc = Service();
        var p = (await svc.PlanAsync("latest", null, null, P("out"), _ct)).Plan!;
        Assert.Equal(1, p.Conflicts);
        Assert.Equal(Small.Length, p.ConflictBytes);
        Assert.Equal(["sub/b.txt"], p.ConflictSample);
        Assert.Equal(0, p.NewDirectories); // sub już jest
        Assert.Null(p.BlockingCode); // konflikt = ostrzeżenie (plik zostanie pominięty), nie blokada

        var r = await svc.RestoreAsync("latest", null, P("out"), _ct);
        Assert.Equal((JobResult.Warning, KbCode.RestoreTargetExists), (r.Result, r.Code));
        Assert.Equal("istniejacy", File.ReadAllText(Path.Combine(P("out"), "sub", "b.txt"))); // bez nadpisania
        Assert.Equal(p.Files - p.Conflicts, Directory.GetFiles(P("out"), "*", SearchOption.AllDirectories).Length - 1);
    }

    [Fact, Trait("BL", "BL-64"), Trait("SEC", "Qb34")]
    public async Task Exact_path_plan_uses_the_same_selection_as_restore()
    {
        var p = (await Service().PlanAsync("latest", null, ["sub"], P("out"), _ct)).Plan!;
        Assert.Equal((1L, (long)Small.Length, 1L), (p.Files, p.Bytes, p.NewDirectories));

        var file = (await Service().PlanAsync("latest", null, ["a.bin"], P("out"), _ct)).Plan!;
        Assert.Equal((1L, 0L), (file.Files, file.NewDirectories));

        var none = (await Service().PlanAsync("latest", null, ["brak"], P("out"), _ct)).Plan!;
        Assert.Equal(KbCode.SnapshotPathNotFound, none.BlockingCode);

        foreach (var bad in new[] { "../repo", "", "sub/.." })
            Assert.Equal(KbCode.SnapshotPathNotFound, (await Service().PlanAsync("latest", null, [bad], P("out"), _ct)).Code);
    }

    [Fact, Trait("BL", "BL-64"), Trait("Plan", "R2.8.1")]
    public async Task Filter_plan_counts_only_matching_files()
    {
        var p = (await Service().PlanAsync("latest", "*.bin", null, P("out"), _ct)).Plan!;
        Assert.Equal(2, p.Files);
        Assert.Equal(2L * _bigLen, p.Bytes);
    }

    [Fact, Trait("BL", "BL-64"), Trait("KB", "KB-5106")]
    public async Task Not_enough_free_space_blocks_and_conflicting_bytes_do_not_count()
    {
        var need = 2L * _bigLen + Small.Length;
        Assert.Equal(KbCode.RestoreTargetSpace, (await Service(free: _ => need - 1).PlanAsync("latest", null, null, P("out"), _ct)).Plan!.BlockingCode);
        Assert.Null((await Service(free: _ => need).PlanAsync("latest", null, null, P("out"), _ct)).Plan!.BlockingCode);
        Assert.Null((await Service(free: _ => null).PlanAsync("latest", null, null, P("out"), _ct)).Plan!.BlockingCode); // nieznane = bez blokady

        File.WriteAllText(Path.Combine(P("out"), "a.bin"), "x"); // konflikt: a.bin nie będzie zapisany
        var p = (await Service(free: _ => _bigLen + Small.Length).PlanAsync("latest", null, null, P("out"), _ct)).Plan!;
        Assert.Null(p.BlockingCode);
        Assert.Equal(_bigLen + Small.Length, p.WriteBytes);
        Assert.Equal(_bigLen + Small.Length, p.FreeBytes);
    }

    [Fact, Trait("BL", "BL-64"), Trait("AC", "AC-07.2")]
    public async Task Target_without_write_permission_is_refused_before_any_data_is_read()
    {
        var p = (await Service(canWrite: _ => false).PlanAsync("latest", null, null, P("out"), _ct)).Plan!;
        Assert.Equal(KbCode.RestoreTargetAccessDenied, p.BlockingCode);
        Assert.True(RestorePlanner.ProbeWritable(P("out")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(P("out"))); // plik próbny usunięty przy zamknięciu
        Assert.False(RestorePlanner.ProbeWritable(Path.Combine(P("out"), "nie-ma")));
    }

    [Fact, Trait("BL", "BL-64")]
    public async Task Invalid_target_or_snapshot_returns_a_code_not_a_plan()
    {
        var svc = Service();
        Assert.Equal(KbCode.RestoreTargetAccessDenied, (await svc.PlanAsync("latest", null, null, "wzgledny", _ct)).Code);
        Assert.Equal(KbCode.RestoreTargetAccessDenied, (await svc.PlanAsync("latest", null, null, P("nie-ma"), _ct)).Code);
        Assert.Equal(KbCode.SnapshotNotFound, (await svc.PlanAsync("deadbeefdeadbeef", null, null, P("out"), _ct)).Code);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(P("link"), P("out"));
            Assert.Equal(KbCode.RestoreReparseRefused, (await svc.PlanAsync("latest", null, null, P("link"), _ct)).Code);
        }
        Assert.False(Directory.Exists(P("nie-ma")));
    }

    [Fact, Trait("BL", "BL-64"), Trait("SEC", "D2.9")]
    public async Task Backend_maps_the_plan_and_the_method_is_operator_and_non_modifying()
    {
        File.WriteAllText(Path.Combine(P("out"), "a.bin"), "x");
        var backend = new CatalogAgentBackend(Catalog, "test", restore: Service());
        var r = await backend.PlanRestoreAsync(new RestoreRequest { Snapshot = "latest", Target = P("out") }, _ct);
        Assert.False(r.HasCode);
        Assert.Equal((3L, 1L, 1L), (r.Files, r.Conflicts, r.NewDirectories));
        Assert.Equal(["a.bin"], r.ConflictSample);
        Assert.True(r.HasFreeBytes);
        Assert.False(r.HasBlockingCode);
        Assert.Equal(32, r.SnapshotId.Length); // id snapshotu = 16 bajtów hex

        var exact = new RestoreRequest { Snapshot = "latest", Target = P("out") };
        exact.ExactPaths.Add("brak");
        Assert.Equal(KbCode.SnapshotPathNotFound, (await backend.PlanRestoreAsync(exact, _ct)).BlockingCode);

        Assert.Equal(KbCode.OperationNotAvailable, (await new CatalogAgentBackend(Catalog, "test").PlanRestoreAsync(exact, _ct)).Code);
        var policy = MethodPolicy.Find("/kobian.v1.Agent/PlanRestore");
        Assert.Equal((AgentRole.Operator, false), (policy!.Required, policy.Modifying));
    }
}
