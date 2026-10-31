using System.Data;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql;
using Kobian.Providers.Sources.MsSql.SqlClient;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kobian.Tests.Unit.Sources;

/// <summary>BL-40 cz. 2: adapter SqlClient bez serwera SQL (S5c = nie). Połączenie z prawdziwym SQL Server — kolejka Windows W-24.</summary>
public sealed class SqlClientAdapterTests
{
    private static MsSqlConnectionOptions Remote(Func<MsSqlConnectionOptions, MsSqlConnectionOptions>? f = null)
    {
        var o = new MsSqlConnectionOptions { Server = @"erp-srv\SQLEXPRESS" };
        return f is null ? o : f(o);
    }

    [Fact, Trait("SEC", "SEC-5.6"), Trait("NFR", "NFR-07")]
    public void Connection_is_always_encrypted_and_identifies_application()
    {
        var b = MsSqlConnectionFactory.Build(Remote());
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.False(b.TrustServerCertificate);
        Assert.Equal("Kobian Backup", b.ApplicationName);
        Assert.True(b.IntegratedSecurity);
        Assert.False(b.PersistSecurityInfo);
        Assert.False(b.MultipleActiveResultSets);
        Assert.Equal("master", b.InitialCatalog);
        Assert.Equal(15, b.ConnectTimeout);
        Assert.Equal(60, b.CommandTimeout);
        Assert.True(string.IsNullOrEmpty(b.ServerCertificate));
    }

    [Fact, Trait("SEC", "SEC-5.6")]
    public void Strict_option_selects_tds8()
    {
        var b = MsSqlConnectionFactory.Build(Remote(o => o with { Strict = true }));
        Assert.Equal(SqlConnectionEncryptOption.Strict, b.Encrypt);
    }

