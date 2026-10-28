using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Kobian.UI.Dashboard;
using Kobian.UI.Destinations;
using Kobian.UI.Services;
using Kobian.UI.Shell;
using Kobian.UI.Wizard;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>
/// SCR-03 S2 cel FTPS i SCR-09 karta FTPS (BL-34, it. 120): tryb TLS ↔ port (Qb68), walidacja pól, zatwierdzenie odcisku SPKI (D4.11) i zapis pinu do szkicu,
/// błędy probe z kodami KB (KB-2109/2110/2102), brak jawnego FTP w kreatorze (D4.3), zrzuty nowych stanów.
/// </summary>
public sealed class WizardFtpsTests
{
    private const string Spki = "SHA256:q1w2E3r4T5y6U7i8O9p0AaSsDdFfGgHhJjKkLlZzXxC"; // syntetyczny, nie pochodzi z żadnego serwera

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public WizardFtpsTests() => CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");

    /// <summary>S1 (jeden folder) → S2 z wybranym celem FTPS i poprawnymi polami.</summary>
    internal static async Task<WizardViewModel> ToFtps(FakeWizardService svc)
    {
        var vm = new WizardViewModel(svc) { NewSource = @"C:\Dane" };
        vm.AddSourceCommand.Execute(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.SelectedDestination = vm.Destinations.Single(d => d.Value == DestinationKind.Ftps);
        vm.FtpsHost = " nas.biuro.example.invalid ";
        vm.FtpsCredential = " nas-ftp ";
        vm.DestinationPath = " /kopie/biuro ";
        return vm;
    }

    [Fact, Trait("BL", "BL-34"), Trait("Plan", "D4.11"), Trait("AC", "AC-01.3")]
    public async Task Untrusted_certificate_requires_spki_approval_and_pin_goes_to_job_definition()
    {
        var svc = new FakeWizardService();
        svc.ProbeResults.Enqueue(new ProbeResult(ProbeOutcome.UnknownIdentity, "KB-2109", Spki));
        var vm = await ToFtps(svc);
        Assert.True(vm.IsFtps);
        Assert.False(vm.IsSftp);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S2Identity, vm.State);
        Assert.True(vm.IsFtpsIdentity);
        Assert.Equal(Spki, vm.PendingHostKey);
        Assert.Equal("Serwer: nas.biuro.example.invalid:21 · Explicit TLS", vm.FtpsServerText);
        Assert.Null(svc.Drafts[^1].ApprovedHostKey); // przed zatwierdzeniem nic nie jest przypięte (bez TOFU)
        await vm.NextCommand.ExecuteAsync(null); // „Odciski są zgodne — zatwierdź” → ponowny probe z pinem
        Assert.Equal(WizardState.S3Schedule, vm.State);
        var d = svc.Drafts[^1];
        Assert.Equal((DestinationKind.Ftps, "nas.biuro.example.invalid", 21, "nas-ftp", Spki, "/kopie/biuro"),
            (d.Destination, d.FtpsHost, d.FtpsPort, d.FtpsSecret, d.ApprovedHostKey, d.DestinationPath));
        Assert.Null(d.SftpHost); // pola SFTP nie przeciekają do celu FTPS
        Assert.Null(d.SmbSecret);
        var spec = GrpcWizardService.Destination(d);
        Assert.Equal(("ftps", "/kopie/biuro", "nas.biuro.example.invalid", 21, "file:nas-ftp", Spki), (spec.Scheme, spec.Root, spec.Host, spec.Port, spec.SecretRef, spec.HostKey));
        Assert.Equal("ftps://nas.biuro.example.invalid:21/kopie/biuro · Explicit TLS · poświadczenie „nas-ftp”", vm.DestinationText);
        Assert.Equal(["validate:S1Sources", "validate:S2Destination", "probe", "probe"], svc.Calls);
    }

