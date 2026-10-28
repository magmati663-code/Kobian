using System.Security.Cryptography;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Providers.Storage.Secrets;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Smb;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>BL-73 (it. 92): poświadczenia SMB z katalogu sekretów (PROWIZORYCZNIE Qb16), rejestr dostawców i walidacja kształtu celu „smb” w ProbeDestination.</summary>
public sealed class SmbAgentWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-smbsec-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public SmbAgentWiringTests()
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

    private sealed class NoConnector : ISmbConnector
    {
        public ValueTask<ISmbConnection> ConnectAsync(SmbShare share, SmbCredential credential, CancellationToken ct) =>
            throw new SmbConnectException(53, "not used");
    }

    [Fact, Trait("Decision", "D4.1")]
    public async Task Smb_credentials_resolve_per_role_from_the_secrets_directory()
    {
        var a = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        var b = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        Put("nas.json", $$$"""{"backup":{"user":"FIRMA\\kobian-bk","password":"{{{a}}}"},"maintenance":{"user":"FIRMA\\kobian-mt","password":"{{{b}}}"}}""");
        var p = new FileSmbCredentialProvider(_dir);
        var bk = await p.ResolveAsync("file:nas", CredentialRole.Backup, _ct);
        var mt = await p.ResolveAsync("file:nas", CredentialRole.Maintenance, _ct);
        Assert.Equal(@"FIRMA\kobian-bk", bk.User);
        Assert.Equal(a, bk.Password);
        Assert.Equal(@"FIRMA\kobian-mt", mt.User);
        Assert.Equal(b, mt.Password);
        Assert.DoesNotContain(a, bk.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Smb_entry_without_password_or_role_is_refused_as_kb2102()
    {
        Put("klucz.json", """{"backup":{"user":"u","privateKeyFile":"id_test"}}""");
        Put("id_test", "-----BEGIN TEST-----");
        var p = new FileSmbCredentialProvider(_dir);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await p.ResolveAsync("file:klucz", CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.AuthRejected, ex.Code);
        ex = await Assert.ThrowsAsync<StorageException>(async () => await p.ResolveAsync("file:klucz", CredentialRole.Maintenance, _ct));
        Assert.Equal(KbCode.AuthRejected, ex.Code);
        ex = await Assert.ThrowsAsync<StorageException>(async () => await p.ResolveAsync("file:../x", CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.AuthRejected, ex.Code);
    }

    [Fact]
    public void Registry_has_smb_only_with_a_platform_connector()
    {
        Assert.False(AgentProviders.Create(_dir).ContainsKey("smb"));
        var withSmb = AgentProviders.Create(_dir, new NoConnector());
        Assert.Equal("smb", withSmb["smb"].Scheme);
        Assert.Equal(5, withSmb.Count); // local, sftp, ftps, ftp (BL-34), smb
    }

    [Theory]
    [InlineData(@"\\nas\kopie\repo", null)]
    [InlineData(@"\\nas\kopie\..\x", KbCode.InvalidObjectName)]
    [InlineData(@"\\nas\IPC$", KbCode.InvalidObjectName)]
    [InlineData(@"C:\kopie", KbCode.DestinationNotFound)]
    [InlineData(@"\\nas", KbCode.DestinationNotFound)]
    public void Probe_validates_smb_root_shape_without_network(string root, string? code)
    {
        var probe = new DestinationProbeService(AgentProviders.Create(_dir, new NoConnector()));
        Assert.Equal(code, probe.ValidateShape(new DestinationRequest("smb", root, SecretRef: "file:nas")));
    }

    [Fact]
    public async Task Probe_reports_kb_code_of_a_failed_smb_connection()
    {
        var a = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        Put("nas.json", $$$"""{"backup":{"user":"u","password":"{{{a}}}"}}""");
        var probe = new DestinationProbeService(AgentProviders.Create(_dir, new NoConnector()));
        var r = await probe.ProbeAsync(new DestinationRequest("smb", @"\\nas\brak", SecretRef: "file:nas"), _ct);
        Assert.Equal(DestinationProbeStatus.Failed, r.Status);
        Assert.Equal(KbCode.DestinationNotFound, r.Code); // Win32 53 ERROR_BAD_NETPATH → KB-2106
    }
}
