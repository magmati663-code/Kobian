using System.Security.Cryptography;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql.SqlClient;
using Kobian.Providers.Storage.Local;
using Kobian.UI.Services;
using Kobian.UI.Wizard;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-40 cz. 4 (it. 73): zadanie „database” z kreatora S1-db przez pipe — walidacja pola „database” (ta sama co discovery: KB-0103, pin w &lt;stan&gt;/certs),
/// InitRepository → job_jobs.type = 'database' z sekcją „database” parsowaną kodem uruchomienia (ADR-0018), staging Agenta PROWIZORYCZNIE Qb31.
/// Bez połączenia z SQL Server (S5c = nie): walidacja nie łączy się z serwerem. Dane syntetyczne, hasło losowe w teście.
/// </summary>
public sealed class WizardDatabaseOverPipeTests : IAsyncLifetime
{
    private const string SqlStaging = @"C:\ProgramData\Kobian\staging";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-wizdb-" + Guid.NewGuid().ToString("N"));
    private readonly string _pw = "Kp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
    private readonly List<WebApplication> _hosts = [];
    private CatalogWriter _writer = null!;
    private string P(string n) => Path.Combine(_dir, n);
    private string Catalog => P("catalog.db");

    private static Dictionary<string, IStorageProviderFactory> Providers => new() { ["local"] = new LocalStorageProviderFactory() };

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("repo"));
        Directory.CreateDirectory(P("repo2"));
        Directory.CreateDirectory(P("state/certs"));
        File.WriteAllBytes(P("state/certs/srv01.cer"), [0x30, 0x82]); // tylko istnienie pliku (pin weryfikuje SqlClient przy połączeniu — W-26)
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private async Task<string> StartAgent(bool configureStaging = true, bool databases = true)
    {
        var pipe = "kobian-test-" + Guid.NewGuid().ToString("N");
        var init = new RepoInitService(Catalog, _writer, Providers, P("state"), TimeProvider.System)
        {
            DatabaseStagingRoot = configureStaging ? P("staging") : null,
            DatabaseSqlStagingRoot = configureStaging ? SqlStaging : null,
        };
        var probe = new DestinationProbeService(Providers);
        // Sesja SQL nigdy nie powinna powstać: walidacja i InitRepository nie łączą się z serwerem.
        var discovery = new DatabaseDiscoveryService(P("state/certs"), (_, _, _) => throw new InvalidOperationException("no SQL connection expected"));
        var backend = new CatalogAgentBackend(Catalog, "9.9.9", null, init, schedules: new ScheduleService(Catalog, _writer), probe: probe,
            validator: new JobDraftValidator(Catalog, probe, TimeProvider.System, databases ? discovery : null));
        var app = AgentHost.Create(new AgentHostOptions(pipe), backend, new FixedRoleResolver(AgentRole.Operator));
        _hosts.Add(app);
        await app.StartAsync(_ct);
        return pipe;
    }

    private WizardDraft Draft(DatabaseSource? db = null, string dest = "repo") =>
        new("ERP", [], DestinationKind.Local, P(dest), null, 22, null, null, "0 22 * * *", 7, 4, 12,
            db ?? new DatabaseSource(@"SRV01\SQLEXPRESS", true, "file:erp-sql", "srv01.cer", "Firma"));

    [Fact, Trait("BL", "BL-40"), Trait("Plan", "R2.5.2"), Trait("AC", "AC-01.3"), Trait("Category", "Integration")]
    public async Task Database_job_from_wizard_is_validated_and_created_with_database_section()
    {
        var pipe = await StartAgent();
        using var source = new GrpcDashboardSource(pipe);
        var svc = source.CreateWizardService();
        Assert.True(svc.SupportsDatabases);

        Assert.Empty(await svc.ValidateStepAsync(WizardState.S1Sources, Draft(), _ct));
        var bad = new[]
        {
            new DatabaseSource("np:SRV01", false, null, null, "Firma"),                    // protokół w nazwie serwera
            new DatabaseSource("SRV01;Trust Server Certificate=true", false, null, null, "Firma"), // wstrzyknięcie do connection stringa
            new DatabaseSource("SRV01", true, null, null, "Firma"),                         // login SQL bez odwołania do sekretu
            new DatabaseSource("SRV01", false, "file:x", null, "Firma"),                    // Windows + sekret = niespójne
            new DatabaseSource("SRV01", false, null, "../../etc/passwd.cer", "Firma"),       // pin poza katalogiem certs
            new DatabaseSource("SRV01", false, null, "brak.cer", "Firma"),                   // pin, którego nie zatwierdzono
            new DatabaseSource("SRV01", false, null, null, ""),                              // pusta nazwa bazy
            new DatabaseSource("SRV01", false, null, null, "Fir\u0001ma"),                   // znak sterujący w nazwie
        };
        foreach (var b in bad)
            Assert.Equal([new StepIssue("database", KbCode.JobDefinitionInvalid)], await svc.ValidateStepAsync(WizardState.S1Sources, Draft(b), _ct));
        // „sources” obok „database” = definicja niejednoznaczna
        Assert.Equal([new StepIssue("sources", KbCode.JobDefinitionInvalid)], await svc.ValidateStepAsync(WizardState.S1Sources, Draft() with { Sources = [P("repo")] }, _ct));

        var repo = await svc.CreateRepositoryAsync(Draft(), _pw, _ct);
        Assert.Empty(repo.Issues);
        Assert.True(File.Exists(P("repo/kobian.repo")));
        var (type, def) = JobRow("ERP");
        Assert.Equal("database", type);
        Assert.Equal(JobDefinition.MsSqlSourceMarker, def.Source);
        var mssql = Assert.IsType<MsSqlSourceDefinition>(def.MsSql);
        Assert.Equal("Firma", mssql.Database);
        Assert.Equal(@"SRV01\SQLEXPRESS", mssql.Connection.Server);
        Assert.Equal(MsSqlAuthentication.SqlLogin, mssql.Connection.Authentication);
        Assert.Equal("file:erp-sql", mssql.SecretRef);
        Assert.Equal(Path.Combine(Path.GetFullPath(P("state/certs")), "srv01.cer"), mssql.Connection.ServerCertificatePath);
        Assert.Equal((P("staging"), SqlStaging), (mssql.StagingRoot, mssql.SqlStagingRoot));
        Assert.Equal(new Kobian.Application.Jobs.RetentionPolicy(KeepDaily: 7, KeepWeekly: 4, KeepMonthly: 12), def.Retention);
        Assert.Null(await svc.SaveAsync(Draft(), _ct)); // włączenie + harmonogram jak dla plików
        Assert.False(Assert.Single(CatalogStatusReader.ReadStatus(Catalog, "x", DateTimeOffset.UtcNow).Jobs).Disabled);
    }

    [Fact, Trait("BL", "BL-40"), Trait("Plan", "D5.10"), Trait("Category", "Integration")]
    public async Task Unusable_staging_or_agent_without_databases_refuses_before_touching_destination()
    {
        // Agent bez skonfigurowanego stagingu: domyślny <stan>/staging jest poprawny dla SQL Server na tym samym hoście — Windows („C:\…”) i Linux („/…”, SQL Server
        // na Linuksie lub w kontenerze z tym samym katalogiem; zweryfikowane na SQL Server 2022).
        var pipe = await StartAgent(configureStaging: false);
        using (var source = new GrpcDashboardSource(pipe))
        {
            var r = await source.CreateWizardService().CreateRepositoryAsync(Draft(), _pw, _ct);
            Assert.Empty(r.Issues);
        }

        var old = await StartAgent(databases: false);
        using var src2 = new GrpcDashboardSource(old);
        var svc = src2.CreateWizardService();
        // Zadanie „ERP” już istnieje (utworzone wyżej) — obok KB-7012 może wrócić „nazwa zajęta”; tu liczy się odmowa części „database”.
        Assert.Contains(new StepIssue("database", KbCode.OperationNotAvailable), await svc.ValidateStepAsync(WizardState.S1Sources, Draft(dest: "repo2"), _ct));
        var refused = await svc.CreateRepositoryAsync(Draft(dest: "repo2"), _pw, _ct);
        Assert.Contains(new StepIssue("database", KbCode.OperationNotAvailable), refused.Issues);
        Assert.Empty(Directory.EnumerateFileSystemEntries(P("repo2")));
    }

    private (string Type, JobDefinition Def) JobRow(string name)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT type, definition FROM job_jobs WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        using var rd = cmd.ExecuteReader();
        Assert.True(rd.Read());
        var type = rd.GetString(0);
        return (type, JobDefinition.Parse(type, (byte[])rd[1]));
    }
}
