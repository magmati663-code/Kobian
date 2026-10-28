using System.Reflection;
using System.Text.Json;
using Kobian.Cli.Commands;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit.Cli;

/// <summary>BL-47: kody wyjścia 6.8, schemat JSON, AC-37.1 i AC-37.2 na kliencie zastępczym (bez usługi).</summary>
public sealed class CliAppTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class FakeAgent(OverallState state, bool tooOld = false) : IAgentClient
    {
        public Task<AgentStatus> GetStatusAsync(CancellationToken ct) =>
            Task.FromResult(new AgentStatus(state, "0.1.0", [new JobStatus("ERP-noc", state, DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), state == OverallState.Ok ? null : "KB-1402")], Hold, Alerts: AlertSummary));

        public AlertSummary? AlertSummary { get; init; }

        public RetentionHoldStatus? Hold { get; init; }

        public int ReleaseCalls { get; private set; }

        public Task<RetentionReleaseResult> ReleaseRetentionHoldAsync(CancellationToken ct)
        {
            ReleaseCalls++;
            return tooOld ? throw new ClientTooOldException() : Task.FromResult(new RetentionReleaseResult(OverallState.Ok, null, Hold is not null));
        }

        public Task<RunOutcome> RunJobAsync(string job, bool wait, CancellationToken ct) =>
            tooOld ? throw new ClientTooOldException() : Task.FromResult(new RunOutcome(state, state == OverallState.Failed ? KbCode.RepoWriteFailed : null, "00112233445566778899aabbccddeeff"));

        public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string? job, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SnapshotInfo>>([new SnapshotInfo("00112233445566778899aabbccddeeff", "ERP-noc", DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), "full", 3, 100),
                new SnapshotInfo("ffeeddccbbaa99887766554433221100", "ERP-noc", DateTimeOffset.FromUnixTimeSeconds(1_759_000_000), "full", 3, 100, "partial", 2)]);

        public Task<RunOutcome> RestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct) =>
            tooOld ? throw new ClientTooOldException() : Task.FromResult(new RunOutcome(state, null, snapshot));

        public List<(string Snapshot, string? Filter, string Target)> PlanCalls { get; } = [];

        public Task<RestorePlanResult> PlanRestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct)
        {
            PlanCalls.Add((snapshot, pathFilter, target));
            return Task.FromResult(target switch
            {
                "/tmp/full" => new RestorePlanResult(null, "aa", 3, 9_000, 1, 0, 0, [], 0, 0, 0, 1, 4_000, 100, KbCode.RestoreTargetSpace),
                "/tmp/conf" => new RestorePlanResult(null, "aa", 3, 9_000, 0, 2, 50, ["sub/b.txt", "c.txt"], 0, 0, 0, 1, 4_000, null, null),
                "/tmp/none" => new RestorePlanResult(KbCode.SnapshotNotFound, "", 0, 0, 0, 0, 0, [], 0, 0, 0, 0, 0, null, null),
                _ => new RestorePlanResult(null, "aa", 3, 9_000, 1, 0, 0, [], 0, 0, 0, 1, 4_000, 1_000_000, null),
            });
        }

        public List<string?> AuditAnchors { get; } = [];

        public Task<AuditVerifyResult> VerifyAuditAsync(string? anchor, CancellationToken ct)
        {
            AuditAnchors.Add(anchor);
            return Task.FromResult(anchor == "bad"
                ? new AuditVerifyResult(OverallState.Failed, KbCode.AuditAnchorMismatch, 9, 7, "ab", 3, "8:cd", "2026-10-17T04:55:00.000Z")
                : new AuditVerifyResult(OverallState.Ok, null, 9, null, "ab", anchor is null ? 2 : 3, "8:cd", "2026-10-17T04:55:00.000Z"));
        }

        public List<(string Job, string? Cron)> ScheduleCalls { get; } = [];

        public Task<IReadOnlyList<ScheduleItem>> ListSchedulesAsync(string? job, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ScheduleItem>>([new ScheduleItem(job ?? "ERP-noc", "cron-j1", "0 2 * * *", "Europe/Warsaw", true)]);

        public Task<ScheduleSetResult> SetScheduleAsync(string job, string? cron, CancellationToken ct)
        {
            ScheduleCalls.Add((job, cron));
            return tooOld ? throw new ClientTooOldException() : Task.FromResult(new ScheduleSetResult(state, state == OverallState.Failed ? KbCode.ScheduleInvalid : null, "cron-j1"));
        }

        public List<(string Job, int? Sample, long? Rotation)> CheckCalls { get; } = [];

        public Task<CheckResult> CheckAsync(string job, int? samplePercent, long? rotation, CancellationToken ct)
        {
            CheckCalls.Add((job, samplePercent, rotation));
            return tooOld ? throw new ClientTooOldException() : Task.FromResult(state == OverallState.Failed
                ? new CheckResult(state, KbCode.CheckPackMissing, 2, 2, 5, 0, 0, ["00112233445566778899aabbccddeeff"], 1, [(KbCode.CheckPackMissing, "pack:ab")])
                : new CheckResult(state, null, samplePercent is > 0 ? 3 : 2, 2, 5, samplePercent is > 0 ? 5 : 0, 40, [], 0, []));
        }

        public List<(string? Job, int? Limit)> RunsCalls { get; } = [];

        public Task<IReadOnlyList<RunItem>> ListRunsAsync(string? job, int? limit, CancellationToken ct)
        {
            RunsCalls.Add((job, limit));
            return Task.FromResult<IReadOnlyList<RunItem>>([
                new RunItem("run-2", "ERP-noc", "backup", "schedule", "Failed", OverallState.Failed, DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), DateTimeOffset.FromUnixTimeSeconds(1_760_000_060), KbCode.RepoWriteFailed, null, 3, 0),
                new RunItem("run-1", "ERP-noc", "restore", "manual", "Running", OverallState.Ok, DateTimeOffset.FromUnixTimeSeconds(1_759_990_000), null, null, null, null, null)]);
        }

        public Task<RetentionResult> ApplyRetentionAsync(string job, bool dryRun, CancellationToken ct) =>
            tooOld ? throw new ClientTooOldException()
            : job == "Bez-polityki" ? Task.FromResult(new RetentionResult(OverallState.Ok, null, false, dryRun, 0, 0, []))
            : job == "Wstrzymane" ? Task.FromResult(new RetentionResult(OverallState.Warning, KbCode.AlertClockJump, true, dryRun, 7, 0, [], Held: true))
            : Task.FromResult(new RetentionResult(state, state == OverallState.Failed ? KbCode.JobNotFound : null, true, dryRun, 5, 2, ["aa11", "bb22"]));

        public Task<PrunePlanResult> PlanPruneAsync(string job, CancellationToken ct) =>
            Task.FromResult(job == "Uszkodzone"
                ? new PrunePlanResult(OverallState.Failed, KbCode.PruneBlockedUnreadable, "index:ab12", 3, 0, 0, 0, 0, 0, 0, 86_400)
                : new PrunePlanResult(state, null, null, 4, 10, 40_960, 2, 1, 3, 12_288, 86_400));

        public Task<PruneRunResult> PruneAsync(string job, CancellationToken ct) =>
            Task.FromResult(job == "Zmienione"
                ? new PruneRunResult(OverallState.Warning, KbCode.PruneStoppedInconsistent, "pack:ab12", 0, 0, 0, 0, 1, 10, 3, 86_400, 0)
                : state == OverallState.Failed
                    ? new PruneRunResult(OverallState.Failed, KbCode.JobNotFound, null, 0, 0, 0, 0, 0, 0, 0, 86_400, 0)
                    : new PruneRunResult(state, null, null, 4, 0, 1, 2, 3, 9000, 5, 86_400, 1, 33, 1, 2));

        public InitRequest? LastInit { get; private set; }

        public Task<InitResult> InitJobAsync(InitRequest request, CancellationToken ct)
        {
            LastInit = request;
            return tooOld ? throw new ClientTooOldException() : Task.FromResult(new InitResult(state, state == OverallState.Failed ? KbCode.JobDefinitionInvalid : null, "job-1", "aa11", "bb22"));
        }
    }

    private async Task<(int Code, string Out, string Err)> Run(IAgentClient c, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(args, c, o, e, _ct);
        return (code, o.ToString(), e.ToString());
    }

    [Theory, Trait("AC", "AC-37.1")]
    [InlineData(OverallState.Ok, 0)]
    [InlineData(OverallState.Warning, 1)]
    [InlineData(OverallState.Failed, 2)]
    public async Task Status_exit_codes_and_versioned_json(OverallState state, int expected)
    {
        var (code, output, _) = await Run(new FakeAgent(state), "status", "--json");
        Assert.Equal(expected, code);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("kobian.cli.status/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("ERP-noc", doc.RootElement.GetProperty("jobs")[0].GetProperty("name").GetString());
    }

    /// <summary>Klient, którego każda metoda kończy się błędem obsługi po stronie Agenta (jak status Unknown z gRPC) — bez ręcznej implementacji całego interfejsu.</summary>
    public class FailingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var ex = new AgentInternalException();
            var rt = targetMethod!.ReturnType;
            if (!rt.IsGenericType) return Task.FromException(ex);
            var tcs = typeof(TaskCompletionSource<>).MakeGenericType(rt.GetGenericArguments()[0]);
            var src = Activator.CreateInstance(tcs)!;
            tcs.GetMethod("SetException", [typeof(Exception)])!.Invoke(src, [ex]);
            return tcs.GetProperty("Task")!.GetValue(src);
        }
    }

    [Fact, Trait("KB", "KB-0001")]
    public async Task Internal_agent_failure_gives_exit_2_and_kb_0001_not_an_unhandled_exception()
    {
        var client = DispatchProxy.Create<IAgentClient, FailingProxy>();
        var (code, output, err) = await Run(client, "status");
        Assert.Equal(CliApp.ExitFailed, code);
        Assert.Empty(output);
        Assert.StartsWith(KbCode.Internal, err, StringComparison.Ordinal);
    }

    [Fact, Trait("AC", "AC-37.1"), Trait("KB", "KB-7010")]
    public async Task No_service_gives_exit_3_quickly_with_kb_code()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (code, output, err) = await Run(new UnavailableAgentClient(), "status", "--json");
        Assert.Equal(3, code);
        Assert.Empty(output);
        Assert.StartsWith(KbCode.AgentUnreachable, err, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact, Trait("BL", "BL-60")]
    public async Task Runs_lists_history_with_versioned_json_text_and_limit_validation()
    {
        var agent = new FakeAgent(OverallState.Ok, tooOld: true); // odczyt działa także dla starego klienta
        var (code, output, _) = await Run(agent, "runs", "--job", "ERP-noc", "--limit", "50", "--json");
        Assert.Equal(0, code);
        Assert.Equal(("ERP-noc", (int?)50), agent.RunsCalls[^1]);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("kobian.cli.runs/1", doc.RootElement.GetProperty("schema").GetString());
        var first = doc.RootElement.GetProperty("runs")[0];
        Assert.Equal("failed", first.GetProperty("result").GetString());
        Assert.Equal(KbCode.RepoWriteFailed, first.GetProperty("code").GetString());
        Assert.Equal("2025-10-09T08:54:20Z", first.GetProperty("finishedUtc").GetString());
        var second = doc.RootElement.GetProperty("runs")[1];
        Assert.Equal("restore", second.GetProperty("kind").GetString());
        Assert.False(second.TryGetProperty("finishedUtc", out _));
        Assert.False(second.TryGetProperty("code", out _));

        var text = await Run(agent, "runs");
        Assert.Equal((string?)null, agent.RunsCalls[^1].Job);
        Assert.Null(agent.RunsCalls[^1].Limit);
        Assert.Contains("ERP-noc  backup  Failed " + KbCode.RepoWriteFailed, text.Out, StringComparison.Ordinal);
        Assert.Contains("ERP-noc  restore  Running", text.Out, StringComparison.Ordinal);

        foreach (var bad in new[] { "0", "1001", "-1", "x" })
            Assert.Equal(64, (await Run(agent, "runs", "--limit", bad)).Code);
        Assert.Equal(3, (await Run(new UnavailableAgentClient(), "runs")).Code);
    }

    [Fact, Trait("BL", "BL-25")]
    public async Task Forget_previews_applies_and_reports_missing_policy()
    {
        var ok = new FakeAgent(OverallState.Ok);
        var dry = await Run(ok, "forget", "ERP-noc", "--dry-run");
        Assert.Equal(0, dry.Code);
        Assert.Contains("dry-run: keep 5, forget 2", dry.Out, StringComparison.Ordinal);
        Assert.Contains("  - aa11", dry.Out, StringComparison.Ordinal);
        var json = await Run(ok, "forget", "ERP-noc", "--json");
        using (var doc = JsonDocument.Parse(json.Out))
        {
            Assert.Equal("kobian.cli.forget/1", doc.RootElement.GetProperty("schema").GetString());
            Assert.False(doc.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal(2, doc.RootElement.GetProperty("forget").GetArrayLength());
        }
        Assert.Contains("brak polityki", (await Run(ok, "forget", "Bez-polityki")).Out, StringComparison.Ordinal);
        var failed = await Run(new FakeAgent(OverallState.Failed), "forget", "Nie-ma");
        Assert.Equal((2, true), (failed.Code, failed.Out.Contains(KbCode.JobNotFound, StringComparison.Ordinal)));
        Assert.Equal(4, (await Run(new FakeAgent(OverallState.Ok, tooOld: true), "forget", "ERP-noc")).Code); // modyfikujące → rewizja klienta
        Assert.Equal(64, (await Run(ok, "forget")).Code);
    }

    [Fact, Trait("BL", "BL-45")]
    public void Alert_summary_counts_active_unacknowledged_critical_and_silenced()
    {
        AlertItem A(string sev, string st) => new("id", "A01", sev, st, "job", "x", null, null, 0, false);
        var s = AlertSummary.From([A("warning", "open"), A("critical", "notified"), A("security", "acknowledged"), A("critical", "silenced"), A("warning", "silenced")]);
        Assert.Equal((3, 2, 2, 2), (s.Active, s.Unacknowledged, s.Critical, s.Silenced));
        Assert.Equal(new AlertSummary(0, 0, 0, 0), AlertSummary.From([]));
    }

    [Fact, Trait("BL", "BL-45")]
    public async Task Status_shows_alert_counts_in_text_and_json_without_changing_exit_code()
    {
        var agent = new FakeAgent(OverallState.Ok) { AlertSummary = new AlertSummary(3, 2, 1, 4) };
        var text = await Run(agent, "status");
        Assert.Equal(0, text.Code);
        Assert.Contains("alerts 3 active (2 unacknowledged, 1 critical/security)", text.Out, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse((await Run(agent, "status", "--json")).Out))
        {
            var al = doc.RootElement.GetProperty("alerts");
            Assert.Equal((3, 2, 1, 4), (al.GetProperty("active").GetInt32(), al.GetProperty("unacknowledged").GetInt32(), al.GetProperty("critical").GetInt32(), al.GetProperty("silenced").GetInt32()));
        }
        var none = await Run(new FakeAgent(OverallState.Ok) { AlertSummary = new AlertSummary(0, 0, 0, 2) }, "status");
        Assert.DoesNotContain("alerts ", none.Out, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse((await Run(new FakeAgent(OverallState.Ok), "status", "--json")).Out))
            Assert.False(doc.RootElement.TryGetProperty("alerts", out _));
    }

    [Fact, Trait("BL", "BL-25"), Trait("SEC", "SEC-17.4")]
    public async Task Retention_hold_is_visible_in_status_and_release_needs_explicit_confirm()
    {
        var held = new FakeAgent(OverallState.Warning) { Hold = new RetentionHoldStatus(KbCode.AlertClockJump, DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), 93_600) };
        var text = await Run(held, "status");
        Assert.Equal(1, text.Code);
        Assert.Contains($"retention HELD {KbCode.AlertClockJump}", text.Out, StringComparison.Ordinal);
        Assert.Contains("clock jump +93600 s", text.Out, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse((await Run(held, "status", "--json")).Out))
        {
            var h = doc.RootElement.GetProperty("retentionHold");
            Assert.Equal((KbCode.AlertClockJump, 93_600L), (h.GetProperty("code").GetString(), h.GetProperty("clockDeltaSeconds").GetInt64()));
        }
        using (var doc = JsonDocument.Parse((await Run(new FakeAgent(OverallState.Ok), "status", "--json")).Out))
            Assert.False(doc.RootElement.TryGetProperty("retentionHold", out _));

        Assert.Equal(64, (await Run(held, "retention", "release")).Code); // bez --confirm nic nie jest wysyłane
        Assert.Equal(0, held.ReleaseCalls);
        var rel = await Run(held, "retention", "release", "--confirm");
        Assert.Equal((0, 1), (rel.Code, held.ReleaseCalls));
        Assert.Contains("released", rel.Out, StringComparison.Ordinal);
        Assert.Contains("nie była wstrzymana", (await Run(new FakeAgent(OverallState.Ok), "retention", "release", "--confirm")).Out, StringComparison.Ordinal);
        Assert.Equal(4, (await Run(new FakeAgent(OverallState.Ok, tooOld: true), "retention", "release", "--confirm")).Code);
        Assert.Equal(3, (await Run(new UnavailableAgentClient(), "retention", "release", "--confirm")).Code);

        var forget = await Run(held, "forget", "Wstrzymane");
        Assert.Equal(1, forget.Code);
        Assert.Contains($"held {KbCode.AlertClockJump}", forget.Out, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse((await Run(held, "forget", "Wstrzymane", "--json")).Out))
            Assert.True(doc.RootElement.GetProperty("held").GetBoolean());
    }

    [Fact, Trait("BL", "BL-25"), Trait("PM", "PM-18")]
    public async Task Prune_dry_run_reports_counts_bytes_and_blocking_object()
    {
        var ok = new FakeAgent(OverallState.Ok);
        var dry = await Run(ok, "prune", "ERP-noc", "--dry-run");
        Assert.Equal(0, dry.Code);
        Assert.Contains("remove after 24 h: 2 snapshots, 1 indexes, 3 packs (12288 B)", dry.Out, StringComparison.Ordinal);
        var json = await Run(ok, "prune", "ERP-noc", "--dry-run", "--json");
        using (var doc = JsonDocument.Parse(json.Out))
        {
            Assert.Equal("kobian.cli.prune/1", doc.RootElement.GetProperty("schema").GetString());
            Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal(3, doc.RootElement.GetProperty("deadPacks").GetInt32());
            Assert.Equal(86_400, doc.RootElement.GetProperty("graceSeconds").GetInt64());
        }
        var blocked = await Run(ok, "prune", "Uszkodzone", "--dry-run");
        Assert.Equal(2, blocked.Code);
        Assert.Contains(KbCode.PruneBlockedUnreadable + " index:ab12", blocked.Out, StringComparison.Ordinal);
        Assert.Equal(64, (await Run(ok, "prune", "--dry-run")).Code);
        Assert.Equal(3, (await Run(new UnavailableAgentClient(), "prune", "ERP-noc", "--dry-run")).Code);
    }

    [Fact, Trait("BL", "BL-25"), Trait("PM", "PM-18"), Trait("KB", "KB-1603")]
    public async Task Prune_without_dry_run_executes_and_maps_warning_and_failure()
    {
        var ok = new FakeAgent(OverallState.Ok);
        var run = await Run(ok, "prune", "ERP-noc");
        Assert.Equal(0, run.Code);
        Assert.Contains("pruned: deleted 1 snapshots, 2 indexes, 3 packs (9000 B); marked 4, waiting 5 (grace 24 h); consolidated 33 indexes into 1", run.Out, StringComparison.Ordinal);
        var json = await Run(ok, "prune", "ERP-noc", "--json");
        using (var doc = JsonDocument.Parse(json.Out))
        {
            Assert.False(doc.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal(3, doc.RootElement.GetProperty("deletedPacks").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("locksCleaned").GetInt32());
            Assert.Equal((33, 1, 2), (doc.RootElement.GetProperty("indexesConsolidated").GetInt32(), doc.RootElement.GetProperty("indexesWritten").GetInt32(),
                doc.RootElement.GetProperty("activeIndexes").GetInt32())); // D3.16
        }
        var stopped = await Run(ok, "prune", "Zmienione");
        Assert.Equal(1, stopped.Code);
        Assert.Contains("warning " + KbCode.PruneStoppedInconsistent, stopped.Out, StringComparison.Ordinal);
        var failed = await Run(new FakeAgent(OverallState.Failed), "prune", "ERP-noc");
        Assert.Equal(2, failed.Code);
        Assert.Contains("failed " + KbCode.JobNotFound, failed.Out, StringComparison.Ordinal);
        Assert.Equal(3, (await Run(new UnavailableAgentClient(), "prune", "ERP-noc")).Code);
    }

    [Fact, Trait("AC", "AC-37.2"), Trait("KB", "KB-7006")]
    public async Task Too_old_client_gets_4_on_mutation_but_read_only_commands_work()
    {
        var agent = new FakeAgent(OverallState.Ok, tooOld: true);
        Assert.Equal(4, (await Run(agent, "job", "run", "ERP-noc", "--wait")).Code);
        Assert.Equal(0, (await Run(agent, "status")).Code);
        Assert.Equal(0, (await Run(agent, "snapshots", "--job", "ERP-noc")).Code);
    }

    [Fact, Trait("RD", "RD-70")]
    public async Task Snapshots_show_damage_marker_in_text_and_json()
    {
        var agent = new FakeAgent(OverallState.Ok);
        var text = await Run(agent, "snapshots");
        Assert.Equal(0, text.Code);
        var lines = text.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("DAMAGED", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("DAMAGED partial (2 files)", lines[1].TrimEnd(), StringComparison.Ordinal);
        using var doc = JsonDocument.Parse((await Run(agent, "snapshots", "--json")).Out);
        var arr = doc.RootElement.GetProperty("snapshots");
        Assert.False(arr[0].TryGetProperty("damage", out _));
        Assert.Equal("partial", arr[1].GetProperty("damage").GetString());
        Assert.Equal(2, arr[1].GetProperty("damagedFiles").GetInt64());
    }

    [Fact, Trait("BL", "BL-47")]
    public async Task Run_failed_maps_to_2_with_code_and_usage_errors_to_64()
    {
        var (code, output, _) = await Run(new FakeAgent(OverallState.Failed), "job", "run", "ERP-noc", "--json");
        Assert.Equal(2, code);
        Assert.Equal(KbCode.RepoWriteFailed, JsonDocument.Parse(output).RootElement.GetProperty("code").GetString());
        Assert.Equal(64, (await Run(new FakeAgent(OverallState.Ok), "frobnicate")).Code);
        Assert.Equal(64, (await Run(new FakeAgent(OverallState.Ok), "restore", "--snapshot", "latest")).Code); // brak --to
        Assert.Equal(64, (await Run(new FakeAgent(OverallState.Ok))).Code);
    }

    [Fact, Trait("BL", "BL-47")]
    public async Task Option_values_are_not_taken_as_positionals()
    {
        var (code, output, _) = await Run(new FakeAgent(OverallState.Ok), "restore", "--snapshot", "latest", "--path", "*.xlsx", "--to", "/tmp/r", "--json");
        Assert.Equal(0, code);
        Assert.Equal("latest", JsonDocument.Parse(output).RootElement.GetProperty("snapshotId").GetString());
    }

    [Fact, Trait("BL", "BL-70"), Trait("AC", "AC-37.1")]
    public async Task Restore_latest_with_job_is_scoped_to_that_job_and_ids_are_sent_unchanged()
    {
        string Sent((int Code, string Out, string Err) r)
        {
            Assert.Equal(0, r.Code);
            return JsonDocument.Parse(r.Out).RootElement.GetProperty("snapshotId").GetString()!;
        }
        Assert.Equal("latest:ERP-noc", Sent(await Run(new FakeAgent(OverallState.Ok), "restore", "--snapshot", "latest", "--job", "ERP-noc", "--to", "/tmp/r", "--json")));
        Assert.Equal("latest:ERP-noc", Sent(await Run(new FakeAgent(OverallState.Ok), "restore", "--job", "ERP-noc", "--snapshot", "LATEST", "--to", "/tmp/r", "--json")));
        Assert.Equal("00112233", Sent(await Run(new FakeAgent(OverallState.Ok), "restore", "--snapshot", "00112233", "--job", "ERP-noc", "--to", "/tmp/r", "--json")));
        Assert.Equal("latest", Sent(await Run(new FakeAgent(OverallState.Ok), "restore", "--snapshot", "latest", "--to", "/tmp/r", "--json")));
    }

    [Fact, Trait("BL", "BL-64"), Trait("Plan", "R2.8.1")]
    public async Task Restore_dry_run_calls_plan_only_and_maps_exit_codes()
    {
        var agent = new FakeAgent(OverallState.Ok);
        var ok = await Run(agent, "restore", "--snapshot", "latest", "--job", "ERP-noc", "--path", "*.xlsx", "--to", "/tmp/r", "--dry-run");
        Assert.Equal(0, ok.Code);
        Assert.Contains("files=3 bytes=9000 new-dirs=1 packs=1 read-bytes=4000 free-bytes=1000000", ok.Out, StringComparison.Ordinal);
        Assert.Contains("dry-run", ok.Out, StringComparison.Ordinal);
        Assert.Equal(("latest:ERP-noc", "*.xlsx", "/tmp/r"), Assert.Single(agent.PlanCalls));

        var conf = await Run(agent, "restore", "--snapshot", "latest", "--to", "/tmp/conf", "--dry-run", "--json");
        Assert.Equal(1, conf.Code);
        var j = JsonDocument.Parse(conf.Out).RootElement;
        Assert.StartsWith("kobian.cli.restore-plan/", j.GetProperty("schema").GetString(), StringComparison.Ordinal);
        Assert.Equal(("warning", 2), (j.GetProperty("result").GetString(), j.GetProperty("conflicts").GetInt32()));
        Assert.Equal(["sub/b.txt", "c.txt"], j.GetProperty("conflictSample").EnumerateArray().Select(x => x.GetString()));
        Assert.False(j.TryGetProperty("freeBytes", out _));

        var full = await Run(agent, "restore", "--snapshot", "latest", "--to", "/tmp/full", "--dry-run");
        Assert.Equal(2, full.Code);
        Assert.Contains("blocked " + KbCode.RestoreTargetSpace, full.Out, StringComparison.Ordinal);
        var none = await Run(agent, "restore", "--snapshot", "latest", "--to", "/tmp/none", "--dry-run", "--json");
        Assert.Equal((2, KbCode.SnapshotNotFound), (none.Code, JsonDocument.Parse(none.Out).RootElement.GetProperty("code").GetString()));
    }

    [Fact, Trait("Plan", "D9.9"), Trait("RD", "RD-51")]
    public async Task Audit_verify_passes_the_external_anchor_and_prints_the_last_anchor()
    {
        var agent = new FakeAgent(OverallState.Ok);
        var plain = await Run(agent, "audit", "verify");
        Assert.Equal(0, plain.Code);
        Assert.Contains("8:cd", plain.Out, StringComparison.Ordinal);
        var ext = await Run(agent, "audit", "verify", "--anchor", "8:cd", "--json");
        var j = JsonDocument.Parse(ext.Out).RootElement;
        Assert.Equal((3, "8:cd"), (j.GetProperty("anchorsChecked").GetInt32(), j.GetProperty("lastAnchor").GetString()));
        var bad = await Run(agent, "audit", "verify", "--anchor", "bad");
        Assert.Equal(2, bad.Code);
        Assert.Contains(KbCode.AuditAnchorMismatch + " seq 7", bad.Out, StringComparison.Ordinal);
        Assert.Equal([null, "8:cd", "bad"], agent.AuditAnchors);
    }

    [Fact, Trait("BL", "BL-24"), Trait("AC", "AC-37.1")]
    public async Task Init_sends_absolute_paths_and_password_only_as_file_reference()
    {
        var agent = new FakeAgent(OverallState.Ok);
        var cwd = Directory.GetCurrentDirectory();
        var (code, output, _) = await Run(agent, "init", "Pliki", "--source", "src", "--dest", "repo", "--password-file", "pw.txt", "--json");
        Assert.Equal(0, code);
        Assert.Equal("Pliki", agent.LastInit!.JobName);
        Assert.Equal("local", agent.LastInit.DestinationScheme);
        Assert.Equal(Path.Combine(cwd, "src"), agent.LastInit.Source);
        Assert.Equal(Path.Combine(cwd, "pw.txt"), agent.LastInit.PasswordFile);
        Assert.True(Path.IsPathFullyQualified(agent.LastInit.DestinationRoot));
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("kobian.cli.init/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("job-1", doc.RootElement.GetProperty("jobId").GetString());
        Assert.False(doc.RootElement.TryGetProperty("password", out _));
    }

    [Fact, Trait("BL", "BL-73")]
    public async Task Init_with_remote_destination_passes_root_unchanged_and_secret_reference()
    {
        var agent = new FakeAgent(OverallState.Ok);
        Assert.Equal(0, (await Run(agent, "init", "Nas", "--source", "src", "--dest", @"\\nas\kopie\biuro", "--dest-scheme", "smb", "--secret-ref", "file:nas", "--password-file", "pw.txt")).Code);
        Assert.Equal(("smb", @"\\nas\kopie\biuro", "file:nas"), (agent.LastInit!.DestinationScheme, agent.LastInit.DestinationRoot, agent.LastInit.SecretRef)); // UNC nie jest „poprawiane” GetFullPath
        Assert.Null(agent.LastInit.Host);

        Assert.Equal(0, (await Run(agent, "init", "Sftp", "--source", "src", "--dest", "/srv/kopie", "--dest-scheme", "sftp", "--host", "backup.example.test", "--port", "2222",
            "--host-key", "SHA256:abc", "--secret-ref", "file:s1", "--password-file", "pw.txt")).Code);
        Assert.Equal(("/srv/kopie", "backup.example.test", 2222, "SHA256:abc"), (agent.LastInit.DestinationRoot, agent.LastInit.Host, agent.LastInit.Port, agent.LastInit.HostKey));

        Assert.Equal(64, (await Run(agent, "init", "X", "--source", "s", "--dest", "/d", "--dest-scheme", "sftp", "--port", "70000", "--password-file", "p")).Code);
        Assert.Equal(64, (await Run(agent, "init", "X", "--source", "s", "--dest", "/d", "--dest-scheme", "sftp", "--port", "-1", "--password-file", "p")).Code);
    }

    [Fact, Trait("BL", "BL-24")]
    public async Task Init_requires_all_options_and_maps_failure_and_old_client()
    {
        Assert.Equal(64, (await Run(new FakeAgent(OverallState.Ok), "init", "Pliki", "--source", "s", "--dest", "d")).Code); // brak --password-file
        Assert.Equal(64, (await Run(new FakeAgent(OverallState.Ok), "init", "--source", "s", "--dest", "d", "--password-file", "p")).Code); // brak nazwy
        var failed = await Run(new FakeAgent(OverallState.Failed), "init", "X", "--source", "s", "--dest", "d", "--password-file", "p");
        Assert.Equal(2, failed.Code);
        Assert.Contains(KbCode.JobDefinitionInvalid, failed.Out, StringComparison.Ordinal);
        Assert.Equal(4, (await Run(new FakeAgent(OverallState.Ok, tooOld: true), "init", "X", "--source", "s", "--dest", "d", "--password-file", "p")).Code);
    }

    [Fact, Trait("BL", "RD-27"), Trait("AC", "AC-03.3")]
    public async Task Schedule_set_off_and_list_map_to_agent_calls_and_exit_codes()
    {
        var agent = new FakeAgent(OverallState.Ok);
        var set = await Run(agent, "schedule", "set", "ERP-noc", "--cron", "0 2 * * *", "--json");
        Assert.Equal(0, set.Code);
        Assert.Equal(("ERP-noc", "0 2 * * *"), agent.ScheduleCalls[0]); // wyrażenie z wieloma polami jako jeden argument, nie jako nazwa zadania
        using (var doc = JsonDocument.Parse(set.Out))
        {
            Assert.Equal("kobian.cli.schedule/1", doc.RootElement.GetProperty("schema").GetString());
            Assert.Equal("cron-j1", doc.RootElement.GetProperty("triggerId").GetString());
        }

        Assert.Equal(0, (await Run(agent, "schedule", "off", "ERP-noc")).Code);
        Assert.Equal(("ERP-noc", (string?)null), agent.ScheduleCalls[1]);

        var list = await Run(agent, "schedule", "list", "--job", "Pliki", "--json");
        Assert.Equal(0, list.Code);
        using var ldoc = JsonDocument.Parse(list.Out);
        Assert.Equal("kobian.cli.schedules/1", ldoc.RootElement.GetProperty("schema").GetString());
        var first = ldoc.RootElement.GetProperty("schedules")[0];
        Assert.Equal("Pliki", first.GetProperty("job").GetString());
        Assert.Equal("Europe/Warsaw", first.GetProperty("timeZone").GetString());
        Assert.True(first.GetProperty("enabled").GetBoolean());
    }

    [Fact, Trait("BL", "RD-27")]
    public async Task Schedule_requires_arguments_and_maps_failure_and_old_client()
    {
        Assert.Equal(64, (await Run(new FakeAgent(OverallState.Ok), "schedule", "set", "ERP-noc")).Code); // brak --cron
        Assert.Equal(64, (await Run(new FakeAgent(OverallState.Ok), "schedule", "off")).Code); // brak nazwy
        Assert.Equal(64, (await Run(new FakeAgent(OverallState.Ok), "schedule", "zmien", "X")).Code);
        var failed = await Run(new FakeAgent(OverallState.Failed), "schedule", "set", "X", "--cron", "to nie cron");
        Assert.Equal(2, failed.Code);
        Assert.Contains(KbCode.ScheduleInvalid, failed.Out, StringComparison.Ordinal);
        Assert.Equal(4, (await Run(new FakeAgent(OverallState.Ok, tooOld: true), "schedule", "off", "X")).Code);
        Assert.Equal(3, (await Run(new UnavailableAgentClient(), "schedule", "list")).Code);
    }
    [Theory, Trait("BL", "BL-26"), Trait("AC", "AC-09.1")]
    [InlineData(OverallState.Ok, 0)]
    [InlineData(OverallState.Failed, 2)]
    public async Task Check_maps_arguments_result_and_json(OverallState state, int expected)
    {
        var agent = new FakeAgent(state);
        var plain = await Run(agent, "check", "ERP-noc");
        Assert.Equal(expected, plain.Code);
        Assert.Equal(("ERP-noc", (int?)null, (long?)null), agent.CheckCalls[^1]);
        if (state == OverallState.Failed)
        {
            Assert.Contains("KB-1501: pack:ab", plain.Err, StringComparison.Ordinal);
            Assert.Contains("damaged snapshot 00112233445566778899aabbccddeeff", plain.Out, StringComparison.Ordinal);
        }
        var json = await Run(agent, "check", "ERP-noc", "--sample", "25", "--rotation", "-3", "--json");
        Assert.Equal(expected, json.Code);
        Assert.Equal(("ERP-noc", (int?)25, (long?)-3), agent.CheckCalls[^1]);
        using var doc = JsonDocument.Parse(json.Out);
        Assert.Equal("kobian.cli.check/1", doc.RootElement.GetProperty("schema").GetString());
        var c = doc.RootElement;
        Assert.Equal(state == OverallState.Ok ? 3 : 2, c.GetProperty("level").GetInt32());
        Assert.Equal(state == OverallState.Ok ? 0 : 1, c.GetProperty("findings").GetArrayLength());
    }

    [Theory, Trait("BL", "BL-26")]
    [InlineData("--sample", "101")]
    [InlineData("--sample", "-1")]
    [InlineData("--sample", "x")]
    [InlineData("--rotation", "1.5")]
    public async Task Check_rejects_bad_numbers_before_calling_the_agent(string opt, string value)
    {
        var agent = new FakeAgent(OverallState.Ok);
        var r = await Run(agent, "check", "ERP-noc", opt, value);
        Assert.Equal(CliApp.ExitUsage, r.Code);
        Assert.Empty(agent.CheckCalls);
        Assert.Equal(CliApp.ExitUsage, (await Run(agent, "check")).Code);
        Assert.Equal(CliApp.ExitClientTooOld, (await Run(new FakeAgent(OverallState.Ok, tooOld: true), "check", "ERP-noc")).Code);
    }
}
