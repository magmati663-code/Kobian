using System.Globalization;
using Kobian.Api.V1;
using Kobian.UI.Dashboard;
using Kobian.UI.Localization;
using Kobian.UI.Services;
using Xunit;
using UiRole = Kobian.UI.Dashboard.CallerRole;

namespace Kobian.Tests.UI;

/// <summary>BL-45/BL-82 (D9.7): lista alertów na pulpicie SCR-01 — mapowanie z Agenta, teksty po kodzie reguły, akcje wg roli, wynik akcji z usługi.</summary>
public sealed class AlertDashboardTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static DashboardViewModel Vm(FakeDashboardSource src) => new(src, new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc);

    private static T Pl<T>(Func<T> f)
    {
        var (ui, cur) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        try { return f(); }
        finally { (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = (ui, cur); }
    }

    private static async Task<T> InPolish<T>(Func<Task<T>> f)
    {
        var (ui, cur) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        try { return await f(); }
        finally { (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = (ui, cur); }
    }

    private static FakeDashboardSource Src(UiRole role) =>
        new(FakeDashboardSource.AllGood() with { Role = role, Alerts = FakeDashboardSource.SampleAlerts() });

    [Fact, Trait("BL", "BL-45")]
    public async Task Alerts_are_listed_security_first_with_rule_titles_and_unknown_codes_still_visible()
    {
        var vm = Vm(Src(UiRole.Admin));
        await InPolish(async () => { await vm.RefreshAsync(_ct); return 0; });
        Assert.True(vm.HasAlerts);
        // bezpieczeństwo → krytyczny → ostrzeżenia; w obrębie wagi oczekujące przed rozwiązanymi
        Assert.Equal(["a2", "a3", "a1", "a4"], vm.Alerts.Select(a => a.Id));
        Assert.Equal("Zmienił się klucz lub certyfikat serwera celu", Pl(() => vm.Alerts[0].Title));
        Assert.Equal("Alert A99", Pl(() => vm.Alerts[3].Title)); // nowszy Agent: kod bez tytułu w tej wersji UI
        Assert.True(vm.Alerts[0].IsSecurity);
        Assert.Equal("Eskalowany", Pl(() => vm.Alerts[0].EscalatedText));
        Assert.Null(vm.Alerts[3].Subject); // host: bez podmiotu
    }

    [Fact, Trait("BL", "BL-45"), Trait("Plan", "6.7")]
    public async Task Actions_follow_role_and_alert_state()
    {
        var viewer = Vm(Src(UiRole.Viewer));
        await viewer.RefreshAsync(_ct);
        Assert.All(viewer.Alerts, a => Assert.False(a.CanAcknowledge || a.CanSilence));

        var op = Vm(Src(UiRole.Operator));
        await op.RefreshAsync(_ct);
        Assert.Equal([true, false, true, false], op.Alerts.Select(a => a.CanAcknowledge).ToArray()); // a2 notified, a3 acknowledged, a1 open, a4 silenced
        Assert.All(op.Alerts, a => Assert.False(a.CanSilence)); // wyciszanie: Administrator

        var admin = Vm(Src(UiRole.Admin));
        await admin.RefreshAsync(_ct);
        Assert.Equal([true, true, true, false], admin.Alerts.Select(a => a.CanSilence).ToArray());
    }

    [Fact, Trait("BL", "BL-45")]
    public async Task Acknowledge_calls_service_then_reloads_and_reports_success()
    {
        var src = Src(UiRole.Operator);
        var vm = Vm(src);
        await InPolish(async () =>
        {
            await vm.RefreshAsync(_ct);
            await vm.AcknowledgeAlertCommand.ExecuteAsync(vm.Alerts[0]);
            return 0;
        });
        Assert.Equal(["a2"], src.Acked);
        Assert.False(vm.AlertMessageIsError);
        Assert.Equal("Alert potwierdzony.", vm.AlertMessage);
        Assert.True(vm.HasAlertMessage);
    }

    [Fact, Trait("BL", "BL-45")]
    public async Task Silence_uses_default_duration_and_requires_admin()
    {
        var src = Src(UiRole.Admin);
        var vm = Vm(src);
        await vm.RefreshAsync(_ct);
        await vm.SilenceAlertCommand.ExecuteAsync(vm.Alerts[2]); // a1 (otwarty)
        Assert.Equal([("a1", DashboardViewModel.DefaultSilenceMinutes)], src.Silenced);
        Assert.InRange(DashboardViewModel.DefaultSilenceMinutes, 1, 10080); // zakres z agent.proto

        var op = Src(UiRole.Operator);
        var opVm = Vm(op);
        await opVm.RefreshAsync(_ct);
        await opVm.SilenceAlertCommand.ExecuteAsync(opVm.Alerts[2]);
        Assert.Empty(op.Silenced); // UI nie wysyła wywołania, którego rola nie ma
    }

    [Fact, Trait("BL", "BL-45")]
    public async Task Silence_duration_can_be_chosen_and_security_alerts_cap_at_24h()
    {
        var src = Src(UiRole.Admin);
        var vm = Vm(src);
        await InPolish(async () => { await vm.RefreshAsync(_ct); return 0; });
        var warning = vm.Alerts[2]; // a1 (ostrzeżenie)
        var security = vm.Alerts[0]; // a2 (bezpieczeństwo)
        Assert.Equal(DashboardViewModel.DefaultSilenceMinutes, warning.SelectedSilence.Minutes); // domyślnie 4 h
        Assert.Equal([60, 240, 1440, 10080], warning.SilenceOptions.Select(o => o.Minutes).ToArray());
        Assert.Equal([60, 240, 1440], security.SilenceOptions.Select(o => o.Minutes).ToArray()); // Agent skraca do 24 h — UI nie obiecuje więcej
        Assert.All(warning.SilenceOptions, o => Assert.InRange(o.Minutes, 1, 10080)); // zakres z agent.proto

        warning.SelectedSilence = warning.SilenceOptions[3];
        await InPolish(async () => { await vm.SilenceAlertCommand.ExecuteAsync(warning); return 0; });
        Assert.Equal([("a1", 10080)], src.Silenced);
        Assert.Equal("Alert wyciszony na 7 dni.", vm.AlertMessage);
    }

    [Fact, Trait("BL", "BL-45")]
    public void Silence_option_labels_exist_in_both_languages()
    {
        foreach (var culture in new[] { "pl-PL", "en-US" })
        {
            var ci = new CultureInfo(culture);
            foreach (var key in new[] { "Alert.Silence.1h", "Alert.Silence.4h", "Alert.Silence.24h", "Alert.Silence.7d", "Alert.Silence.Duration" })
                Assert.False(Strings.Get(key, ci).StartsWith('['), $"{key} {culture}");
        }
    }

    [Fact, Trait("BL", "BL-45")]
    public async Task Service_refusal_shows_kb_message_and_keeps_list()
    {
        var src = Src(UiRole.Operator);
        src.AlertActionCode = "KB-6120";
        var vm = Vm(src);
        await InPolish(async () =>
        {
            await vm.RefreshAsync(_ct);
            await vm.AcknowledgeAlertCommand.ExecuteAsync(vm.Alerts[0]);
            return 0;
        });
        Assert.True(vm.AlertMessageIsError);
        Assert.Contains("KB-6120", vm.AlertMessage, StringComparison.Ordinal);
        Assert.Equal(4, vm.Alerts.Count);
    }

    [Fact, Trait("BL", "BL-45")]
    public async Task Connection_loss_during_action_reports_offline_code()
    {
        var src = Src(UiRole.Operator);
        var vm = new DashboardViewModel(new ThrowingAckSource(src.Data), new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc);
        await vm.RefreshAsync(_ct);
        await InPolish(async () => { await vm.AcknowledgeAlertCommand.ExecuteAsync(vm.Alerts[0]); return 0; });
        Assert.True(vm.AlertMessageIsError);
        Assert.Contains("KB-9001", vm.AlertMessage, StringComparison.Ordinal);
    }

    [Fact, Trait("BL", "BL-45")]
    public async Task Source_without_alert_methods_answers_operation_not_available()
    {
        IDashboardSource plain = new PlainSource();
        Assert.Equal("KB-7012", await plain.AcknowledgeAlertAsync("x", _ct));
        Assert.Equal("KB-7012", await plain.SilenceAlertAsync("x", 60, _ct));
    }

    [Fact, Trait("BL", "BL-45"), Trait("BL", "BL-82")]
    public void Grpc_mapping_converts_alerts_and_skips_rows_without_id()
    {
        var status = new GetStatusResponse();
        status.Alerts.Add(new AlertInfo { AlertId = "0123456789abcdef0123456789abcdef", Code = "A11", Severity = "security", State = "notified", ScopeKind = "job", Subject = "Zdjęcia", OpenedUnixSeconds = 1_792_000_000, Kb = "KB-3201", Reminders = 2, Escalated = true });
        status.Alerts.Add(new AlertInfo { Code = "A01", Severity = "critical", State = "open" }); // stary Agent bez alert_id
        status.Alerts.Add(new AlertInfo { AlertId = "ffffffffffffffffffffffffffffffff", Code = "A02", Severity = "nieznana", State = "nieznany" });
        var d = GrpcDashboardSource.Map(status, new ListSnapshotsResponse(), FakeDashboardSource.Now);
        Assert.Equal(2, d.Alerts!.Count);
        var a = d.Alerts[0];
        Assert.Equal((AlertSeverity.Security, AlertState.Notified, "KB-3201", 2, true), (a.Severity, a.State, a.Kb, a.Reminders, a.Escalated));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_792_000_000), a.OpenedUtc);
        Assert.Equal((AlertSeverity.Warning, AlertState.Open), (d.Alerts[1].Severity, d.Alerts[1].State)); // nieznane wartości → najłagodniejsze, nie wyjątek
    }

    [Fact, Trait("BL", "BL-45")]
    public void Grpc_action_reply_maps_to_kb_code_or_null()
    {
        Assert.Null(GrpcDashboardSource.MapAlertAction(new AlertActionReply { Result = OverallState.Ok, State = "acknowledged" }));
        Assert.Equal("KB-6120", GrpcDashboardSource.MapAlertAction(new AlertActionReply { Result = OverallState.Failed, Code = "KB-6120" }));
        Assert.Equal(GrpcDashboardSource.KbUnknown, GrpcDashboardSource.MapAlertAction(new AlertActionReply { Result = OverallState.Failed }));
    }

    [Fact, Trait("BL", "BL-45")]
    public void Every_rule_has_polish_and_english_title()
    {
        foreach (var culture in new[] { new CultureInfo("pl-PL"), new CultureInfo("en-US") })
            for (var i = 1; i <= 17; i++)
            {
                var key = $"Alert.A{i:00}";
                var text = Strings.Get(key, culture);
                Assert.False(text.StartsWith('['), $"{key} brak w {culture.Name}");
            }
    }

    private sealed class ThrowingAckSource(DashboardData data) : IDashboardSource
    {
        public Task<DashboardData> LoadAsync(CancellationToken ct) => Task.FromResult(data);

        public Task RunJobAsync(string jobId, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> AcknowledgeAlertAsync(string alertId, CancellationToken ct) => throw new IOException("down");
    }

    private sealed class PlainSource : IDashboardSource
    {
        public Task<DashboardData> LoadAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task RunJobAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }
}
