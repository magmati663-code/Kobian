using Kobian.Agent.Hosting;
using Kobian.Api.Contracts;
using Kobian.Catalog.Sqlite;
using Kobian.Cli.Commands;
using Kobian.UI.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>Runda 15: restart usługi nie wymaga restartu klienta — CLI i aplikacja (ten sam obiekt) wracają do pracy po powrocie usługi.</summary>
public sealed class AgentRestartReconnectTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-rst-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _hosts = [];
    private string CatalogPath => Path.Combine(_dir, "catalog.db");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        CatalogBootstrap.Bootstrap(CatalogPath, TimeProvider.System);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private async Task<WebApplication> StartAsync(string pipe)
    {
        var app = AgentHost.Create(new AgentHostOptions(pipe, ApiConventions.CurrentRevision), new CatalogAgentBackend(CatalogPath, "9.9.9"), new FixedRoleResolver(AgentRole.Operator));
        _hosts.Add(app);
        await app.StartAsync(_ct);
        return app;
    }

    private async Task StopAsync(WebApplication app)
    {
        await app.StopAsync(_ct);
        await app.DisposeAsync();
        _hosts.Remove(app);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Cli_client_recovers_after_the_service_restarts_without_being_recreated()
    {
        var pipe = "kobian-rst-" + Guid.NewGuid().ToString("N");
        var first = await StartAsync(pipe);
        using var client = new GrpcAgentClient(pipe, TimeSpan.FromSeconds(2));
        Assert.Equal("9.9.9", (await client.GetStatusAsync(_ct)).AgentVersion);

        await StopAsync(first);
        await Assert.ThrowsAsync<AgentUnavailableException>(() => client.GetStatusAsync(_ct)); // usługa wyłączona: czytelny błąd, nie zawieszenie

        await StartAsync(pipe);
        Assert.Equal("9.9.9", (await client.GetStatusAsync(_ct)).AgentVersion); // ten sam klient, nowa usługa
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Ui_source_goes_offline_with_service_down_and_comes_back_by_itself()
    {
        var pipe = "kobian-rst-" + Guid.NewGuid().ToString("N");
        var first = await StartAsync(pipe);
        using var source = new GrpcDashboardSource(pipe);
        Assert.NotNull(await source.LoadAsync(_ct));

        await StopAsync(first);
        await Assert.ThrowsAsync<IOException>(() => source.LoadAsync(_ct)); // „Usługa Kobian nie działa” (KB-9001) w pulpicie

        await StartAsync(pipe);
        Assert.NotNull(await source.LoadAsync(_ct)); // pulpit odświeża się bez restartu aplikacji
    }
}
