using System.Security.Cryptography;
using Kobian.Agent.Hosting;
using Kobian.Providers.Storage.Secrets;
using Kobian.Application.Storage;
using Kobian.Providers.Storage.Sftp;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>BL-33/BL-15 (it. 52): wiring poświadczeń SFTP w Agencie — SecretRef file:&lt;nazwa&gt; (AC-01.3, D4.1; PROWIZORYCZNIE Qb16).</summary>
public sealed class FileSftpCredentialProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-sec-" + Guid.NewGuid().ToString("N"));

    public FileSftpCredentialProviderTests()
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Put(string file, string content)
    {
        var p = Path.Combine(_dir, file);
        File.WriteAllText(p, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return p;
    }

    private static string RandomSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private FileSftpCredentialProvider Sut() => new(_dir);

    [Fact]
    public async Task Backup_and_maintenance_roles_resolve_separately()
    {
        var a = RandomSecret();
        var b = RandomSecret();
        Put("nas.json", $$$"""{"backup":{"user":"bk","password":"{{{a}}}"},"maintenance":{"user":"mt","password":"{{{b}}}"}}""");
        var bk = await Sut().ResolveAsync("file:nas", CredentialRole.Backup, CancellationToken.None);
        var mt = await Sut().ResolveAsync("file:nas", CredentialRole.Maintenance, CancellationToken.None);
        Assert.Equal("bk", bk.UserName);
        Assert.Equal(a, bk.Password);
        Assert.Equal("mt", mt.UserName);
        Assert.Equal(b, mt.Password);
    }

    [Fact]
    public async Task Missing_role_entry_is_refused_without_fallback()
    {
        Put("nas.json", $$$"""{"backup":{"user":"bk","password":"{{{RandomSecret()}}}"}}""");
        var ex = await Assert.ThrowsAsync<SftpProtocolException>(async () =>
            await Sut().ResolveAsync("file:nas", CredentialRole.Maintenance, CancellationToken.None));
        Assert.Equal(SftpStatus.AuthRejected, ex.Status);
    }

    [Fact]
    public async Task Private_key_file_is_loaded_from_secrets_dir()
    {
        var pem = "-----BEGIN TEST-----" + RandomSecret() + "-----END TEST-----";
        Put("k1.key", pem);
        Put("host.json", """{"backup":{"user":"bk","privateKeyFile":"k1.key"}}""");
        var c = await Sut().ResolveAsync("file:host", CredentialRole.Backup, CancellationToken.None);
        Assert.Null(c.Password);
        Assert.Equal(pem, System.Text.Encoding.UTF8.GetString(c.PrivateKey!));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("file:../x")]
    [InlineData("file:a/b")]
    [InlineData("file:a\\b")]
    [InlineData("file:..")]
    [InlineData("file:")]
    [InlineData("file:a..b")]
    [InlineData("file:.hidden")]
    [InlineData("file:CON")] // przegląd it. 60: nazwy urządzeń Windows i kropka końcowa (D8.9)
    [InlineData("file:nul.x")]
    [InlineData("file:Com1")]
    [InlineData("file:a.")]
    public async Task Invalid_references_are_refused(string secretRef)
    {
        var ex = await Assert.ThrowsAsync<SftpProtocolException>(async () =>
            await Sut().ResolveAsync(secretRef, CredentialRole.Backup, CancellationToken.None));
        Assert.Equal(SftpStatus.AuthRejected, ex.Status);
    }

    [Theory]
    [InlineData("../k")]
    [InlineData("sub/k")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("lpt1.key")]
    [InlineData("k.")]
    public async Task Key_file_name_cannot_escape_secrets_dir(string keyName)
    {
        Put("h.json", $$$"""{"backup":{"user":"bk","privateKeyFile":"{{{keyName}}}"}}""");
        await Assert.ThrowsAsync<SftpProtocolException>(async () =>
            await Sut().ResolveAsync("file:h", CredentialRole.Backup, CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_or_incomplete_entries_are_refused_and_errors_do_not_leak_secrets()
    {
        var leak = RandomSecret();
        Put("bad.json", "{\"backup\":{\"user\":\"bk\",\"password\":\"" + leak + "\"");
        Put("inc.json", $$$"""{"backup":{"password":"{{{leak}}}"}}""");
        foreach (var n in new[] { "bad", "inc" })
        {
            var ex = await Assert.ThrowsAsync<SftpProtocolException>(async () =>
                await Sut().ResolveAsync("file:" + n, CredentialRole.Backup, CancellationToken.None));
            Assert.DoesNotContain(leak, ex.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(_dir, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Missing_entry_and_missing_directory_are_refused()
    {
        await Assert.ThrowsAsync<SftpProtocolException>(async () =>
            await Sut().ResolveAsync("file:nope", CredentialRole.Backup, CancellationToken.None));
        var gone = new FileSftpCredentialProvider(Path.Combine(_dir, "absent"));
        await Assert.ThrowsAsync<SftpProtocolException>(async () =>
            await gone.ResolveAsync("file:nas", CredentialRole.Backup, CancellationToken.None));
    }

    [Fact]
    public async Task Entry_accessible_to_others_is_refused_on_unix()
    {
        if (OperatingSystem.IsWindows()) return; // ACL katalogu sekretów: BL-17 / kolejka W-14
        var p = Put("nas.json", $$$"""{"backup":{"user":"bk","password":"{{{RandomSecret()}}}"}}""");
        File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        await Assert.ThrowsAsync<SftpProtocolException>(async () =>
            await Sut().ResolveAsync("file:nas", CredentialRole.Backup, CancellationToken.None));
    }

    [Fact]
    public async Task Symlinked_entry_is_refused_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = Put("real.json", $$$"""{"backup":{"user":"bk","password":"{{{RandomSecret()}}}"}}""");
        File.CreateSymbolicLink(Path.Combine(_dir, "ln.json"), real);
        await Assert.ThrowsAsync<SftpProtocolException>(async () =>
            await Sut().ResolveAsync("file:ln", CredentialRole.Backup, CancellationToken.None));
    }

    [Fact]
    public async Task Entry_in_directory_writable_by_others_is_refused_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        Put("nas.json", $$$"""{"backup":{"user":"bk","password":"{{{RandomSecret()}}}"}}""");
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        try
        {
            await Assert.ThrowsAsync<SftpProtocolException>(async () =>
                await Sut().ResolveAsync("file:nas", CredentialRole.Backup, CancellationToken.None));
        }
        finally
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Agent_registry_has_local_and_sftp_providers()
    {
        var p = AgentProviders.Create(_dir);
        Assert.Equal("local", p["local"].Scheme);
        Assert.Equal("sftp", p["sftp"].Scheme);
        Assert.Equal(4, p.Count); // + ftps, ftp (BL-34)
    }
}
