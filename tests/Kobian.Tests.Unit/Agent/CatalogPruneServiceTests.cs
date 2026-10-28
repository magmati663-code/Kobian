using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.V1;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Tests.Unit.Engine;
using Kobian.Providers.Storage.Local;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-25 cz. 3 (it. 67): plan prune w Agencie na prawdziwym repozytorium lokalnym — zapomniane w katalogu → nieżywe obiekty na celu; tylko odczyt
/// (cel i katalog bez zmian, bez wiersza run_runs). Dane syntetyczne w katalogu tymczasowym.
/// </summary>
public sealed class CatalogPruneServiceTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-prn-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private CatalogJobService _jobs = null!;
    private Dictionary<string, IStorageProviderFactory> _providers = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.Combine(_dir, n);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), RandomNumberGenerator.GetBytes(300_000));
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = Kobian.Crypto.RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(P("wrapped.key"), prov.KeyFileJson);
        File.WriteAllText(P("kobian.repo"), prov.HeaderJson);
        File.WriteAllText(P("pw.txt"), pw + "\n");
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        _providers = new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() };
        _jobs = new CatalogJobService(Catalog, _writer, _providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        var def = $$"""{"version":1,"sources":[{{J(P("src"))}}],"destination":{"scheme":"local","root":{{J(P("repo"))}}},"key":{"file":{{J(P("wrapped.key"))}},"passwordFile":{{J(P("pw.txt"))}},"repoHeader":{{J(P("kobian.repo"))}}},"packTargetSize":1048576}""";
        Exec("INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ('j1','Pliki','files',$d,x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');",
            ("$d", Encoding.UTF8.GetBytes(def)));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private void Exec(string sql, params (string Name, object Value)[] p)
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private Dictionary<string, string> TargetState() =>
        Directory.GetFiles(P("repo"), "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(P("repo"), f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);

    private async Task<string> Backup(string change)
    {
        File.WriteAllBytes(Path.Combine(P("src"), change + ".bin"), RandomNumberGenerator.GetBytes(200_000));
        var r = await _jobs.RunAsync("Pliki", _ct);
        Assert.True(r.Result == JobResult.Succeeded, r.Code);
        return r.SnapshotId!;
    }

    [Fact, Trait("BL", "BL-25"), Trait("AC", "AC-10.3"), Trait("PM", "PM-18")]
    public async Task Plan_reports_dead_objects_of_forgotten_chain_and_changes_nothing()
    {
        var s1 = await Backup("b");
        var s2 = await Backup("c"); // przyrost od s1
        Exec("UPDATE snap_snapshots SET suspect=1 WHERE snap_id=$s;", ("$s", s2)); // suspect wymusza kopię pełną = nowy łańcuch
        var s3 = await Backup("d");
        Assert.Equal(0, Scalar($"SELECT mode FROM snap_snapshots WHERE snap_id='{s3}';")); // Full

        var svc = new CatalogPruneService(Catalog, _providers, TimeProvider.System);
        var nothing = await svc.PlanAsync("Pliki", _ct);
        Assert.Null(nothing.Code);
        Assert.Empty(nothing.Plan!.Dead);
        Assert.Equal(3, nothing.Plan.LiveSnapshots);

        Exec("UPDATE snap_snapshots SET forgotten_at='2026-10-14T00:00:00Z' WHERE snap_id IN ($a,$b);", ("$a", s1), ("$b", s2));
        var before = TargetState();
        var runs = Scalar("SELECT COUNT(*) FROM run_runs;");
        var o = await svc.PlanAsync("Pliki", _ct);
        Assert.Null(o.Code);
        Assert.Equal(1, o.Plan!.LiveSnapshots);
        Assert.Equal(new[] { s1, s2 }.Order(StringComparer.Ordinal), o.Plan.Dead.Where(d => d.Kind == "snapshot").Select(d => d.Id));
        Assert.Contains(o.Plan.Dead, d => d.Kind == "pack");
        Assert.Contains(o.Plan.Dead, d => d.Kind == "index");
        Assert.Empty(o.Plan.Deletable); // bez wcześniejszych oznaczeń nic nie jest usuwalne (dwufazowość)
        Assert.Equal(before, TargetState());
        Assert.Equal(runs, Scalar("SELECT COUNT(*) FROM run_runs;"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM snap_snapshots WHERE forgotten_at IS NULL AND snap_id <> '" + s3 + "';"));

        var reply = await new CatalogAgentBackend(Catalog, "1", prune: svc).PlanPruneAsync(new PlanPruneArgs { Job = "Pliki" }, _ct);
        Assert.Equal(OverallState.Ok, reply.Result);
        Assert.Equal((1, 2), (reply.LiveSnapshots, reply.DeadSnapshots));
        Assert.True(reply.DeadPacks > 0 && reply.DeadIndexes > 0 && reply.DeadBytes > 0);
        Assert.Equal(86_400, reply.GraceSeconds);
        Assert.False(reply.HasCode);
    }

    [Fact, Trait("BL", "BL-25"), Trait("PM", "PM-18")]
    public async Task Forgetting_every_snapshot_blocks_and_unknown_job_or_missing_service_fail()
    {
        var s1 = await Backup("b");
        Exec("UPDATE snap_snapshots SET forgotten_at='2026-10-14T00:00:00Z' WHERE snap_id=$a;", ("$a", s1));
        var svc = new CatalogPruneService(Catalog, _providers, TimeProvider.System);
        var o = await svc.PlanAsync("Pliki", _ct);
        Assert.Equal(KbCode.PruneWouldEmptyRepository, o.Code);
        Assert.Empty(o.Plan!.Dead);

        // snapshot nieznany katalogowi (np. innego zadania lub instalacji) jest żywy — usunięcie wiersza katalogu nie czyni go nieżywym
        Exec("DELETE FROM snap_snapshots WHERE snap_id=$a;", ("$a", s1));
        var unknown = await svc.PlanAsync("Pliki", _ct);
        Assert.Null(unknown.Code);
        Assert.Equal(1, unknown.Plan!.LiveSnapshots);
        Assert.Empty(unknown.Plan.Dead);

        Assert.Equal(KbCode.JobNotFound, (await svc.PlanAsync("Nie-ma", _ct)).Code);
        Assert.Equal(KbCode.JobNotFound, (await svc.PlanAsync(new string('x', 201), _ct)).Code);
        var none = await new CatalogAgentBackend(Catalog, "1").PlanPruneAsync(new PlanPruneArgs { Job = "Pliki" }, _ct);
        Assert.Equal((OverallState.Failed, KbCode.OperationNotAvailable), (none.Result, none.Code));
        Assert.Equal(new MethodPolicy.Entry(AgentRole.Operator, false), MethodPolicy.Find("/kobian.v1.Agent/PlanPrune"));
    }

    private CatalogPruneService Executor(ManualTime t) =>
        new(Catalog, _providers, t, _writer, new byte[16], (d, _) => t.Advance(d));

    private string[] RepoFiles(string dir) =>
        Directory.Exists(Path.Combine(P("repo"), dir)) ? Directory.GetFiles(Path.Combine(P("repo"), dir), "*", SearchOption.AllDirectories) : [];

    [Fact, Trait("BL", "BL-25"), Trait("AC", "AC-10.3"), Trait("PM", "PM-18"), Trait("D", "D3.13")]
    public async Task Two_phase_prune_marks_first_deletes_after_grace_and_keeps_repository_restorable()
    {
        var s1 = await Backup("b");
        var s2 = await Backup("c");
        Exec("UPDATE snap_snapshots SET suspect=1 WHERE snap_id=$s;", ("$s", s2)); // nowy łańcuch
        var s3 = await Backup("d");
        Exec("UPDATE snap_snapshots SET forgotten_at='2026-10-14T00:00:00Z' WHERE snap_id IN ($a,$b);", ("$a", s1), ("$b", s2));
        Assert.Equal(3, RepoFiles("locks").Length); // każda kopia zostawia blokadę współdzieloną (sesja Backup nie usuwa — wygasa)

        var t = new ManualTime(DateTimeOffset.UtcNow.AddHours(1)); // blokady kopii już przeterminowane
        var svc = Executor(t);
        var packsBefore = RepoFiles("data").Length;

        // Faza 1: tylko oznaczenia — nic nie znika z celu poza przeterminowanymi blokadami
        var first = await svc.PruneAsync("Pliki", _ct);
        Assert.Null(first.Code);
        Assert.True(first.Marked > 0);
        Assert.Null(first.Deleted);
        Assert.Equal(3, first.LocksCleaned);
        Assert.Empty(RepoFiles("locks")); // własna wyłączna usunięta przy zwolnieniu
        Assert.Equal(packsBefore, RepoFiles("data").Length);
        Assert.Equal(first.Marked, Scalar("SELECT COUNT(*) FROM repo_prune_marks;"));
        var preview = await svc.PlanAsync("Pliki", _ct);
        Assert.Empty(preview.Plan!.Deletable); // podgląd widzi oznaczenia, karencja trwa

        // przed końcem karencji — nadal nic
        t.Now = t.Now.AddHours(23);
        var early = await svc.PruneAsync("Pliki", _ct);
        Assert.Null(early.Code);
        Assert.Equal(0, early.Marked);
        Assert.Null(early.Deleted);
        Assert.Equal(packsBefore, RepoFiles("data").Length);

        // Faza 2 po 24 h
        t.Now = t.Now.AddHours(2);
        Assert.Equal(first.Marked, (await svc.PlanAsync("Pliki", _ct)).Plan!.Deletable.Count);
        var second = await svc.PruneAsync("Pliki", _ct);
        Assert.Null(second.Code);
        Assert.Equal(first.Marked, second.Deleted!.Deleted.Count);
        Assert.Equal(2, second.Deleted.Deleted.Count(d => d.Kind == "snapshot"));
        Assert.True(RepoFiles("data").Length < packsBefore);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM repo_prune_marks;"));
        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM run_transitions WHERE reason='prune' AND seq=1 AND to_state='Succeeded';"));

        // repozytorium spójne: pełne check (V2 + V3 100 %) i odtworzenie żywego snapshotu bit w bit
        var check = await new CatalogCheckService(Catalog, _writer, _providers, TimeProvider.System).CheckAsync("Pliki", 100, null, _ct);
        Assert.Equal(CheckStatus.Succeeded, check.Status);
        Directory.CreateDirectory(P("out"));
        var restored = await new CatalogRestoreService(Catalog, _writer, _providers, TimeProvider.System).RestoreAsync(s3, null, P("out"), _ct);
        Assert.True(restored.Result == JobResult.Succeeded, restored.Code);
        foreach (var f in Directory.GetFiles(P("src")))
            Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(Directory.GetFiles(P("out"), Path.GetFileName(f), SearchOption.AllDirectories).Single()));

        // przyrost po prune nie wskazuje usuniętych packów
        var s4 = await Backup("e");
        Assert.Equal(1, Scalar($"SELECT mode FROM snap_snapshots WHERE snap_id='{s4}';"));
        Assert.Equal(CheckStatus.Succeeded, (await new CatalogCheckService(Catalog, _writer, _providers, TimeProvider.System).CheckAsync("Pliki", 100, null, _ct)).Status);
    }

    [Fact, Trait("BL", "BL-25"), Trait("D", "D3.16"), Trait("PM", "PM-17")]
    public async Task Prune_consolidates_indexes_and_deletes_superseded_ones_after_grace()
    {
        string last = "";
        for (var i = 0; i < 5; i++) last = await Backup("x" + i); // łańcuch przyrostów: 5 indeksów, ostatni snapshot odwołuje się do wszystkich
        var t = new ManualTime(DateTimeOffset.UtcNow.AddHours(1));
        var svc = new CatalogPruneService(Catalog, _providers, t, _writer, new byte[16], (d, _) => t.Advance(d))
        {
            Consolidation = new IndexConsolidationOptions(MaxActive: 3, MaxSmall: 10_000),
        };
        var first = await svc.PruneAsync("Pliki", _ct);
        Assert.Null(first.Code);
        Assert.NotNull(first.Consolidation);
        Assert.Equal((true, 5, 1, 1), (first.Consolidation!.Needed, first.Consolidation.Superseded, first.Consolidation.Written.Count, first.Consolidation.ActiveAfter));
        Assert.Equal(5, first.Marked); // zastąpione indeksy oznaczone w tym samym przebiegu
        Assert.Equal(6, RepoFiles("index").Length); // nic nie usunięto przed karencją

        t.Now = t.Now.AddHours(25);
        var reply = await new CatalogAgentBackend(Catalog, "1", prune: svc).PruneAsync(new PruneArgs { Job = "Pliki" }, _ct);
        Assert.Equal(OverallState.Ok, reply.Result);
        Assert.Equal((5, 0, 0, 1), (reply.DeletedIndexes, reply.IndexesConsolidated, reply.IndexesWritten, reply.ActiveIndexes)); // drugi przebieg: nic do scalania
        Assert.Single(RepoFiles("index"));

        var check = await new CatalogCheckService(Catalog, _writer, _providers, TimeProvider.System).CheckAsync("Pliki", 100, null, _ct);
        Assert.Equal(CheckStatus.Succeeded, check.Status);
        Directory.CreateDirectory(P("out"));
        var restored = await new CatalogRestoreService(Catalog, _writer, _providers, TimeProvider.System).RestoreAsync(last, null, P("out"), _ct);
        Assert.True(restored.Result == JobResult.Succeeded, restored.Code);
        foreach (var f in Directory.GetFiles(P("src")))
            Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(Directory.GetFiles(P("out"), Path.GetFileName(f), SearchOption.AllDirectories).Single()));

        // przyrost po konsolidacji: baza z indeksu skonsolidowanego (dedup) — kopia przyrostowa, check zielony
        var next = await Backup("y");
        Assert.Equal(1, Scalar($"SELECT mode FROM snap_snapshots WHERE snap_id='{next}';"));
        Assert.Equal(CheckStatus.Succeeded, (await new CatalogCheckService(Catalog, _writer, _providers, TimeProvider.System).CheckAsync("Pliki", 100, null, _ct)).Status);
    }

    [Fact, Trait("BL", "BL-25"), Trait("D", "D3.13"), Trait("KB", "KB-1202")]
    public async Task Prune_is_skipped_while_a_backup_lock_is_live_and_backup_waits_for_exclusive()
    {
        var s1 = await Backup("b");
        await Backup("c");
        Exec("UPDATE snap_snapshots SET forgotten_at='2026-10-14T00:00:00Z' WHERE snap_id=$a;", ("$a", s1));
        var before = TargetState();
        var now = new ManualTime(DateTimeOffset.UtcNow); // blokady kopii są żywe
        var o = await Executor(now).PruneAsync("Pliki", _ct);
        Assert.Equal(KbCode.RepoLocked, o.Code);
        Assert.Equal(before, TargetState());
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM repo_prune_marks;"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM run_runs r JOIN run_transitions t ON t.run_id=r.run_id AND t.seq=1 WHERE t.reason='prune' AND r.state='Skipped' AND r.kb_code='KB-1202';"));
        var reply = await new CatalogAgentBackend(Catalog, "1", prune: Executor(now)).PruneAsync(new PruneArgs { Job = "Pliki" }, _ct);
        Assert.Equal((OverallState.Warning, KbCode.RepoLocked), (reply.Result, reply.Code));

        // żywa blokada wyłączna (prune innego hosta) → kopia nie startuje, błąd przejściowy KB-1202
        using var keys = KeyFileLoader.Load(P("wrapped.key"), P("pw.txt"), P("kobian.repo"));
        foreach (var f in RepoFiles("locks")) File.Delete(f);
        var maint = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", P("repo")), CredentialRole.Maintenance, _ct);
        await using (maint)
        {
            var clock = new ManualTime(DateTimeOffset.UtcNow);
            await using var ex = await RepoLocks.AcquireExclusiveAsync(maint, keys, RepoLockOwner.For(new byte[16]), clock, null, (d, _) => clock.Advance(d), _ct);
            var r = await _jobs.RunAsync("Pliki", _ct);
            Assert.Equal((JobResult.Failed, KbCode.RepoLocked), (r.Result, r.Code));
        }
    }

    [Fact, Trait("BL", "BL-25"), Trait("PM", "PM-18"), Trait("KB", "KB-1603")]
    public async Task Deletion_stops_at_the_first_object_that_is_no_longer_deletable()
    {
        var s1 = await Backup("b");
        var s2 = await Backup("c");
        Exec("UPDATE snap_snapshots SET suspect=1 WHERE snap_id=$s;", ("$s", s2));
        await Backup("d");
        foreach (var f in RepoFiles("locks")) File.Delete(f);
        using var keys = KeyFileLoader.Load(P("wrapped.key"), P("pw.txt"), P("kobian.repo"));
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        var maint = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", P("repo")), CredentialRole.Maintenance, _ct);
        await using (maint)
        {
            var forgotten = new HashSet<string>([s1, s2], StringComparer.Ordinal);
            var inv = await PrunePlanner.CollectAsync(maint, keys, forgotten, _ct);
            var marks = PrunePlanner.Plan(inv, new Dictionary<string, DateTimeOffset>(), clock.Now, PrunePlanner.DefaultGrace).Dead
                .ToDictionary(d => d.Key, _ => clock.Now.AddDays(-2), StringComparer.Ordinal);
            var approved = PrunePlanner.Plan(inv, marks, clock.Now, PrunePlanner.DefaultGrace).Deletable;
            Assert.NotEmpty(approved);
            var before = TargetState();
            var deletedCount = -1;

            await using (var shared = await RepoLocks.AcquireSharedAsync(maint, keys, RepoLockOwner.For(new byte[16]), clock, null, _ct))
                await Assert.ThrowsAsync<InvalidOperationException>(() => PruneExecutor.DeleteAsync(maint, keys, shared, forgotten, marks, approved, clock, PrunePlanner.DefaultGrace, _ct));
            var backupSession = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", P("repo")), CredentialRole.Backup, _ct);
            await using (backupSession)
            {
                await using var lease = await RepoLocks.AcquireExclusiveAsync(maint, keys, RepoLockOwner.For(new byte[16]), clock, null, (d, _) => clock.Advance(d), _ct);
                await Assert.ThrowsAsync<InvalidOperationException>(() => PruneExecutor.DeleteAsync(backupSession, keys, lease, forgotten, marks, approved, clock, PrunePlanner.DefaultGrace, _ct));

                // s2 „wraca” (np. ktoś cofnął zapomnienie) między planem a usuwaniem → jego obiekty są żywe; usuwanie staje na pierwszym z nich, przed czymkolwiek żywym
                var stillForgotten = new HashSet<string>([s1], StringComparer.Ordinal);
                var d = await PruneExecutor.DeleteAsync(maint, keys, lease, stillForgotten, marks, approved, clock, PrunePlanner.DefaultGrace, _ct);
                Assert.Equal(KbCode.PruneStoppedInconsistent, d.StopCode);
                Assert.NotNull(d.StopObject);
                Assert.All(d.Deleted, x => Assert.Equal("snapshot:" + s1, x.Key)); // kolejność id: s1 przed s2 albo nic
                deletedCount = d.Deleted.Count;
            }
            Assert.Equal(before.Keys.Where(k => !k.StartsWith("locks", StringComparison.Ordinal)).Count() - deletedCount, TargetState().Keys.Count(k => !k.StartsWith("locks", StringComparison.Ordinal)));
        }
        var check = await new CatalogCheckService(Catalog, _writer, _providers, TimeProvider.System).CheckAsync("Pliki", 100, null, _ct);
        Assert.Equal(CheckStatus.Succeeded, check.Status);
    }
}