    [Fact, Trait("BL", "BL-34"), Trait("Plan", "D4.11")]
    public async Task Trusted_certificate_needs_no_pin()
    {
        var svc = new FakeWizardService(); // probe OK: łańcuch zaufany przez system
        var vm = await ToFtps(svc);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S3Schedule, vm.State);
        Assert.Null(svc.Drafts[^1].ApprovedHostKey);
        Assert.False(GrpcWizardService.Destination(svc.Drafts[^1]).HasHostKey);
    }

    [Fact, Trait("BL", "BL-34"), Trait("Plan", "D4.3")]
    public void Tls_mode_and_port_stay_consistent_with_qb68()
    {
        var vm = new WizardViewModel(new FakeWizardService());
        Assert.Equal((FtpsTlsMode.Explicit, 21), (vm.SelectedFtpsMode!.Value, vm.FtpsPort));
        vm.SelectedFtpsMode = vm.FtpsModes[1];
        Assert.Equal(990, vm.FtpsPort);
        vm.SelectedFtpsMode = vm.FtpsModes[0];
        Assert.Equal(21, vm.FtpsPort);
        vm.FtpsPort = 2121; // niestandardowy port Explicit — tryb bez zmian
        Assert.Equal(FtpsTlsMode.Explicit, vm.SelectedFtpsMode.Value);
        vm.SelectedFtpsMode = vm.FtpsModes[0];
        Assert.Equal(2121, vm.FtpsPort);
        vm.FtpsPort = 990; // 990 = Implicit (Agent tak to zinterpretuje) — UI nie pokazuje sprzecznej kombinacji
        Assert.Equal(FtpsTlsMode.Implicit, vm.SelectedFtpsMode!.Value);
        vm.SelectedDestination = vm.Destinations.Single(d => d.Value == DestinationKind.Ftps);
        Assert.Equal(990, GrpcWizardService.Destination(vm.Draft()).Port);
        Assert.Contains("Implicit TLS", vm.FtpsServerText, StringComparison.Ordinal);
    }

    [Fact, Trait("Plan", "D4.3")]
    public void Plain_ftp_is_not_offered_in_wizard()
    {
        var vm = new WizardViewModel(new FakeWizardService());
        Assert.Equal([DestinationKind.Local, DestinationKind.Sftp, DestinationKind.Smb, DestinationKind.Ftps, DestinationKind.WebDav], vm.Destinations.Select(d => d.Value));
        Assert.DoesNotContain(vm.Destinations, d => d.Label.Equals("Serwer FTP", StringComparison.Ordinal));
        Assert.Contains("nie jest dostępny", Strings("Wizard.Ftps.PlainFtp"), StringComparison.Ordinal);
    }

    [Theory, Trait("BL", "BL-34"), Trait("SEC", "walidacja wejść")]
    [InlineData("ftps://nas.local", "Wizard.Ftps.Err.HostScheme")]
    [InlineData("ftp:nas", "Wizard.Ftps.Err.HostScheme")]
    [InlineData("nas.local/kopie", "Wizard.Ftps.Err.HostPath")]
    [InlineData(@"nas\kopie", "Wizard.Ftps.Err.HostPath")]
    [InlineData("jan@nas.local", "Wizard.Ftps.Err.HostUser")]
    [InlineData("nas.local:990", "Wizard.Ftps.Err.HostPort")]
    [InlineData("nas lokalny", "Wizard.Ftps.Err.HostInvalid")]
    [InlineData("-nas", "Wizard.Ftps.Err.HostInvalid")]
    [InlineData("", "Wizard.Ftps.Err.HostRequired")]
    [InlineData("nas.biuro.local", null)]
    [InlineData("192.168.1.20", null)]
    [InlineData("fd00::20", null)]
    [InlineData("NAS_01", null)]
    public void Host_rules(string host, string? key) => Assert.Equal(key, FtpsFieldRules.HostError(host));

    [Theory, Trait("BL", "BL-34"), Trait("SEC", "traversal")]
    [InlineData("/kopie/../etc", "Wizard.Ftps.Err.PathDotDot")]
    [InlineData("/..", "Wizard.Ftps.Err.PathDotDot")]
    [InlineData("kopie/biuro", "Wizard.Ftps.Err.PathAbsolute")]
    [InlineData(@"\kopie", "Wizard.Ftps.Err.PathAbsolute")]
    [InlineData(@"E:\Kopie", "Wizard.Ftps.Err.PathAbsolute")]
    [InlineData("/kopie\u0007", "Wizard.Ftps.Err.PathInvalid")]
    [InlineData(" ", "Wizard.Ftps.Err.PathRequired")]
    [InlineData("/kopie/biuro", null)]
    [InlineData("/kopie/..stare", null)] // „..” tylko jako cały segment
    [InlineData("/", null)]
    public void Path_rules(string path, string? key) => Assert.Equal(key, FtpsFieldRules.PathError(path));

    [Theory]
    [InlineData(0, "Wizard.Ftps.Err.Port")]
    [InlineData(65536, "Wizard.Ftps.Err.Port")]
    [InlineData(1, null)]
    [InlineData(65535, null)]
    public void Port_rules(int port, string? key) => Assert.Equal(key, FtpsFieldRules.PortError(port));

    [Theory, Trait("AC", "AC-01.3")]
    [InlineData("nas-ftp", null)]
    [InlineData("", "Wizard.Ftps.Err.CredentialRequired")]
    [InlineData("../sekrety", "Wizard.Ftps.Err.CredentialInvalid")]
    [InlineData("a..b", "Wizard.Ftps.Err.CredentialInvalid")]
    [InlineData("file:nas", "Wizard.Ftps.Err.CredentialInvalid")]
    [InlineData("nas.", "Wizard.Ftps.Err.CredentialInvalid")]
    public void Credential_rules(string name, string? key) => Assert.Equal(key, FtpsFieldRules.CredentialError(name));

    [Fact, Trait("BL", "BL-34"), Trait("AC", "AC-01.2")]
    public async Task Invalid_fields_are_shown_next_to_fields_without_calling_service()
    {
        var svc = new FakeWizardService();
        var vm = await ToFtps(svc);
        vm.FtpsHost = "ftps://nas.local/kopie";
        vm.DestinationPath = "/kopie/../..";
        vm.FtpsCredential = "";
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S2Destination, vm.State);
        Assert.DoesNotContain("validate:S2Destination", svc.Calls);
        Assert.DoesNotContain("probe", svc.Calls);
        Assert.Contains("ftps://", vm.FtpsHostError, StringComparison.Ordinal);
        Assert.True(vm.HasFtpsPathError);
        Assert.True(vm.HasFtpsCredentialError);
        Assert.False(vm.HasFtpsPortError);
        Assert.Empty(vm.Messages); // błędy przy polach, nie w liście komunikatów KB
        vm.FtpsHost = "nas.local"; // poprawka zdejmuje komunikat od razu
        Assert.False(vm.HasFtpsHostError);
        vm.DestinationPath = "/kopie";
        vm.FtpsCredential = "nas-ftp";
        Assert.False(vm.HasFtpsPathError || vm.HasFtpsCredentialError);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S3Schedule, vm.State);
    }

    [Fact]
    public async Task Switching_destination_clears_ftps_errors()
    {
        var vm = await ToFtps(new FakeWizardService());
        vm.FtpsHost = "";
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.HasFtpsHostError);
        vm.SelectedDestination = vm.Destinations[0];
        Assert.False(vm.HasFtpsHostError);
    }

    [Theory, Trait("BL", "BL-34"), Trait("AC", "AC-01.2")]
    [InlineData("KB-2110", "AUTH TLS")]
    [InlineData("KB-2102", "poświadczenia")]
    [InlineData("KB-2105", "uprawnień")]
    public async Task Probe_errors_return_to_s2_with_kb_message_and_action(string code, string fragment)
    {
        var svc = new FakeWizardService();
        svc.ProbeResults.Enqueue(new ProbeResult(ProbeOutcome.PermanentError, code));
        var vm = await ToFtps(svc);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S2Destination, vm.State);
        var msg = Assert.Single(vm.Messages);
        Assert.Contains(code, msg, StringComparison.Ordinal);
        Assert.Contains(fragment, msg, StringComparison.Ordinal);
        Assert.Contains(" — ", msg, StringComparison.Ordinal); // jedna akcja naprawcza (6.3)
    }

    [Fact, Trait("Plan", "D4.11")]
    public async Task Changed_server_key_is_an_alarm_with_presented_fingerprint_not_a_question()
    {
        var svc = new FakeWizardService();
        svc.ProbeResults.Enqueue(new ProbeResult(ProbeOutcome.UnknownIdentity, "KB-2109", Spki));
        svc.ProbeResults.Enqueue(new ProbeResult(ProbeOutcome.PermanentError, "KB-2109", Spki + "x")); // między zatwierdzeniem a ponownym probe klucz się zmienił
        var vm = await ToFtps(svc);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S2Destination, vm.State);
        Assert.Equal(2, vm.Messages.Count);
        Assert.Contains("KB-2109", vm.Messages[0], StringComparison.Ordinal);
        Assert.Contains(Spki + "x", vm.Messages[1], StringComparison.Ordinal);
    }

    [Fact, Trait("Plan", "D4.11")]
    public async Task Approved_pin_is_forgotten_when_server_changes()
    {
        var svc = new FakeWizardService();
        svc.ProbeResults.Enqueue(new ProbeResult(ProbeOutcome.UnknownIdentity, "KB-2109", Spki));
        var vm = await ToFtps(svc);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(Spki, vm.Draft().ApprovedHostKey);
        vm.BackCommand.Execute(null);
        Assert.Equal(Spki, vm.Draft().ApprovedHostKey); // samo „Wstecz” nie unieważnia
        vm.FtpsHost = "inny-nas.example.invalid";
        Assert.Null(vm.Draft().ApprovedHostKey); // pin nie przechodzi na inny serwer
    }

    [Fact, Trait("Plan", "6.9")]
    public void English_texts_exist_for_ftps_states()
    {
        var en = new CultureInfo("en");
        foreach (var key in new[] { "Wizard.Dest.Ftps", "Wizard.Ftps.Mode.Explicit", "Wizard.Ftps.Mode.Implicit", "Wizard.Ftps.Identity.Hint", "Wizard.Ftps.Err.PathDotDot", "Dest.Ftps.NoDetails" })
        {
            var pl = Kobian.UI.Localization.Strings.Get(key, new CultureInfo("pl-PL"));
            var text = Kobian.UI.Localization.Strings.Get(key, en);
            Assert.DoesNotContain("[", text, StringComparison.Ordinal);
            Assert.NotEqual(pl, text);
        }
        Assert.Contains("AUTH TLS", KbText.Message("KB-2110", en), StringComparison.Ordinal);
    }

    // ---------- SCR-09 ----------

    [Fact, Trait("BL", "BL-34")]
    public void Destination_card_shows_ftps_mode_and_short_fingerprint_when_known()
    {
        var full = new DestinationItem("dest-f1", "ftps", "/kopie/biuro", ["Biuro"], "nas.biuro.example.invalid", 990, Spki);
        Assert.Equal("nas.biuro.example.invalid:990 · Implicit TLS · przypięty klucz SHA256:q1w2E3r4…lZzXxC", DestinationsViewModel.Details(full));
        var trusted = new DestinationItem("dest-f2", "ftps", "/kopie", ["A"], "ftp.example.invalid", 21);
        Assert.Equal("ftp.example.invalid:21 · Explicit TLS · certyfikat zaufany przez system", DestinationsViewModel.Details(trusted));
        var v1 = new DestinationItem("dest-f3", "ftps", "/kopie", ["A"]); // ListDestinations v1: tylko schemat i korzeń
        Assert.Contains("definicji zadania", DestinationsViewModel.Details(v1), StringComparison.Ordinal);
        Assert.Equal("", DestinationsViewModel.Details(new DestinationItem("dest-l", "local", @"E:\K", ["A"])));
        var card = new DestinationCardViewModel(full, "Serwer FTPS", "");
        Assert.True(card.IsEncrypted);
        Assert.Equal(Spki, card.FingerprintTip);
        Assert.False(new DestinationCardViewModel(v1 with { Scheme = "local" }, "", "").IsEncrypted);
    }

    [Fact]
    public void Remote_destination_without_server_address_cannot_be_checked_from_list_and_unknown_space_has_no_bar()
    {
        var v1 = new DestinationCardViewModel(new DestinationItem("d", "ftps", "/k", ["A"]), "", "");
        Assert.False(v1.CanCheck); // Agent odpowiedziałby mylącym KB-2106
        Assert.True(v1.ShowCheckUnavailable);
        Assert.True(new DestinationCardViewModel(new DestinationItem("d", "ftps", "/k", ["A"], "nas.example.invalid", 21), "", "").CanCheck);
        Assert.True(new DestinationCardViewModel(new DestinationItem("d", "smb", @"\\nas\k", ["A"]), "", "").CanCheck);
        var noAvbl = new DestinationCardViewModel(new DestinationItem("d", "ftps", "/k", ["A"], "nas.example.invalid", 21), "", "");
        DestinationsViewModel.Apply(noAvbl, new DestinationCheck(null, null, null, true));
        Assert.True(noAvbl.HasSpace); // tekst „Połączenie działa — cel nie podaje wolnego miejsca”
        Assert.False(noAvbl.SpaceKnown); // bez paska
        Assert.False(noAvbl.LowSpace); // i bez ostrzeżenia „mało miejsca”
    }

    [Theory]
    [InlineData("SHA256:abc", "SHA256:abc")]
    [InlineData("SHA256:AAAAAAAABBBBBBBBCCCCCC", "SHA256:AAAAAAAA…CCCCCC")]
    public void Short_fingerprint(string full, string expected) => Assert.Equal(expected, FtpsFieldRules.ShortFingerprint(full));

    [Fact, Trait("BL", "BL-34")]
    public async Task Destinations_screen_lists_ftps_with_scheme_label()
    {
        var vm = new DestinationsViewModel(new FtpsDestinations());
        await vm.RefreshAsync(_ct);
        var ftps = vm.Cards.Where(c => c.Item.Scheme == "ftps").ToList();
        Assert.Equal(2, ftps.Count);
        Assert.All(ftps, c => Assert.Equal("Serwer FTPS", c.SchemeText));
        Assert.All(ftps, c => Assert.True(c.HasDetail));
    }

    // ---------- zrzuty ----------

    public static TheoryData<string, string, int> Shots() => new()
    {
        { "S2-ftps", "Light", 1180 },
        { "S2-ftps", "Dark", 800 },
        { "S2-ftps-invalid", "Light", 800 },
        { "S2-ftps-identity", "Light", 1180 },
        { "S2-ftps-identity", "Dark", 800 },
        { "S2-ftps-kb2110", "Light", 1180 },
    };

    [Theory, MemberData(nameof(Shots)), Trait("Plan", "R2.6.1"), Trait("AC", "AC-UX.3"), Trait("BL", "BL-34")]
    public Task Ftps_states_render(string state, string theme, int width) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var svc = new FakeWizardService();
        if (state == "S2-ftps-identity") svc.ProbeResults.Enqueue(new ProbeResult(ProbeOutcome.UnknownIdentity, "KB-2109", Spki));
        if (state == "S2-ftps-kb2110") svc.ProbeResults.Enqueue(new ProbeResult(ProbeOutcome.PermanentError, "KB-2110"));
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed()), new FixedTime(FakeDashboardSource.Now), TimeZoneInfo.Utc),
            wizardFactory: () => new WizardViewModel(svc));
        shell.OpenWizard();
        var vm = (WizardViewModel)shell.CurrentPage!;
        foreach (var p in new[] { @"C:\Users\Anna\Documents", @"D:\Firma\Faktury" })
        {
            vm.NewSource = p;
            vm.AddSourceCommand.Execute(null);
        }
        await vm.NextCommand.ExecuteAsync(null);
        vm.SelectedDestination = vm.Destinations.Single(d => d.Value == DestinationKind.Ftps);
        vm.FtpsHost = "nas.biuro.example.invalid";
        vm.FtpsCredential = "nas-ftp";
        vm.DestinationPath = "/volume1/kobian";
        if (state == "S2-ftps-identity") vm.SelectedFtpsMode = vm.FtpsModes[1];
        if (state == "S2-ftps-invalid")
        {
            vm.FtpsHost = "ftps://nas.biuro/kopie";
            vm.DestinationPath = "/volume1/../kobian";
            vm.FtpsCredential = "";
        }
        if (state != "S2-ftps") await vm.NextCommand.ExecuteAsync(null);
        var window = new MainWindow { DataContext = shell, Width = width, Height = width < 1000 ? 1400 : 1000 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"SCR-03-{state}-{theme}-{width}.png");
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 10_000, "zrzut pusty");
        var ids = Visible(window);
        Assert.Contains("SCR-03.Next", ids);
        switch (state)
        {
            case "S2-ftps":
                Assert.Contains("SCR-03.S2.FtpsHost", ids);
                Assert.Contains("SCR-03.S2.FtpsMode", ids);
                Assert.Contains("SCR-03.S2.FtpsCredential", ids);
                Assert.Contains("SCR-03.S2.PlainFtp", ids);
                Assert.DoesNotContain("SCR-03.S2.Host", ids); // pola SFTP ukryte
                Assert.DoesNotContain("SCR-03.S2.FtpsHostError", ids);
                break;
            case "S2-ftps-invalid":
                Assert.Contains("SCR-03.S2.FtpsHostError", ids);
                Assert.Contains("SCR-03.S2.FtpsPathError", ids);
                Assert.Contains("SCR-03.S2.FtpsCredentialError", ids);
                break;
            case "S2-ftps-identity":
                Assert.Equal(WizardState.S2Identity, vm.State);
                Assert.Contains("SCR-03.S2.Fingerprint", ids);
                Assert.Contains("SCR-03.S2.FtpsIdentityHint", ids);
                Assert.Contains("SCR-03.S2.FtpsServer", ids);
                break;
            case "S2-ftps-kb2110":
                Assert.Contains("SCR-03.Messages", ids);
                Assert.Contains("KB-2110", Assert.Single(vm.Messages), StringComparison.Ordinal);
                break;
        }
        window.Close();
    }, _ct);

    public static TheoryData<string, int> DestinationShots() => new() { { "Light", 1180 }, { "Dark", 800 } };

    [Theory, MemberData(nameof(DestinationShots)), Trait("Plan", "R2.6.1"), Trait("BL", "BL-34")]
    public Task Destinations_with_ftps_render(string theme, int width) => UiSession.Session.Dispatch(async () =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var vm = new DestinationsViewModel(new FtpsDestinations());
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed())), destinations: vm);
        shell.Navigate("SCR-09");
        await vm.RefreshAsync(CancellationToken.None);
        foreach (var c in vm.Cards.Where(c => c.CanCheck).ToList()) await vm.CheckCommand.ExecuteAsync(c);
        var window = new MainWindow { DataContext = shell, Width = width, Height = width < 1000 ? 1100 : 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(UiSession.SnapshotDir(), $"SCR-09-ftps-{theme}-{width}.png");
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618
        Assert.True(new FileInfo(path).Length > 10_000, "zrzut pusty");
        var ids = Visible(window);
        Assert.Contains("SCR-09.Dest.dest-ftps-full", ids);
        Assert.Contains("SCR-09.Detail", ids);
        Assert.Contains("SCR-09.CheckUnavailable", ids); // cel v1 bez adresu serwera
        window.Close();
    }, _ct);

    private static HashSet<string> Visible(Visual root) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Control>().Where(c => c.IsEffectivelyVisible)
            .Select(Avalonia.Automation.AutomationProperties.GetAutomationId).OfType<string>().ToHashSet(StringComparer.Ordinal);

    private static string Strings(string key) => Kobian.UI.Localization.Strings.Get(key);

    /// <summary>SCR-09 z celami FTPS: z pełnymi szczegółami (przyszły kontrakt) i w kształcie ListDestinations v1 (tylko schemat + korzeń).</summary>
    private sealed class FtpsDestinations : IDestinationsSource
    {
        public Task<DestinationList> ListDestinationsAsync(CancellationToken ct) => Task.FromResult(new DestinationList(
        [
            new("dest-0000000000000001", "local", @"E:\Kopie\Biuro", ["Dokumenty biura"]),
            new("dest-ftps-full", "ftps", "/volume1/kobian", ["Faktury — noc", "Poczta"], "nas.biuro.example.invalid", 990, Spki),
            new("dest-ftps-v1", "ftps", "/kopie/archiwum", ["Archiwum"]),
        ], 0));

        public Task<DestinationCheck> CheckDestinationAsync(DestinationItem d, CancellationToken ct) => Task.FromResult(d.Id switch
        {
            "dest-0000000000000001" => new DestinationCheck(null, 412L << 30, 931L << 30, true),
            "dest-ftps-full" => new DestinationCheck("KB-2109", null, null, false), // przypięty klucz ≠ przedstawiony
            _ => new DestinationCheck(null, null, null, true),
        });
    }
}
