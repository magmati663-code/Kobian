using System.Text;
using Kobian.Agent.Hosting;
using Kobian.Api.V1;
using Kobian.Catalog.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>SCR-09 (it. 82): cele z definicji zadań — grupowanie po schemacie i korzeniu, id jak w katalogu repozytoriów, nieczytelne definicje liczone.</summary>
public sealed class ListDestinationsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-dest-" + Guid.NewGuid().ToString("N"));
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.GetFullPath(Path.Combine(_dir, n));

    public ListDestinationsTests()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private void AddJob(string id, string name, string def)
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ($i,$n,'files',$d,x'00',1,'UTC','skip','2026-10-14T00:00:00Z','2026-10-14T00:00:00Z');";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$d", Encoding.UTF8.GetBytes(def));
        cmd.ExecuteNonQuery();
    }

    private string Def(string src, string repo) =>
        "{\"version\":1,\"sources\":[" + J(P(src)) + "],\"destination\":{\"scheme\":\"local\",\"root\":" + J(P(repo)) + "},\"key\":{\"file\":" + J(P("k.key")) + ",\"passwordFile\":null,\"repoHeader\":null}}";

    [Fact, Trait("Plan", "R2.6.1")]
    public async Task Destinations_are_grouped_from_job_definitions_and_bad_definitions_counted()
    {
        AddJob("j1", "Zdjęcia", Def("a", "repoA"));
        AddJob("j2", "Dokumenty", Def("b", "repoA"));
        AddJob("j3", "Poczta", Def("c", "repoB"));
        AddJob("j4", "Zepsute", "{\"version\":2}");
        var r = await new CatalogAgentBackend(Catalog, "t").ListDestinationsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, r.UnreadableJobs);
        Assert.Equal(2, r.Destinations.Count);
        var a = r.Destinations.Single(d => d.Root == P("repoA"));
        Assert.Equal(["Dokumenty", "Zdjęcia"], a.Jobs);
        Assert.Equal("local", a.Scheme);
        Assert.StartsWith("dest-", a.Id, StringComparison.Ordinal);
        Assert.Equal(21, a.Id.Length);
        Assert.NotEqual(a.Id, r.Destinations.Single(d => d.Root == P("repoB")).Id);
        var p = MethodPolicy.Find("/kobian.v1.Agent/ListDestinations");
        Assert.Equal((AgentRole.Operator, false), (p!.Required, p.Modifying));
    }
}
