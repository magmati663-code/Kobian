using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Agent.Notifications;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Format.Pack;
using Kobian.Providers.Storage.Local;
using Kobian.Scheduler.Locks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-26 (3.7 V3, D7.4, D3.13, D3.14): okresowa weryfikacja próbkowa z harmonogramu Agenta na wirtualnym zegarze — termin, pierwszeństwo kopii (blokada repo),
/// brak duplikatów po awarii, rotacja zapamiętana w katalogu (pokrycie 100% w ⌈100/%⌉ cyklach), wykrycie uszkodzonego bloku w cyklu jego kubełka
/// (suspect, KB-15xx, run.finished w outboxie dla A04, wymuszenie kopii pełnej), verify_level 3 i pola GetStatus. Repozytorium lokalne, dane syntetyczne.
/// </summary>
public sealed class VerifyScheduleTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-v3s-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(T0);
    private readonly Dictionary<string, IStorageProviderFactory> _providers = new() { ["local"] = new LocalStorageProviderFactory() };
    private CatalogWriter _writer = null!;
    private CatalogJobService _jobs = null!;
    private CatalogCheckService _check = null!;

    private string Catalog => Path.Combine(_dir, "catalog.db");

    private string P(string n) => Path.Combine(_dir, n);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        var rnd = new Random(2610);
        for (var i = 0; i < 6; i++)
        {
            var b = new byte[1_200_000 + (i * 1000)];
            rnd.NextBytes(b);
            File.WriteAllBytes(Path.Combine(P("src"), $"f{i}.bin"), b); // ≥ 1 MiB → osobne packi danych
        }
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(P("wrapped.key"), prov.KeyFileJson);
        File.WriteAllText(P("kobian.repo"), prov.HeaderJson);
        File.WriteAllText(P("pw.txt"), pw + "\n");
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        _jobs = new CatalogJobService(Catalog, _writer, _providers, new byte[16], _time, (_, _) => Task.CompletedTask) { Locks = new LockManager(2) };
        _check = new CatalogCheckService(Catalog, _writer, _providers, _time) { NotifyChannels = [new NotifyChannel(OutboxDispatcher.WebhookChannel, true)] };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static string J(string s) => JsonSerializer.Serialize(s);

    private string Definition(string? verify) =>
        "{\"version\":1,\"sources\":[" + J(P("src")) + "],\"destination\":{\"scheme\":\"local\",\"root\":" + J(P("repo")) + "},\"key\":{\"file\":" + J(P("wrapped.key"))
        + ",\"passwordFile\":" + J(P("pw.txt")) + ",\"repoHeader\":" + J(P("kobian.repo")) + "},\"packTargetSize\":1048576" + (verify is null ? "" : ",\"verify\":" + verify) + "}";

    private async Task<string> JobWithBackup(string? verify = null)
    {
        Exec("INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ('j1','Pliki','files',$d,x'00',1,'UTC','skip','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');",
            ("$d", Encoding.UTF8.GetBytes(Definition(verify))));
        var b = await _jobs.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, b.Result);
        return b.SnapshotId!;
    }

    private CatalogVerifyScheduler Scheduler() => new(Catalog, _check, _jobs.Locks!, _time, _jobs) { EvaluateEvery = TimeSpan.Zero };

    private async Task<IReadOnlyList<string>> TickAndDrain(CatalogVerifyScheduler s)
    {
        var started = s.Tick(_ct);
        await s.DrainAsync();
        return started;
    }

    private void Exec(string sql, params (string Name, object? Value)[] p)
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object Value)[] p)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return (T)cmd.ExecuteScalar()!;
    }

    private List<(string State, string? Code, string Cause)> VerifyRuns()
    {
        var list = new List<(string, string?, string)>();
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT r.state, r.kb_code, r.cause FROM run_runs r JOIN run_transitions t ON t.run_id=r.run_id AND t.seq=0 WHERE t.reason='verify' ORDER BY r.queued_at, r.rowid;";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add((rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetString(2)));
        return list;
    }

    private List<string> PackHexOnTarget() =>
        [.. Directory.EnumerateFiles(Path.Combine(P("repo"), "data"), "*.pack", SearchOption.AllDirectories).Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal)];

    private HashSet<string> VerifiedPacks(int ok) =>
        [.. Rows("SELECT pack_id FROM sched_verify_packs WHERE ok=" + ok + ";")];

    private List<string> Rows(string sql)
    {
        var list = new List<string>();
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetString(0));
        return list;
    }

    /// <summary>Pack bez bloków drzew (uszkodzenie nie psuje V2 — wykryć go może tylko V3).</summary>
    private async Task<string> DataOnlyPackHex(string snapId)
    {
        using var keys = KeyFileLoader.Load(P("wrapped.key"), P("pw.txt"), P("kobian.repo"));
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", P("repo")), CredentialRole.Backup, _ct);
        var snap = await MetadataStore.LoadSnapshotAsync(s, keys, Convert.FromHexString(snapId), _ct);
        foreach (var (indexId, _) in snap.Indexes)
        {
            var (idx, _) = await MetadataStore.LoadIndexAsync(s, keys, indexId, _ct);
            foreach (var pk in idx.Packs)
                if (pk.Entries.All(e => e.BlobType != BlobType.Tree)) return Convert.ToHexStringLower(pk.PackId);
        }
        throw new InvalidOperationException("brak packa danych");
    }

    private string PackPath(string hex) => Path.Combine(P("repo"), "data", hex[..2], hex + ".pack");

    // ---------------- czyste reguły ----------------

    [Fact, Trait("BL", "BL-26"), Trait("V", "V3"), Trait("Plan", "3.7")]
    public void Due_time_follows_interval_from_last_completed_cycle_and_backs_off_after_unfinished_attempt()
    {
        var p = VerifyPolicy.Default;
        var hour = TimeSpan.FromHours(1);
        Assert.Equal((true, 7, 5, 20), (p.Enabled, p.IntervalDays, p.SamplePercent, p.CyclesForFullCoverage)); // 3.7: tygodniowo, 5%, pokrycie w 20 cyklach
        Assert.Null(CatalogVerifyScheduler.DueAt(p, null, null, null, hour)); // brak kopii — nic do sprawdzenia
        Assert.Null(CatalogVerifyScheduler.DueAt(p with { Enabled = false }, T0, null, null, hour));
        Assert.Equal(T0.AddDays(7), CatalogVerifyScheduler.DueAt(p, T0, null, null, hour)); // pierwsza: tydzień po pierwszej kopii
        Assert.Equal(T0.AddDays(10), CatalogVerifyScheduler.DueAt(p, T0, T0.AddDays(3), T0.AddDays(3), hour)); // od ostatniego ukończonego cyklu
        // próba nieukończona (awaria / cel niedostępny) po terminie → ponowienie po odstępie, nie od razu
        Assert.Equal(T0.AddDays(10).AddHours(5), CatalogVerifyScheduler.DueAt(p, T0, T0.AddDays(3), T0.AddDays(10).AddHours(4), hour));
        // próba starsza niż ukończony cykl nie przesuwa terminu
        Assert.Equal(T0.AddDays(10), CatalogVerifyScheduler.DueAt(p, T0, T0.AddDays(3), T0.AddDays(2), hour));
    }

    [Fact, Trait("BL", "BL-26"), Trait("V", "V3"), Trait("Plan", "3.7")]
    public void Verify_policy_is_optional_backward_compatible_and_validated()
    {
        var baseDef = Definition(null);
        Assert.Null(JobDefinition.Parse("files", Encoding.UTF8.GetBytes(baseDef)).Verify);
        Assert.Equal(VerifyPolicy.Default, JobDefinition.Parse("files", Encoding.UTF8.GetBytes(baseDef)).EffectiveVerify);
        Assert.Equal(new VerifyPolicy(false, 14, 10), JobDefinition.Parse("files", Encoding.UTF8.GetBytes(Definition("{\"enabled\":false,\"intervalDays\":14,\"samplePercent\":10}"))).Verify);
        Assert.Equal(new VerifyPolicy(true, 7, 100), JobDefinition.Parse("files", Encoding.UTF8.GetBytes(Definition("{\"samplePercent\":100}"))).Verify);
        foreach (var bad in new[] { "{\"samplePercent\":0}", "{\"samplePercent\":101}", "{\"intervalDays\":0}", "{\"intervalDays\":367}", "{\"enabled\":1}", "{\"x\":1}", "[]", "{\"samplePercent\":\"5\"}" })
        {
            var ex = Assert.Throws<KobianException>(() => JobDefinition.Parse("files", Encoding.UTF8.GetBytes(Definition(bad))));
            Assert.Equal(KbCode.JobDefinitionInvalid, ex.Code);
        }
    }

    [Fact, Trait("BL", "BL-26"), Trait("US", "US-29")]
    public void Verify_section_survives_export_canonicalisation_and_unknown_fields_are_rejected()
    {
        var (canonical, problems) = JobExportFormat.CanonicalDefinition("files", Encoding.UTF8.GetBytes(Definition("{\"intervalDays\":14,\"samplePercent\":10}")));
        Assert.Empty(problems);
        Assert.Equal(new VerifyPolicy(true, 14, 10), JobDefinition.Parse("files", canonical!).Verify);
        var (_, bad) = JobExportFormat.CanonicalDefinition("files", Encoding.UTF8.GetBytes(Definition("{\"cron\":\"* * * * *\"}")));
        Assert.NotEmpty(bad);
    }

    [Fact, Trait("BL", "BL-26"), Trait("V", "V3")]
    public async Task Manual_check_with_full_sample_also_grants_verify_level_3_without_touching_the_schedule()
    {
        var snap = await JobWithBackup();
        var o = await _check.CheckAsync("Pliki", 100, null, _ct);
        Assert.Equal(CheckStatus.Succeeded, o.Status);
        Assert.Equal(3L, Scalar<long>("SELECT verify_level FROM snap_snapshots WHERE snap_id=$s;", ("$s", snap)));
        Assert.Equal(PackHexOnTarget().ToHashSet(), VerifiedPacks(1));
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM sched_verify_jobs;")); // ręczny check nie przesuwa rotacji ani terminu
    }

    [Fact, Trait("BL", "BL-26"), Trait("V", "V3"), Trait("Plan", "3.7"), Trait("Category", "Property")]
    public void Stable_sample_covers_every_pack_exactly_once_in_N_consecutive_cycles_even_when_packs_are_added()
    {
        var rnd = new Random(3_7_26);
        foreach (var percent in new[] { 1, 3, 5, 7, 10, 20, 33, 34, 50, 99, 100 })
        {
            var buckets = RepoChecker.Buckets(percent);
            Assert.Equal((int)Math.Ceiling(100.0 / percent), buckets);
            for (var trial = 0; trial < 20; trial++)
            {
                var start = rnd.NextInt64(-1_000, 1_000_000);
                var initial = Enumerable.Range(0, rnd.Next(0, 300)).Select(_ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))).ToList();
                var present = new List<string>(initial);
                var seen = initial.ToDictionary(h => h, _ => 0, StringComparer.Ordinal);
                for (var cycle = 0; cycle < buckets; cycle++)
                {
                    var slot = RepoChecker.Slot(start + cycle, buckets);
                    foreach (var h in present.Where(h => RepoChecker.SampleBucket(h, buckets) == slot))
                        if (seen.TryGetValue(h, out var n)) seen[h] = n + 1;
                    // packi dopisane między cyklami (nowe kopie) nie zmieniają kubełków istniejących
                    present.AddRange(Enumerable.Range(0, rnd.Next(0, 20)).Select(_ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))));
                }
                Assert.All(seen.Values, n => Assert.Equal(1, n)); // każdy pack obecny przez N cykli — dokładnie raz
            }
        }
        Assert.Throws<ArgumentException>(() => RepoChecker.SampleBucket("zz", 3));
        Assert.Equal(RepoChecker.Slot(2, 3), RepoChecker.Slot(-1, 3));
    }

    [Fact, Trait("BL", "BL-26"), Trait("Plan", "D7.4")]
    public void Verify_takes_the_repository_exclusively_and_backups_share_it()
    {
        var def = JobDefinition.Parse("files", Encoding.UTF8.GetBytes(Definition(null)));
        var bytes = Encoding.UTF8.GetBytes(Definition(null));
        var other = JobDefinition.Parse("files", Encoding.UTF8.GetBytes(Definition(null).Replace(J(P("repo")), J(P("repo2")), StringComparison.Ordinal)));
        var locks = new LockManager(4);
        Assert.True(locks.TryAcquireAll("b1", RunResourceLocks.ForBackup("files", bytes)));
        Assert.True(locks.TryAcquireAll("b2", RunResourceLocks.ForBackup("files", bytes))); // kopie tego repo — jak dotąd równolegle
        Assert.False(locks.TryAcquireAll("v", RunResourceLocks.ForVerify(def)));         // V3 czeka na kopie
        Assert.True(locks.TryAcquireAll("v2", RunResourceLocks.ForVerify(other)));       // inne repozytorium — bez konfliktu
        locks.Release("b1");
        locks.Release("b2");
        Assert.True(locks.TryAcquireAll("v", RunResourceLocks.ForVerify(def)));
        Assert.False(locks.TryAcquireAll("b3", RunResourceLocks.ForBackup("files", bytes))); // kopia czeka na V3 (D7.4)
        Assert.Empty(RunResourceLocks.ForBackup("files", Encoding.UTF8.GetBytes("nie json")));
    }

    [Fact, Trait("BL", "BL-26"), Trait("AC", "AC-09.1"), Trait("V", "V3")]
    public void Scheduled_code_is_always_in_the_15xx_range_for_damage()
    {
        CheckReport R(CheckStatus s, params string[] codes) =>
            new(s, 3, 1, 1, 1, 1, 1, [], [.. codes.Select(c => new CheckIssue(c, "pack:" + new string('a', 32), null))], false);
        Assert.Equal(KbCode.VerifyScheduledFailed, CatalogCheckService.ScheduledCode(R(CheckStatus.Failed, KbCode.AuthenticationFailed)));
        Assert.Equal(KbCode.CheckPackMissing, CatalogCheckService.ScheduledCode(R(CheckStatus.Failed, KbCode.CheckOrphanObject, KbCode.ReferenceIntegrityFailed, KbCode.CheckPackMissing)));
        Assert.Equal(KbCode.CheckOrphanObject, CatalogCheckService.ScheduledCode(R(CheckStatus.Warning, KbCode.CheckOrphanObject)));
        Assert.Null(CatalogCheckService.ScheduledCode(R(CheckStatus.Succeeded)));
    }

    // ---------------- harmonogram na wirtualnym zegarze ----------------

    [Fact, Trait("BL", "BL-26"), Trait("V", "V3"), Trait("Plan", "3.7")]
    public async Task Due_cycle_runs_once_through_the_check_path_records_rotation_and_emits_run_finished()
    {
        await JobWithBackup();
        var s = Scheduler();
        _time.Now = T0.AddDays(7).AddMinutes(-1);
        Assert.Empty(await TickAndDrain(s)); // przed terminem
        Assert.Empty(VerifyRuns());

        _time.Now = T0.AddDays(7);
        Assert.Equal(["j1"], await TickAndDrain(s));
        Assert.Equal([("Succeeded", (string?)null, "schedule")], VerifyRuns());
        Assert.Equal(1L, Scalar<long>("SELECT rotation FROM sched_verify_jobs WHERE job_id='j1';"));
        Assert.Equal(5L, Scalar<long>("SELECT sample_percent FROM sched_verify_jobs WHERE job_id='j1';"));
        Assert.Empty(await TickAndDrain(s)); // ukończony — następny za tydzień
        _time.Now = T0.AddDays(14).AddMinutes(-1);
        Assert.Empty(await TickAndDrain(s));
        _time.Now = T0.AddDays(14);
        Assert.Equal(["j1"], await TickAndDrain(s));
        Assert.Equal(2L, Scalar<long>("SELECT rotation FROM sched_verify_jobs WHERE job_id='j1';"));

        // run.finished w outboxie (cause schedule) — dane dla A04 (usługa kopii w tym teście nie ma kanałów, więc to wyłącznie zdarzenia V3)
        var payloads = Rows("SELECT CAST(payload AS TEXT) FROM core_outbox ORDER BY rowid;");
        Assert.Equal(2, payloads.Count);
        using var ev = JsonDocument.Parse(payloads[^1]);
        Assert.Equal("pl.kobian.run.finished.v1", ev.RootElement.GetProperty("type").GetString());
        Assert.Equal("schedule", ev.RootElement.GetProperty("data").GetProperty("cause").GetString());
        Assert.Equal("succeeded", ev.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(2, _jobs.Locks!.GlobalFree); // blokady zwolnione
    }

    [Fact, Trait("BL", "BL-26"), Trait("Plan", "D7.9")]
    public async Task Agent_scheduler_loop_turns_the_verification_after_queued_backups()
    {
        await JobWithBackup();
        using var loop = new CatalogScheduler(Catalog, _writer, _jobs, _time, 2, (min, _) => min) { Verify = Scheduler() };
        _time.Now = T0.AddDays(7);
        loop.Tick(_ct);
        await loop.DrainAsync();
        Assert.Equal([("Succeeded", (string?)null, "schedule")], VerifyRuns());
    }

    [Fact, Trait("BL", "BL-26"), Trait("Plan", "D7.4"), Trait("Plan", "D3.13")]
    public async Task Verification_never_starts_while_a_backup_holds_the_repository_or_a_run_of_the_job_is_active()
    {
        await JobWithBackup();
        var s = Scheduler();
        _time.Now = T0.AddDays(8);
        var bytes = Encoding.UTF8.GetBytes(Definition(null));
        Assert.True(_jobs.Locks!.TryAcquireAll("backup-in-progress", RunResourceLocks.ForBackup("files", bytes)));
        Assert.Empty(await TickAndDrain(s));
        Assert.Empty(VerifyRuns());
        Assert.False(Scalar<long>("SELECT COUNT(*) FROM sched_verify_jobs;") > 0); // nawet próba nie została zapisana
        _jobs.Locks.Release("backup-in-progress");

        // wiersz uruchomienia w toku (np. kopia czekająca w kolejce) — V3 ustępuje (priorytet Low)
        Exec("INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at) VALUES ('q1','j1','schedule','Queued',0,'2026-10-09T02:00:00.000Z');");
        Assert.Empty(await TickAndDrain(s));
        Exec("UPDATE run_runs SET state='Skipped', finished_at='2026-10-09T02:00:00.000Z' WHERE run_id='q1';");

        Assert.Equal(["j1"], await TickAndDrain(s));
        Assert.Single(VerifyRuns());
    }

    [Fact, Trait("BL", "BL-26"), Trait("Plan", "D7.8"), Trait("V", "V3")]
    public async Task After_a_crash_the_cycle_is_not_duplicated_and_repeats_the_same_bucket_after_the_backoff()
    {
        await JobWithBackup();
        var s = Scheduler();
        _time.Now = T0.AddDays(7);
        // Awaria w trakcie: wiersz Running + próba zapisane razem (jak VerifyScheduledAsync), bez zakończenia.
        Exec("INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at,started_at) VALUES ('crash','j1','schedule','Running',0,'2026-10-08T02:00:00.000Z','2026-10-08T02:00:00.000Z');");
        Exec("INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason) VALUES ('crash',0,NULL,'Running','2026-10-08T02:00:00.000Z','verify');");
        Exec("INSERT INTO sched_verify_jobs(job_id,last_attempt_at,last_run_id) VALUES ('j1','2026-10-08T02:00:00.000Z','crash');");
        Assert.Empty(await TickAndDrain(s)); // wciąż Running — nic równolegle

        await _jobs.RecoverInterruptedAsync(_ct); // restart usługi
        var restarted = Scheduler();
        Assert.Empty(await TickAndDrain(restarted)); // ponowienie dopiero po odstępie
        _time.Now = T0.AddDays(7).AddMinutes(59);
        Assert.Empty(await TickAndDrain(restarted));
        _time.Now = T0.AddDays(7).AddHours(1);
        var first = restarted.Tick(_ct);
        var second = restarted.Tick(_ct); // obrót w trakcie przebiegu — bez duplikatu
        await restarted.DrainAsync();
        Assert.Equal(["j1"], first);
        Assert.Empty(second);
        Assert.Equal(["Interrupted", "Succeeded"], VerifyRuns().Select(r => r.State));
        Assert.Equal(1L, Scalar<long>("SELECT rotation FROM sched_verify_jobs WHERE job_id='j1';")); // przerwany cykl nie przesunął rotacji
        Assert.Empty(await TickAndDrain(restarted));
    }

    [Fact, Trait("BL", "BL-26"), Trait("V", "V3"), Trait("Plan", "3.7")]
    public async Task Disabled_policy_never_runs()
    {
        await JobWithBackup("{\"enabled\":false}");
        var s = Scheduler();
        _time.Now = T0.AddDays(60);
        Assert.Empty(await TickAndDrain(s));
        Assert.Empty(VerifyRuns());
        var st = Assert.Single(CatalogStatusReader.ReadStatus(Catalog, "t", _time.Now).Jobs);
        Assert.False(st.HasNextVerifyUnixSeconds);
    }

    [Fact, Trait("BL", "BL-26"), Trait("V", "V3"), Trait("Plan", "3.7"), Trait("Category", "Property")]
    public async Task Rotation_in_the_catalog_covers_the_whole_repository_in_N_cycles_and_grants_verify_level_3()
    {
        var snap1 = await JobWithBackup("{\"intervalDays\":1,\"samplePercent\":34}"); // 3 kubełki
        var s = Scheduler();
        var initialPacks = PackHexOnTarget();
        Assert.True(initialPacks.Count >= 6);
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            _time.Now = T0.AddDays(cycle);
            if (cycle == 2)
            {
                // nowa kopia między cyklami: dopisane packi nie mogą „przesunąć” próbki starych
                File.WriteAllBytes(Path.Combine(P("src"), "nowy.bin"), RandomNumberGenerator.GetBytes(1_100_000));
                Assert.Equal(JobResult.Succeeded, (await _jobs.RunAsync("Pliki", _ct)).Result);
            }
            Assert.Equal(["j1"], await TickAndDrain(s));
        }
        Assert.All(VerifyRuns(), r => Assert.Equal("Succeeded", r.State));
        Assert.Equal(3L, Scalar<long>("SELECT rotation FROM sched_verify_jobs WHERE job_id='j1';"));
        var verified = VerifiedPacks(1);
        Assert.Subset(verified, initialPacks.ToHashSet()); // 100% packów sprzed cykli sprawdzone w N = 3 cyklach
        Assert.Empty(VerifiedPacks(0));
        Assert.Equal(3L, Scalar<long>("SELECT verify_level FROM snap_snapshots WHERE snap_id=$s;", ("$s", snap1)));
        Assert.True(Scalar<long>("SELECT SUM(bytes_read) FROM run_runs r JOIN run_transitions t ON t.run_id=r.run_id AND t.seq=0 WHERE t.reason='verify';") > 0); // koszt transferu widoczny

        var st = Assert.Single(CatalogStatusReader.ReadStatus(Catalog, "t", _time.Now).Jobs);
        Assert.Equal(_time.Now.ToUnixTimeSeconds(), st.LastVerifyUnixSeconds);
        Assert.False(st.HasLastVerifyCode);
        Assert.Equal(_time.Now.AddDays(1).ToUnixTimeSeconds(), st.NextVerifyUnixSeconds);
    }

    [Fact, Trait("BL", "BL-26"), Trait("AC", "AC-09.1"), Trait("V", "V3"), Trait("Plan", "D3.14"), Trait("Plan", "3.7")]
    public async Task Corrupted_block_is_detected_in_the_cycle_whose_bucket_holds_the_pack_with_suspect_KB_and_forced_full()
    {
        var snap = await JobWithBackup("{\"intervalDays\":1,\"samplePercent\":34}");
        var victim = await DataOnlyPackHex(snap);
        var path = PackPath(victim);
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x20; // bit-rot w środku packa: rozmiar i stopka bez zmian — V2 nie widzi
        File.WriteAllBytes(path, bytes);
        var bucket = RepoChecker.SampleBucket(victim, RepoChecker.Buckets(34));

        var s = Scheduler();
        var detected = false;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            _time.Now = T0.AddDays(cycle + 1);
            Assert.Equal(["j1"], await TickAndDrain(s));
            var run = VerifyRuns()[^1];
            if (RepoChecker.Slot(cycle, 3) != bucket)
            {
                Assert.Equal("Succeeded", run.State); // pack spoza próbki — cykl poprawny
                Assert.Equal(detected ? 1L : 0L, Scalar<long>("SELECT suspect FROM snap_snapshots WHERE snap_id=$s;", ("$s", snap))); // suspect zostaje po wykryciu
                continue;
            }
            Assert.Equal(("Failed", (string?)KbCode.VerifyScheduledFailed), (run.State, run.Code));
            Assert.Equal(1L, Scalar<long>("SELECT suspect FROM snap_snapshots WHERE snap_id=$s;", ("$s", snap)));
            Assert.True(Scalar<long>("SELECT verify_level FROM snap_snapshots WHERE snap_id=$s;", ("$s", snap)) < 3);
            Assert.Equal([victim], VerifiedPacks(0));
            Assert.Equal(1L, Scalar<long>("SELECT force_full FROM sched_verify_jobs WHERE job_id='j1';"));
            Assert.Equal((KbCode.VerifyScheduledFailed, "Failed"), (Scalar<string>("SELECT last_kb_code FROM sched_verify_jobs;"), Scalar<string>("SELECT last_status FROM sched_verify_jobs;")));
            using var ev = JsonDocument.Parse(Rows("SELECT CAST(payload AS TEXT) FROM core_outbox ORDER BY rowid;")[^1]);
            Assert.Equal(("failed", KbCode.VerifyScheduledFailed), (ev.RootElement.GetProperty("data").GetProperty("status").GetString(), ev.RootElement.GetProperty("data").GetProperty("kb").GetString()));
            var st = Assert.Single(CatalogStatusReader.ReadStatus(Catalog, "t", _time.Now).Jobs);
            Assert.Equal(KbCode.VerifyScheduledFailed, st.LastVerifyCode);
            Assert.True(st.RepositorySuspect);
            Assert.True(File.Exists(path)); // nic nie usunięto (rola Backup)
            detected = true;
        }
        Assert.True(detected);
        Assert.Contains(VerifyRuns(), r => r.State == "Failed");

        // 3.7: następna kopia pełna (bez bazy przyrostu), po niej wymuszenie zdjęte
        File.WriteAllBytes(path, bytes); // uszkodzenie zostaje — kopia pełna i tak wysyła bloki od nowa
        var next = await _jobs.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, next.Result);
        Assert.Equal(0L, Scalar<long>("SELECT mode FROM snap_snapshots WHERE snap_id=$s;", ("$s", next.SnapshotId!)));
        Assert.Equal(string.Empty, Scalar<string>("SELECT COALESCE(parent,'') FROM snap_snapshots WHERE snap_id=$s;", ("$s", next.SnapshotId!)));
        Assert.Equal(0L, Scalar<long>("SELECT force_full FROM sched_verify_jobs WHERE job_id='j1';"));
    }

    [Fact, Trait("BL", "BL-26"), Trait("V", "V3")]
    public async Task Unreachable_destination_leaves_the_cycle_unfinished_without_moving_the_rotation()
    {
        await JobWithBackup();
        Directory.Move(P("repo"), P("repo-off")); // cel odłączony (np. dysk USB)
        var s = Scheduler();
        _time.Now = T0.AddDays(7);
        Assert.Equal(["j1"], await TickAndDrain(s));
        var run = Assert.Single(VerifyRuns());
        Assert.Equal("Failed", run.State);
        Assert.NotNull(run.Code);
        Assert.Equal(0L, Scalar<long>("SELECT rotation FROM sched_verify_jobs WHERE job_id='j1';"));
        Assert.Empty(await TickAndDrain(s)); // bez burzy ponowień
        Directory.Move(P("repo-off"), P("repo"));
        _time.Now = T0.AddDays(7).AddHours(1);
        Assert.Equal(["j1"], await TickAndDrain(s));
        Assert.Equal("Succeeded", VerifyRuns()[^1].State);
        Assert.Equal(1L, Scalar<long>("SELECT rotation FROM sched_verify_jobs WHERE job_id='j1';"));
    }

    [Fact, Trait("BL", "BL-26"), Trait("AC", "AC-09.1"), Trait("V", "V3")]
    public async Task Empty_or_foreign_medium_does_not_pass_as_verified()
    {
        var snap = await JobWithBackup();
        Directory.Move(P("repo"), P("repo-real"));
        Directory.CreateDirectory(P("repo")); // podłączony inny (pusty) nośnik pod tą samą ścieżką
        var s = Scheduler();
        _time.Now = T0.AddDays(7);
        Assert.Equal(["j1"], await TickAndDrain(s));
        Assert.Equal(("Failed", (string?)KbCode.VerifySnapshotMissing), (VerifyRuns()[^1].State, VerifyRuns()[^1].Code));
        Assert.Equal(1L, Scalar<long>("SELECT suspect FROM snap_snapshots WHERE snap_id=$s;", ("$s", snap)));
        Assert.Equal(1L, Scalar<long>("SELECT force_full FROM sched_verify_jobs WHERE job_id='j1';"));
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
