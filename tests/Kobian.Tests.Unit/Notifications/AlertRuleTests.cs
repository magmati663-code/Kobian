using System.Reflection;
using Kobian.Domain;
using Kobian.Notifications.Alerts;
using Kobian.Notifications.Alerts.Rules;
using Xunit;

namespace Kobian.Tests.Unit.Notifications;

public sealed class AlertRuleCatalogTests
{
    private static readonly AlertRuleCatalog Catalog = AlertRuleCatalog.LoadEmbedded();

    [Fact, Trait("D", "D9.8")]
    public void Embedded_catalog_has_A01_to_A17_with_planned_severity_and_trigger()
    {
        Assert.Equal(17, Catalog.Rules.Count);
        Assert.Equal(AlertSeverity.Critical, Catalog.Get("A01").Severity);
        Assert.Equal(AlertSeverity.Warning, Catalog.Get("A02").Severity);
        Assert.Equal(TimeSpan.FromMinutes(15), Catalog.Get("A03").Every);
        foreach (var c in new[] { "A07", "A08", "A09", "A11", "A15", "A17" }) Assert.Equal(AlertSeverity.Security, Catalog.Get(c).Severity);
        Assert.Equal(TriggerKind.External, Catalog.Get("A12").Trigger);
        Assert.Equal(TimeSpan.FromHours(24), Catalog.Get("A01").EscalateAfter);
    }

    [Fact, Trait("D", "D9.8")]
    public void Every_rule_kb_code_exists_in_the_registry()
    {
        var known = typeof(KbCode).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string?)f.GetRawConstantValue()).ToHashSet();
        foreach (var r in Catalog.Rules.Where(r => r.Kb is not null)) Assert.Contains(r.Kb, known);
        Assert.Equal("KB-6103", Catalog.Get("A03").Kb);
        Assert.Equal("KB-6117", Catalog.Get("A17").Kb);
    }

    [Fact, Trait("D", "D9.8")]
    public void Quiet_hours_only_for_warnings()
    {
        foreach (var r in Catalog.Rules.Where(r => r.QuietHours)) Assert.Equal(AlertSeverity.Warning, r.Severity);
    }

    private static string Mutate(Func<string, string> f)
    {
        using var s = typeof(AlertRuleCatalog).Assembly.GetManifestResourceStream("Kobian.Notifications.Alerts.Rules.alert-rules.json")!;
        using var r = new StreamReader(s);
        return f(r.ReadToEnd().ReplaceLineEndings("\n")); // checkout Windows (autocrlf) daje CRLF w zasobie — wzorce testu są z LF
    }

    [Theory, Trait("D", "D9.8"), Trait("SEC", "SEC-19.3")]
    [InlineData("\"fn\": \"flag\", \"args\": { \"name\": \"verificationFailed\" }", "\"fn\": \"eval\", \"args\": { \"name\": \"verificationFailed\" }")] // nieznana funkcja
    [InlineData("\"fn\": \"flag\", \"args\": { \"name\": \"verificationFailed\" }", "\"fn\": \"flag\", \"args\": { \"name\": \"nope\" }")]       // nieznana flaga
    [InlineData("\"fn\": \"flag\", \"args\": { \"name\": \"verificationFailed\" }", "\"fn\": \"flag\", \"args\": { \"name\": \"verificationFailed\", \"x\": 1 }")] // dodatkowy argument
    [InlineData("\"everySeconds\": 900", "\"everySeconds\": 1")]                                       // zbyt często
    [InlineData("\"kb\": \"KB-6101\"", "\"kb\": \"6101\"")]                                          // zły format KB
    [InlineData("\"severity\": \"critical\", \"trigger\": { \"kind\": \"event\" }, \"scope\": \"job\",\n    \"condition\": { \"fn\": \"lastRunStatus\", \"args\": { \"status\": \"Failed\", \"requireRetriesExhausted\": true } },\n    \"kb\": \"KB-6101\", \"quietHours\": false", "\"severity\": \"critical\", \"trigger\": { \"kind\": \"event\" }, \"scope\": \"job\",\n    \"condition\": { \"fn\": \"lastRunStatus\", \"args\": { \"status\": \"Failed\", \"requireRetriesExhausted\": true } },\n    \"kb\": \"KB-6101\", \"quietHours\": true")] // cisza nocna dla krytycznego
    public void Invalid_rule_data_is_rejected_at_load(string from, string to)
    {
        var json = Mutate(s => s.Replace(from, to));
        Assert.NotEqual(Mutate(s => s), json);
        Assert.Throws<FormatException>(() => AlertRuleCatalog.Parse(json));
    }

    [Fact, Trait("D", "D9.8")]
    public void Missing_duplicate_or_malformed_documents_are_rejected()
    {
        Assert.Throws<FormatException>(() => AlertRuleCatalog.Parse("{}"));
        Assert.Throws<FormatException>(() => AlertRuleCatalog.Parse("[]")); // brak A01…A17
        Assert.Throws<FormatException>(() => AlertRuleCatalog.Parse(Mutate(s => s.Replace("\"code\": \"A17\"", "\"code\": \"A16\""))));
        Assert.Throws<FormatException>(() => AlertRuleCatalog.Parse(Mutate(s => s.Replace("\"scope\": \"job\"", "\"scoop\": \"job\""))));
        Assert.Throws<FormatException>(() => AlertRuleCatalog.Parse(Mutate(s => "// komentarz\n" + s)));
    }
}

