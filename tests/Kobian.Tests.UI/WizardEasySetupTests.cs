using System.Globalization;
using Kobian.UI.Services;
using Kobian.UI.Wizard;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>
/// Konfiguracja bez „magazynu sekretów” od strony użytkownika: login i hasło do serwera w kreatorze (zapisuje usługa), serwer jako źródło dodawany panelem
/// z kontrolą połączenia i zatwierdzeniem odcisku, kilka źródeł (foldery + serwery) w jednym zadaniu.
/// </summary>
public sealed class WizardEasySetupTests
{
    private const string Fp = "SHA256:q1w2E3r4T5y6U7i8O9p0AaSsDdFfGgHhJjKkLlZzXxC"; // syntetyczny

    public WizardEasySetupTests() => CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");

    [Fact]
    public async Task Destination_login_and_password_are_saved_by_the_service_and_the_job_gets_only_the_entry_name()
    {
        var svc = new FakeWizardService();
        var vm = await WizardWebDavTestsHelper.ToS2(svc, DestinationKind.WebDav);
        vm.FtpsHost = "cloud.firma.pl";
        vm.FtpsCredential = "jan.kowalski@firma.pl"; // login z „@” — dozwolony, gdy podano hasło
        vm.DestinationPath = "/remote.php/dav/files/jan/kopie";
        vm.ServerPassword = "tajne haslo 2026";
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S3Schedule, vm.State);
        var entry = Assert.Single(svc.SavedCredentials);
        Assert.Equal(("cloud.firma.pl-jan.kowalski-firma.pl", "jan.kowalski@firma.pl", 16), (entry.Key, entry.Value.Login, entry.Value.PasswordLength));
        Assert.Equal("cloud.firma.pl-jan.kowalski-firma.pl", svc.Drafts[^1].FtpsSecret);
        Assert.Equal("", vm.ServerPassword); // hasło nie zostaje w modelu widoku
        Assert.Equal("file:cloud.firma.pl-jan.kowalski-firma.pl", GrpcWizardService.Destination(svc.Drafts[^1]).SecretRef);
    }

    [Fact]
    public async Task Without_password_the_login_field_is_an_existing_entry_name_as_before()
    {
        var svc = new FakeWizardService();
        var vm = await WizardWebDavTestsHelper.ToS2(svc, DestinationKind.Sftp);
        vm.SftpHost = "nas";
        vm.SftpUser = "nas-kopie";
        vm.DestinationPath = "/kopie";
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Empty(svc.SavedCredentials);
        Assert.Equal("nas-kopie", svc.Drafts[^1].SftpUser);
    }

    [Fact]
    public async Task Server_source_is_checked_approved_and_added_next_to_local_folders()
    {
        var svc = new FakeWizardService();
        svc.ServerProbeResults.Enqueue(new ProbeResult(ProbeOutcome.UnknownIdentity, "KB-2104", Fp));
        var vm = new WizardViewModel(svc);
        vm.AddPickedSource(@"C:\Dane");
        vm.ShowAddServerCommand.Execute(null);
        vm.SrcKind = vm.SourceServerKinds.Single(k => k.Value == "sftp");
        vm.SrcHost = "nas.biuro";
        vm.SrcPath = "/dane/ksiegowosc";
        vm.SrcLogin = "kopie";
        vm.SrcPassword = "haslo do nas 2026";
        await vm.AddSourceServerCommand.ExecuteAsync(null);
        Assert.Equal(Fp, vm.SrcPendingFingerprint); // pierwsze połączenie — pytanie o odcisk, nic nie dodano
        Assert.Empty(vm.SourceServers);
        await vm.ApproveSourceServerCommand.ExecuteAsync(null);
        var server = Assert.Single(vm.SourceServers);
        Assert.Equal(("sftp", "nas.biuro", 22, "/dane/ksiegowosc", "nas.biuro-kopie", Fp), (server.Scheme, server.Host, server.Port, server.Root, server.SecretName, server.HostKey));
        Assert.Equal(Fp, svc.ProbedServers[^1].HostKey);
        Assert.False(vm.IsAddingServer);
        Assert.Equal("", vm.SrcPassword);

        await vm.NextCommand.ExecuteAsync(null); // S1 → S2: szkic zawiera folder i serwer
        var d = svc.Drafts[^1];
        Assert.Equal([@"C:\Dane"], d.Sources);
        Assert.Equal(server, Assert.Single(d.SourceServers!));
        var spec = GrpcWizardService.Spec(server);
        Assert.Equal(("sftp", "/dane/ksiegowosc", "file:nas.biuro-kopie", Fp), (spec.Scheme, spec.Root, spec.SecretRef, spec.HostKey));
    }

    [Fact]
    public async Task Server_only_job_is_allowed_and_bad_fields_are_reported_in_the_panel()
    {
        var svc = new FakeWizardService();
        var vm = new WizardViewModel(svc);
        vm.ShowAddServerCommand.Execute(null);
        vm.SrcKind = vm.SourceServerKinds.Single(k => k.Value == "webdav");
        Assert.Equal(443, vm.SrcPort);
        vm.SrcHost = "https://cloud";
        await vm.AddSourceServerCommand.ExecuteAsync(null);
        Assert.True(vm.HasSrcMessage);
        Assert.Empty(svc.ProbedServers);
        vm.SrcHost = "cloud.example";
        vm.SrcPath = "/dav/zdjecia";
        vm.SrcLogin = "jan";
        vm.SrcPassword = "x";
        await vm.AddSourceServerCommand.ExecuteAsync(null);
        Assert.Single(vm.SourceServers);
        Assert.True(vm.HasSources);
        Assert.Equal("zdjecia", vm.JobName);
    }
}

internal static class WizardWebDavTestsHelper
{
    /// <summary>S1 (jeden folder) → S2 z wybranym rodzajem celu.</summary>
    public static async Task<WizardViewModel> ToS2(FakeWizardService svc, DestinationKind kind)
    {
        var vm = new WizardViewModel(svc) { NewSource = @"C:\Dane" };
        vm.AddSourceCommand.Execute(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.SelectedDestination = vm.Destinations.Single(d => d.Value == kind);
        return vm;
    }
}
