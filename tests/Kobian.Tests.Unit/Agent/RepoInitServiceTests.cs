using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Jobs;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Engine.Restore;
using Kobian.Providers.Storage.Local;
using Kobian.Providers.Storage.Secrets;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>BL-24 / BL-15: init repozytorium w Agencie → backup z katalogu → kobian-restore (bez klucza deweloperskiego i --repo-id). Dane syntetyczne, hasło losowe w teście.</summary>
public sealed class RepoInitServiceTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-init-" + Guid.NewGuid().ToString("N"));
    private readonly string _pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    private CatalogWriter _writer = null!;
    private string P(string n) => Path.Combine(_dir, n);

    private static Dictionary<string, IStorageProviderFactory> Providers => new() { ["local"] = new LocalStorageProviderFactory() };

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        File.WriteAllText(Path.Combine(P("src"), "b.txt"), "syntetyczne dane");
        File.WriteAllText(P("pw.txt"), _pw + "\n");
        CatalogBootstrap.Bootstrap(P("catalog.db"), TimeProvider.System);
        _writer = new CatalogWriter(P("catalog.db"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private RepoInitService Init() => new(P("catalog.db"), _writer, Providers, P("state"), TimeProvider.System);

    private InitJobRequest Request(string name = "Pliki") => new(name, P("src"), "local", P("repo"), P("pw.txt"), 1048576);

    [Fact, Trait("BL", "BL-24"), Trait("BL", "BL-15"), Trait("D", "D3.11")]
    public async Task Init_creates_repo_job_and_backup_restores_with_password_only()
    {
        var r = await Init().InitAsync(Request(), _ct);
        Assert.True(File.Exists(P("repo/kobian.repo")));
        Assert.True(File.Exists(P("repo/keys/" + r.KeyIdHex + ".key")));
        Assert.True(File.Exists(r.LocalKeyFile) && File.Exists(r.LocalRepoHeader));

        var def = Encoding.UTF8.GetString(ReadDefinition("Pliki"));
        Assert.DoesNotContain(_pw, def, StringComparison.Ordinal); // hasło tylko w pliku, nigdy w definicji (NFR-11)
        Assert.DoesNotContain(_pw, File.ReadAllText(r.LocalKeyFile), StringComparison.Ordinal);

        var svc = new CatalogJobService(P("catalog.db"), _writer, Providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        var outcome = await svc.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, outcome.Result);

        Directory.CreateDirectory(P("dst"));
        var e = new StringWriter();
        var code = await OfflineRestore.RunAsync(["restore", "--repo", P("repo"), "--key-file", r.LocalKeyFile, "--password-file", P("pw.txt"), "--to", P("dst")], new LocalStorageProviderFactory(), new StringWriter(), e, _ct);
        Assert.True(code == 0, e.ToString());
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(P("src/a.bin"))), SHA256.HashData(File.ReadAllBytes(P("dst/a.bin"))));
    }

    private sealed class RecordingRemote(IStorageProviderFactory inner) : IStorageProviderFactory
    {
        public DestinationConfig? Seen { get; private set; }

        public string Scheme => "sftp";

        public ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
        {
            Seen = config;
            return inner.OpenAsync(config with { Scheme = "local" }, role, ct);
        }
    }

    [Fact, Trait("BL", "BL-43"), Trait("US", "US-43"), Trait("D", "D8.3")]
    public async Task Standalone_restore_reads_remote_destination_with_pinned_identity_and_local_header()
    {
        var r = await Init().InitAsync(Request(), _ct);
        var svc = new CatalogJobService(P("catalog.db"), _writer, Providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        Assert.Equal(JobResult.Succeeded, (await svc.RunAsync("Pliki", _ct)).Result);
        Directory.CreateDirectory(P("dst"));
        var rec = new RecordingRemote(new LocalStorageProviderFactory());
        string? gotDir = null, gotScheme = null;
        var e = new StringWriter();
        var code = await OfflineRestore.RunAsync(
            ["restore", "--repo", P("repo"), "--key-file", r.LocalKeyFile, "--password-file", P("pw.txt"), "--to", P("dst"),
             "--dest-scheme", "sftp", "--host", "nas.example", "--port", "2222", "--hostkey", "SHA256:AAAA", "--secrets-dir", P("sec"),
             "--secret-ref", "file:nas1", "--repo-header", r.LocalRepoHeader],
            new LocalStorageProviderFactory(), (dir, scheme) => { gotDir = dir; gotScheme = scheme; return rec; }, new StringWriter(), e, _ct);
        Assert.True(code == 0, e.ToString());
        Assert.Equal(P("sec"), gotDir);
        Assert.Equal("sftp", gotScheme);
        Assert.NotNull(rec.Seen);
        Assert.Equal("sftp", rec.Seen!.Scheme);
        Assert.Equal(P("repo"), rec.Seen.Root);
        Assert.Equal("file:nas1", rec.Seen.SecretRef);
        Assert.Equal("nas.example", rec.Seen.Options!["host"]);
        Assert.Equal("2222", rec.Seen.Options["port"]);
        Assert.Equal("SHA256:AAAA", rec.Seen.Options["hostkey"]);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(P("src/a.bin"))), SHA256.HashData(File.ReadAllBytes(P("dst/a.bin"))));
    }

    [Theory, Trait("BL", "BL-43"), Trait("US", "US-43")]
    [InlineData("sftp", false, "--hostkey")] // brak przypięcia klucza hosta = odmowa (usage, 64)
    [InlineData("sftp", true, "")]            // fabryka zdalna niedostępna
    [InlineData("smb", false, "--hostkey")]   // schemat nieobsługiwany w narzędziu samodzielnym
    public async Task Standalone_restore_remote_misuse_is_a_usage_error(string scheme, bool noFactory, string pinOpt)
    {
        var e = new StringWriter();
        List<string> a = ["list", "--repo", "/x", "--key-file", "k", "--password-file", "p", "--repo-header", "h", "--dest-scheme", scheme, "--host", "h", "--secrets-dir", "s", "--secret-ref", "file:a"];
        if (scheme == "sftp" && pinOpt.Length == 0) a.AddRange(["--hostkey", "SHA256:AAAA"]);
        var code = await OfflineRestore.RunAsync([.. a], new LocalStorageProviderFactory(),
            noFactory ? null : (_, sc) => StandaloneRemoteProviders.Create("s", sc), new StringWriter(), e, _ct);
        Assert.Equal(64, code);
    }

    [Fact, Trait("BL", "BL-43")]
    public void Standalone_remote_providers_cover_sftp_and_ftps_only()
    {
        Assert.NotNull(StandaloneRemoteProviders.Create("s", "sftp"));
        Assert.Equal("ftps", StandaloneRemoteProviders.Create("s", "ftps")!.Scheme);
        Assert.Null(StandaloneRemoteProviders.Create("s", "ftp")); // jawny FTP nie w narzędziu awaryjnym (D4.3)
        Assert.Null(StandaloneRemoteProviders.Create("s", "smb"));
    }

    [Fact, Trait("BL", "BL-24")]
    public async Task Init_refuses_existing_repository_and_duplicate_job_name()
    {
        await Init().InitAsync(Request(), _ct);
        var again = await Assert.ThrowsAsync<KobianException>(() => Init().InitAsync(Request("Inna"), _ct));
        Assert.Equal(KbCode.ObjectAlreadyExists, again.Code);
        Directory.CreateDirectory(P("repo2"));
        var dup = await Assert.ThrowsAsync<KobianException>(() => Init().InitAsync(Request() with { DestinationRoot = P("repo2") }, _ct));
        Assert.Equal(KbCode.JobNameTaken, dup.Code);
        Assert.False(File.Exists(P("repo2/kobian.repo"))); // odmowa przed zapisem na cel
    }

    [Fact, Trait("BL", "BL-24")]
    public async Task Init_validates_inputs_and_empty_password()
    {
        var rel = await Assert.ThrowsAsync<KobianException>(() => Init().InitAsync(Request() with { Source = "wzgledna" }, _ct));
        Assert.Equal(KbCode.JobDefinitionInvalid, rel.Code);
        var scheme = await Assert.ThrowsAsync<KobianException>(() => Init().InitAsync(Request() with { DestinationScheme = "ftp" }, _ct));
        Assert.Equal(KbCode.JobDefinitionInvalid, scheme.Code);
        File.WriteAllText(P("empty.txt"), "\n");
        var empty = await Assert.ThrowsAsync<KobianException>(() => Init().InitAsync(Request() with { PasswordFile = P("empty.txt") }, _ct));
        Assert.Equal(KbCode.KeyUnwrapFailed, empty.Code);
        Assert.False(File.Exists(P("repo/kobian.repo")));
    }

    private byte[] ReadDefinition(string name)
    {
        using var c = CatalogBootstrap.Open(P("catalog.db"), readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT definition FROM job_jobs WHERE name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        return (byte[])cmd.ExecuteScalar()!;
    }
}
