using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Sftp;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>BL-33 (it. 51): czyste elementy adaptera SSH.NET — opcje, pinning klucza hosta (D4.11, AC-04.1), tłumaczenie wyjątków (D4.8).</summary>
public sealed class SshNetAdapterTests
{
    private static DestinationConfig Cfg(params (string K, string V)[] o) =>
        new("d", "sftp", "/r", "ref", o.ToDictionary(x => x.K, x => x.V));

    [Fact]
    public void Fingerprint_is_openssh_format_and_matches_only_exact_key()
    {
        var key = RandomNumberGenerator.GetBytes(51);
        var fp = HostKeyPin.Fingerprint(key);
        Assert.StartsWith("SHA256:", fp, StringComparison.Ordinal);
        Assert.DoesNotContain('=', fp);
        Assert.True(HostKeyPin.Matches(fp, key));
        var other = (byte[])key.Clone();
        other[10] ^= 1;
        Assert.False(HostKeyPin.Matches(fp, other));
    }

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "D4.11")]
    public void Identity_probe_mode_only_without_pin_and_never_trusts_a_key()
    {
        var probe = SshNetOptions.Parse(Cfg(("host", "h"), ("identity_probe", "1")));
        Assert.True(probe.IdentityProbe);
        Assert.Equal("", probe.PinnedSha256);
        Assert.False(HostKeyPin.Matches(probe.PinnedSha256, RandomNumberGenerator.GetBytes(51))); // brak przypięcia ≠ zgodność
        var pinned = SshNetOptions.Parse(Cfg(("host", "h"), ("identity_probe", "1"), ("hostkey", "SHA256:abc")));
        Assert.False(pinned.IdentityProbe); // z przypięciem — normalne połączenie z weryfikacją
        Assert.Throws<SftpProtocolException>(() => SshNetOptions.Parse(Cfg(("host", "h"), ("identity_probe", "true"))));
        Assert.Throws<SftpProtocolException>(() => SshNetOptions.Parse(Cfg(("host", "h"), ("identity_probe", "1"), ("hostkey", "MD5:aa"))));
    }

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "D4.11")]
    public void Host_key_mismatch_carries_presented_identity_to_storage_exception()
    {
        var id = new ServerIdentity("ssh-ed25519", "SHA256:xyz", "ssh-ed25519");
        var mapped = SftpErrorMap.Map(new SftpProtocolException(SftpStatus.HostKeyMismatch, "m") { PresentedIdentity = id });
        Assert.Equal(KbCode.HostKeyMismatch, mapped.Code);
        Assert.Equal(id, mapped.PresentedIdentity);
        Assert.Null(SftpErrorMap.Map(new SftpProtocolException(SftpStatus.NoSuchFile, "m")).PresentedIdentity);
    }

    [Fact, Trait("AC", "AC-04.1")]
    public void Missing_pin_is_refused_with_host_key_status()
    {
        var ex = Assert.Throws<SftpProtocolException>(() => SshNetOptions.Parse(Cfg(("host", "example.test"))));
        Assert.Equal(SftpStatus.HostKeyMismatch, ex.Status);
        Assert.Equal(KbCode.HostKeyMismatch, SftpErrorMap.Map(ex).Code);
        Assert.Equal(ErrorClass.Security, SftpErrorMap.Map(ex).ErrorClass);
    }

    [Fact]
    public void Options_parse_defaults_and_validate()
    {
        var o = SshNetOptions.Parse(Cfg(("host", "backup.example.test"), ("hostkey", "SHA256:abc")));
        Assert.Equal(22, o.Port);
        Assert.Equal(TimeSpan.FromSeconds(30), o.ConnectTimeout);
        Assert.Equal(2222, SshNetOptions.Parse(Cfg(("host", "h"), ("port", "2222"), ("hostkey", "SHA256:abc"))).Port);
        foreach (var bad in new[] { "0", "70000", "-1", "x", "22 " })
            Assert.Throws<SftpProtocolException>(() => SshNetOptions.Parse(Cfg(("host", "h"), ("port", bad), ("hostkey", "SHA256:abc"))));
        foreach (var badHost in new[] { "", "a b", "h;rm", "h/../x" })
            Assert.Throws<SftpProtocolException>(() => SshNetOptions.Parse(Cfg(("host", badHost), ("hostkey", "SHA256:abc"))));
    }

    public static TheoryData<Exception, SftpStatus> Cases => new()
    {
        { new SshAuthenticationException("x"), SftpStatus.AuthRejected },
        { new SftpPathNotFoundException("x"), SftpStatus.NoSuchFile },
        { new SftpPermissionDeniedException("x"), SftpStatus.PermissionDenied },
        { new SshConnectionException("x"), SftpStatus.ConnectionLost },
        { new System.Net.Sockets.SocketException(), SftpStatus.NoConnection },
        { new SshException("Failure"), SftpStatus.Failure },
    };

    [Theory, MemberData(nameof(Cases))]
    public void Translator_maps_library_exceptions(Exception ex, SftpStatus expected)
    {
        var t = SshNetErrorTranslator.Translate(ex);
        Assert.Equal(expected, t.Status);
        Assert.DoesNotContain("password", t.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connector_without_secret_ref_is_refused_before_any_network()
    {
        var c = new SshNetSftpConnector(new NoCreds());
        var cfg = new DestinationConfig("d", "sftp", "/r", null, new Dictionary<string, string> { ["host"] = "h", ["hostkey"] = "SHA256:abc" });
        var ex = await Assert.ThrowsAsync<SftpProtocolException>(async () => await c.ConnectAsync(cfg, CredentialRole.Backup, TestContext.Current.CancellationToken));
        Assert.Equal(SftpStatus.AuthRejected, ex.Status);
    }

    private sealed class NoCreds : ISftpCredentialProvider
    {
        public ValueTask<SftpCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct) => throw new InvalidOperationException();
    }
}
