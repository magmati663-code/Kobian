using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Exceptions;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Ftp;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>BL-34: czyste elementy adaptera FluentFTP bez serwera — opcje (D4.3), konfiguracja klienta (SEC-4.6), pinning SPKI (D4.11), parsery, mapa wyjątków.</summary>
public sealed class FluentFtpAdapterTests
{
    private static DestinationConfig Cfg(string scheme, params (string K, string V)[] o) =>
        new("d", scheme, "/kopie", "file:ftp1", o.ToDictionary(x => x.K, x => x.V));

    internal static X509Certificate2 SelfSigned(string cn, ECDsa? key = null)
    {
        key ??= ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(cn);
        req.CertificateExtensions.Add(san.Build());
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact, Trait("RD", "D4.3")]
    public void Ftps_defaults_to_explicit_tls_on_port_21_and_implicit_uses_990()
    {
        var e = FtpOptions.Parse(Cfg("ftps", ("host", "nas.example")));
        Assert.Equal(FtpTlsMode.Explicit, e.Tls);
        Assert.Equal(21, e.Port);
        Assert.Equal(FtpPassiveMode.PasvControlIp, e.Passive);
        var i = FtpOptions.Parse(Cfg("ftps", ("host", "nas.example"), ("tls", "implicit")));
        Assert.Equal(FtpTlsMode.Implicit, i.Tls);
        Assert.Equal(990, i.Port);
        var p = FtpOptions.Parse(Cfg("ftps", ("host", "nas.example"), ("port", "2121"), ("passive", "epsv")));
        Assert.Equal(2121, p.Port);
        Assert.Equal(FtpPassiveMode.Epsv, p.Passive);
    }

    [Fact, Trait("SEC", "D4.3")]
    public void Plain_ftp_requires_insecure_flag_and_never_downgrades_ftps()
    {
        var ex = Assert.Throws<FtpProtocolException>(() => FtpOptions.Parse(Cfg("ftp", ("host", "nas.example"))));
        Assert.Equal(FtpFailure.InsecureNotConfirmed, ex.Failure);
        Assert.Throws<FtpProtocolException>(() => FtpOptions.Parse(Cfg("ftp", ("host", "nas.example"), ("insecure", "true"))));
        Assert.Equal(FtpTlsMode.None, FtpOptions.Parse(Cfg("ftp", ("host", "nas.example"), ("insecure", "1"))).Tls);
        // „ftps” nie ma trybu bez TLS; nieznany tryb = odmowa, nie cichy spadek do jawnego FTP.
        var ex2 = Assert.Throws<FtpProtocolException>(() => FtpOptions.Parse(Cfg("ftps", ("host", "nas.example"), ("tls", "none"))));
        Assert.Equal(FtpFailure.TlsNotSupported, ex2.Failure);
        Assert.Equal(FtpTlsMode.Explicit, FtpOptions.Parse(Cfg("ftps", ("host", "nas.example"), ("insecure", "1"))).Tls);
    }

    [Theory, Trait("SEC", "D4.6")]
    [InlineData("")]
    [InlineData("nas example")]
    [InlineData("nas\r\nUSER x")]
    [InlineData("nas/../x")]
    public void Invalid_host_rejected(string host)
    {
        Assert.Throws<FtpProtocolException>(() => FtpOptions.Parse(Cfg("ftps", ("host", host))));
    }

    [Theory, Trait("SEC", "D4.11")]
    [InlineData("abc")]
    [InlineData("SHA256:")]
    [InlineData("SHA256:not base64!")]
    [InlineData("MD5:abcd")]
    public void Invalid_spki_pin_rejected(string pin)
    {
        var ex = Assert.Throws<FtpProtocolException>(() => FtpOptions.Parse(Cfg("ftps", ("host", "nas"), ("spki", pin))));
        Assert.Equal(FtpFailure.TlsIdentityRejected, ex.Failure);
    }

