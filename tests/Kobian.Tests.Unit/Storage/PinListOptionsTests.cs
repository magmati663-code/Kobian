using System.Net.Security;
using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Providers.Storage.Ftp;
using Kobian.Providers.Storage.Sftp;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>
/// BL-35 (D4.11, AC-12.2, SEC-14.5): adaptery dostawców przyjmują zbiór pinów z katalogu tożsamości — SSH: lista „hostkey” (≤ 4, każdy porównywany
/// stałoczasowo); FTPS: opcja „require_pin” (cel wysokiego ryzyka) — ważny łańcuch CA bez zgodnego pinu = odmowa.
/// </summary>
public sealed class PinListOptionsTests
{
    private static DestinationConfig Cfg(string scheme, params (string K, string V)[] o) => new("d", scheme, "/r", "ref", o.ToDictionary(x => x.K, x => x.V));

    [Fact, Trait("AC", "AC-12.2"), Trait("D", "D4.11")]
    public void Ssh_pin_list_matches_any_listed_key_only()
    {
        var a = RandomNumberGenerator.GetBytes(51);
        var b = RandomNumberGenerator.GetBytes(51);
        var c = RandomNumberGenerator.GetBytes(51);
        var opt = SshNetOptions.Parse(Cfg("sftp", ("host", "h"), ("hostkey", HostKeyPin.Fingerprint(a) + "," + HostKeyPin.Fingerprint(b))));
        Assert.Equal(2, opt.Pins.Count);
        Assert.False(opt.IdentityProbe);
        Assert.True(HostKeyPin.MatchesAny(opt.Pins, a));
        Assert.True(HostKeyPin.MatchesAny(opt.Pins, b));
        Assert.False(HostKeyPin.MatchesAny(opt.Pins, c));
        Assert.False(HostKeyPin.MatchesAny([], a));
        Assert.Single(SshNetOptions.Parse(Cfg("sftp", ("host", "h"), ("hostkey", "SHA256:abc"))).Pins); // dotychczasowy pojedynczy pin bez zmian

        var five = string.Join(',', Enumerable.Range(0, 5).Select(_ => HostKeyPin.Fingerprint(RandomNumberGenerator.GetBytes(51))));
        foreach (var bad in new[] { five, HostKeyPin.Fingerprint(a) + ",", "," + HostKeyPin.Fingerprint(a), HostKeyPin.Fingerprint(a) + ",MD5:aa" })
            Assert.Equal(SftpStatus.HostKeyMismatch, Assert.Throws<SftpProtocolException>(() => SshNetOptions.Parse(Cfg("sftp", ("host", "h"), ("hostkey", bad)))).Status);
    }

    [Fact, Trait("SEC", "SEC-14.5"), Trait("D", "D4.11")]
    public void Ftps_require_pin_refuses_a_valid_ca_chain_without_a_matching_pin()
    {
        using var cert = FluentFtpAdapterTests.SelfSigned("nas.example");
        using var other = FluentFtpAdapterTests.SelfSigned("nas.example");
        var pin = TlsSpkiPin.Fingerprint(cert);
        Assert.True(FtpsCertificatePolicy.Evaluate(cert, SslPolicyErrors.None, []).Accepted); // zwykły cel: CA wystarcza
        Assert.False(FtpsCertificatePolicy.Evaluate(cert, SslPolicyErrors.None, [], requirePin: true).Accepted);
        Assert.False(FtpsCertificatePolicy.Evaluate(cert, SslPolicyErrors.None, [TlsSpkiPin.Fingerprint(other)], requirePin: true).Accepted);
        var ok = FtpsCertificatePolicy.Evaluate(cert, SslPolicyErrors.None, [pin], requirePin: true);
        Assert.True(ok.Accepted && ok.ViaPin);
        Assert.False(FtpsCertificatePolicy.Evaluate(cert, SslPolicyErrors.RemoteCertificateNotAvailable, [pin], requirePin: true).Accepted);

        Assert.True(FtpOptions.Parse(Cfg("ftps", ("host", "n"), ("require_pin", "1"))).RequirePin);
        Assert.False(FtpOptions.Parse(Cfg("ftps", ("host", "n"))).RequirePin);
        Assert.False(FtpOptions.Parse(Cfg("ftps", ("host", "n"), ("require_pin", "0"))).RequirePin);
        Assert.Throws<FtpProtocolException>(() => FtpOptions.Parse(Cfg("ftps", ("host", "n"), ("require_pin", "true")))); // tylko 0/1 — bez cichego „wyłączone”
    }
}