public sealed class AlertConditionTests
{
    private static readonly AlertRuleCatalog Catalog = AlertRuleCatalog.LoadEmbedded();
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static bool Active(string code, AlertFacts f, DateTimeOffset? now = null) => Catalog.Get(code).Condition(f, now ?? T0);

    [Fact, Trait("D", "D9.8")]
    public void A01_requires_failed_after_retries_exhausted()
    {
        Assert.False(Active("A01", new AlertFacts { LastOutcome = RunOutcome.Failed, RetriesExhausted = false }));
        Assert.True(Active("A01", new AlertFacts { LastOutcome = RunOutcome.Failed, RetriesExhausted = true }));
        Assert.False(Active("A01", new AlertFacts { LastOutcome = RunOutcome.Warning, RetriesExhausted = true }));
        Assert.True(Active("A02", new AlertFacts { LastOutcome = RunOutcome.Warning }));
        Assert.False(Active("A01", new AlertFacts()));
    }

    [Fact, Trait("D", "D9.8")]
    public void A03_daily_job_threshold_is_one_and_a_half_intervals_but_not_below_26h()
    {
        var f = new AlertFacts { ScheduleInterval = TimeSpan.FromHours(24), LastSuccessAt = T0 - TimeSpan.FromHours(35) };
        Assert.False(Active("A03", f));
        Assert.True(Active("A03", f with { LastSuccessAt = T0 - TimeSpan.FromHours(37) }));
    }

    [Fact, Trait("D", "D9.8")]
    public void A03_hourly_job_uses_factor_without_the_daily_minimum_and_rpo_overrides()
    {
        var f = new AlertFacts { ScheduleInterval = TimeSpan.FromHours(1), LastSuccessAt = T0 - TimeSpan.FromMinutes(100) };
        Assert.True(Active("A03", f));
        Assert.False(Active("A03", f with { LastSuccessAt = T0 - TimeSpan.FromMinutes(80) }));
        var rpo = f with { Rpo = TimeSpan.FromHours(4), LastSuccessAt = T0 - TimeSpan.FromHours(3) };
        Assert.False(Active("A03", rpo));
        Assert.True(Active("A03", rpo with { LastSuccessAt = T0 - TimeSpan.FromHours(5) }));
    }

    [Fact, Trait("D", "D9.8")]
    public void A03_without_any_success_counts_from_creation_and_without_reference_is_inactive()
    {
        var f = new AlertFacts { ScheduleInterval = TimeSpan.FromHours(24), Since = T0 - TimeSpan.FromHours(40) };
        Assert.True(Active("A03", f));
        Assert.False(Active("A03", new AlertFacts { ScheduleInterval = TimeSpan.FromHours(24) }));
    }

    [Fact, Trait("D", "D9.8")]
    public void A06_free_space_or_forecast()
    {
        Assert.True(Active("A06", new AlertFacts { FreeSpaceRatio = 0.14 }));
        Assert.False(Active("A06", new AlertFacts { FreeSpaceRatio = 0.15, DaysToFull = 14 }));
        Assert.True(Active("A06", new AlertFacts { FreeSpaceRatio = 0.5, DaysToFull = 13.9 }));
        Assert.False(Active("A06", new AlertFacts()));
    }

