using System.Security.Cryptography;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.V1;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Providers.Storage.Local;
using Kobian.UI.Services;
using Kobian.UI.Wizard;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-61/BL-60 (it. 63): kreator SCR-03 na prawdziwym Agencie przez pipe — adapter UI <see cref="GrpcWizardService"/> → walidacja kroków, probe,
/// InitRepository z kluczem odzyskiwania, zapis (włączenie + harmonogram), pierwsza kopia. Dane syntetyczne, hasło losowe w teście.
/// </summary>
public sealed class WizardOverPipeTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-wizpipe-" + Guid.NewGuid().ToString("N"));
    private readonly string _pw = "Kp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
    private readonly List<WebApplication> _hosts = [];
    private CatalogWriter _writer = null!;
    private readonly Kobian.Tests.Unit.Sources.MsSqlDiscoveryTests.ScriptedSql _sql = new();
    private string P(string n) => Path.Combine(_dir, n);
    private string Catalog => P("catalog.db");

    private static Dictionary<string, IStorageProviderFactory> Providers => new() { ["local"] = new LocalStorageProviderFactory() };

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), RandomNumberGenerator.GetBytes(200_000));
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

    private async Task<string> StartAgent(AgentRole role = AgentRole.Operator)
    {
        var pipe = "kobian-test-" + Guid.NewGuid().ToString("N");
        var jobs = new CatalogJobService(Catalog, _writer, Providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        var init = new RepoInitService(Catalog, _writer, Providers, P("state"), TimeProvider.System);
        var probe = new DestinationProbeService(Providers);
        var backend = new CatalogAgentBackend(Catalog, "9.9.9", jobs, init, schedules: new ScheduleService(Catalog, _writer), probe: probe,
            validator: new JobDraftValidator(Catalog, probe, TimeProvider.System), retention: new CatalogRetentionService(Catalog, _writer, TimeProvider.System))
        {
            Discovery = new DatabaseDiscoveryService(P("certs"), (_, _, _) => ValueTask.FromResult<Kobian.Providers.Sources.MsSql.IMsSqlSession>(_sql)),
            SqlCertificates = new ServerCertificateService(P("certs"), new Kobian.Providers.Sources.MsSql.Tls.TdsCertificateFetcher()),
        };
        var app = AgentHost.Create(new AgentHostOptions(pipe), backend, new FixedRoleResolver(role));
        _hosts.Add(app);
        await app.StartAsync(_ct);
        return pipe;
    }

    private WizardDraft Draft(string name = "Dokumenty", string? dest = null) =>
        new(name, [P("src")], DestinationKind.Local, dest ?? P("repo"), null, 22, null, null, "0 21 * * *", 7, 4, 12);

    [Fact, Trait("BL", "BL-61"), Trait("BL", "BL-60"), Trait("AC", "AC-28.1"), Trait("Category", "Integration")]
    public async Task Wizard_creates_repository_with_recovery_key_saves_and_runs_first_backup()
    {
        var pipe = await StartAgent();
        using var source = new GrpcDashboardSource(pipe);
        var svc = source.CreateWizardService();

        Assert.Empty(await svc.ValidateStepAsync(WizardState.S1Sources, Draft(), _ct));
        Assert.Equal([new StepIssue("sources", KbCode.SourcesNotReadable)],
            await svc.ValidateStepAsync(WizardState.S1Sources, Draft() with { Sources = [P("nie-ma")] }, _ct));
        Assert.Empty(await svc.ValidateStepAsync(WizardState.S1Sources, Draft() with { DestinationPath = "wzgledna" }, _ct)); // pola innych kroków odfiltrowane
        Assert.Equal([new StepIssue("destination", KbCode.SourcesOverlap)],
            await svc.ValidateStepAsync(WizardState.S2Destination, Draft(dest: Path.Combine(P("src"), "kopie")), _ct));
        Assert.Equal(ProbeOutcome.Ok, (await svc.ProbeAsync(Draft(), _ct)).Outcome);
        Assert.Equal(new ProbeResult(ProbeOutcome.PermanentError, KbCode.DestinationNotFound), await svc.ProbeAsync(Draft(dest: P("brak")), _ct));

        var weak = await svc.CreateRepositoryAsync(Draft(), "krotkie", _ct);
        Assert.Equal([new StepIssue("password", KbCode.PasswordWeak)], weak.Issues);
        Assert.False(File.Exists(P("repo/kobian.repo")));

        var repo = await svc.CreateRepositoryAsync(Draft(), _pw, _ct);
        Assert.Empty(repo.Issues);
        Assert.NotNull(repo.RecoveryKey);
        Assert.Equal(RecoveryKey.GroupCount, RecoveryKeyText.Groups(repo.RecoveryKey).Count);

        // Klucz odzyskiwania otwiera to samo repozytorium co hasło (drugi klucz na uwierzytelnionej liście nagłówka).
        var header = File.ReadAllText(P("repo/kobian.repo"));
        var rk = new byte[RecoveryKey.KeyLength];
        Assert.True(RecoveryKey.TryDecode(repo.RecoveryKey, rk));
        var keyFiles = Directory.GetFiles(P("repo/keys"));
        Assert.Equal(2, keyFiles.Length);
        var recoveryFile = keyFiles.Single(f => File.ReadAllText(f).Contains("\"recovery\"", StringComparison.Ordinal));
        var mk = KeyFiles.UnwrapWithRecoveryKey(File.ReadAllText(recoveryFile), rk);
        using (var keys = RepoKeys.Derive(mk, RepoHeader.PeekRepoId(header)))
            Assert.Equal(2, RepoHeader.Verify(header, keys).KeyIds.Count);

        // Sekrety: hasło tylko w katalogu stanu Agenta (0600), nie na celu; klucz odzyskiwania nigdzie na dysku.
        var pwFile = Path.GetFullPath(Assert.Single(Directory.GetFiles(P("state/secrets"))));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(pwFile));
        foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".db", StringComparison.Ordinal) && !f.Contains("catalog.db", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain(repo.RecoveryKey, text, StringComparison.Ordinal);
            if (Path.GetFullPath(f) != pwFile) Assert.DoesNotContain(_pw, text, StringComparison.Ordinal);
        }

        // Retencja z S5 trafiła do definicji zadania (BL-25): 7 dziennych / 4 tygodniowe / 12 miesięcznych.
        Assert.Equal(new Kobian.Application.Jobs.RetentionPolicy(KeepDaily: 7, KeepWeekly: 4, KeepMonthly: 12), JobDefinitionOf("Dokumenty").Retention);

        // Przed zapisem zadanie jest wyłączone (porzucony kreator nie uruchamia kopii); zapis włącza i ustawia harmonogram.
        var before = Assert.Single(CatalogStatusReader.ReadStatus(Catalog, "x", DateTimeOffset.UtcNow).Jobs);
        Assert.True(before.Disabled);
        Assert.Equal(KbCode.JobNameTaken, Assert.Single(await svc.ValidateStepAsync(WizardState.S1Sources, Draft(), _ct)).KbCode);
        Assert.Equal([new StepIssue("retention", KbCode.RetentionInvalid)], await svc.ValidateStepAsync(WizardState.S5Retention, Draft() with { KeepDaily = 0, KeepWeekly = 0, KeepMonthly = 0 }, _ct));
        var preview = await svc.PreviewRetentionAsync(Draft(), _ct);
        Assert.NotNull(preview);
        Assert.InRange(preview.Kept, 19, 22);
        Assert.Null(await svc.SaveAsync(Draft() with { KeepDaily = 14 }, _ct)); // zmiana w S5 po utworzeniu repozytorium trafia do definicji przy zapisie
        Assert.Equal(new Kobian.Application.Jobs.RetentionPolicy(KeepDaily: 14, KeepWeekly: 4, KeepMonthly: 12), JobDefinitionOf("Dokumenty").Retention);
        var after = Assert.Single(CatalogStatusReader.ReadStatus(Catalog, "x", DateTimeOffset.UtcNow).Jobs);
        Assert.False(after.Disabled);
        Assert.True(after.HasNextRunUnixSeconds);
        Assert.Equal(86_400, after.IntervalSeconds);

        Assert.Null(await svc.RunFirstBackupAsync("Dokumenty", _ct));
        var status = await source.LoadAsync(_ct);
        var job = Assert.Single(status.Jobs);
        Assert.NotNull(job.LastSuccessUtc);
        Assert.Equal(2, job.LastSuccessVerifyLevel); // V2 po backupie (it. 56)

        // Drugi kreator na ten sam cel: repozytorium już istnieje → pole celu, bez nadpisania.
        var again = await svc.CreateRepositoryAsync(Draft("Inne"), _pw, _ct);
        Assert.Contains(new StepIssue("destination", KbCode.ObjectAlreadyExists), again.Issues);
        Assert.Equal(KbCode.JobNotFound, await svc.SaveAsync(Draft("Nie-ma"), _ct));
    }

    private JobDefinition JobDefinitionOf(string name)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT type, definition FROM job_jobs WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        using var rd = cmd.ExecuteReader();
        Assert.True(rd.Read());
        return JobDefinition.Parse(rd.GetString(0), (byte[])rd[1]);
    }

    [Fact, Trait("BL", "BL-61"), Trait("Plan", "D2.9"), Trait("Category", "Integration")]
    public async Task Reader_cannot_create_repository_and_gets_no_secret()
    {
        var pipe = await StartAgent(AgentRole.Reader);
        using var source = new GrpcDashboardSource(pipe);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CreateWizardService().CreateRepositoryAsync(Draft(), _pw, _ct));
        Assert.False(Directory.Exists(P("state")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(P("repo")));
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.3"), Trait("Plan", "D2.9"), Trait("Category", "Integration")]
    public async Task Wizard_discovers_databases_over_pipe_and_reader_is_refused()
    {
        _sql.Databases.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "Firma", ["recovery_model_desc"] = "FULL", ["data_bytes"] = 1L << 20 });
        _sql.Databases.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "Kadry", ["recovery_model_desc"] = "SIMPLE" });
        _sql.Perms["Kadry"] = (false, false);
        var pipe = await StartAgent();
        using (var source = new GrpcDashboardSource(pipe))
        {
            var d = await source.CreateWizardService().DiscoverDatabasesAsync(new DatabaseServerRequest("srv01\\ERP"), _ct);
            Assert.NotNull(d);
            Assert.Null(d.KbCode);
            Assert.Equal(["Firma", "Kadry"], d.Databases.Select(x => x.Name));
            Assert.Equal(1L << 20, d.Databases[0].DataBytes);
            Assert.False(d.Databases[1].CanBackup);
            Assert.Contains("USE [Kadry];", d.PermissionScript, StringComparison.Ordinal);
            var bad = await source.CreateWizardService().DiscoverDatabasesAsync(new DatabaseServerRequest("srv01", SqlLogin: true), _ct);
            Assert.Equal(KbCode.JobDefinitionInvalid, bad!.KbCode);
        }

        var readerPipe = await StartAgent(AgentRole.Reader);
        using var readerSource = new GrpcDashboardSource(readerPipe);
        var before = _sql.Seen.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => readerSource.CreateWizardService().DiscoverDatabasesAsync(new DatabaseServerRequest("srv01"), _ct));
        Assert.Equal(before, _sql.Seen.Count); // odmowa przed połączeniem z serwerem SQL
    }

    [Fact, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.6"), Trait("Category", "Integration")]
    public async Task Sql_certificate_is_fetched_approved_and_used_as_pin_over_pipe()
    {
        await using var sql = Kobian.Tests.Unit.Sources.SqlCertificateFetcherTests.FakeTdsTlsServer.Start(strict: false);
        var server = $"127.0.0.1,{sql.Port}";
        var pipe = await StartAgent();
        using (var source = new GrpcDashboardSource(pipe))
        {
            var svc = source.CreateWizardService();
            var c = await svc.FetchSqlCertificateAsync(server, _ct);
            Assert.NotNull(c);
            Assert.Null(c.KbCode);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(sql.Certificate.RawData)), c.Sha256);
            Assert.True(c.SelfIssued);
            Assert.False(Directory.Exists(P("certs")) && Directory.EnumerateFiles(P("certs")).Any()); // pobranie niczego nie zapisuje

            var wrong = await svc.ApproveSqlCertificateAsync(server, new string('0', 64), c.SuggestedName, _ct);
            Assert.Equal(KbCode.SqlCertificateChanged, wrong!.KbCode);
            var ok = await svc.ApproveSqlCertificateAsync(server, c.Sha256, c.SuggestedName, _ct);
            Assert.Null(ok!.KbCode);
            Assert.Equal(sql.Certificate.RawData, await File.ReadAllBytesAsync(Path.Combine(P("certs"), ok.FileName!), _ct));

            var d = await svc.DiscoverDatabasesAsync(new DatabaseServerRequest("srv01", Certificate: ok.FileName), _ct); // pin przyjęty przez discovery
            Assert.Null(d!.KbCode);
        }

        var readerPipe = await StartAgent(AgentRole.Reader);
        using var readerSource = new GrpcDashboardSource(readerPipe);
        var before = sql.Connections;
        await Assert.ThrowsAsync<InvalidOperationException>(() => readerSource.CreateWizardService().FetchSqlCertificateAsync(server, _ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => readerSource.CreateWizardService().ApproveSqlCertificateAsync(server, new string('0', 64), "x.cer", _ct));
        Assert.Equal(before, sql.Connections); // odmowa przed połączeniem z serwerem SQL
    }
}
