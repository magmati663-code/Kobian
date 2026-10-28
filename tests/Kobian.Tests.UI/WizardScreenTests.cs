using System.Globalization;
using Kobian.UI.Dashboard;
using Kobian.UI.Jobs;
using Kobian.UI.Shell;
using Kobian.UI.Wizard;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>SCR-03 (D6.7): przejście 6 kroków na ViewModelu z atrapą usługi — walidacja w usłudze, probe, repozytorium, klucz odzyskiwania, zapis, pierwsza kopia.</summary>
public sealed class WizardScreenTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public WizardScreenTests() => CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");

    /// <summary>Doprowadza kreator do S4 (źródło + cel lokalny + domyślny harmonogram).</summary>
    internal static async Task<WizardViewModel> ToSecurity(FakeWizardService svc)
    {
        var vm = new WizardViewModel(svc) { NewSource = @"C:\Dane\Dokumenty" };
        vm.AddSourceCommand.Execute(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.DestinationPath = @"E:\KopieKobian";
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        return vm;
    }

    private static void TypeChallenge(WizardViewModel vm, string key)
    {
        var g = RecoveryKeyText.Groups(key);
        vm.ChallengeFirst = g[vm.ChallengeGroups.First - 1].ToLowerInvariant(); // wielkość liter bez znaczenia
        vm.ChallengeSecond = g[vm.ChallengeGroups.Second - 1];
    }

    [Fact, Trait("Plan", "D6.7"), Trait("Plan", "AC-01.1 część UI — pełne AC wymaga Agenta i NFR-01")]
    public async Task Six_steps_on_presets_save_job_and_run_first_backup()
    {
        var svc = new FakeWizardService();
        var vm = await ToSecurity(svc);
        Assert.Equal(WizardState.S4Security, vm.State);
        Assert.Equal("Dokumenty", vm.JobName); // nazwa podpowiedziana z pierwszego folderu
        var secret = FakeWizardService.RandomSecret();
        vm.Password = vm.PasswordConfirm = secret;
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S4RecoveryKey, vm.State);
        Assert.Equal(secret.Length, svc.PasswordLengthSeen);
        Assert.Equal("", vm.Password); // hasło nie zostaje w VM po InitRepository
        Assert.False(vm.CanGoBack); // AC-28.1
        TypeChallenge(vm, svc.RecoveryKey!);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S5Retention, vm.State);
        Assert.Empty(vm.RecoveryKeyGroups); // klucz znika z VM po potwierdzeniu
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S6Summary, vm.State);
        Assert.Contains("codziennie o 21:00", vm.SchedulePreview, StringComparison.Ordinal);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.Done, vm.State);
        Assert.Contains("chronione", vm.FinalText, StringComparison.Ordinal);
        Assert.Equal(["validate:S1Sources", "validate:S2Destination", "probe", "validate:S3Schedule", "init", "validate:S5Retention", "save", "run:Dokumenty"], svc.Calls);
        var saved = svc.Drafts[^1];
        Assert.Equal("0 21 * * *", saved.Cron);
        Assert.Equal([@"C:\Dane\Dokumenty"], saved.Sources);
        Assert.Equal((7, 4, 12), (saved.KeepDaily, saved.KeepWeekly, saved.KeepMonthly));
        var finished = false;
        vm.Finished += (_, _) => finished = true;
        await vm.NextCommand.ExecuteAsync(null); // „Zamknij”
        Assert.True(finished);
    }

    [Fact, Trait("BL", "BL-25"), Trait("Plan", "D3.12")]
    public async Task S5_shows_keep_preview_and_refreshes_it_when_rules_change()
    {
        var (ui, cur) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        try
        {
            var svc = new FakeWizardService();
            var vm = await ToSecurity(svc);
            vm.Password = vm.PasswordConfirm = FakeWizardService.RandomSecret();
            await vm.NextCommand.ExecuteAsync(null);
            TypeChallenge(vm, svc.RecoveryKey!);
            await vm.NextCommand.ExecuteAsync(null);
            Assert.Equal(WizardState.S5Retention, vm.State);
            await vm.RefreshRetentionPreviewAsync();
            Assert.True(vm.HasRetentionPreview);
            Assert.Equal("Po ustabilizowaniu: ok. 21 kopii, najstarsza sprzed ok. 12 mies.", vm.RetentionPreviewText);
            vm.KeepMonthly = 0;
            await vm.RefreshRetentionPreviewAsync();
            Assert.Equal("Po ustabilizowaniu: ok. 9 kopii, najstarsza sprzed 7 dni", vm.RetentionPreviewText);
            Assert.True(svc.PreviewCalls >= 3);
            svc.OfferPreview = false;
            await vm.RefreshRetentionPreviewAsync();
            Assert.False(vm.HasRetentionPreview); // usługa bez podglądu — S5 bez komunikatu, krok nadal działa
            Assert.DoesNotContain("preview", svc.Calls); // podgląd nie jest operacją z efektem — nie liczy się do sekwencji wywołań
        }
        finally
        {
            (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = (ui, cur);
        }
    }

    [Theory, Trait("Plan", "6.9")]
    [InlineData(7, "7 dni")]
    [InlineData(20, "ok. 2 tyg.")]
    [InlineData(59, "ok. 8 tyg.")]
    [InlineData(365, "ok. 12 mies.")]
    [InlineData(1095, "ok. 36 mies.")]
    public void Retention_age_is_human(int days, string expected)
    {
        var ui = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("pl-PL");
        try { Assert.Equal(expected, WizardViewModel.AgeText(days)); }
        finally { CultureInfo.CurrentUICulture = ui; }
    }

    [Fact, Trait("AC", "AC-28.1")]
    public async Task Recovery_key_requires_two_correct_distinct_groups()
    {
        var svc = new FakeWizardService();
        var vm = await ToSecurity(svc);
        vm.Password = vm.PasswordConfirm = FakeWizardService.RandomSecret();
        await vm.NextCommand.ExecuteAsync(null);
        Assert.NotEqual(vm.ChallengeGroups.First, vm.ChallengeGroups.Second);
        Assert.InRange(vm.ChallengeGroups.First, 1, 8);
        Assert.InRange(vm.ChallengeGroups.Second, 1, 8);
        vm.ChallengeFirst = "XXXXX";
        vm.ChallengeSecond = "YYYYY";
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S4RecoveryKey, vm.State);
        Assert.Contains("nie zgadzają", Assert.Single(vm.Messages), StringComparison.Ordinal);
        TypeChallenge(vm, svc.RecoveryKey!);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S5Retention, vm.State);
        vm.BackCommand.Execute(null);
        Assert.Equal(WizardState.S3Schedule, vm.State); // repo utworzone — krok 4 pomijany wstecz (D6.7 reguła 1)
    }

    [Fact, Trait("Plan", "D6.7")]
    public async Task Password_mismatch_and_weak_password_block_without_side_effects()
    {
        var svc = new FakeWizardService();
        var vm = await ToSecurity(svc);
        vm.PasswordConfirm = FakeWizardService.RandomSecret();
        vm.Password = vm.PasswordConfirm + "x"; // różne wartości (bez stałych haseł w kodzie — skaner NFR-11)
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S4Security, vm.State);
        Assert.DoesNotContain("init", svc.Calls); // różne hasła — usługa nie jest wołana
        vm.Password = vm.PasswordConfirm = "krotkie";
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S4Security, vm.State);
        Assert.Contains("KB-0301", Assert.Single(vm.Messages), StringComparison.Ordinal);
        Assert.False(vm.RepositoryCreated);
    }

    [Fact, Trait("AC", "AC-04.1"), Trait("Plan", "D4.11")]
    public async Task Sftp_unknown_host_key_must_be_approved_and_is_pinned_in_draft()
    {
        var svc = new FakeWizardService();
        svc.ProbeResults.Enqueue(new ProbeResult(ProbeOutcome.UnknownIdentity, HostKeyFingerprint: "SHA256:synthetic-fingerprint"));
        var vm = new WizardViewModel(svc) { NewSource = @"C:\Dane" };
        vm.AddSourceCommand.Execute(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.SelectedDestination = vm.Destinations.Single(d => d.Label == "Serwer SFTP");
        Assert.True(vm.IsSftp);
        vm.SftpHost = "backup.example.invalid";
        vm.SftpUser = "kopie";
        vm.DestinationPath = "/srv/kobian";
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S2Identity, vm.State);
        Assert.Equal("SHA256:synthetic-fingerprint", vm.PendingHostKey);
        Assert.Null(svc.Drafts[^1].ApprovedHostKey); // przed zatwierdzeniem odcisk nie jest przypięty
        await vm.NextCommand.ExecuteAsync(null); // „Odciski są zgodne — zatwierdź”
        Assert.Equal(WizardState.S3Schedule, vm.State);
        Assert.Equal("SHA256:synthetic-fingerprint", svc.Drafts[^1].ApprovedHostKey);
        Assert.Equal("sftp://kopie@backup.example.invalid:22/srv/kobian", vm.DestinationText);
    }

    [Fact, Trait("AC", "AC-01.3"), Trait("Plan", "D7.4")]
    public async Task Smb_destination_carries_unc_path_and_credential_reference_without_password()
    {
        var svc = new FakeWizardService();
        var vm = new WizardViewModel(svc) { NewSource = @"C:\Dane" };
        vm.AddSourceCommand.Execute(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.SelectedDestination = vm.Destinations.Single(d => d.Label == "Udział sieciowy (SMB)");
        Assert.True(vm.IsSmb);
        Assert.False(vm.IsSftp);
        vm.SftpUser = "nie-dla-smb"; // pole SFTP nie przecieka do celu SMB
        vm.SmbCredential = " nas-biuro ";
        vm.DestinationPath = @" \\nas\kopie\biuro ";
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S3Schedule, vm.State); // SMB nie ma kroku tożsamości serwera (D4.11 dotyczy SFTP)
        var d = svc.Drafts[^1];
        Assert.Equal((DestinationKind.Smb, @"\\nas\kopie\biuro", "nas-biuro", null, null), (d.Destination, d.DestinationPath, d.SmbSecret, d.SftpUser, d.ApprovedHostKey));
        Assert.Equal(@"\\nas\kopie\biuro", vm.DestinationText);
        var spec = Kobian.UI.Services.GrpcWizardService.Destination(d);
        Assert.Equal(("smb", @"\\nas\kopie\biuro", "file:nas-biuro", ""), (spec.Scheme, spec.Root, spec.SecretRef, spec.Host));
    }

    [Fact, Trait("AC", "AC-01.2")]
    public async Task Probe_error_returns_to_destination_with_kb_message()
    {
        var svc = new FakeWizardService();
        svc.ProbeResults.Enqueue(new ProbeResult(ProbeOutcome.PermanentError, "KB-2105"));
        var vm = new WizardViewModel(svc) { NewSource = @"C:\Dane" };
        vm.AddSourceCommand.Execute(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.DestinationPath = @"\\nas\kopie";
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S2Destination, vm.State);
        Assert.Contains("KB-2105", Assert.Single(vm.Messages), StringComparison.Ordinal);
    }

    [Theory, Trait("Plan", "D6.7")]
    [InlineData("Weekdays", "7:30", "30 7 * * 1-5")]
    [InlineData("Daily", "00:05", "5 0 * * *")]
    [InlineData("Hourly", "x", "0 * * * *")]
    [InlineData("Manual", "21:00", null)]
    public void Schedule_presets_produce_cron(string preset, string time, string? cron)
    {
        var vm = new WizardViewModel(new FakeWizardService());
        vm.SelectedSchedule = vm.Schedules.Single(s => s.AutomationId.EndsWith(preset, StringComparison.Ordinal));
        vm.ScheduleTime = time;
        Assert.Equal(cron, vm.Cron);
        Assert.Equal(preset is "Daily" or "Weekdays", vm.ScheduleNeedsTime);
    }

    [Fact, Trait("Plan", "D6.7")]
    public async Task Bad_time_is_rejected_by_service_with_kb_0201()
    {
        var svc = new FakeWizardService();
        var vm = new WizardViewModel(svc) { NewSource = @"C:\Dane" };
        vm.AddSourceCommand.Execute(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.DestinationPath = @"E:\K";
        await vm.NextCommand.ExecuteAsync(null);
        vm.ScheduleTime = "25:99";
        Assert.Contains("GG:MM", vm.SchedulePreview, StringComparison.Ordinal);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S3Schedule, vm.State);
        Assert.Contains("KB-0201", Assert.Single(vm.Messages), StringComparison.Ordinal);
    }

    [Fact, Trait("AC", "AC-01.2")]
    public async Task Overlapping_sources_block_step_one()
    {
        var svc = new FakeWizardService();
        var vm = new WizardViewModel(svc);
        foreach (var p in new[] { @"C:\Dane", @"C:\Dane\Faktury", @"c:\dane" })
        {
            vm.NewSource = p;
            vm.AddSourceCommand.Execute(null);
        }
        Assert.Equal(2, vm.Sources.Count); // duplikat (bez wielkości liter) nie jest dodawany
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S1Sources, vm.State);
        Assert.Contains("KB-0102", Assert.Single(vm.Messages), StringComparison.Ordinal);
        vm.RemoveSourceCommand.Execute(@"C:\Dane\Faktury");
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S2Destination, vm.State);
    }

    [Fact, Trait("Plan", "D6.7")]
    public async Task Cancel_after_repository_never_saves_job()
    {
        var svc = new FakeWizardService { IssueRecoveryKey = false };
        var vm = await ToSecurity(svc);
        vm.Password = vm.PasswordConfirm = FakeWizardService.RandomSecret();
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S5Retention, vm.State); // usługa bez klucza odzyskiwania — ekran klucza pomijany (Qb20)
        var finished = 0;
        vm.Finished += (_, _) => finished++;
        vm.CancelCommand.Execute(null);
        Assert.Equal(WizardState.Cancelled, vm.State);
        Assert.Equal(1, finished);
        Assert.DoesNotContain("save", svc.Calls); // reguła 2: przerwanie po S4 nie tworzy zadania
    }

    [Fact]
    public async Task First_backup_failure_and_refused_save_are_reported()
    {
        var svc = new FakeWizardService { IssueRecoveryKey = false, SaveCode = "KB-0103" };
        var vm = await ToSecurity(svc);
        vm.Password = vm.PasswordConfirm = FakeWizardService.RandomSecret();
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S6Summary, vm.State);
        Assert.Contains("KB-0103", Assert.Single(vm.Messages), StringComparison.Ordinal);
        svc.SaveCode = null;
        svc.FirstBackupCode = "KB-2201";
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.Done, vm.State);
        Assert.Contains("KB-2201", vm.FinalText, StringComparison.Ordinal);
        Assert.False(vm.CanCancel);
    }

    [Fact]
    public async Task Without_run_now_job_is_only_saved()
    {
        var svc = new FakeWizardService { IssueRecoveryKey = false };
        var vm = await ToSecurity(svc);
        vm.Password = vm.PasswordConfirm = FakeWizardService.RandomSecret();
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.RunNow = false;
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.Done, vm.State);
        Assert.DoesNotContain(svc.Calls, c => c.StartsWith("run:", StringComparison.Ordinal));
        Assert.Contains("zgodnie z harmonogramem", vm.FinalText, StringComparison.Ordinal);
    }

    [Fact]
    public void Step_rail_marks_current_and_done_steps()
    {
        var vm = new WizardViewModel(new FakeWizardService());
        vm.Apply(WizardEvent.Next);
        vm.Apply(WizardEvent.Next);
        vm.Apply(WizardEvent.ProbeOk);
        var steps = vm.Steps;
        Assert.Equal(6, steps.Count);
        Assert.Equal([true, true, false, false, false, false], steps.Select(s => s.IsDone));
        Assert.Equal(3, steps.Single(s => s.IsCurrent).Number);
        Assert.Equal("Krok 3 z 6", vm.StepCaption);
    }

    [Fact, Trait("Plan", "R2.6.1")]
    public async Task Shell_opens_fresh_wizard_and_returns_to_screen_after_finish()
    {
        var clock = new FixedTime(FakeDashboardSource.Now);
        var jobsSrc = new FakeJobsSource(FakeJobsSource.Mixed());
        var shell = new ShellViewModel(new DashboardViewModel(new FakeDashboardSource(FakeDashboardSource.Mixed()), clock, TimeZoneInfo.Utc),
            new JobsViewModel(jobsSrc, clock, TimeZoneInfo.Utc), wizardFactory: () => new WizardViewModel(new FakeWizardService()));
        shell.Navigate("SCR-02");
        await shell.Jobs!.RefreshAsync(_ct);
        shell.Jobs.NewJobCommand.Execute(null);
        var first = Assert.IsType<WizardViewModel>(shell.CurrentPage);
        first.CancelCommand.Execute(null);
        Assert.Same(shell.Jobs, shell.CurrentPage);
        shell.OpenWizard();
        Assert.NotSame(first, shell.CurrentPage); // każde otwarcie = nowy szkic (D6.7)
        Assert.Equal("SCR-02", shell.Selected.ScreenId);
    }

    [Fact, Trait("AC", "AC-28.1")]
    public void Recovery_key_comparison_ignores_case_and_spaces_only()
    {
        Assert.True(RecoveryKeyText.Matches("AB3CD", " ab 3cd "));
        Assert.False(RecoveryKeyText.Matches("AB3CD", "AB3CE"));
        Assert.False(RecoveryKeyText.Matches("AB3CD", null));
        Assert.Equal(["AB3CD", "EF4GH"], RecoveryKeyText.Groups("AB3CD-EF4GH"));
    }
}