    [Fact, Trait("D", "D9.8")]
    public void A13_A16_and_A15_thresholds()
    {
        Assert.True(Active("A13", new AlertFacts { DaysToExpiry = 30 }));
        Assert.False(Active("A13", new AlertFacts { DaysToExpiry = 31 }));
        Assert.True(Active("A16", new AlertFacts { VendorSupported = false }));
        Assert.True(Active("A16", new AlertFacts { DaysSincePatch = 61 }));
        Assert.False(Active("A16", new AlertFacts { DaysSincePatch = 60, VendorSupported = true }));
        Assert.False(Active("A16", new AlertFacts())); // nieznana data łatki sama nie wyzwala alertu
        Assert.False(Active("A15", new AlertFacts { DenialsInWindow = 4 }));
        Assert.True(Active("A15", new AlertFacts { DenialsInWindow = 5 }));
    }

    [Fact, Trait("D", "D9.8"), Trait("SEC", "SEC-17.4")]
    public void A17_clock_jump_forward_over_24h_or_back_over_2min()
    {
        Assert.True(Active("A17", new AlertFacts { ClockDelta = TimeSpan.FromHours(24.1) }));
        Assert.False(Active("A17", new AlertFacts { ClockDelta = TimeSpan.FromHours(23) }));
        Assert.True(Active("A17", new AlertFacts { ClockDelta = TimeSpan.FromMinutes(-2.5) }));
        Assert.False(Active("A17", new AlertFacts { ClockDelta = TimeSpan.FromMinutes(-1) }));
        Assert.False(Active("A17", new AlertFacts()));
    }

    [Fact, Trait("D", "D9.8")]
    public void Flag_rules_react_only_to_their_own_flag()
    {
        var f = new AlertFacts { Flags = new HashSet<string> { "hostKeyMismatch" } };
        Assert.True(Active("A09", f));
        Assert.False(Active("A04", f));
        Assert.False(Active("A11", f));
    }
}

public sealed class AlertEvaluatorTests
{
    private static readonly AlertRuleCatalog Catalog = AlertRuleCatalog.LoadEmbedded();
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private sealed class World
    {
        public Dictionary<string, AlertFacts> Job = new();
        public Dictionary<string, AlertFacts> Host = new();
        public List<ScopedFacts> Scopes(AlertRule r) => (r.Scope switch { RuleScope.Job => Job, RuleScope.Host => Host, _ => new() })
            .Select(kv => new ScopedFacts(kv.Key, kv.Value)).ToList();
    }

    private static readonly AlertFacts Failed = new() { LastOutcome = RunOutcome.Failed, RetriesExhausted = true };

    [Fact, Trait("D", "D9.8")]
    public void Failed_job_opens_once_reminds_after_24h_escalates_once_and_resolves_on_success()
    {
        var w = new World(); w.Job["job-1"] = Failed;
        var ev = new AlertEvaluator(Catalog, w.Scopes);
        var u = Assert.Single(ev.Tick(T0, false), x => x.Rule.Code == "A01");
        Assert.Equal(AlertOutbox.Opened, u.Outbox);
        Assert.Equal("KB-6101", u.Rule.Kb);
        Assert.DoesNotContain(ev.Tick(T0.AddMinutes(1), false), x => x.Rule.Code == "A01"); // deduplikacja
        var day = ev.Tick(T0.AddHours(24), false).Where(x => x.Rule.Code == "A01").ToList();
        var rem = Assert.Single(day);
        Assert.Equal(AlertOutbox.Reminder, rem.Outbox);
        Assert.True(rem.Escalate);
        Assert.DoesNotContain(ev.Tick(T0.AddHours(24).AddMinutes(1), false), x => x.Rule.Code == "A01"); // eskalacja tylko raz
        w.Job["job-1"] = new AlertFacts { LastOutcome = RunOutcome.Success };
        var res = Assert.Single(ev.Tick(T0.AddHours(25), false), x => x.Rule.Code == "A01");
        Assert.Equal(AlertOutbox.Resolved, res.Outbox);
    }

    [Fact, Trait("D", "D9.8")]
    public void Quiet_hours_delay_warning_but_never_critical()
    {
        var w = new World();
        w.Job["j"] = Failed with { };
        w.Job["w"] = new AlertFacts { LastOutcome = RunOutcome.Warning };
        var ev = new AlertEvaluator(Catalog, w.Scopes);
        var ups = ev.Tick(T0, quietHours: true);
        Assert.Contains(ups, x => x.Rule.Code == "A01" && x.Scope == "j" && x.Outbox == AlertOutbox.Opened);
        Assert.DoesNotContain(ups, x => x.Rule.Code == "A02" && x.Outbox != AlertOutbox.None);
        var later = ev.Tick(T0.AddHours(8), quietHours: false);
        Assert.Contains(later, x => x.Rule.Code == "A02" && x.Scope == "w" && x.Outbox == AlertOutbox.Opened);
    }

