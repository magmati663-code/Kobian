using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Jobs;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Providers.Storage.Local;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>RD-26/BL-42/US-03: wyzwalacze cron z katalogu → SchedulerCore → uruchomienia w Agencie (dane syntetyczne, zegar wirtualny).</summary>
public sealed class CatalogSchedulerTests : IAsyncLifetime
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
    }

    private const string R1 = "00000000000000000000000000000001";
    private const string R2 = "00000000000000000000000000000002";
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 1, 59, 0, TimeSpan.Zero);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-sched-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualTime _time = new(T0);
    private CatalogWriter _writer = null!;
    private CatalogJobService _jobs = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.Combine(_dir, n);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), RandomNumberGenerator.GetBytes(300 * 1024));
        File.WriteAllText(Path.Combine(P("src"), "b.txt"), "syntetyczne dane");
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = Kobian.Crypto.RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(P("wrapped.key"), prov.KeyFileJson);
        File.WriteAllText(P("kobian.repo"), prov.HeaderJson);
        File.WriteAllText(P("pw.txt"), pw + "\n");
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        _jobs = new CatalogJobService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, new byte[16], _time, (_, _) => Task.CompletedTask);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private CatalogScheduler Scheduler() => new(Catalog, _writer, _jobs, _time, 2, (min, _) => min);

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private string Definition() =>
        $$"""{"version":1,"sources":[{{J(P("src"))}}],"destination":{"scheme":"local","root":{{J(P("repo"))}}},"key":{"file":{{J(P("wrapped.key"))}},"passwordFile":{{J(P("pw.txt"))}},"repoHeader":{{J(P("kobian.repo"))}}},"packTargetSize":1048576}""";

    private void AddJob(string id, string name, string misfire = "skip", bool enabled = true, int? within = null)
    {
        Exec("INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,misfire_within_sec,created_at,updated_at) VALUES ($i,$n,'files',$d,x'00',$e,'UTC',$m,$w,'2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');",
            ("$i", id), ("$n", name), ("$d", Encoding.UTF8.GetBytes(Definition())), ("$e", enabled ? 1 : 0), ("$m", misfire), ("$w", within));
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

    [Fact, Trait("AC", "AC-03.1")]
    public async Task Cron_trigger_fires_once_at_due_time_and_runs_the_backup()
    {
        AddJob("j1", "Pliki");
        await CatalogSchedulerStore.UpsertCronTriggerAsync(_writer, "t1", "j1", "0 2 * * *", "UTC", _ct);
        var sched = Scheduler();
        sched.Reload();
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), sched.NextFire("t1"));

        sched.Tick(_ct); // 01:59 — jeszcze nic
        Assert.Empty(Query("SELECT 1 FROM run_runs;", 1));

        _time.Advance(TimeSpan.FromMinutes(1));
        sched.Tick(_ct);
        await sched.DrainAsync();
        sched.Tick(_ct); // kolejny obrót: brak drugiego wystąpienia
        await sched.DrainAsync();

        var runs = Query("SELECT trigger_id, scheduled_for_utc, cause, state, snapshot_id FROM run_runs;", 5);
        var run = Assert.Single(runs);
        Assert.Equal("t1", run[0]);
        Assert.Equal("2026-10-13T02:00:00.000Z", run[1]);
        Assert.Equal(("schedule", "Succeeded"), ((string)run[2]!, (string)run[3]!));
        Assert.NotNull(run[4]);
        Assert.Single(Query("SELECT 1 FROM snap_snapshots;", 1));
        var tr = Query("SELECT seq, from_state, to_state FROM run_transitions ORDER BY seq;", 3);
        Assert.Equal(new[] { "Queued", "Running", "Succeeded" }, tr.Select(r => (string)r[2]!).ToArray());
        Assert.Equal(new long[] { 0, 1, 2 }, tr.Select(r => (long)r[0]!).ToArray());
        Assert.Empty(sched.Diagnostics);
    }

    [Fact, Trait("AC", "AC-03.1")]
    public async Task After_downtime_RunOnceIfMissed_records_missed_occurrences_and_one_misfire_run()
    {
        AddJob("j1", "Pliki", "runOnceIfMissed");
        await CatalogSchedulerStore.UpsertCronTriggerAsync(_writer, "t1", "j1", "0 2 * * *", "UTC", _ct);
        Exec("INSERT INTO run_runs(run_id,job_id,trigger_id,scheduled_for_utc,cause,state,attempt,queued_at) VALUES ('aa','j1','t1','2026-10-10T02:00:00.000Z','schedule','Succeeded',0,'2026-10-10T02:00:00.000Z');");
        _time.Advance(new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero) - _time.Wall); // 3 dni przestoju

        var sched = Scheduler();
        sched.Reload();
        Assert.Equal(3, Query("SELECT 1 FROM run_runs WHERE state='Missed';", 1).Count);
        Assert.Empty(Query("SELECT 1 FROM run_runs WHERE cause='misfire';", 1));

        _time.Advance(TimeSpan.FromMinutes(5));
        sched.Tick(_ct);
        await sched.DrainAsync();

        var misfire = Assert.Single(Query("SELECT state, scheduled_for_utc FROM run_runs WHERE cause='misfire';", 2));
        Assert.Equal("Succeeded", misfire[0]);
        Assert.Null(misfire[1]);
        Assert.Single(Query("SELECT 1 FROM snap_snapshots;", 1));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 2, 0, 0, TimeSpan.Zero), sched.NextFire("t1"));
    }

    [Fact]
    public async Task Restart_does_not_duplicate_an_already_handled_occurrence()
    {
        AddJob("j1", "Pliki");
        await CatalogSchedulerStore.UpsertCronTriggerAsync(_writer, "t1", "j1", "0 2 * * *", "UTC", _ct);
        var first = Scheduler();
        first.Reload();
        _time.Advance(TimeSpan.FromMinutes(1));
        first.Tick(_ct);
        await first.DrainAsync();

        _time.Advance(TimeSpan.FromSeconds(30));
        var second = Scheduler(); // „restart” Agenta: nowy rdzeń, stan tylko z katalogu
        second.Reload();
        second.Tick(_ct);
        await second.DrainAsync();

        Assert.Single(Query("SELECT 1 FROM run_runs;", 1));
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public async Task Broken_trigger_is_skipped_with_KB_0201_and_does_not_stop_the_others()
    {
        AddJob("j1", "Pliki");
        await CatalogSchedulerStore.UpsertCronTriggerAsync(_writer, "t-good", "j1", "0 2 * * *", "UTC", _ct);
        Exec("INSERT INTO sched_triggers(trigger_id,job_id,kind,spec,enabled) VALUES ('t-bad1','j1','cron',$s,1);", ("$s", Encoding.UTF8.GetBytes("""{"cron":"to nie jest cron"}""")));
        Exec("INSERT INTO sched_triggers(trigger_id,job_id,kind,spec,enabled) VALUES ('t-bad2','j1','cron',$s,1);", ("$s", Encoding.UTF8.GetBytes("nie json")));
        Exec("INSERT INTO sched_triggers(trigger_id,job_id,kind,spec,enabled) VALUES ('t-off','j1','cron',$s,0);", ("$s", Encoding.UTF8.GetBytes("""{"cron":"0 3 * * *"}""")));

        var sched = Scheduler();
        sched.Reload();

        Assert.Equal(1, sched.ActiveTriggerCount);
        Assert.Equal(new[] { "t-bad1", "t-bad2" }, sched.Diagnostics.Select(d => d.TriggerId!).Order().ToArray());
        Assert.All(sched.Diagnostics, d => Assert.Equal(KbCode.ScheduleInvalid, d.KbCode));
        Assert.NotNull(sched.NextFire("t-good"));
    }

    [Fact, Trait("AC", "AC-03.3")]
    public async Task Upsert_rejects_invalid_cron_and_intervals_below_five_minutes()
    {
        AddJob("j1", "Pliki");
        var bad = await Assert.ThrowsAsync<KobianException>(() => CatalogSchedulerStore.UpsertCronTriggerAsync(_writer, "t1", "j1", "to nie cron", "UTC", _ct));
        Assert.Equal(KbCode.ScheduleInvalid, bad.Code);
        var tooOften = await Assert.ThrowsAsync<KobianException>(() => CatalogSchedulerStore.UpsertCronTriggerAsync(_writer, "t1", "j1", "* * * * *", "UTC", _ct));
        Assert.Equal(KbCode.ScheduleInvalid, tooOften.Code);
        var zone = await Assert.ThrowsAsync<KobianException>(() => CatalogSchedulerStore.UpsertCronTriggerAsync(_writer, "t1", "j1", "0 2 * * *", "Nie/Ma", _ct));
        Assert.Equal(KbCode.ScheduleInvalid, zone.Code);
        Assert.Empty(Query("SELECT 1 FROM sched_triggers;", 1));

        await CatalogSchedulerStore.UpsertCronTriggerAsync(_writer, "t1", "j1", "0 2 * * *", "UTC", _ct);
        await CatalogSchedulerStore.UpsertCronTriggerAsync(_writer, "t1", "j1", "30 3 * * *", "UTC", _ct); // zmiana w miejscu
        var row = Assert.Single(Query("SELECT spec FROM sched_triggers;", 1));
        Assert.Contains("30 3", Encoding.UTF8.GetString((byte[])row[0]!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlapping_queued_run_of_the_same_job_is_skipped_with_KB_4103()
    {
        AddJob("j1", "Pliki");
        Seed(R1, "j1", "2026-10-13T02:00:00.000Z");
        Seed(R2, "j1", "2026-10-13T02:05:00.000Z");

        var both = await Task.WhenAll(_jobs.RunQueuedAsync(R1, _ct), _jobs.RunQueuedAsync(R2, _ct));

        Assert.Equal(JobResult.Succeeded, both[0].Result);
        Assert.Equal((JobResult.Warning, KbCode.PreviousRunActive), (both[1].Result, both[1].Code));
        var r2 = Assert.Single(Query($"SELECT state, kb_code FROM run_runs WHERE run_id='{R2}';", 2));
        Assert.Equal(("Skipped", KbCode.PreviousRunActive), ((string)r2[0]!, (string)r2[1]!));
        Assert.Equal(new[] { "Queued", "Skipped" }, Query($"SELECT to_state FROM run_transitions WHERE run_id='{R2}' ORDER BY seq;", 1).Select(r => (string)r[0]!).ToArray());
        Assert.Single(Query("SELECT 1 FROM snap_snapshots;", 1));
    }

    [Fact]
    public async Task Queued_run_of_a_job_disabled_in_the_meantime_is_skipped_with_KB_0105()
    {
        AddJob("j1", "Pliki", enabled: false);
        Seed(R1, "j1", "2026-10-13T02:00:00.000Z");

        var outcome = await _jobs.RunQueuedAsync(R1, _ct);

        Assert.Equal((JobResult.Warning, KbCode.JobDisabled), (outcome.Result, outcome.Code));
        Assert.Equal("Skipped", Query($"SELECT state FROM run_runs WHERE run_id='{R1}';", 1)[0][0]);
        Assert.Empty(Query("SELECT 1 FROM snap_snapshots;", 1));
        // wiersz, który nie jest już Queued, nie może zostać uruchomiony drugi raz
        Assert.Equal(KbCode.Internal, (await _jobs.RunQueuedAsync(R1, _ct)).Code);
    }

    private void Seed(string runId, string jobId, string scheduledFor)
    {
        Exec("INSERT INTO run_runs(run_id,job_id,trigger_id,scheduled_for_utc,cause,state,attempt,queued_at) VALUES ($i,$j,'t1',$s,'schedule','Queued',0,$s);", ("$i", runId), ("$j", jobId), ("$s", scheduledFor));
        Exec("INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc) VALUES ($i,0,NULL,'Queued',$s);", ("$i", runId), ("$s", scheduledFor));
    }

    private ScheduleService Schedules() => new(Catalog, _writer);

    [Fact, Trait("BL", "RD-27"), Trait("AC", "AC-03.3")]
    public async Task ScheduleService_validates_replaces_in_place_and_disables_idempotently()
    {
        AddJob("j1", "Pliki");
        var svc = Schedules();
        Assert.Equal(KbCode.JobNotFound, (await svc.SetAsync("Nie-ma", "0 2 * * *", _ct)).Code);
        Assert.Equal(KbCode.ScheduleInvalid, (await svc.SetAsync("Pliki", "to nie cron", _ct)).Code);
        Assert.Equal(KbCode.ScheduleInvalid, (await svc.SetAsync("Pliki", "* * * * *", _ct)).Code); // gęściej niż co 5 min
        Assert.Equal(KbCode.ScheduleInvalid, (await svc.SetAsync("Pliki", new string('1', ScheduleService.MaxCronLength + 1), _ct)).Code);
        Assert.Empty(ScheduleService.List(Catalog, null)); // odrzucone żądania nic nie zapisały

        var ok = await svc.SetAsync("Pliki", " 0 2 * * * ", _ct);
        Assert.Null(ok.Code);
        Assert.Equal("cron-j1", ok.TriggerId);
        await svc.SetAsync("Pliki", "30 3 * * *", _ct); // zastąpienie w miejscu
        var one = Assert.Single(ScheduleService.List(Catalog, null));
        Assert.Equal(new ScheduleEntry("Pliki", "cron-j1", "30 3 * * *", "UTC", true), one);
        Assert.Empty(ScheduleService.List(Catalog, "Inne"));

        Assert.Null((await svc.ClearAsync("Pliki", _ct)).Code);
        Assert.Null((await svc.ClearAsync("Pliki", _ct)).Code);
        Assert.False(Assert.Single(ScheduleService.List(Catalog, "Pliki")).Enabled);
        Assert.Equal(KbCode.JobNotFound, (await svc.ClearAsync("Nie-ma", _ct)).Code);

        await svc.SetAsync("Pliki", "30 3 * * *", _ct); // ponowne ustawienie włącza wyzwalacz
        Assert.True(Assert.Single(ScheduleService.List(Catalog, "Pliki")).Enabled);
    }

    [Fact, Trait("BL", "RD-27"), Trait("AC", "AC-03.1")]
    public async Task Running_scheduler_picks_up_new_trigger_without_restart_and_fires_it()
    {
        AddJob("j1", "Pliki");
        var sched = Scheduler();
        sched.Reload();
        Assert.Equal(0, sched.ActiveTriggerCount);

        Assert.Null((await Schedules().SetAsync("Pliki", "0 2 * * *", _ct)).Code); // „z CLI”, Agent działa
        _time.Advance(TimeSpan.FromSeconds(1));
        sched.Tick(_ct);
        Assert.Equal(1, sched.ActiveTriggerCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), sched.NextFire("cron-j1"));

        _time.Advance(TimeSpan.FromSeconds(61)); // 02:00:01
        sched.Tick(_ct);
        await sched.DrainAsync();
        var run = Assert.Single(Query("SELECT cause, state, scheduled_for_utc FROM run_runs;", 3));
        Assert.Equal(("schedule", "Succeeded", "2026-10-13T02:00:00.000Z"), ((string)run[0]!, (string)run[1]!, (string)run[2]!));
        Assert.Empty(sched.Diagnostics);
    }

    [Fact, Trait("BL", "RD-27"), Trait("AC", "AC-03.3")]
    public async Task Edit_changes_next_fire_in_running_scheduler_and_off_stops_it()
    {
        AddJob("j1", "Pliki");
        var svc = Schedules();
        await svc.SetAsync("Pliki", "0 2 * * *", _ct);
        var sched = Scheduler();
        sched.Reload();
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), sched.NextFire("cron-j1"));

        await svc.SetAsync("Pliki", "30 3 * * *", _ct);
        _time.Advance(TimeSpan.FromSeconds(1));
        sched.Tick(_ct);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 3, 30, 0, TimeSpan.Zero), sched.NextFire("cron-j1")); // stare 02:00 nie odpali

        await svc.ClearAsync("Pliki", _ct);
        _time.Advance(TimeSpan.FromSeconds(1));
        sched.Tick(_ct);
        Assert.Equal(0, sched.ActiveTriggerCount);
        Assert.Null(sched.NextFire("cron-j1"));
        _time.Advance(TimeSpan.FromHours(3)); // 04:59 — minęły oba terminy
        sched.Tick(_ct);
        await sched.DrainAsync();
        Assert.Empty(Query("SELECT 1 FROM run_runs;", 1));
    }

    [Fact, Trait("BL", "RD-27"), Trait("AC", "AC-03.1")]
    public async Task Unrelated_edit_keeps_pending_misfire_of_untouched_trigger()
    {
        AddJob("j1", "Pliki", "runOnceIfMissed");
        AddJob("j2", "Inne");
        var svc = Schedules();
        await svc.SetAsync("Pliki", "0 2 * * *", _ct);
        Exec("INSERT INTO run_runs(run_id,job_id,trigger_id,scheduled_for_utc,cause,state,attempt,queued_at) VALUES ('aa','j1','cron-j1','2026-10-11T02:00:00.000Z','schedule','Succeeded',0,'2026-10-11T02:00:00.000Z');");
        _time.Advance(new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero) - _time.Wall); // przestój: zaległości + oczekujący misfire po 5 min
        var sched = Scheduler();
        sched.Reload();

        await svc.SetAsync("Inne", "0 5 * * *", _ct); // edycja innego zadania w oknie oczekiwania na misfire
        _time.Advance(TimeSpan.FromSeconds(1));
        sched.Tick(_ct);
        Assert.Equal(2, sched.ActiveTriggerCount);

        _time.Advance(TimeSpan.FromMinutes(5));
        sched.Tick(_ct);
        await sched.DrainAsync();
        var misfire = Assert.Single(Query("SELECT job_id, state FROM run_runs WHERE cause='misfire';", 2)); // dokładnie jeden, nie zgubiony i nie podwojony
        Assert.Equal(("j1", "Succeeded"), ((string)misfire[0]!, (string)misfire[1]!));
    }
}
