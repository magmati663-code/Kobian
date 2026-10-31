using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.V1;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql;
using Kobian.Providers.Sources.MsSql.SqlClient;
using Kobian.Tests.Unit.Sources;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-40 cz. 3: DiscoverDatabases w Agencie (kreator S1-db) — walidacja przed połączeniem (KB-0103), pin jako nazwa pliku w &lt;stan&gt;/certs (Qb30),
/// login SQL wyłącznie przez SecretRef, skrypt uprawnień D5.7 dla baz bez BACKUP DATABASE, mapowanie na odpowiedź gRPC.
/// </summary>
public sealed class DatabaseDiscoveryTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _certs = Path.Combine(Path.GetTempPath(), "kobian-certs-" + Guid.NewGuid().ToString("N"));
    private readonly List<(MsSqlConnectionOptions Options, string? SecretRef)> _opened = [];
    private MsSqlDiscoveryTests.ScriptedSql _sql = new();
    private int _disposed;

    public DatabaseDiscoveryTests() => Directory.CreateDirectory(_certs);

    public void Dispose() => Directory.Delete(_certs, recursive: true);

    private DatabaseDiscoveryService Service() => new(_certs, (o, s, _) =>
    {
        _opened.Add((o, s));
        return ValueTask.FromResult<IMsSqlSession>(new Disposable(_sql, () => _disposed++));
    });

    [Theory, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.6")]
    [InlineData("srv", "kerberos", null, null)]
    [InlineData("srv", "sql", null, null)]
    [InlineData("srv", "sql", "  ", null)]
    [InlineData("srv", "integrated", "file:erp", null)]
    [InlineData("np:srv", null, null, null)]
    [InlineData("admin:srv", null, null, null)]
    [InlineData("srv;Trusted_Connection=no", null, null, null)]
    [InlineData("", null, null, null)]
    [InlineData("srv", null, null, "../secrets/x.cer")]
    [InlineData("srv", null, null, "sub/x.cer")]
    [InlineData("srv", null, null, "C:\\x.cer")]
    [InlineData("srv", null, null, "x.pem")]
    [InlineData("srv", null, null, "brak.cer")]
    public async Task Invalid_request_is_refused_before_any_connection(string server, string? auth, string? secretRef, string? cert)
    {
        var o = await Service().DiscoverAsync(new DatabaseDiscoveryRequest(server, auth, secretRef, cert), _ct);
        Assert.Equal(KbCode.JobDefinitionInvalid, o.Code);
        Assert.Null(o.Result);
        Assert.Empty(_opened);
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Untrusted_certificate_only_for_localhost()
    {
        var bad = await Service().DiscoverAsync(new DatabaseDiscoveryRequest("srv01", AllowLocalhostUntrusted: true), _ct);
        Assert.Equal(KbCode.JobDefinitionInvalid, bad.Code);
        var ok = await Service().DiscoverAsync(new DatabaseDiscoveryRequest("localhost\\SQLEXPRESS", AllowLocalhostUntrusted: true), _ct);
        Assert.Null(ok.Code);
        Assert.True(_opened.Single().Options.AllowLocalhostUntrusted);
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.3"), Trait("D", "D5.7")]
    public async Task Integrated_discovery_returns_permission_script_for_databases_without_rights()
    {
        await File.WriteAllBytesAsync(Path.Combine(_certs, "srv01.cer"), [0x30, 0x82], _ct);
        _sql.Databases.Add(Row("Firma]1"));
        _sql.Databases.Add(Row("Kadry"));
        _sql.Perms["Firma]1"] = (false, false);

        var o = await Service().DiscoverAsync(new DatabaseDiscoveryRequest("srv01\\ERP", Certificate: "srv01.cer"), _ct);

        Assert.Null(o.Code);
        Assert.Equal(2, o.Result!.Databases.Count);
        Assert.NotNull(o.PermissionScript);
        Assert.Contains("USE [Firma]]1];", o.PermissionScript, StringComparison.Ordinal);
        Assert.DoesNotContain("[Kadry]", o.PermissionScript, StringComparison.Ordinal);
        Assert.Contains(@"[NT SERVICE\KobianAgent]", o.PermissionScript, StringComparison.Ordinal);
        var (opts, secretRef) = _opened.Single();
        Assert.Null(secretRef);
        Assert.Equal(MsSqlAuthentication.Integrated, opts.Authentication);
        Assert.Equal(Path.Combine(Path.GetFullPath(_certs), "srv01.cer"), opts.ServerCertificatePath);
        Assert.Null(opts.UserName);
        Assert.Equal(1, _disposed);
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-01.3")]
    public async Task Sql_login_goes_only_through_secret_ref_and_gets_no_windows_script()
    {
        _sql.Databases.Add(Row("Firma"));
        _sql.Perms["Firma"] = (false, false);
        var o = await Service().DiscoverAsync(new DatabaseDiscoveryRequest("srv01,1433", "sql", "file:erp-sql"), _ct);
        Assert.Null(o.Code);
        Assert.Null(o.PermissionScript);
        var (opts, secretRef) = _opened.Single();
        Assert.Equal("file:erp-sql", secretRef);
        Assert.Equal(MsSqlAuthentication.SqlLogin, opts.Authentication);
        Assert.Null(opts.UserName); // login rozwiązuje fabryka z magazynu sekretów
    }

    [Theory, Trait("BL", "BL-40")]
    [InlineData(18456, "KB-3002")]
    [InlineData(-2, "KB-3001")]
    public async Task Server_errors_become_kb_codes_and_session_is_disposed(int error, string code)
    {
        _sql = new MsSqlDiscoveryTests.ScriptedSql { ServerError = error };
        var o = await Service().DiscoverAsync(new DatabaseDiscoveryRequest("srv01"), _ct);
        Assert.Equal(code, o.Code);
        Assert.Equal(1, _disposed);
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Factory_failure_is_reported_with_its_code()
    {
        var s = new DatabaseDiscoveryService(_certs, (_, _, _) => throw new KobianException(KbCode.SqlLoginFailed, ErrorClass.Permanent, "x"));
        var o = await s.DiscoverAsync(new DatabaseDiscoveryRequest("srv01", "sql", "file:missing"), _ct);
        Assert.Equal(KbCode.SqlLoginFailed, o.Code);
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Backend_maps_reply_and_reports_warning_state()
    {
        _sql = new MsSqlDiscoveryTests.ScriptedSql { Major = 13 };
        _sql.Databases.Add(Row("Firma", encrypted: true));
        var backend = new CatalogAgentBackend(Path.Combine(_certs, "none.db"), "1.0.0") { Discovery = Service() };

        var r = await backend.DiscoverDatabasesAsync(new DiscoverDatabasesArgs { Server = "srv01" }, _ct);

        Assert.Equal(OverallState.Warning, r.Result);
        Assert.False(r.HasCode);
        Assert.Equal("conditional", r.Support);
        Assert.Equal([KbCode.SqlVersionUnsupported], r.Warnings);
        var db = Assert.Single(r.Databases);
        Assert.Equal("Firma", db.Name);
        Assert.True(db.Encrypted);
        Assert.Contains(KbCode.SqlTdeCertificateMissing, db.Warnings);
        Assert.False(r.HasPermissionScript);
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Backend_without_discovery_and_invalid_request_report_codes()
    {
        var none = new CatalogAgentBackend(Path.Combine(_certs, "none.db"), "1.0.0");
        Assert.Equal(KbCode.OperationNotAvailable, (await none.DiscoverDatabasesAsync(new DiscoverDatabasesArgs { Server = "s" }, _ct)).Code);
        var backend = new CatalogAgentBackend(Path.Combine(_certs, "none.db"), "1.0.0") { Discovery = Service() };
        var r = await backend.DiscoverDatabasesAsync(new DiscoverDatabasesArgs { Server = "s", Auth = "sql" }, _ct);
        Assert.Equal(OverallState.Failed, r.Result);
        Assert.Equal(KbCode.JobDefinitionInvalid, r.Code);
        Assert.Empty(r.Databases);
    }

    [Fact, Trait("BL", "BL-40"), Trait("D", "D2.9")]
    public void Discovery_requires_operator_and_is_not_modifying()
    {
        var e = MethodPolicy.Find("/kobian.v1.Agent/DiscoverDatabases")!;
        Assert.Equal(AgentRole.Operator, e.Required);
        Assert.False(e.Modifying);
    }

    private static Dictionary<string, object?> Row(string name, bool encrypted = false) => new(StringComparer.Ordinal)
    {
        ["name"] = name, ["recovery_model_desc"] = "SIMPLE", ["is_encrypted"] = encrypted, ["data_bytes"] = 4096L, ["compatibility_level"] = 150,
    };

    private sealed class Disposable(IMsSqlSession inner, Action onDispose) : IMsSqlSession, IAsyncDisposable
    {
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(SqlCommandSpec command, CancellationToken cancellationToken) =>
            inner.QueryAsync(command, cancellationToken);

        public Task ExecuteAsync(SqlCommandSpec command, CancellationToken cancellationToken) => inner.ExecuteAsync(command, cancellationToken);

        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
