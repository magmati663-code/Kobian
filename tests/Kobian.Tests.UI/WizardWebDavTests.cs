using System.Globalization;
using Kobian.UI.Destinations;
using Kobian.UI.Services;
using Kobian.UI.Wizard;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>SCR-03 S2 cel WebDAV (HTTPS): wspólne pola serwera TLS z FTPS, port 443, zatwierdzenie odcisku SPKI, schemat „webdav” w szkicu i karta SCR-09.</summary>
public sealed class WizardWebDavTests
{
    private const string Spki = "SHA256:q1w2E3r4T5y6U7i8O9p0AaSsDdFfGgHhJjKkLlZzXxC"; // syntetyczny

    public WizardWebDavTests() => CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");

    private static async Task<WizardViewModel> ToWebDav(FakeWizardService svc)
    {
        var vm = new WizardViewModel(svc) { NewSource = @"C:\Dane" };
        vm.AddSourceCommand.Execute(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.SelectedDestination = vm.Destinations.Single(d => d.Value == DestinationKind.WebDav);
        vm.FtpsHost = " cloud.biuro.example.invalid ";
        vm.FtpsCredential = " dav-kopie ";
        vm.DestinationPath = " /remote.php/dav/files/kopie/biuro ";
        return vm;
    }

    [Fact, Trait("Plan", "D4.11")]
    public async Task Untrusted_certificate_requires_spki_approval_and_draft_uses_https_port()
    {
        var svc = new FakeWizardService();
        svc.ProbeResults.Enqueue(new ProbeResult(ProbeOutcome.UnknownIdentity, "KB-2109", Spki));
        var vm = await ToWebDav(svc);
        Assert.True(vm.IsWebDav && vm.IsTlsServer);
        Assert.False(vm.IsFtps);
        Assert.Equal(443, vm.FtpsPort);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S2Identity, vm.State);
        Assert.True(vm.IsFtpsIdentity);
        Assert.Equal("Serwer: cloud.biuro.example.invalid:443 · HTTPS", vm.FtpsServerText);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S3Schedule, vm.State);
        var d = svc.Drafts[^1];
        Assert.Equal((DestinationKind.WebDav, "cloud.biuro.example.invalid", 443, "dav-kopie", Spki, "/remote.php/dav/files/kopie/biuro"),
            (d.Destination, d.FtpsHost, d.FtpsPort, d.FtpsSecret, d.ApprovedHostKey, d.DestinationPath));
        var spec = GrpcWizardService.Destination(d);
        Assert.Equal(("webdav", "cloud.biuro.example.invalid", 443, "file:dav-kopie", Spki), (spec.Scheme, spec.Host, spec.Port, spec.SecretRef, spec.HostKey));
        Assert.Contains("https://cloud.biuro.example.invalid:443/remote.php/dav/files/kopie/biuro", vm.DestinationText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Url_in_host_field_is_refused_next_to_the_field()
    {
        var svc = new FakeWizardService();
        var vm = await ToWebDav(svc);
        vm.FtpsHost = "https://cloud.example";
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S2Destination, vm.State);
        Assert.True(vm.HasFtpsHostError);
        Assert.DoesNotContain(svc.Drafts, x => x.Destination == DestinationKind.WebDav);
    }

    [Fact]
    public async Task Default_port_follows_destination_kind_unless_custom()
    {
        var vm = await ToWebDav(new FakeWizardService());
        vm.SelectedDestination = vm.Destinations.Single(d => d.Value == DestinationKind.Ftps);
        Assert.Equal(21, vm.FtpsPort);
        vm.SelectedDestination = vm.Destinations.Single(d => d.Value == DestinationKind.WebDav);
        Assert.Equal(443, vm.FtpsPort);
        vm.FtpsPort = 8443;
        vm.SelectedDestination = vm.Destinations.Single(d => d.Value == DestinationKind.Ftps);
        Assert.Equal(8443, vm.FtpsPort);
    }

    [Fact]
    public void Destination_card_shows_https_and_pin()
    {
        var d = new DestinationItem("dest-1", "webdav", "/remote.php/dav/files/kopie", ["biuro"], "cloud.example", null, Spki);
        var text = DestinationsViewModel.Details(d);
        Assert.StartsWith("cloud.example:443 · HTTPS · ", text, StringComparison.Ordinal);
    }
}
