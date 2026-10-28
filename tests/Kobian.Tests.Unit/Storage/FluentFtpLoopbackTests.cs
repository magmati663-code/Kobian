using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Ftp;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>
/// BL-34: prawdziwy adapter FluentFTP (AsyncFtpClient) wobec skryptowego serwera sterowania na 127.0.0.1 — tryby TLS (D4.3), pinning SPKI (D4.11),
/// brak poświadczeń przed TLS (SEC-4.6/STRIDE I). Kanał danych (PASV + TLS reuse) — kolejka SP-04 (prawdziwe serwery).
/// </summary>
public sealed class FluentFtpLoopbackTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private static X509Certificate2 ServerCert()
    {
        using var c = FluentFtpAdapterTests.SelfSigned("nas.example");
        // SslStream po stronie serwera wymaga certyfikatu z kluczem w postaci przenośnej (PFX) na każdej platformie.
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pkcs12), null);
    }

    private static DestinationConfig Cfg(string scheme, int port, params (string K, string V)[] extra)
    {
        var o = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = port.ToString(CultureInfo.InvariantCulture),
            ["connect_timeout_s"] = "5",
        };
        foreach (var (k, v) in extra) o[k] = v;
        return new DestinationConfig("d", scheme, "/kopie", "file:ftp1", o);
    }

    private FluentFtpConnector Connector(FluentFtpAdapterTests.CountingCredentials? creds = null) =>
        new(creds ?? new FluentFtpAdapterTests.CountingCredentials { Secret = _secret });

    [Fact, Trait("SEC", "D4.11"), Trait("SEC", "SEC-4.6")]
    public async Task Self_signed_without_pin_is_refused_before_credentials_with_spki_to_approve()
    {
        using var cert = ServerCert();
        await using var srv = new LoopbackFtpsControlServer(cert, user: "kopie", secret: _secret);
        var ex = await Assert.ThrowsAsync<FtpProtocolException>(async () => await Connector().ConnectAsync(Cfg("ftps", srv.Port), CredentialRole.Backup, _ct));
        Assert.Equal(FtpFailure.TlsIdentityRejected, ex.Failure);
        Assert.Equal("tls-spki", ex.PresentedIdentity!.Kind);
        Assert.Equal(TlsSpkiPin.Fingerprint(cert), ex.PresentedIdentity.Fingerprint);
        Assert.False(srv.PassReceived);
        Assert.DoesNotContain(srv.Commands, c => c.StartsWith("USER", StringComparison.Ordinal));
    }

    [Fact, Trait("SEC", "D4.11"), Trait("AC", "AC-12.2")]
    public async Task Wrong_pin_is_refused_and_mapped_to_kb_2109()
    {
        using var cert = ServerCert();
        using var other = FluentFtpAdapterTests.SelfSigned("nas.example");
        await using var srv = new LoopbackFtpsControlServer(cert, user: "kopie", secret: _secret);
        var factory = new FtpStorageProviderFactory(Connector());
        var ex = await Assert.ThrowsAsync<StorageException>(async () =>
            await factory.OpenAsync(Cfg("ftps", srv.Port, ("spki", TlsSpkiPin.Fingerprint(other))), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.TlsIdentityMismatch, ex.Code);
        Assert.Equal(ErrorClass.Security, ex.ErrorClass);
        Assert.Equal(TlsSpkiPin.Fingerprint(cert), ex.PresentedIdentity!.Fingerprint);
        Assert.False(srv.PassReceived);
    }

    [Fact, Trait("SEC", "D4.11"), Trait("RD", "D4.3")]
    public async Task Pinned_explicit_ftps_logs_in_over_tls_and_opens_session()
    {
        using var cert = ServerCert();
        await using var srv = new LoopbackFtpsControlServer(cert, user: "kopie", secret: _secret);
        var factory = new FtpStorageProviderFactory(Connector());
        await using (var s = await factory.OpenAsync(Cfg("ftps", srv.Port, ("spki", TlsSpkiPin.Fingerprint(cert))), CredentialRole.Backup, _ct))
        {
            Assert.Equal(TlsSpkiPin.Fingerprint(cert), s.PresentedIdentity!.Fingerprint);
            Assert.Equal(StorageCaps.None, s.Capabilities.Caps & StorageCaps.DeleteAllowed);
            Assert.NotEqual(StorageCaps.None, s.Capabilities.Caps & StorageCaps.RangeRead); // FEAT: REST STREAM
            Assert.Null(await s.StatAsync("kobian.repo", _ct));
        }
        Assert.True(srv.PassReceived);
        Assert.False(srv.CredentialsSentInClear);
        Assert.Equal("AUTH TLS", srv.Commands[0]);
        Assert.Contains("PROT P", srv.Commands); // TLS także na kanale danych
        Assert.DoesNotContain(srv.Commands, c => c.Contains(_secret, StringComparison.Ordinal));
    }

    [Fact, Trait("RD", "D4.3")]
    public async Task Implicit_ftps_with_pin_connects()
    {
        using var cert = ServerCert();
        await using var srv = new LoopbackFtpsControlServer(cert, implicitTls: true, user: "kopie", secret: _secret);
        await using var t = await Connector().ConnectAsync(Cfg("ftps", srv.Port, ("tls", "implicit"), ("spki", TlsSpkiPin.Fingerprint(cert))), CredentialRole.Backup, _ct);
        Assert.Equal("tls-spki", t.Identity!.Kind);
        Assert.Equal(FtpEntryKind.Directory, (await t.StatAsync("/kopie", _ct))!.Kind);
        Assert.False(srv.CredentialsSentInClear);
        Assert.DoesNotContain("AUTH TLS", srv.Commands);
        // D4.14 / CE-22: SIZE tylko po jawnym TYPE I; 550 = brak obiektu (null), nie wyjątek.
        Assert.Null(await t.SizeAsync("/kopie/x.pack", _ct));
        var i = srv.Commands.IndexOf("SIZE /kopie/x.pack");
        Assert.Equal("TYPE I", srv.Commands[i - 1]);
    }

    [Fact, Trait("RD", "D4.8"), Trait("SEC", "D4.3")]
    public async Task Server_refusing_auth_tls_is_kb_2110_and_never_falls_back_to_plain()
    {
        await using var srv = new LoopbackFtpsControlServer(null, refuseAuthTls: true, user: "kopie", secret: _secret);
        var factory = new FtpStorageProviderFactory(Connector());
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await factory.OpenAsync(Cfg("ftps", srv.Port), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.FtpsNotSupported, ex.Code);
        Assert.False(srv.PassReceived);
        Assert.False(srv.CredentialsSentInClear);
    }

    [Fact, Trait("RD", "D4.8")]
    public async Task Rejected_password_is_kb_2102()
    {
        using var cert = ServerCert();
        await using var srv = new LoopbackFtpsControlServer(cert, user: "kopie", secret: "other-" + _secret);
        var factory = new FtpStorageProviderFactory(Connector());
        var ex = await Assert.ThrowsAsync<StorageException>(async () =>
            await factory.OpenAsync(Cfg("ftps", srv.Port, ("spki", TlsSpkiPin.Fingerprint(cert))), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.AuthRejected, ex.Code);
        Assert.Equal(ErrorClass.Permanent, ex.ErrorClass);
        Assert.DoesNotContain(_secret, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact, Trait("SEC", "D4.3")]
    public async Task Plain_ftp_with_insecure_flag_has_no_identity()
    {
        await using var srv = new LoopbackFtpsControlServer(null, user: "kopie", secret: _secret);
        await using var t = await Connector().ConnectAsync(Cfg("ftp", srv.Port, ("insecure", "1")), CredentialRole.Backup, _ct);
        Assert.Null(t.Identity);
        Assert.True(srv.CredentialsSentInClear); // dlatego wymagana flaga i ostrzeżenie (D4.3)
        Assert.DoesNotContain("AUTH TLS", srv.Commands);
    }
}
