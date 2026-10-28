using Kobian.Agent.Jobs;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql;
using Kobian.Providers.Sources.MsSql.Tls;
using Xunit;

namespace Kobian.Tests.Unit.Sources;

/// <summary>RD-39 (Qb35, ADR-0023): lista dozwolonych serwerów SQL — parsowanie, dopasowanie i egzekwowanie przed połączeniem (discovery, certyfikat).</summary>
public sealed class SqlServerAllowListTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory, Trait("SEC", "RD-39")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_configuration_allows_only_this_computer(string? value)
    {
        var a = SqlServerAllowList.Parse(value);
        foreach (var ok in new[] { "localhost", ".", "LOCALHOST\\SQLEXPRESS", "127.0.0.1,1433", "[::1]", ".\\SQLEXPRESS" }) Assert.True(a.IsAllowed(ok), ok);
        foreach (var bad in new[] { "srv01", "attacker.example", "10.0.0.5", "localhost.evil", "" }) Assert.False(a.IsAllowed(bad), bad);
    }

    [Fact, Trait("SEC", "RD-39")]
    public void Entries_match_host_instance_and_port_without_dns()
    {
        var a = SqlServerAllowList.Parse("srv01; sql02\\ERP , sql03:1500");
        Assert.Equal(3, a.Count);
        Assert.True(a.IsAllowed("SRV01"));
        Assert.True(a.IsAllowed("srv01\\ANY"));
        Assert.True(a.IsAllowed("srv01,1444"));
        Assert.False(a.IsAllowed("srv01.firma.local")); // bez DNS — nazwa jak w zadaniu
        Assert.True(a.IsAllowed("sql02\\erp"));
        Assert.False(a.IsAllowed("sql02"));
        Assert.False(a.IsAllowed("sql02\\OTHER"));
        Assert.True(a.IsAllowed("sql03,1500"));
        Assert.False(a.IsAllowed("sql03"));      // domyślny port 1433 ≠ 1500
        Assert.False(a.IsAllowed("sql03\\X"));   // port instancji nieznany przed SSRP — nie dopasowuje wpisu z portem
        Assert.False(a.IsAllowed("localhost"));  // jawna lista zastępuje domyślne „tylko lokalnie”
        Assert.False(a.IsAllowed("np:srv01"));   // niepoprawny adres = odmowa
        Assert.True(SqlServerAllowList.Parse("srv01;*").AllowsAny);
    }

    [Fact, Trait("SEC", "RD-39")]
    public void Invalid_entry_fails_at_startup_and_demand_has_no_server_name()
    {
        Assert.Equal(KbCode.JobDefinitionInvalid, Assert.Throws<KobianException>(() => SqlServerAllowList.Parse("np:srv01")).Code);
        Assert.Equal(KbCode.JobDefinitionInvalid, Assert.Throws<KobianException>(() => SqlServerAllowList.Parse("a;b c")).Code);
        var e = Assert.Throws<KobianException>(() => SqlServerAllowList.LocalOnly.Demand("tajny-serwer"));
        Assert.Equal((KbCode.SqlServerNotAllowed, ErrorClass.Security), (e.Code, e.ErrorClass));
        Assert.DoesNotContain("tajny", e.Message, StringComparison.Ordinal);
    }

    private sealed class CountingFetcher : ISqlServerCertificateFetcher
    {
        public int Calls { get; private set; }

        public Task<SqlServerCertificateInfo> FetchAsync(string server, bool strict, CancellationToken ct)
        {
            Calls++;
            throw new KobianException(KbCode.SqlUnreachable, ErrorClass.Transient, "x");
        }
    }

    [Fact, Trait("SEC", "RD-39")]
    public async Task Discovery_validation_and_certificate_service_refuse_before_connecting()
    {
        var certs = Path.Combine(Path.GetTempPath(), "kb-allow-" + Guid.NewGuid().ToString("N"));
        var opened = 0;
        var discovery = new DatabaseDiscoveryService(certs, (_, _, _) =>
        {
            opened++;
            throw new InvalidOperationException("nie powinno się połączyć");
        }) { AllowedServers = SqlServerAllowList.LocalOnly };
        var o = await discovery.DiscoverAsync(new DatabaseDiscoveryRequest("srv01", AllowLocalhostUntrusted: false), _ct);
        Assert.Equal(KbCode.SqlServerNotAllowed, o.Code);
        Assert.Equal(0, opened);
        Assert.Equal(KbCode.SqlServerNotAllowed, discovery.Validate(new DatabaseDiscoveryRequest("10.1.2.3,1433")).Code); // ta sama ścieżka co UpsertJob/InitRepository
        Assert.Null(discovery.Validate(new DatabaseDiscoveryRequest("localhost\\SQLEXPRESS", AllowLocalhostUntrusted: true)).Code);

        var fetcher = new CountingFetcher();
        var certSvc = new ServerCertificateService(certs, fetcher) { AllowedServers = SqlServerAllowList.LocalOnly };
        Assert.Equal(KbCode.SqlServerNotAllowed, (await certSvc.FetchAsync("srv01", false, _ct)).Code);
        Assert.Equal(KbCode.SqlServerNotAllowed, (await certSvc.ApproveAsync("srv01", false, new string('a', 64), "srv01.cer", _ct)).Code);
        Assert.Equal(0, fetcher.Calls);
        Assert.Equal(KbCode.SqlUnreachable, (await certSvc.FetchAsync("localhost", false, _ct)).Code); // dozwolony → dopiero wtedy sieć
        Assert.Equal(1, fetcher.Calls);
        Assert.False(Directory.Exists(certs));
    }
}
