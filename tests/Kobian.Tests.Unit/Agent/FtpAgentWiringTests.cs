using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Providers.Storage.Secrets;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Ftp;
using Kobian.Tests.Unit.Storage;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-34: cel FTPS w Agencie — poświadczenia z katalogu sekretów przez SecretRef (AC-01.3, D4.1, PROWIZORYCZNIE Qb16), rejestr dostawców (ftps/ftp,
/// z dławieniem otwarć D4.16), definicja zadania v1 (pin SPKI w „hostKey”, port 990 = Implicit) i ProbeDestination z odciskiem SPKI do zatwierdzenia.
/// </summary>
public sealed class FtpAgentWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-ftpsec-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public FtpAgentWiringTests()
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Put(string file, string content)
    {
        var p = Path.Combine(_dir, file);
        File.WriteAllText(p, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact, Trait("SEC", "D4.1"), Trait("AC", "AC-01.3")]
    public async Task Ftp_credentials_resolve_per_role_and_entries_without_password_are_refused()
    {
        var a = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        var b = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        Put("nas.json", $$$"""{"backup":{"user":"kobian-bk","password":"{{{a}}}"},"maintenance":{"user":"kobian-mt","password":"{{{b}}}"}}""");
        var p = new FileFtpCredentialProvider(_dir);
        var bk = await p.ResolveAsync("file:nas", CredentialRole.Backup, _ct);
        var mt = await p.ResolveAsync("file:nas", CredentialRole.Maintenance, _ct);
        Assert.Equal(("kobian-bk", a), (bk.UserName, bk.Password));
        Assert.Equal(("kobian-mt", b), (mt.UserName, mt.Password));
        Assert.DoesNotContain(a, bk.ToString()!, StringComparison.Ordinal);

        Put("only.json", $$$"""{"backup":{"user":"u","password":"{{{a}}}"}}""");
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await p.ResolveAsync("file:only", CredentialRole.Maintenance, _ct));
        Assert.Equal(KbCode.AuthRejected, ex.Code); // brak cichego przejścia na drugą rolę
        Put("klucz.json", """{"backup":{"user":"u","privateKeyFile":"id_test"}}""");
        Put("id_test", "-----BEGIN TEST-----");
        ex = await Assert.ThrowsAsync<StorageException>(async () => await p.ResolveAsync("file:klucz", CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.AuthRejected, ex.Code);
        ex = await Assert.ThrowsAsync<StorageException>(async () => await p.ResolveAsync("file:../x", CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.AuthRejected, ex.Code);
        Assert.DoesNotContain(a, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_has_ftps_and_flagged_ftp()
    {
        var p = AgentProviders.Create(_dir);
        Assert.Equal("ftps", p["ftps"].Scheme);
        Assert.Equal("ftp", p["ftp"].Scheme);
    }

    [Fact, Trait("SEC", "D4.3")]
    public async Task Registry_refuses_plain_ftp_without_flag()
    {
        var p = AgentProviders.Create(_dir);
        var ex = await Assert.ThrowsAsync<StorageException>(async () =>
            await p["ftp"].OpenAsync(new DestinationConfig("d", "ftp", "/kopie", "file:nas", new Dictionary<string, string> { ["host"] = "127.0.0.1" }), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.InsecureTransportNotConfirmed, ex.Code);
    }

    private static JobDefinition Parse(string destination)
    {
        var src = OperatingSystem.IsWindows() ? @"C:\\Dane" : "/dane";
        var key = OperatingSystem.IsWindows() ? @"C:\\k.key" : "/k.key";
        var json = "{\"version\":1,\"sources\":[\"" + src + "\"],\"destination\":" + destination + ",\"key\":{\"file\":\"" + key + "\"}}";
        return JobDefinition.Parse("files", Encoding.UTF8.GetBytes(json));
    }

    [Fact, Trait("D", "D4.11"), Trait("D", "D4.3")]
    public void Ftps_definition_maps_host_port_spki_and_implicit_on_990()
    {
        var pin = "SHA256:" + new string('Q', 43);
        var def = Parse($$"""{"scheme":"ftps","root":"/kopie","host":"nas.example.test","port":990,"hostKey":"{{pin}}","secretRef":"file:f1"}""");
        var cfg = def.ToDestination("d1");
        Assert.Equal(("ftps", "/kopie", "file:f1"), (cfg.Scheme, cfg.Root, cfg.SecretRef));
        Assert.Equal("nas.example.test", cfg.Options!["host"]);
        Assert.Equal("990", cfg.Options["port"]);
        Assert.Equal("implicit", cfg.Options["tls"]);
        Assert.Equal(pin, cfg.Options["spki"]);
        var explicitDef = Parse("""{"scheme":"ftps","root":"/kopie","host":"nas.example.test","secretRef":"file:f1"}""");
        var e = explicitDef.ToDestination("d2");
        Assert.False(e.Options!.ContainsKey("tls"));
        Assert.False(e.Options.ContainsKey("spki"));
        Assert.NotEqual(def.DestinationId, explicitDef.DestinationId); // inny port = inny cel
        var ex = Assert.Throws<KobianException>(() => Parse("""{"scheme":"ftp","root":"/kopie","host":"nas.example.test","secretRef":"file:f1"}"""));
        Assert.Equal(KbCode.JobDefinitionInvalid, ex.Code);
    }

    [Fact, Trait("SEC", "D4.11"), Trait("BL", "BL-60")]
    public async Task Probe_of_self_signed_ftps_returns_spki_to_approve_then_pinned_probe_succeeds()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        Put("nas.json", $$$"""{"backup":{"user":"kopie","password":"{{{secret}}}"}}""");
        using var tmp = FluentFtpAdapterTests.SelfSigned("nas.example");
        using var cert = X509CertificateLoader.LoadPkcs12(tmp.Export(X509ContentType.Pkcs12), null);
        await using var srv = new LoopbackFtpsControlServer(cert, user: "kopie", secret: secret);
        var probe = new DestinationProbeService(AgentProviders.Create(_dir));
        var req = new DestinationRequest("ftps", "/kopie", "127.0.0.1", srv.Port, "file:nas");
        Assert.Null(probe.ValidateShape(req));
        var r = await probe.ProbeAsync(req, _ct);
        Assert.Equal(DestinationProbeStatus.UnknownIdentity, r.Status);
        Assert.Equal(KbCode.TlsIdentityMismatch, r.Code);
        Assert.Equal(TlsSpkiPin.Fingerprint(cert), r.Identity!.Fingerprint);
        Assert.False(srv.PassReceived); // poświadczenie nie poszło do niezatwierdzonego serwera

        var ok = await probe.ProbeAsync(req with { HostKey = r.Identity.Fingerprint }, _ct);
        Assert.Equal(DestinationProbeStatus.Ok, ok.Status);
        Assert.False(ok.RepositoryExists);
        Assert.Equal("tls-spki", ok.Identity!.Kind);

        var bad = await probe.ProbeAsync(req with { HostKey = "SHA256:" + new string('A', 43) }, _ct);
        Assert.Equal(DestinationProbeStatus.Failed, bad.Status); // przypięty, a inny klucz = alarm, nie pytanie
        Assert.Equal(KbCode.TlsIdentityMismatch, bad.Code);
    }
}
