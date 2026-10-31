using Xunit;

namespace Kobian.Tests.Unit.Security;

/// <summary>
/// Strażnik poziomu poprawek środowiska uruchomieniowego (it. 71): .NET 10.0.13 (2026-10-13) naprawia m.in. krytyczne
/// CVE-2026-70188 / CVE-2026-88092 w ASP.NET Core (Kestrel Agenta) i DoS w System.Security.Cryptography.Pkcs.
/// Testy (i CI) na starszym runtime = porażka — podnieś SDK w global.json i budowa/narzedzia.md.
/// Na maszynie docelowej minimalny runtime egzekwuje instalator (BL MSI, kolejka W-25).
/// </summary>
public sealed class RuntimePatchLevelTests
{
    public static readonly Version MinimumRuntime = Kobian.Agent.Hosting.RuntimeGuard.MinimumRuntime;

    [Fact, Trait("SEC", "SEC-DEP")]
    public void Tests_run_on_patched_runtime() =>
        Assert.True(Environment.Version >= MinimumRuntime, $"runtime {Environment.Version} < {MinimumRuntime}");

    [Fact, Trait("SEC", "SEC-DEP")]
    public void Aspnetcore_shared_framework_is_patched()
    {
        // Kestrel/gRPC Agenta: wersja zestawu Microsoft.AspNetCore.Server.Kestrel.Core z katalogu współdzielonego.
        var dir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;           // .../shared/Microsoft.NETCore.App/<v>
        var shared = Path.GetDirectoryName(Path.GetDirectoryName(dir))!;               // .../shared
        var asp = Path.Combine(shared, "Microsoft.AspNetCore.App");
        Assert.True(Directory.Exists(asp), "brak Microsoft.AspNetCore.App obok runtime");
        var versions = Directory.GetDirectories(asp).Select(Path.GetFileName)
            .Select(v => Version.TryParse(v, out var x) ? x : null).Where(v => v is not null).ToList();
        Assert.Contains(versions, v => v! >= MinimumRuntime);
    }

    [Theory, Trait("SEC", "SEC-DEP"), Trait("BL", "BL-62")]
    [InlineData("10.0.12", new[] { "10.0.13" }, "KB-7013")]
    [InlineData("10.0.13", new[] { "10.0.12" }, "KB-7013")]
    [InlineData("10.0.13", new[] { "10.0.12", "10.0.13" }, null)]
    [InlineData("10.0.14", new string[0], null)]
    [InlineData("10.0.13", new[] { "9.0.20" }, null)]
    public void Runtime_guard_flags_unpatched_runtime_or_aspnetcore(string runtime, string[] asp, string? expected) =>
        Assert.Equal(expected, Kobian.Agent.Hosting.RuntimeGuard.Evaluate(Version.Parse(runtime), asp.Select(Version.Parse)));

    [Fact, Trait("BL", "BL-62")]
    public async Task Status_reports_runtime_warning_without_stopping_anything()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kobian-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var catalog = Path.Combine(dir, "c.db");
        Kobian.Catalog.Sqlite.CatalogBootstrap.Bootstrap(catalog, TimeProvider.System);
        var ok = await new Kobian.Agent.Hosting.CatalogAgentBackend(catalog, "1").GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.False(ok.HasRuntimeCode);
        var st = await new Kobian.Agent.Hosting.CatalogAgentBackend(catalog, "1") { RuntimeCode = "KB-7013" }.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("KB-7013", Kobian.Api.V1.OverallState.Warning), (st.RuntimeCode, st.State));
        Assert.Equal(Environment.Version.ToString(), st.RuntimeVersion);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    [Fact, Trait("BL", "BL-62")]
    public void Current_test_process_is_not_flagged() => Assert.Null(Kobian.Agent.Hosting.RuntimeGuard.EvaluateCurrent());
}