    [Fact, Trait("SEC", "SEC-4.6"), Trait("RD", "D4.3")]
    public void Client_config_never_accepts_any_certificate_and_never_logs_credentials()
    {
        var c = FluentFtpConfig.Build(FtpOptions.Parse(Cfg("ftps", ("host", "nas.example"))));
        Assert.False(c.ValidateAnyCertificate);
        Assert.True(c.ValidateCertificateRevocation);
        Assert.Equal(FtpEncryptionMode.Explicit, c.EncryptionMode);
        Assert.True(c.DataConnectionEncryption);
        Assert.False(c.EncryptAuthenticationOnly);
        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, c.SslProtocols);
        Assert.False(c.LogPassword);
        Assert.False(c.LogUserName);
        Assert.False(c.LogHost);
        Assert.False(c.LogToConsole);
        Assert.Equal(FtpDataConnectionType.PassiveExtended, c.DataConnectionType); // PASV bez adresu z odpowiedzi
        Assert.Equal(FtpDataType.Binary, c.UploadDataType);
        Assert.Equal(FtpDataType.Binary, c.DownloadDataType);
        Assert.Equal(FtpDataType.Binary, c.ListingDataType);
        Assert.Equal(FtpSelfConnectMode.Never, c.SelfConnectMode);
        Assert.Equal(30_000, c.ConnectTimeout);
        Assert.Equal(60_000, c.DataConnectionReadTimeout);
        Assert.Equal(FtpEncryptionMode.Implicit, FluentFtpConfig.Build(FtpOptions.Parse(Cfg("ftps", ("host", "n"), ("tls", "implicit")))).EncryptionMode);
        var plain = FluentFtpConfig.Build(FtpOptions.Parse(Cfg("ftp", ("host", "n"), ("insecure", "1"))));
        Assert.Equal(FtpEncryptionMode.None, plain.EncryptionMode);
        Assert.False(plain.ValidateAnyCertificate);
        Assert.Equal(FtpDataConnectionType.EPSV, FluentFtpConfig.Build(FtpOptions.Parse(Cfg("ftps", ("host", "n"), ("passive", "epsv")))).DataConnectionType);
    }

    [Fact, Trait("SEC", "D4.11")]
    public void Spki_fingerprint_is_sha256_of_subject_public_key_info_and_survives_reissue()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var a = SelfSigned("nas.example", key);
        using var b = SelfSigned("nas.example", key); // odnowiony certyfikat, ten sam klucz
        using var other = SelfSigned("nas.example");
        var expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).TrimEnd('=');
        Assert.Equal(expected, TlsSpkiPin.Fingerprint(a));
        Assert.Equal(TlsSpkiPin.Fingerprint(a), TlsSpkiPin.Fingerprint(b));
        Assert.NotEqual(TlsSpkiPin.Fingerprint(a), TlsSpkiPin.Fingerprint(other));
        Assert.NotEqual(a.Thumbprint, b.Thumbprint);
    }

    [Fact, Trait("SEC", "D4.11"), Trait("AC", "AC-12.2")]
    public void Untrusted_certificate_requires_matching_pin()
    {
        using var cert = SelfSigned("nas.example");
        using var other = SelfSigned("nas.example");
        var pin = TlsSpkiPin.Fingerprint(cert);
        const SslPolicyErrors chain = SslPolicyErrors.RemoteCertificateChainErrors;

        var noPin = FtpsCertificatePolicy.Evaluate(cert, chain, []);
        Assert.False(noPin.Accepted);
        Assert.Equal("tls-spki", noPin.Identity.Kind);
        Assert.Equal(pin, noPin.Identity.Fingerprint); // odcisk do zatwierdzenia poza pasmem

        var pinned = FtpsCertificatePolicy.Evaluate(cert, chain | SslPolicyErrors.RemoteCertificateNameMismatch, [pin]);
        Assert.True(pinned.Accepted);
        Assert.True(pinned.ViaPin);

        // Dwa zatwierdzone piny (rotacja/klaster): oba węzły akceptowane, trzeci klucz odrzucony.
        var twoPins = new[] { TlsSpkiPin.Fingerprint(other), pin };
        Assert.True(FtpsCertificatePolicy.Evaluate(cert, chain, twoPins).Accepted);
        Assert.True(FtpsCertificatePolicy.Evaluate(other, chain, twoPins).Accepted);
        using var third = SelfSigned("nas.example");
        Assert.False(FtpsCertificatePolicy.Evaluate(third, chain, twoPins).Accepted);

        var mismatch = FtpsCertificatePolicy.Evaluate(cert, chain, [TlsSpkiPin.Fingerprint(other)]);
        Assert.False(mismatch.Accepted);
        Assert.Equal(pin, mismatch.Identity.Fingerprint);
    }

    [Fact, Trait("SEC", "D4.11")]
    public void Trusted_chain_is_accepted_without_pin_and_missing_certificate_never()
    {
        using var cert = SelfSigned("nas.example");
        var ok = FtpsCertificatePolicy.Evaluate(cert, SslPolicyErrors.None, []);
        Assert.True(ok.Accepted);
        Assert.False(ok.ViaPin);
        // Ważny łańcuch, inny SPKI niż przypięty — zdarzenie informacyjne, nie błąd (D4.11).
        Assert.True(FtpsCertificatePolicy.Evaluate(cert, SslPolicyErrors.None, ["SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"]).Accepted);
        Assert.False(FtpsCertificatePolicy.Evaluate(null, SslPolicyErrors.RemoteCertificateNotAvailable, ["SHA256:x"]).Accepted);
        Assert.False(FtpsCertificatePolicy.Evaluate(cert, SslPolicyErrors.RemoteCertificateNotAvailable, [TlsSpkiPin.Fingerprint(cert)]).Accepted);
    }

    [Fact, Trait("RD", "D4.14")]
    public void Mlst_facts_are_parsed_without_following_links()
    {
        var f = FtpReplyParser.ParseMlstLine(" type=file;size=1234;modify=20260101123045.123;UNIX.mode=0644; /kopie/data/ab/x.pack", "x.pack");
        Assert.Equal(new FtpEntry("x.pack", FtpEntryKind.File, 1234, new DateTimeOffset(2026, 1, 1, 12, 30, 45, TimeSpan.Zero)), f);
        Assert.Equal(FtpEntryKind.Directory, FtpReplyParser.ParseMlstLine(" Type=dir;modify=20260101000000; /kopie", "kopie")!.Kind);
        Assert.Equal(FtpEntryKind.Directory, FtpReplyParser.ParseMlstLine(" type=cdir; /kopie", "kopie")!.Kind);
        Assert.Equal(FtpEntryKind.Link, FtpReplyParser.ParseMlstLine(" type=OS.unix=slink:/etc;size=4; /kopie/data", "data")!.Kind);
        Assert.Equal(FtpEntryKind.Link, FtpReplyParser.ParseMlstLine(" type=OS.unix=symlink;size=4; /kopie/data", "data")!.Kind);
        Assert.Null(FtpReplyParser.ParseMlstLine("Listing /kopie", "kopie"));
    }

    [Fact, Trait("RD", "D4.14")]
    public void Size_and_hash_replies_parse_strictly()
    {
        Assert.Equal(1234L, FtpReplyParser.ParseNumber213("213", "1234"));
        Assert.Null(FtpReplyParser.ParseNumber213("550", "1234"));
        Assert.Null(FtpReplyParser.ParseNumber213("213", "-5"));
        Assert.Null(FtpReplyParser.ParseNumber213("213", "12 34"));
        var h = SHA256.HashData([1, 2, 3]);
        Assert.Equal(h, FtpReplyParser.ParseSha256Hex(Convert.ToHexStringLower(h)));
        Assert.Null(FtpReplyParser.ParseSha256Hex("abcd"));
        Assert.Null(FtpReplyParser.ParseSha256Hex(new string('z', 64)));
    }

    [Fact, Trait("RD", "D4.8"), Trait("SEC", "SEC-4.6")]
    public void Library_exceptions_are_translated_without_command_text()
    {
        Assert.Equal(FtpFailure.AuthRejected, FluentFtpErrorTranslator.Translate(new FtpAuthenticationException("530", "Login incorrect.")).Failure);
        Assert.Equal(FtpFailure.TlsNotSupported, FluentFtpErrorTranslator.Translate(new FtpSecurityNotAvailableException()).Failure);
        Assert.Equal(FtpFailure.TlsIdentityRejected, FluentFtpErrorTranslator.Translate(new FtpInvalidCertificateException("bad")).Failure);
        Assert.Equal(FtpFailure.TlsIdentityRejected, FluentFtpErrorTranslator.Translate(new AuthenticationException("handshake")).Failure);
        Assert.Equal(FtpFailure.Transient, FluentFtpErrorTranslator.Translate(new TimeoutException()).Failure);
        Assert.Equal(FtpFailure.Transient, FluentFtpErrorTranslator.Translate(new IOException()).Failure);
        var full = FluentFtpErrorTranslator.Translate(new FtpCommandException("552", "Quota exceeded for user kopie PASS secret"), writeContext: true);
        Assert.Equal(FtpFailure.NoSpace, full.Failure);
        Assert.Equal(552, full.ReplyCode);
        Assert.DoesNotContain("PASS", full.Message, StringComparison.Ordinal);
        Assert.Equal("FTP server replied 552.", full.Message);
        Assert.Equal(FtpFailure.PermissionDenied, FluentFtpErrorTranslator.Translate(new FtpCommandException("550", "denied"), writeContext: true).Failure);
        Assert.Equal(FtpFailure.NotFound, FluentFtpErrorTranslator.Translate(new FtpCommandException("550", "no such file")).Failure);
    }

    [Fact, Trait("SEC", "D4.3")]
    public async Task Connector_refuses_plain_ftp_before_resolving_credentials()
    {
        var creds = new CountingCredentials();
        var c = new FluentFtpConnector(creds);
        var ex = await Assert.ThrowsAsync<FtpProtocolException>(async () =>
            await c.ConnectAsync(Cfg("ftp", ("host", "127.0.0.1")), CredentialRole.Backup, TestContext.Current.CancellationToken));
        Assert.Equal(FtpFailure.InsecureNotConfirmed, ex.Failure);
        Assert.Equal(0, creds.Calls);
        var noRef = await Assert.ThrowsAsync<FtpProtocolException>(async () =>
            await c.ConnectAsync(new DestinationConfig("d", "ftps", "/kopie", null, new Dictionary<string, string> { ["host"] = "127.0.0.1" }), CredentialRole.Backup, TestContext.Current.CancellationToken));
        Assert.Equal(FtpFailure.AuthRejected, noRef.Failure);
        Assert.Equal(0, creds.Calls);
    }

    [Fact]
    public void Kb_codes_for_ftps_exist_with_expected_classes()
    {
        Assert.Equal("KB-2109", KbCode.TlsIdentityMismatch);
        Assert.Equal("KB-2110", KbCode.FtpsNotSupported);
        Assert.Equal("KB-2115", KbCode.InsecureTransportNotConfirmed);
    }

    internal sealed class CountingCredentials : IFtpCredentialProvider
    {
        public int Calls { get; private set; }

        public string User { get; init; } = "kopie";

        public string Secret { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

        public ValueTask<FtpCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(new FtpCredential(User, Secret));
        }
    }
}