    [Fact, Trait("D", "D9.8")]
    public void Periodic_rules_wait_for_their_interval_and_external_rules_are_never_evaluated_locally()
    {
        var w = new World();
        w.Job["j"] = new AlertFacts { ScheduleInterval = TimeSpan.FromHours(24), LastSuccessAt = T0 - TimeSpan.FromHours(50) };
        var calls = new List<string>();
        var ev = new AlertEvaluator(Catalog, r => { calls.Add(r.Code); return w.Scopes(r); });
        ev.Tick(T0, false);
        Assert.Contains("A03", calls);
        Assert.DoesNotContain("A12", calls);
        calls.Clear();
        ev.Tick(T0.AddMinutes(5), false);
        Assert.DoesNotContain("A03", calls);
        Assert.Contains("A01", calls); // zdarzeniowe — co tick
        ev.Tick(T0.AddMinutes(15), false);
        Assert.Contains("A03", calls);
    }

    [Fact, Trait("D", "D9.8")]
    public void Alert_for_a_vanished_scope_is_resolved()
    {
        var w = new World(); w.Job["j"] = Failed;
        var ev = new AlertEvaluator(Catalog, w.Scopes);
        ev.Tick(T0, false);
        w.Job.Clear();
        var res = Assert.Single(ev.Tick(T0.AddMinutes(1), false), x => x.Rule.Code == "A01");
        Assert.Equal(AlertOutbox.Resolved, res.Outbox);
        Assert.All(ev.Alerts, a => Assert.Equal(AlertState.Resolved, a.State));
    }

    [Theory, Trait("SEC", "SEC-9.6")]
    [InlineData(@"C:\Users\ktos\Documents")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\srv\\share")]
    [InlineData("")]
    public void Scope_must_be_an_opaque_identifier_not_a_path(string scope)
    {
        var ev = new AlertEvaluator(Catalog, _ => []);
        Assert.Throws<ArgumentException>(() => ev.Apply(Catalog.Get("A01"), scope, Failed, T0, false));
    }

    [Fact, Trait("D", "D9.8"), Trait("SEC", "SEC-17.4")]
    public void Clock_jump_alert_is_security_and_ignores_quiet_hours()
    {
        var w = new World(); w.Host["h"] = new AlertFacts { ClockDelta = TimeSpan.FromHours(30) };
        var ev = new AlertEvaluator(Catalog, w.Scopes);
        var u = Assert.Single(ev.Tick(T0, quietHours: true), x => x.Rule.Code == "A17");
        Assert.Equal(AlertOutbox.Opened, u.Outbox);
        Assert.Equal(AlertSeverity.Security, u.Alert!.Severity);
    }

    [Fact, Trait("D", "D9.8")]
    public void External_heartbeat_result_can_be_applied_directly()
    {
        var ev = new AlertEvaluator(Catalog, _ => []);
        var u = ev.Apply(Catalog.Get("A12"), "host-1", new AlertFacts { Flags = new HashSet<string> { "heartbeatMissing" } }, T0, false);
        Assert.Equal(AlertOutbox.Opened, u!.Outbox);
        var r = ev.Apply(Catalog.Get("A12"), "host-1", null, T0.AddHours(1), false);
        Assert.Equal(AlertOutbox.Resolved, r!.Outbox);
    }

    [Fact, Trait("D", "D9.8"), Trait("SEC", "SEC-3.3")]
    public void Scope_count_over_limit_fails_closed_instead_of_truncating()
    {
        var facts = new AlertFacts();
        var many = Enumerable.Range(0, AlertEvaluator.MaxScopesPerRule + 1).Select(i => new ScopedFacts("s" + i, facts)).ToList();
        var ev = new AlertEvaluator(Catalog, _ => many);
        Assert.Throws<InvalidOperationException>(() => ev.Tick(T0, quietHours: false));
        var ok = new AlertEvaluator(Catalog, _ => many.Take(AlertEvaluator.MaxScopesPerRule).ToList());
        ok.Tick(T0, quietHours: false);
    }
}