    [Fact, Trait("SEC", "SEC-5.6")]
    public void Pinned_certificate_is_passed_as_server_certificate()
    {
        var pin = Path.Combine(Path.GetTempPath(), "kobian-pin.cer");
        var b = MsSqlConnectionFactory.Build(Remote(o => o with { ServerCertificatePath = pin }));
        Assert.Equal(pin, b.ServerCertificate);
        Assert.False(b.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
    }

    [Theory, Trait("SEC", "SEC-5.6")]
    [InlineData("relative.cer")]
    [InlineData("/tmp/a;TrustServerCertificate=true.cer")]
    public void Invalid_pin_path_is_rejected(string pin)
    {
        var e = Assert.Throws<KobianException>(() => MsSqlConnectionFactory.Build(Remote(o => o with { ServerCertificatePath = pin })));
        Assert.Equal(KbCode.JobDefinitionInvalid, e.Code);
    }

    [Fact, Trait("SEC", "SEC-5.6")]
    public void Trust_server_certificate_only_for_localhost_with_explicit_consent()
    {
        var remote = Assert.Throws<KobianException>(() => MsSqlConnectionFactory.Build(Remote(o => o with { AllowLocalhostUntrusted = true })));
        Assert.Equal(KbCode.JobDefinitionInvalid, remote.Code);

        foreach (var local in new[] { "localhost", @"LOCALHOST\SQLEXPRESS", ".", @".\SQLEXPRESS", "127.0.0.1,1433", "[::1]" })
        {
            Assert.False(MsSqlConnectionFactory.Build(new MsSqlConnectionOptions { Server = local }).TrustServerCertificate);
            Assert.True(MsSqlConnectionFactory.Build(new MsSqlConnectionOptions { Server = local, AllowLocalhostUntrusted = true }).TrustServerCertificate);
        }

        Assert.Throws<KobianException>(() => MsSqlConnectionFactory.Build(new MsSqlConnectionOptions
        {
            Server = "localhost", AllowLocalhostUntrusted = true, ServerCertificatePath = Path.Combine(Path.GetTempPath(), "x.cer"),
        }));
    }

    [Theory]
    [InlineData("srv")]
    [InlineData(@"srv\SQLEXPRESS")]
    [InlineData("srv.firma.local,1433")]
    [InlineData("10.0.0.5")]
    [InlineData("[fe80::1],1433")]
    [InlineData("SRV-ERP_01")]
    public void Valid_server_names_are_accepted(string server) =>
        Assert.Equal(server, MsSqlConnectionFactory.ValidateServer(server));

    [Theory, Trait("SEC", "SEC-5.1")]
    [InlineData("")]
    [InlineData("np:srv")]
    [InlineData("tcp:srv")]
    [InlineData("admin:srv")]
    [InlineData("lpc:.")]
    [InlineData("srv;Trust Server Certificate=true")]
    [InlineData("srv;Password=x")]
    [InlineData("srv'--")]
    [InlineData(@"srv\a\b")]
    [InlineData(@"\srv")]
    [InlineData(@"srv\")]
    [InlineData("srv,0")]
    [InlineData("srv,70000")]
    [InlineData("srv,1,2")]
    [InlineData("srv, 1433")]
    [InlineData("a b")]
    [InlineData("(local)")]
    public void Invalid_or_injecting_server_names_are_rejected(string server)
    {
        var e = Assert.Throws<KobianException>(() => MsSqlConnectionFactory.ValidateServer(server));
        Assert.Equal(KbCode.JobDefinitionInvalid, e.Code);
    }

    [Fact, Trait("SEC", "NFR-11")]
    public void Sql_login_password_never_appears_in_connection_string_or_options()
    {
        var o = new MsSqlConnectionOptions { Server = "srv", Authentication = MsSqlAuthentication.SqlLogin, UserName = "kobian_backup" };
        var cs = MsSqlConnectionFactory.Build(o).ConnectionString;
        Assert.DoesNotContain("Password", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User ID", cs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Integrated Security=False", cs, StringComparison.OrdinalIgnoreCase);
        var text = o.ToString();
        Assert.DoesNotContain("srv", text, StringComparison.Ordinal);
        Assert.DoesNotContain("kobian_backup", text, StringComparison.Ordinal);
    }

    [Fact]
    public void User_name_rules_follow_authentication_mode()
    {
        Assert.Throws<KobianException>(() => MsSqlConnectionFactory.Build(new MsSqlConnectionOptions { Server = "srv", Authentication = MsSqlAuthentication.SqlLogin }));
        Assert.Throws<KobianException>(() => MsSqlConnectionFactory.Build(new MsSqlConnectionOptions { Server = "srv", UserName = "sa" }));
        Assert.Throws<KobianException>(() => MsSqlConnectionFactory.Build(new MsSqlConnectionOptions { Server = "srv", Authentication = MsSqlAuthentication.SqlLogin, UserName = "a\nb" }));
        Assert.Throws<KobianException>(() => MsSqlConnectionFactory.Build(Remote(o => o with { ConnectTimeoutSeconds = 0 })));
        Assert.Throws<KobianException>(() => MsSqlConnectionFactory.Build(Remote(o => o with { QueryTimeoutSeconds = 100_000 })));
    }

    [Fact, Trait("SEC", "SEC-5.1")]
    public void Parameters_have_explicit_types_and_sizes()
    {
        var s = SqlParameterMapper.Map("@db", "Firma");
        Assert.Equal(SqlDbType.NVarChar, s.SqlDbType);
        Assert.Equal(5, s.Size);
        Assert.Equal(-1, SqlParameterMapper.Map("@p", new string('x', 4001)).Size);
        Assert.Equal(1, SqlParameterMapper.Map("@p", "").Size);
        Assert.Equal(SqlDbType.Int, SqlParameterMapper.Map("@n", 8).SqlDbType);
        Assert.Equal(SqlDbType.BigInt, SqlParameterMapper.Map("@n", 8L).SqlDbType);
        Assert.Equal(SqlDbType.Bit, SqlParameterMapper.Map("@b", true).SqlDbType);
        Assert.Equal(SqlDbType.UniqueIdentifier, SqlParameterMapper.Map("@g", Guid.NewGuid()).SqlDbType);
        Assert.Equal(SqlDbType.DateTime2, SqlParameterMapper.Map("@t", DateTime.UtcNow).SqlDbType);
        Assert.Equal(SqlDbType.VarBinary, SqlParameterMapper.Map("@x", new byte[] { 1 }).SqlDbType);
        Assert.Equal(DBNull.Value, SqlParameterMapper.Map("@bufferCount", DBNullValue.Instance).Value);
        Assert.Equal(DBNull.Value, SqlParameterMapper.Map("@x", null).Value);
    }

    [Theory, Trait("SEC", "SEC-5.1")]
    [InlineData("db")]
    [InlineData("@")]
    [InlineData("@1a")]
    [InlineData("@a;b")]
    [InlineData("@a b")]
    [InlineData("@a--")]
    public void Invalid_parameter_names_are_rejected(string name) =>
        Assert.Throws<ArgumentException>(() => SqlParameterMapper.Map(name, 1));

    [Fact]
    public void Unsupported_parameter_types_are_rejected() =>
        Assert.Throws<ArgumentException>(() => SqlParameterMapper.Map("@d", 1.5m));

    [Fact]
    public void Every_parameter_of_existing_commands_maps()
    {
        var specs = new[]
        {
            MsSqlBackupCommand.FullCopyOnly("Firma", @"C:\ProgramData\Kobian\staging\r1\a.bak", BackupImpactProfile.LowImpact),
            MsSqlBackupCommand.FullCopyOnly("Firma", @"C:\s\a.bak", BackupImpactProfile.Default),
            MsSqlBackupCommand.VerifyOnly(@"C:\s\a.bak"),
        };
        foreach (var spec in specs)
            foreach (var (name, value) in spec.Parameters)
                Assert.NotNull(SqlParameterMapper.Map(name, value));
    }

    [Fact]
    public void Session_validates_options_and_requires_password_provider_for_sql_login()
    {
        var e = Assert.Throws<KobianException>(() => new SqlClientMsSqlSession(new MsSqlConnectionOptions { Server = "np:srv" }));
        Assert.Equal(KbCode.JobDefinitionInvalid, e.Code);
        Assert.Throws<ArgumentException>(() => new SqlClientMsSqlSession(
            new MsSqlConnectionOptions { Server = "srv", Authentication = MsSqlAuthentication.SqlLogin, UserName = "u" }));
    }

    [Fact, Trait("SEC", "NFR-11")]
    public async Task Failed_connection_yields_numbers_only_and_zeroes_password_buffer()
    {
        // Pętla zwrotna, zamknięty port: bez kontaktu z cudzymi serwerami (bezpieczeństwo pkt 4).
        var buffer = "S3cr3t-test-only".ToCharArray();
        var options = new MsSqlConnectionOptions
        {
            Server = "127.0.0.1,1", Authentication = MsSqlAuthentication.SqlLogin, UserName = "kobian_test", ConnectTimeoutSeconds = 2,
        };
        await using var session = new SqlClientMsSqlSession(options, _ => ValueTask.FromResult(buffer));
        var e = await Assert.ThrowsAsync<MsSqlServerException>(() =>
            session.QueryAsync(new SqlCommandSpec("SELECT 1", new Dictionary<string, object>()), TestContext.Current.CancellationToken));
        Assert.All(buffer, c => Assert.Equal('\0', c));
        Assert.DoesNotContain("127.0.0.1", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("kobian_test", e.Message, StringComparison.Ordinal);
        Assert.Null(e.InnerException);
        Assert.NotEmpty(e.ErrorNumbers);
    }

    [Theory, Trait("SEC", "SEC-5.6")]
    [InlineData(-2146893019)]
    [InlineData(-2146893022)]
    [InlineData(-2146762487)]
    public void Tls_failures_map_to_pin_mismatch(int number) =>
        Assert.Equal((KbCode.SqlTlsPinMismatch, ErrorClass.Security), SqlErrorMap.Map(number));

    [Fact]
    public void Tls_failure_wins_over_other_errors() =>
        Assert.Equal(KbCode.SqlTlsPinMismatch, SqlErrorMap.MapMany([3013, 3043, -2146893019]).Code);

    [Fact]
    public void Authentication_exception_is_detected_in_inner_chain()
    {
        var e = new InvalidOperationException("x", new IOException("y", new System.Security.Authentication.AuthenticationException("z")));
        Assert.True(SqlClientMsSqlSession.HasAuthenticationFailure(e));
        Assert.False(SqlClientMsSqlSession.HasAuthenticationFailure(new InvalidOperationException("x")));
    }
}
