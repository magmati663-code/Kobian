using System.Globalization;
using Kobian.UI.Dashboard;
using Kobian.UI.Localization;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>SCR-01 (6.3, D6.6): ViewModel pulpitu bez okna — stan globalny, kolejność kafelków, teksty PL, role.</summary>
public sealed class DashboardViewModelTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static DashboardViewModel Vm(FakeDashboardSource src) => new(src, new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc);

    private static T InPolish<T>(Func<T> f)
    {
        var (ui, cur) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        try { return f(); }
        finally { (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = (ui, cur); }
    }

    [Fact, Trait("Plan", "6.3")]
    public async Task Mixed_jobs_worst_first_with_one_main_action()
    {
        var src = new FakeDashboardSource(FakeDashboardSource.Mixed());
        var vm = Vm(src);
        await InPolish(async () => { await vm.RefreshAsync(_ct); return 0; });
        Assert.Equal(ProtectionState.AtRisk, vm.Overall);
        Assert.Equal(["ERP Optima — noc", "Zdjęcia", "Archiwum 2025", "Dokumenty biura"], vm.Jobs.Select(j => j.Name));
        Assert.Equal("Część danych nie jest chroniona", vm.Headline);
        Assert.Equal("ERP Optima — noc: Ostatnia kopia nie powiodła się.", vm.Subline);
        Assert.Equal("Pokaż przyczynę", vm.PrimaryActionText);
        Assert.Equal("1 z 4 zadań chronionych", vm.Summary);
        Assert.Equal("Kopia wykonana z ostrzeżeniami — pominięto 3 pliki (zablokowane).", vm.Jobs[1].Detail);
        Assert.Equal("KB-2104", vm.Jobs[0].Code);
        Assert.True(vm.CanRun);
    }

    [Fact, Trait("Plan", "6.3")]
    public async Task Protected_job_detail_uses_relative_days_in_users_zone()
    {
        var src = new FakeDashboardSource(FakeDashboardSource.Mixed());
        var vm = Vm(src);
        await InPolish(async () => { await vm.RefreshAsync(_ct); return 0; });
        var ok = vm.Jobs.Single(j => j.Name == "Dokumenty biura");
        Assert.Equal("Kopie wykonywane zgodnie z harmonogramem i zweryfikowane.", ok.Detail);
        Assert.Equal("wczoraj 22:00", ok.LastCopy);
        Assert.Equal("dziś 22:00", ok.Next);
        Assert.Equal("Zadanie wyłączone — kopie nie są wykonywane.", vm.Jobs.Single(j => j.Name == "Archiwum 2025").Detail);
    }

    [Fact]
    public async Task Overdue_text_uses_polish_genitive_plural()
    {
        var data = FakeDashboardSource.Mixed();
        var erp = data.Jobs[1] with { LastResult = LastRunResult.Succeeded };
        var vm = Vm(new FakeDashboardSource(data with { Jobs = [erp] }));
        await InPolish(async () => { await vm.RefreshAsync(_ct); return 0; });
        Assert.Equal(ProtectionReason.Overdue, vm.Jobs[0].Reason);
        Assert.Equal("Brak udanej kopii od 3 dni.", vm.Jobs[0].Detail);
        Assert.Equal("Uruchom kopię teraz", vm.PrimaryActionText);
    }

    [Fact]
    public async Task Viewer_cannot_run_and_gets_an_explanation()
    {
        var src = new FakeDashboardSource(FakeDashboardSource.Mixed(CallerRole.Viewer));
        var vm = Vm(src);
        await InPolish(async () => { await vm.RefreshAsync(_ct); return 0; });
        Assert.False(vm.ActCommand.CanExecute(vm.Jobs[0]));
        Assert.False(vm.RunJobCommand.CanExecute(vm.Jobs[0]));
        Assert.Equal("Wymaga roli Operator Kobian", InPolish(() => vm.RunHint));
    }

    [Fact, Trait("RD", "RD-57"), Trait("Plan", "6.7")]
    public async Task Viewer_sees_read_only_banner_with_a_way_out_that_depends_on_uac()
    {
        var src = new FakeDashboardSource(FakeDashboardSource.Mixed(CallerRole.Viewer));
        var vm = Vm(src);
        Assert.False(vm.ShowReadOnlyBanner); // przed odczytem rola nieznana (domyślna wartość enum = Viewer)
        await vm.RefreshAsync(_ct);
        Assert.True(vm.ShowReadOnlyBanner);
        Assert.Contains("Kobian Operators", InPolish(() => vm.ReadOnlyDetail), StringComparison.Ordinal);

        var uac = new DashboardViewModel(src, new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc) { Elevation = new FakeElevation(true) };
        await uac.RefreshAsync(_ct);
        Assert.Contains("Uruchom jako administrator", InPolish(() => uac.ReadOnlyDetail), StringComparison.Ordinal);
        Assert.Contains("Run as administrator", InCulture("en-US", () => uac.ReadOnlyDetail), StringComparison.Ordinal);
    }

    [Theory, Trait("RD", "RD-57")]
    [InlineData(CallerRole.Operator)]
    [InlineData(CallerRole.Admin)]
    public async Task Operator_and_admin_see_no_read_only_banner(CallerRole role)
    {
        var vm = Vm(new FakeDashboardSource(FakeDashboardSource.Mixed(role)));
        await vm.RefreshAsync(_ct);
        Assert.False(vm.ShowReadOnlyBanner);
    }

    [Fact, Trait("RD", "RD-57")]
    public async Task Read_only_banner_hides_when_connection_is_lost_and_role_change_is_announced()
    {
        var src = new FakeDashboardSource(FakeDashboardSource.Mixed(CallerRole.Viewer));
        var vm = Vm(src);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await vm.RefreshAsync(_ct);
        Assert.True(vm.ShowReadOnlyBanner);
        src.Fail = true;
        await vm.RefreshAsync(_ct);
        Assert.False(vm.ShowReadOnlyBanner); // bez połączenia rola nieznana — nie twierdzimy „tylko podgląd”
        src.Fail = false;
        src.Data = FakeDashboardSource.Mixed(CallerRole.Operator);
        changed.Clear();
        await vm.RefreshAsync(_ct);
        Assert.False(vm.ShowReadOnlyBanner);
        Assert.Contains(nameof(DashboardViewModel.ShowReadOnlyBanner), changed);
    }

    private static T InCulture<T>(string name, Func<T> f)
    {
        var ui = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(name);
        try { return f(); }
        finally { CultureInfo.CurrentUICulture = ui; }
    }

    [Fact]
    public async Task Act_runs_job_for_run_reasons_and_navigates_otherwise()
    {
        var src = new FakeDashboardSource(FakeDashboardSource.Mixed());
        var vm = Vm(src);
        await vm.RefreshAsync(_ct);
        JobTileViewModel? nav = null;
        vm.NavigationRequested += (_, t) => nav = t;
        await vm.ActCommand.ExecuteAsync(vm.Jobs.Single(j => j.Name == "Dokumenty biura"));
        Assert.Equal(["j1"], src.Runs);
        await vm.ActCommand.ExecuteAsync(vm.Jobs[0]); // LastRunFailed → szczegóły, nie ślepe ponowienie
        Assert.Equal("j2", nav?.Job.JobId);
        Assert.Single(src.Runs);
    }

    [Fact]
    public async Task Empty_state_and_lost_connection_keep_last_data()
    {
        var src = new FakeDashboardSource(FakeDashboardSource.Empty());
        var vm = Vm(src);
        await InPolish(async () => { await vm.RefreshAsync(_ct); return 0; });
        Assert.True(vm.IsEmpty);
        Assert.Equal("Utwórz pierwsze zadanie", vm.PrimaryActionText);
        var wizard = false;
        vm.CreateJobRequested += (_, _) => wizard = true;
        await vm.PrimaryCommand.ExecuteAsync(null);
        Assert.True(wizard);
        Assert.Empty(src.Runs);

        src.Data = FakeDashboardSource.AllGood();
        await vm.RefreshAsync(_ct);
        Assert.Equal(ProtectionState.Protected, vm.Overall);
        Assert.Equal("Ostatnia kopia: dziś 08:40 · Następna: dziś 09:40", InPolish(() => vm.Subline));
        src.Fail = true;
        await vm.RefreshAsync(_ct);
        Assert.True(vm.ConnectionLost);
        Assert.True(vm.ShowStaleBanner);
        Assert.False(vm.IsOffline);
        Assert.Equal(2, vm.Jobs.Count);
    }

    [Fact, Trait("SEC", "SEC-17.4")]
    public async Task Retention_hold_banner_follows_data_and_role()
    {
        var src = new FakeDashboardSource(FakeDashboardSource.AllGood() with { RetentionHeld = true, Role = CallerRole.Operator });
        var vm = Vm(src);
        await InPolish(async () => { await vm.RefreshAsync(_ct); return 0; });
        Assert.True(vm.ShowRetentionHoldBanner);
        Assert.Contains("administratora", InPolish(() => vm.RetentionHoldDetail), StringComparison.Ordinal);
        Assert.Null(vm.RetentionHoldCommand);
        Assert.Equal(ProtectionState.Protected, vm.Overall); // kopie są wykonywane — wstrzymane jest tylko porządkowanie
        src.Data = src.Data with { Role = CallerRole.Admin };
        await vm.RefreshAsync(_ct);
        Assert.Equal("kobian retention release --confirm", vm.RetentionHoldCommand);
        Assert.Contains("zegara", InPolish(() => vm.Headline + vm.RetentionHoldDetail + Strings.Get("Retention.Held")), StringComparison.Ordinal);
        src.Data = FakeDashboardSource.AllGood();
        await vm.RefreshAsync(_ct);
        Assert.False(vm.ShowRetentionHoldBanner);
    }

    [Fact, Trait("Plan", "R2.6.4")]
    public async Task Offline_without_data_is_not_an_empty_dashboard()
    {
        var src = new FakeDashboardSource(FakeDashboardSource.Mixed()) { Fail = true };
        var vm = Vm(src);
        await InPolish(async () => { await vm.RefreshAsync(_ct); return 0; });
        Assert.True(vm.IsOffline);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.ShowStaleBanner);
        Assert.Equal("Usługa Kobian nie działa", vm.Headline);
        Assert.Equal("KB-9001", vm.OfflineCode);
        src.Fail = false;
        await vm.PrimaryCommand.ExecuteAsync(null); // „Spróbuj ponownie” = ponowny odczyt
        Assert.False(vm.IsOffline);
        Assert.Equal(ProtectionState.AtRisk, vm.Overall);
        Assert.Null(vm.OfflineCode);
    }

    [Theory]
    [InlineData(300, 1, 1), InlineData(700, 2, 4), InlineData(1100, 3, 4), InlineData(1100, 2, 2), InlineData(double.PositiveInfinity, 3, 9)]
    public void Adaptive_grid_columns(double width, int expected, int count) =>
        Assert.Equal(expected, Kobian.UI.Controls.AdaptiveGrid.Columns(width, 320, 16, 3, count));

    [Theory]
    [InlineData(1, "1 plik"), InlineData(2, "2 pliki"), InlineData(5, "5 plików"), InlineData(12, "12 plików"), InlineData(22, "22 pliki"), InlineData(114, "114 plików")]
    public void Polish_plural(long n, string expected) => Assert.Equal(expected, Plural.Text("Plural.File", n, new CultureInfo("pl-PL")));

    [Fact]
    public void Every_key_has_an_english_translation()
    {
        var pl = Strings.Keys(CultureInfo.InvariantCulture);
        var en = Strings.Keys(new CultureInfo("en"));
        Assert.NotEmpty(pl);
        Assert.Empty(pl.Except(en));
        Assert.Empty(en.Except(pl));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
