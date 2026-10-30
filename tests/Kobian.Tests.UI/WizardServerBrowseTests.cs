using System.Globalization;
using Kobian.UI.Wizard;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>„Przeglądaj…” dla folderu na serwerze: zamiast wpisywać ścieżkę, użytkownik wchodzi w foldery i wybiera jeden (cel w S2, serwer-źródło w S1).</summary>
public sealed class WizardServerBrowseTests
{
    private const string Fp = "SHA256:q1w2E3r4T5y6U7i8O9p0AaSsDdFfGgHhJjKkLlZzXxC"; // syntetyczny

    public WizardServerBrowseTests() => CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");

    private static FakeWizardService Server()
    {
        var svc = new FakeWizardService();
        svc.ServerFolders["/"] = ["home", "srv"];
        svc.ServerFolders["/srv"] = ["kopie", "www"];
        svc.ServerFolders["/srv/kopie"] = [];
        return svc;
    }

    [Fact]
    public async Task Sftp_destination_first_connection_asks_for_fingerprint_then_user_walks_folders_and_chooses_one()
    {
        var svc = Server();
        svc.RequiredHostKey = Fp;
        svc.ServerProbeResults.Enqueue(new ProbeResult(ProbeOutcome.UnknownIdentity, "KB-2104", Fp));
        var vm = await WizardWebDavTestsHelper.ToS2(svc, DestinationKind.Sftp);
        Assert.True(vm.CanBrowseServerDestination);
        vm.SftpHost = "nas.biuro";
        vm.SftpUser = "kopie";
        vm.ServerPassword = "haslo do nas 2026";

        await vm.BrowseServerDestinationCommand.ExecuteAsync(null);
        Assert.True(vm.IsBrowsingServer);
        Assert.Equal(Fp, vm.BrowsePendingFingerprint); // nieznany serwer — najpierw odcisk
        Assert.Empty(vm.BrowseFolders);
        Assert.Equal("nas.biuro-kopie", Assert.Single(svc.SavedCredentials).Key); // hasło zapisane w usłudze przed połączeniem

        await vm.BrowseApproveCommand.ExecuteAsync(null);
        Assert.Equal(["home", "srv"], vm.BrowseFolders);
        await vm.BrowseOpenCommand.ExecuteAsync("srv");
        Assert.Equal("/srv", vm.BrowsePath);
        await vm.BrowseOpenCommand.ExecuteAsync("kopie");
        Assert.Equal("/srv/kopie", vm.BrowsePath);
        Assert.True(vm.HasBrowseMessage); // pusty folder — podpowiedź
        await vm.BrowseUpCommand.ExecuteAsync(null);
        Assert.Equal(["kopie", "www"], vm.BrowseFolders);
        await vm.BrowseOpenCommand.ExecuteAsync("kopie");
        vm.BrowseChooseCommand.Execute(null);
        Assert.False(vm.IsBrowsingServer);
        Assert.Equal("/srv/kopie", vm.DestinationPath);

        // „Dalej”: zatwierdzony w przeglądaniu odcisk idzie w szkicu — bez drugiego pytania
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S3Schedule, vm.State);
        var d = svc.Drafts[^1];
        Assert.Equal((Fp, "/srv/kopie", "nas.biuro-kopie"), (d.ApprovedHostKey, d.DestinationPath, d.SftpUser));
    }

    [Fact]
    public async Task Server_source_is_browsed_and_added_without_asking_for_the_fingerprint_twice()
    {
        var svc = Server();
        svc.RequiredHostKey = Fp;
        svc.ServerProbeResults.Enqueue(new ProbeResult(ProbeOutcome.UnknownIdentity, "KB-2109", Fp));
        var vm = new WizardViewModel(svc);
        vm.ShowAddServerCommand.Execute(null);
        vm.SrcKind = vm.SourceServerKinds.Single(k => k.Value == "ftps");
        vm.SrcHost = "ftp.biuro";
        vm.SrcLogin = "jan";
        vm.SrcPassword = "haslo ftp 2026";
        await vm.BrowseServerSourceCommand.ExecuteAsync(null);
        await vm.BrowseApproveCommand.ExecuteAsync(null);
        await vm.BrowseOpenCommand.ExecuteAsync("home");
        Assert.True(vm.HasBrowseMessage); // „/home” nie istnieje w atrapie → kod błędu, lista pusta
        Assert.Empty(vm.BrowseFolders);
        vm.BrowsePath = "/srv/www";
        svc.ServerFolders["/srv/www"] = ["strona"];
        await vm.BrowseGoCommand.ExecuteAsync(null);
        Assert.Equal(["strona"], vm.BrowseFolders);
        vm.BrowseChooseCommand.Execute(null);
        Assert.Equal("/srv/www", vm.SrcPath);

        await vm.AddSourceServerCommand.ExecuteAsync(null);
        var server = Assert.Single(vm.SourceServers);
        Assert.Equal(("ftps", "/srv/www", Fp), (server.Scheme, server.Root, server.HostKey));
        Assert.Equal(Fp, svc.ProbedServers[^1].HostKey);
    }

    [Fact]
    public async Task Trusted_server_lists_folders_at_once_and_cancel_leaves_the_path_untouched()
    {
        var svc = Server();
        var vm = await WizardWebDavTestsHelper.ToS2(svc, DestinationKind.WebDav);
        vm.FtpsHost = "cloud.firma.pl";
        vm.FtpsCredential = "jan";
        vm.ServerPassword = "x";
        vm.DestinationPath = "/srv";
        await vm.BrowseServerDestinationCommand.ExecuteAsync(null);
        Assert.Null(vm.BrowsePendingFingerprint);
        Assert.Equal("/srv", vm.BrowsePath); // start od wpisanej ścieżki
        Assert.Equal(["kopie", "www"], vm.BrowseFolders);
        Assert.Equal(("webdav", 443), (svc.BrowsedServers[^1].Scheme, svc.BrowsedServers[^1].Port));
        await vm.BrowseOpenCommand.ExecuteAsync("www");
        vm.BrowseCancelCommand.Execute(null);
        Assert.False(vm.IsBrowsingServer);
        Assert.Equal("/srv", vm.DestinationPath);
    }

    [Fact]
    public async Task Missing_login_is_reported_and_nothing_is_sent()
    {
        var svc = Server();
        var vm = await WizardWebDavTestsHelper.ToS2(svc, DestinationKind.Sftp);
        vm.SftpHost = "nas";
        await vm.BrowseServerDestinationCommand.ExecuteAsync(null);
        Assert.False(vm.IsBrowsingServer);
        Assert.DoesNotContain(svc.Calls, c => c.StartsWith("browse:", StringComparison.Ordinal));
        Assert.False(new WizardViewModel(svc).CanBrowseServerDestination); // dysk lokalny — okno systemowe, nie serwer
    }

    [Theory]
    [InlineData("/a/b", "/a")]
    [InlineData("/a", "/")]
    [InlineData("/", "/")]
    [InlineData("/a/b/", "/a")]
    public void Parent_path(string path, string parent) => Assert.Equal(parent, WizardViewModel.ParentPath(path));
}
