using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.V1;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Providers.Storage.Local;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>BL-60 cz. 2: ProbeDestination i UpsertJob(validate_only) po stronie Agenta — katalog i foldery tymczasowe, dane syntetyczne.</summary>
public sealed class WizardBackendTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-wiz-" + Guid.NewGuid().ToString("N"));
    private string CatalogPath => Path.Combine(_dir, "catalog.db");
    private string Src => Path.Combine(_dir, "src");
    private string Dst => Path.Combine(_dir, "dst");

    public WizardBackendTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Dst);
        File.WriteAllText(Path.Combine(Src, "a.txt"), "x");
        CatalogBootstrap.Bootstrap(CatalogPath, TimeProvider.System);
        using var c = CatalogBootstrap.Open(CatalogPath);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,time_zone,misfire,created_at,updated_at) VALUES ('j1','Istniejace','files',x'00',x'00','UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class IdentityRefusingFactory : IStorageProviderFactory
    {
        public string Scheme => "sftp";

        public DestinationConfig? Last { get; private set; }

        public ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
        {
            Last = config;
            Assert.Equal(CredentialRole.Backup, role); // probe nigdy nie używa sesji z prawem usuwania (D4.6)
            throw new StorageException(ErrorClass.Security, KbCode.HostKeyMismatch, "pin") { PresentedIdentity = new ServerIdentity("ssh-ed25519", "SHA256:Zm9vYmFy", "ssh-ed25519") };
        }
    }

    private static Dictionary<string, IStorageProviderFactory> Providers(IStorageProviderFactory? sftp = null)
    {
        var d = new Dictionary<string, IStorageProviderFactory>(StringComparer.Ordinal) { ["local"] = new LocalStorageProviderFactory() };
        if (sftp is not null) d["sftp"] = sftp;
        return d;
    }

    private JobDraftValidator Validator(IStorageProviderFactory? sftp = null) =>
        new(CatalogPath, new DestinationProbeService(Providers(sftp)), new FixedTime(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero)));

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "R2.6.1 SCR-03")]
    public async Task Local_probe_reports_space_and_existing_repository_without_writing()
    {
        var probe = new DestinationProbeService(Providers());
        var r = await probe.ProbeAsync(new DestinationRequest("local", Dst), _ct);
        Assert.Equal(DestinationProbeStatus.Ok, r.Status);
        Assert.True(r.FreeBytes > 0);
        Assert.True(r.TotalBytes >= r.FreeBytes);
        Assert.False(r.RepositoryExists);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Dst)); // nic nie zapisano

        File.WriteAllText(Path.Combine(Dst, "kobian.repo"), "naglowek");
        Assert.True((await probe.ProbeAsync(new DestinationRequest("local", Dst), _ct)).RepositoryExists);

        Assert.Equal((DestinationProbeStatus.Failed, KbCode.DestinationNotFound), Pair(await probe.ProbeAsync(new DestinationRequest("local", Path.Combine(_dir, "brak")), _ct)));
        Assert.Equal((DestinationProbeStatus.Failed, KbCode.DestinationNotFound), Pair(await probe.ProbeAsync(new DestinationRequest("local", "wzgledna"), _ct)));
        Assert.Equal((DestinationProbeStatus.Failed, KbCode.JobDefinitionInvalid), Pair(await probe.ProbeAsync(new DestinationRequest("ftp", Dst), _ct)));
        Assert.Equal((DestinationProbeStatus.Failed, KbCode.DestinationNotFound), Pair(await probe.ProbeAsync(new DestinationRequest("local", Dst + "\n"), _ct)));
    }

    private static (DestinationProbeStatus, string?) Pair(DestinationProbeResult r) => (r.Status, r.Code);

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "D4.11"), Trait("AC", "AC-04.1")]
    public async Task Sftp_probe_without_pin_asks_for_approval_and_with_wrong_pin_fails()
    {
        var f = new IdentityRefusingFactory();
        var probe = new DestinationProbeService(Providers(f));
        var unknown = await probe.ProbeAsync(new DestinationRequest("sftp", "/kopie", "backup.example.test", 2222, "file:nas"), _ct);
        Assert.Equal(DestinationProbeStatus.UnknownIdentity, unknown.Status);
        Assert.Equal("SHA256:Zm9vYmFy", unknown.Identity?.Fingerprint);
        Assert.Equal("1", f.Last!.Options!["identity_probe"]);
        Assert.False(f.Last.Options.ContainsKey("hostkey"));
        Assert.Equal("2222", f.Last.Options["port"]);

        var changed = await probe.ProbeAsync(new DestinationRequest("sftp", "/kopie", "backup.example.test", null, "file:nas", "SHA256:inny"), _ct);
        Assert.Equal((DestinationProbeStatus.Failed, KbCode.HostKeyMismatch), Pair(changed)); // zmiana klucza = alarm, nie pytanie
        Assert.Equal("SHA256:inny", f.Last!.Options!["hostkey"]);
        Assert.False(f.Last.Options.ContainsKey("identity_probe"));

        Assert.Equal(KbCode.DestinationNotFound, (await probe.ProbeAsync(new DestinationRequest("sftp", "/k", null), _ct)).Code);
        Assert.Equal(KbCode.DestinationNotFound, (await probe.ProbeAsync(new DestinationRequest("sftp", "/k", "h", 70000), _ct)).Code);
    }

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "D6.7")]
    public void Validator_accepts_good_draft_and_reports_each_field()
    {
        var v = Validator();
        Assert.Empty(v.Validate(new JobDraft("Dokumenty", [Src], new DestinationRequest("local", Dst), "0 21 * * *")));
        Assert.Empty(v.Validate(new JobDraft("Reczne", [Src], new DestinationRequest("local", Dst), null)));

        var bad = v.Validate(new JobDraft("Istniejace", [Path.Combine(_dir, "nie-ma")], new DestinationRequest("local", "wzgledna"), "* * * * *"));
        Assert.Equal(
            [new FieldProblem("name", KbCode.JobNameTaken), new FieldProblem("sources", KbCode.SourcesNotReadable), new FieldProblem("destination", KbCode.DestinationNotFound), new FieldProblem("schedule", KbCode.ScheduleInvalid)],
            bad);
        Assert.Contains(new FieldProblem("name", KbCode.JobDefinitionInvalid), v.Validate(new JobDraft(" spacja", [Src], new DestinationRequest("local", Dst), null)));
        Assert.Contains(new FieldProblem("name", KbCode.JobDefinitionInvalid), v.Validate(new JobDraft(new string('x', 201), [Src], new DestinationRequest("local", Dst), null)));
        Assert.Contains(new FieldProblem("sources", KbCode.SourcesNotReadable), v.Validate(new JobDraft("A", [], new DestinationRequest("local", Dst), null)));
        // Kilka źródeł jest dozwolonych; źródło będące celem = nakładanie z celem, foldery zawierające się nawzajem = KB-0102.
        Assert.Contains(new FieldProblem("destination", KbCode.SourcesOverlap), v.Validate(new JobDraft("A", [Src, Dst], new DestinationRequest("local", Dst), null)));
        Assert.Contains(new FieldProblem("sources", KbCode.SourcesOverlap), v.Validate(new JobDraft("A", [Src, Path.Combine(Src, ".")], new DestinationRequest("local", Dst), null)));
        Assert.Contains(new FieldProblem("sources", KbCode.JobDefinitionInvalid), v.Validate(new JobDraft("A", [Src], new DestinationRequest("local", Dst), null, null,
            [new DestinationRequest("smb", @"\\nas\k", SecretRef: "file:x")])));
        Assert.Contains(new FieldProblem("schedule", KbCode.ScheduleInvalid), v.Validate(new JobDraft("A", [Src], new DestinationRequest("local", Dst), "to nie cron")));
    }

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "D6.7")]
    public void Validator_refuses_destination_inside_source_and_not_yet_supported_scheme()
    {
        var v = Validator(new IdentityRefusingFactory());
        Assert.Equal([new FieldProblem("destination", KbCode.SourcesOverlap)], v.Validate(new JobDraft("A", [_dir], new DestinationRequest("local", Dst), null)));
        Assert.Equal([new FieldProblem("destination", KbCode.SourcesOverlap)], v.Validate(new JobDraft("A", [Src], new DestinationRequest("local", Path.Combine(Src)), null)));
        Assert.DoesNotContain(v.Validate(new JobDraft("A", [Src], new DestinationRequest("local", Src + "2"), null)), p => p.Code == KbCode.SourcesOverlap); // prefiks nazwy ≠ zagnieżdżenie
        // it. 93 (BL-73 cz. 2): sftp jest już obsługiwany w definicji, ale zadanie bez przypiętego klucza hosta = KB-2104 (bez TOFU, Qb15), bez poświadczenia = KB-2102
        Assert.Equal([new FieldProblem("destination", KbCode.HostKeyMismatch)], v.Validate(new JobDraft("A", [Src], new DestinationRequest("sftp", "/k", "h"), null)));
        Assert.Equal([new FieldProblem("destination", KbCode.AuthRejected)],
            v.Validate(new JobDraft("A", [Src], new DestinationRequest("sftp", "/k", "h", HostKey: "SHA256:" + new string('A', 43)), null)));
    }

    [Fact, Trait("BL", "BL-60")]
    public async Task Backend_upsert_validate_only_and_honest_refusal_to_save()
    {
        var probe = new DestinationProbeService(Providers());
        var backend = new CatalogAgentBackend(CatalogPath, "1", probe: probe, validator: new JobDraftValidator(CatalogPath, probe, TimeProvider.System));
        UpsertJobArgs Args(string name, bool validateOnly)
        {
            var a = new UpsertJobArgs { JobName = name, ValidateOnly = validateOnly, Destination = new DestinationSpec { Scheme = "local", Root = Dst }, Cron = "0 21 * * *" };
            a.Sources.Add(Src);
            return a;
        }
        var ok = await backend.UpsertJobAsync(Args("Nowe", true), _ct);
        Assert.Equal(OverallState.Ok, ok.Result);
        Assert.Empty(ok.Issues);
        var taken = await backend.UpsertJobAsync(Args("Istniejace", true), _ct);
        Assert.Equal((OverallState.Failed, KbCode.JobNameTaken), (taken.Result, taken.Code));
        Assert.Equal("name", Assert.Single(taken.Issues).Field);
        Assert.DoesNotContain(Src, taken.ToString(), StringComparison.Ordinal); // NFR-11: odpowiedź bez wartości pól
        var save = await backend.UpsertJobAsync(Args("Nowe", false), _ct);
        Assert.Equal((OverallState.Failed, KbCode.OperationNotAvailable), (save.Result, save.Code));
        var noDest = await backend.UpsertJobAsync(new UpsertJobArgs { JobName = "X", ValidateOnly = true }, _ct);
        Assert.Contains(noDest.Issues, i => i.Field == "destination");

        var p = await backend.ProbeDestinationAsync(new ProbeDestinationArgs { Destination = new DestinationSpec { Scheme = "local", Root = Dst } }, _ct);
        Assert.Equal(ProbeStatus.Ok, p.Status);
        Assert.True(p.HasFreeBytes);
        Assert.Equal(KbCode.JobDefinitionInvalid, (await backend.ProbeDestinationAsync(new ProbeDestinationArgs(), _ct)).Code);
        var bare = new CatalogAgentBackend(CatalogPath, "1");
        Assert.Equal(KbCode.OperationNotAvailable, (await bare.ProbeDestinationAsync(new ProbeDestinationArgs(), _ct)).Code);
        Assert.Equal(KbCode.OperationNotAvailable, (await bare.UpsertJobAsync(new UpsertJobArgs(), _ct)).Code);
    }

    [Fact, Trait("BL", "BL-60"), Trait("Plan", "D2.9")]
    public void New_methods_require_operator()
    {
        Assert.Equal(new MethodPolicy.Entry(AgentRole.Operator, false), MethodPolicy.Find("/kobian.v1.Agent/ProbeDestination"));
        Assert.Equal(new MethodPolicy.Entry(AgentRole.Operator, true), MethodPolicy.Find("/kobian.v1.Agent/UpsertJob"));
    }
}
