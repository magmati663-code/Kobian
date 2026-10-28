using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kobian.Agent.Jobs;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql.Tls;
using Kobian.Tests.Unit.Sources;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-40 cz. 5 (Qb32): zatwierdzanie certyfikatu serwera SQL w Agencie — ponowne pobranie i stałoczasowe porównanie odcisku (KB-3007),
/// zapis DER w &lt;stan&gt;/certs bez nadpisywania innej treści (KB-3008), walidacja nazwy/odcisku przed połączeniem, pin używalny w discovery.
/// </summary>
public sealed class ServerCertificateServiceTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _state = Path.Combine(Path.GetTempPath(), "kobian-cert-" + Guid.NewGuid().ToString("N"));
    private readonly X509Certificate2 _a = SqlCertificateFetcherTests.FakeTdsTlsServer.CreateCertificate("sql-a");
    private readonly X509Certificate2 _b = SqlCertificateFetcherTests.FakeTdsTlsServer.CreateCertificate("sql-b");
    private readonly ScriptedFetcher _fetcher = new();

    private string Certs => Path.Combine(_state, "certs");

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        if (Directory.Exists(_state)) Directory.Delete(_state, recursive: true);
    }

    private ServerCertificateService Service() => new(Certs, _fetcher);

    private static string Sha(X509Certificate2 c) => Convert.ToHexString(SHA256.HashData(c.RawData));

    [Fact, Trait("BL", "BL-40")]
    public async Task Fetch_returns_fingerprint_and_suggested_name_without_writing()
    {
        _fetcher.Next = _a;
        var o = await Service().FetchAsync("sql01.firma.local\\ERP", false, _ct);
        Assert.Null(o.Code);
        Assert.Equal(Sha(_a), o.Certificate!.Sha256Hex);
        Assert.Equal("sql01-firma-local-ERP-" + Sha(_a)[..8].ToLowerInvariant() + ".cer", o.SuggestedName);
        Assert.True(DatabaseDiscoveryService.IsValidCertificateName(o.SuggestedName));
        Assert.False(Directory.Exists(Certs));
    }

    [Fact, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.6")]
    public async Task Approve_writes_der_pin_usable_by_discovery()
    {
        _fetcher.Next = _a;
        var r = await Service().ApproveAsync("sql01", false, Sha(_a), "sql01.cer", _ct);
        Assert.Null(r.Code);
        Assert.Equal("sql01.cer", r.FileName);
        Assert.Equal(_a.RawData, await File.ReadAllBytesAsync(Path.Combine(Certs, "sql01.cer"), _ct));
        Assert.Empty(Directory.GetFiles(Certs, "*.tmp", SearchOption.AllDirectories).Concat(Directory.GetFiles(Certs, ".*")));

        var discovery = new DatabaseDiscoveryService(Certs, (_, _, _) => throw new InvalidOperationException());
        var (code, options) = discovery.Validate(new DatabaseDiscoveryRequest("sql01", Certificate: "sql01.cer"));
        Assert.Null(code);
        Assert.Equal(Path.Combine(Path.GetFullPath(Certs), "sql01.cer"), options!.ServerCertificatePath);
    }

    [Fact, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.6")]
    public async Task Changed_certificate_between_fetch_and_approve_is_KB3007_and_nothing_written()
    {
        _fetcher.Next = _b; // serwer (albo MITM) przedstawia inny certyfikat niż ten, który Operator widział
        var r = await Service().ApproveAsync("sql01", false, Sha(_a), "sql01.cer", _ct);
        Assert.Equal(KbCode.SqlCertificateChanged, r.Code);
        Assert.False(File.Exists(Path.Combine(Certs, "sql01.cer")));
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Same_name_is_idempotent_for_same_certificate_and_KB3008_for_different()
    {
        _fetcher.Next = _a;
        Assert.Null((await Service().ApproveAsync("sql01", false, Sha(_a), "sql01.cer", _ct)).Code);
        Assert.Null((await Service().ApproveAsync("sql01", false, Sha(_a).ToLowerInvariant(), "sql01.cer", _ct)).Code);

        _fetcher.Next = _b;
        var r = await Service().ApproveAsync("sql01", false, Sha(_b), "sql01.cer", _ct);
        Assert.Equal(KbCode.SqlCertificateNameTaken, r.Code);
        Assert.Equal(_a.RawData, await File.ReadAllBytesAsync(Path.Combine(Certs, "sql01.cer"), _ct)); // stary pin nietknięty
    }

    [Theory, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.6")]
    [InlineData("srv", "../x.cer", true)]
    [InlineData("srv", "sub/x.cer", true)]
    [InlineData("srv", "x.pem", true)]
    [InlineData("srv", "", true)]
    [InlineData("srv", "x.cer", false)]
    [InlineData("np:srv", "x.cer", true)]
    public async Task Invalid_input_is_refused_before_connecting(string server, string name, bool goodSha)
    {
        _fetcher.Next = _a;
        var sha = goodSha ? Sha(_a) : "ABC";
        var r = await Service().ApproveAsync(server, false, sha, name, _ct);
        Assert.Equal(KbCode.JobDefinitionInvalid, r.Code);
        Assert.Equal(0, _fetcher.Calls);
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Fetch_errors_map_to_codes()
    {
        _fetcher.Error = new KobianException(KbCode.SqlTlsUnavailable, ErrorClass.Security, "x");
        Assert.Equal(KbCode.SqlTlsUnavailable, (await Service().FetchAsync("srv", false, _ct)).Code);
        Assert.Equal(KbCode.SqlTlsUnavailable, (await Service().ApproveAsync("srv", false, Sha(_a), "x.cer", _ct)).Code);
    }

    [Fact, Trait("BL", "BL-40")]
    public void Sha_parsing_accepts_colons_and_rejects_wrong_length()
    {
        var hex = Sha(_a);
        Assert.True(ServerCertificateService.TryParseSha256(string.Join(':', Enumerable.Range(0, 32).Select(i => hex.Substring(i * 2, 2))), out var v));
        Assert.Equal(SHA256.HashData(_a.RawData), v);
        Assert.False(ServerCertificateService.TryParseSha256(hex[..62], out _));
        Assert.False(ServerCertificateService.TryParseSha256(new string('Z', 64), out _));
        Assert.False(ServerCertificateService.TryParseSha256(null, out _));
    }

    private sealed class ScriptedFetcher : ISqlServerCertificateFetcher
    {
        public X509Certificate2? Next { get; set; }
        public KobianException? Error { get; set; }
        public int Calls { get; private set; }

        public Task<SqlServerCertificateInfo> FetchAsync(string server, bool strict, CancellationToken ct)
        {
            Calls++;
            if (Error is not null) throw Error;
            return Task.FromResult(SqlServerCertificateInfo.From(Next!, SqlEndpoint.Parse(server).Host));
        }
    }
}
