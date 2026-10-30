using System.Security.Cryptography.X509Certificates;
using System.Text;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Ftp;
using Kobian.Providers.Storage.Secrets;
using Kobian.Tests.Unit.Storage;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>Cel WebDAV w Agencie: rejestr dostawców, definicja zadania v1 (HTTPS, pin SPKI w „hostKey”), tożsamość serwera i ProbeDestination z odciskiem do zatwierdzenia.</summary>
public sealed class WebDavAgentWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-davsec-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public WebDavAgentWiringTests()
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void PutSecret(string name, string user, string password)
    {
        var p = Path.Combine(_dir, name + ".json");
        File.WriteAllText(p, $$$"""{"backup":{"user":"{{{user}}}","password":"{{{password}}}"}}""");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static JobDefinition Parse(string destination)
    {
        var src = OperatingSystem.IsWindows() ? @"C:\\Dane" : "/dane";
        var key = OperatingSystem.IsWindows() ? @"C:\\k.key" : "/k.key";
        var json = "{\"version\":1,\"sources\":[\"" + src + "\"],\"destination\":" + destination + ",\"key\":{\"file\":\"" + key + "\"}}";
        return JobDefinition.Parse("files", Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public void Registry_has_webdav_and_standalone_restore_supports_it()
    {
        Assert.Equal("webdav", AgentProviders.Create(_dir)["webdav"].Scheme);
        Assert.Contains("webdav", StandaloneRemoteProviders.Schemes);
        Assert.Equal("webdav", StandaloneRemoteProviders.Create(_dir, "webdav")!.Scheme);
    }

    [Fact]
    public void Definition_maps_host_port_and_spki_and_never_insecure()
    {
        var pin = "SHA256:" + new string('Q', 43);
        var def = Parse($$"""{"scheme":"webdav","root":"/remote.php/dav/files/kopie/kobian","host":"cloud.example.test","port":8443,"hostKey":"{{pin}}","secretRef":"file:dav1"}""");
        var cfg = def.ToDestination("d1");
        Assert.Equal(("webdav", "/remote.php/dav/files/kopie/kobian", "file:dav1"), (cfg.Scheme, cfg.Root, cfg.SecretRef));
        Assert.Equal("cloud.example.test", cfg.Options!["host"]);
        Assert.Equal("8443", cfg.Options["port"]);
        Assert.Equal(pin, cfg.Options["spki"]);
        Assert.False(cfg.Options.ContainsKey("insecure"));
        var other = Parse("""{"scheme":"webdav","root":"/remote.php/dav/files/kopie/kobian","host":"cloud.example.test","secretRef":"file:dav1"}""");
        Assert.NotEqual(def.DestinationId, other.DestinationId); // inny port = inny cel
        Assert.Equal("webdav://cloud.example.test:443", ServerIdentityStore.Endpoint("webdav", "cloud.example.test", null));
        Assert.Equal("tls-spki", ServerIdentityStore.KindOf("webdav"));
    }

    [Theory]
    [InlineData("/kopie", "cloud.example", null, "file:dav1", null, null)]
    [InlineData("kopie", "cloud.example", null, "file:dav1", null, "KB-2106")]
    [InlineData("/kopie/../etc", "cloud.example", null, "file:dav1", null, "KB-2106")]
    [InlineData("/kopie", "https://cloud.example", null, "file:dav1", null, "KB-2106")]
    [InlineData("/kopie", "cloud.example", 70000, "file:dav1", null, "KB-2106")]
    [InlineData("/kopie", "cloud.example", null, null, null, "KB-2102")]
    [InlineData("/kopie", "cloud.example", null, "file:dav1", "md5:x", "KB-2109")]
    public void Validation_follows_ftps_rules(string root, string host, int? port, string? secretRef, string? pin, string? expected) =>
        Assert.Equal(expected, JobDestination.Validate(new DestinationRequest("webdav", root, host, port, secretRef, pin)));

    [Fact, Trait("SEC", "D4.11")]
    public async Task Probe_of_self_signed_webdav_returns_spki_to_approve_then_pinned_probe_succeeds()
    {
        using var tmp = FluentFtpAdapterTests.SelfSigned("nas.example");
        using var cert = X509CertificateLoader.LoadPkcs12(tmp.Export(X509ContentType.Pkcs12), null);
        await using var srv = new LoopbackWebDavServer(cert);
        srv.AddCollection("/kopie");
        PutSecret("dav1", srv.User, srv.Secret);
        var probe = new DestinationProbeService(AgentProviders.Create(_dir));
        var req = new DestinationRequest("webdav", "/kopie", "127.0.0.1", srv.Port, "file:dav1");
        Assert.Null(probe.ValidateShape(req));
        var r = await probe.ProbeAsync(req, _ct);
        Assert.Equal(DestinationProbeStatus.UnknownIdentity, r.Status);
        Assert.Equal(KbCode.TlsIdentityMismatch, r.Code);
        Assert.Equal(TlsSpkiPin.Fingerprint(cert), r.Identity!.Fingerprint);
        Assert.False(srv.AuthorizationSeen); // hasło nie poszło do niezatwierdzonego serwera

        var ok = await probe.ProbeAsync(req with { HostKey = r.Identity.Fingerprint }, _ct);
        Assert.Equal(DestinationProbeStatus.Ok, ok.Status);
        Assert.False(ok.RepositoryExists);
        Assert.Equal("tls-spki", ok.Identity!.Kind);

        var bad = await probe.ProbeAsync(req with { HostKey = "SHA256:" + new string('A', 43) }, _ct);
        Assert.Equal(DestinationProbeStatus.Failed, bad.Status);
        Assert.Equal(KbCode.TlsIdentityMismatch, bad.Code);
    }

    [Fact, Trait("SEC", "D4.11")]
    public async Task Browse_lists_subfolders_only_and_rejects_plain_ftp_and_local()
    {
        using var tmp = FluentFtpAdapterTests.SelfSigned("nas.example");
        using var cert = X509CertificateLoader.LoadPkcs12(tmp.Export(X509ContentType.Pkcs12), null);
        await using var srv = new LoopbackWebDavServer(cert);
        srv.AddCollection("/dane");
        srv.AddCollection("/dane/biuro");
        srv.AddCollection("/dane/archiwum");
        srv.AddFile("/dane/plik.txt", "x"u8.ToArray());
        PutSecret("dav1", srv.User, srv.Secret);
        var probe = new DestinationProbeService(AgentProviders.Create(_dir));
        var req = new DestinationRequest("webdav", "/dane", "127.0.0.1", srv.Port, "file:dav1");

        var untrusted = await probe.BrowseAsync(req, _ct); // certyfikat bez CA i bez pinu — bez listy i bez hasła
        Assert.Equal(KbCode.TlsIdentityMismatch, untrusted.Code);
        Assert.False(srv.AuthorizationSeen);

        var ok = await probe.BrowseAsync(req with { HostKey = TlsSpkiPin.Fingerprint(cert) }, _ct);
        Assert.Null(ok.Code);
        Assert.Equal(["archiwum", "biuro"], ok.Folders);
        Assert.False(ok.Truncated);

        Assert.Equal(KbCode.JobDefinitionInvalid, (await probe.BrowseAsync(new DestinationRequest("local", _dir), _ct)).Code);
        Assert.Equal(KbCode.JobDefinitionInvalid, (await probe.BrowseAsync(req with { Scheme = "ftp" }, _ct)).Code);
        Assert.Equal(KbCode.HostKeyMismatch, (await probe.BrowseAsync(new DestinationRequest("sftp", "/", "nas.example", 22, "file:dav1"), _ct)).Code);
    }
}
