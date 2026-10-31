using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Jobs;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Providers.Storage.Smb;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-73 cz. 2 (it. 93, ADR-0026): cel smb/sftp w definicji zadania v1 — walidacja wg schematu, przenoszenie SecretRef i opcji SFTP do dostawcy
/// (wcześniej 10 miejsc budowało <c>DestinationConfig</c> bez SecretRef), init repozytorium na udziale i backup z katalogu; blokada per serwer w SharedSmbConnector.
/// Poświadczenia i dane syntetyczne; udział SMB = katalog tymczasowy (natywny WNet — kolejka W-35).
/// </summary>
public sealed class JobDestinationTests : IAsyncLifetime
{
    private static readonly string Pin = "SHA256:" + new string('Q', 43);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-jobdest-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly string _pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    private CatalogWriter _writer = null!;

    private string P(string n) => Path.Combine(_dir, n);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(Path.Combine(_dir, "shares", "nas", "kopie", "biuro"));
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), RandomNumberGenerator.GetBytes(300_000));
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

    // ---- reguły celu ----

    public static TheoryData<string, string, string?, string?, int?, string?, string?> Cases()
    {
        var abs = OperatingSystem.IsWindows() ? @"C:\Kopie" : "/kopie";
        return new()
        {
            { "local", abs, null, null, null, null, null },
            { "local", "kopie", null, null, null, null, KbCode.DestinationNotFound },
            { "local", abs, "file:nas", null, null, null, KbCode.JobDefinitionInvalid }, // cel lokalny nie ma poświadczenia
            { "smb", @"\\nas\kopie\biuro", "file:nas", null, null, null, null },
            { "smb", @"\\nas\kopie\biuro", null, null, null, null, KbCode.AuthRejected }, // brak poświadczenia = odmowa zanim powstanie zadanie
            { "smb", @"\\nas\kopie\biuro", "file:../etc", null, null, null, KbCode.AuthRejected }, // traversal w nazwie wpisu
            { "smb", @"\\nas\kopie\biuro", "env:X", null, null, null, KbCode.AuthRejected },
            { "smb", @"\\nas\kopie\biuro", "file:nas", "nas", null, null, KbCode.JobDefinitionInvalid }, // host tylko dla sftp
            { "smb", @"\\nas\c$\x", "file:nas", null, null, null, "*" }, // udział administracyjny (kod z UncPath)
            { "smb", @"\\nas\kopie\..\x", "file:nas", null, null, null, "*" },
            { "smb", "/kopie", "file:nas", null, null, null, "*" },
            { "sftp", "/srv/kopie", "file:s1", "backup.example.test", 2222, Pin, null },
            { "sftp", "/srv/kopie", "file:s1", "backup.example.test", null, null, KbCode.HostKeyMismatch }, // bez TOFU (Qb15)
            { "sftp", "/srv/kopie", "file:s1", "backup.example.test", null, "MD5:aa", KbCode.HostKeyMismatch },
            { "sftp", "/srv/kopie", null, "backup.example.test", null, Pin, KbCode.AuthRejected },
            { "sftp", "srv/kopie", "file:s1", "backup.example.test", null, Pin, KbCode.DestinationNotFound },
            { "sftp", "/srv/../etc", "file:s1", "backup.example.test", null, Pin, KbCode.DestinationNotFound },
            { "sftp", @"/srv\kopie", "file:s1", "backup.example.test", null, Pin, KbCode.DestinationNotFound },
            { "sftp", "/srv/kopie", "file:s1", "bad host;x", null, Pin, KbCode.DestinationNotFound },
            { "sftp", "/srv/kopie", "file:s1", "backup.example.test", 0, Pin, KbCode.DestinationNotFound },
            { "ftp", "/srv", "file:f1", "nas.example.test", null, null, KbCode.InsecureTransportNotConfirmed }, // BL-34: jawny FTP nie w definicji v1 (D4.3)
            { "ftps", "/kopie", "file:f1", "nas.example.test", null, null, null }, // D4.11: certyfikat z zaufanego CA nie wymaga pinu
            { "ftps", "/kopie", "file:f1", "nas.example.test", 990, Pin, null },
            { "ftps", "/kopie", "file:f1", "nas.example.test", null, "MD5:aa", KbCode.TlsIdentityMismatch },
            { "ftps", "/kopie", null, "nas.example.test", null, null, KbCode.AuthRejected },
            { "ftps", "kopie", "file:f1", "nas.example.test", null, null, KbCode.DestinationNotFound },
            { "ftps", "/kopie/../etc", "file:f1", "nas.example.test", null, null, KbCode.DestinationNotFound },
            { "ftps", "/kopie", "file:f1", null, null, null, KbCode.DestinationNotFound },
            { "ftps", "/kopie", "file:f1", "nas\r\nUSER", null, null, KbCode.DestinationNotFound },
            { "ftps", "/kopie", "file:f1", "nas.example.test", 70000, null, KbCode.DestinationNotFound },
        };
    }

    [Theory, MemberData(nameof(Cases)), Trait("BL", "BL-73"), Trait("D", "D8.9"), Trait("D", "D4.11")]
    public void Validate_is_per_scheme(string scheme, string root, string? secretRef, string? host, int? port, string? hostKey, string? expected)
    {
        var code = JobDestination.Validate(new DestinationRequest(scheme, root, host, port, secretRef, hostKey));
        if (expected == "*") Assert.NotNull(code);
        else Assert.Equal(expected, code);
    }

    // ---- definicja v1 ----

    private static JobDefinition Parse(string destination)
    {
        var src = OperatingSystem.IsWindows() ? @"C:\\Dane" : "/dane";
        var key = OperatingSystem.IsWindows() ? @"C:\\k.key" : "/k.key";
        var json = "{\"version\":1,\"sources\":[\"" + src + "\"],\"destination\":" + destination + ",\"key\":{\"file\":\"" + key + "\"}}";
        return JobDefinition.Parse("files", Encoding.UTF8.GetBytes(json));
    }

    [Fact, Trait("BL", "BL-73")]
    public void Smb_definition_carries_secret_reference_to_the_provider_config()
    {
        var def = Parse("""{"scheme":"smb","root":"\\\\nas\\kopie\\biuro","secretRef":"file:nas"}""");
        var cfg = def.ToDestination("d1");
        Assert.Equal(("d1", "smb", @"\\nas\kopie\biuro", "file:nas"), (cfg.Id, cfg.Scheme, cfg.Root, cfg.SecretRef));
        Assert.Null(cfg.Options);
    }

    [Fact, Trait("BL", "BL-33"), Trait("D", "D4.11")]
    public void Sftp_definition_maps_host_port_and_pinned_key_without_identity_probe()
    {
        var def = Parse($$"""{"scheme":"sftp","root":"/srv/kopie","host":"backup.example.test","port":2222,"hostKey":"{{Pin}}","secretRef":"file:s1"}""");
        var cfg = def.ToDestination("d2");
        Assert.Equal("file:s1", cfg.SecretRef);
        Assert.Equal("backup.example.test", cfg.Options!["host"]);
        Assert.Equal("2222", cfg.Options["port"]);
        Assert.Equal(Pin, cfg.Options["hostkey"]);
        Assert.False(cfg.Options.ContainsKey("identity_probe")); // tryb sondy nigdy w zadaniu
        // ten sam katalog na innym serwerze = inny cel
        var other = Parse($$"""{"scheme":"sftp","root":"/srv/kopie","host":"other.example.test","port":2222,"hostKey":"{{Pin}}","secretRef":"file:s1"}""");
        Assert.NotEqual(def.DestinationId, other.DestinationId);
    }

    [Theory, Trait("BL", "BL-73")]
    [InlineData("""{"scheme":"smb","root":"\\\\nas\\kopie"}""")] // bez secretRef
    [InlineData("""{"scheme":"sftp","root":"/srv","host":"h","secretRef":"file:s1"}""")] // bez klucza hosta
    [InlineData("""{"scheme":"smb","root":"\\\\nas\\kopie","secretRef":"file:nas","user":"x"}""")] // nieznane pole (poświadczenie nie ma miejsca w definicji)
    [InlineData("""{"scheme":"sftp","root":"/srv","host":"h","port":"22","hostKey":"SHA256:QQ","secretRef":"file:s1"}""")] // port jako tekst
    [InlineData("""{"scheme":"nfs","root":"/srv"}""")]
    public void Invalid_destination_section_is_KB_0103(string destination)
    {
        var ex = Assert.Throws<KobianException>(() => Parse(destination));
        Assert.Equal(KbCode.JobDefinitionInvalid, ex.Code);
    }

    [Fact, Trait("BL", "BL-73")]
    public void Local_destination_id_is_unchanged_for_existing_jobs()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\\Kopie" : "/kopie"; // zapis JSON: ukośnik wsteczny podwojony
        var def = Parse($$"""{"scheme":"local","root":"{{root}}"}""");
        var legacy = "dest-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("local:" + def.DestinationRoot)))[..16];
        Assert.Equal(legacy, def.DestinationId); // repo_repositories i zasoby kolejki z it. 47–92 nie zmieniają identyfikatora
        Assert.Null(def.Remote);
        Assert.Null(def.ToDestination("x").SecretRef);
    }

    // ---- init na udziale SMB → backup z katalogu ----

    /// <summary>Udział = katalog <c>&lt;dir&gt;/shares/serwer/udział</c>; zapamiętuje SecretRef każdego otwarcia (dowód, że dociera do dostawcy).</summary>
    private sealed class RecordingCredentials : ISmbCredentialProvider
    {
        public List<(string Ref, CredentialRole Role)> Seen { get; } = [];

        public ValueTask<SmbCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct)
        {
            lock (Seen) Seen.Add((secretRef, role));
            return ValueTask.FromResult(new SmbCredential(role == CredentialRole.Maintenance ? "mt" : "bk", Convert.ToHexString(RandomNumberGenerator.GetBytes(8))));
        }
    }

    private sealed class DirConnector(string baseDir) : ISmbConnector
    {
        public ValueTask<ISmbConnection> ConnectAsync(SmbShare share, SmbCredential credential, CancellationToken ct) =>
            ValueTask.FromResult<ISmbConnection>(new Conn(Path.Combine(baseDir, share.Server, share.Share)));

        private sealed class Conn(string root) : ISmbConnection
        {
            public string LocalRoot { get; } = root;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact, Trait("BL", "BL-73"), Trait("BL", "BL-24"), Trait("AC", "AC-01.3")]
    public async Task Init_on_smb_share_stores_reference_and_backup_uses_it()
    {
        var creds = new RecordingCredentials();
        using var shared = new SharedSmbConnector(new DirConnector(Path.Combine(_dir, "shares")));
        var providers = new Dictionary<string, IStorageProviderFactory> { ["smb"] = new SmbStorageProviderFactory(shared, creds) };
        var init = new RepoInitService(P("catalog.db"), _writer, providers, P("state"), TimeProvider.System);

        var req = new InitJobRequest("Nas", P("src"), "smb", @"\\nas\kopie\biuro", P("pw.txt"), 1 << 20, new RemoteDestination("file:nas"));
        var r = await init.InitAsync(req, _ct);
        Assert.True(File.Exists(Path.Combine(_dir, "shares", "nas", "kopie", "biuro", "kobian.repo")));
        var def = JobDefinition.Parse("files", ReadDefinition("Nas"));
        Assert.Equal(("smb", "file:nas"), (def.DestinationScheme, def.Remote!.SecretRef));
        Assert.DoesNotContain(_pw, Encoding.UTF8.GetString(ReadDefinition("Nas")), StringComparison.Ordinal);

        var svc = new CatalogJobService(P("catalog.db"), _writer, providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        var outcome = await svc.RunAsync("Nas", _ct);
        Assert.Equal(JobResult.Succeeded, outcome.Result);
        Assert.All(creds.Seen, s => Assert.Equal("file:nas", s.Ref)); // przed it. 93 zadanie gubiło SecretRef → KB-2102 przy każdym uruchomieniu
        Assert.DoesNotContain(creds.Seen, s => s.Role == CredentialRole.Maintenance); // backup bez poświadczenia z prawem usuwania (D4.6)
        Assert.Equal(0, shared.ActiveConnections);
        Assert.NotEmpty(r.RepoIdHex);
    }

    [Fact, Trait("BL", "BL-73")]
    public async Task Init_refuses_smb_without_reference_before_touching_the_share()
    {
        var creds = new RecordingCredentials();
        var providers = new Dictionary<string, IStorageProviderFactory> { ["smb"] = new SmbStorageProviderFactory(new DirConnector(Path.Combine(_dir, "shares")), creds) };
        var init = new RepoInitService(P("catalog.db"), _writer, providers, P("state"), TimeProvider.System);
        var ex = await Assert.ThrowsAsync<KobianException>(() => init.InitAsync(new InitJobRequest("Nas", P("src"), "smb", @"\\nas\kopie\biuro", P("pw.txt")), _ct));
        Assert.Equal(KbCode.AuthRejected, ex.Code);
        Assert.Empty(creds.Seen);
        Assert.False(File.Exists(Path.Combine(_dir, "shares", "nas", "kopie", "biuro", "kobian.repo")));
    }

    private byte[] ReadDefinition(string name)
    {
        using var c = CatalogBootstrap.Open(P("catalog.db"), readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT definition FROM job_jobs WHERE name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        return (byte[])cmd.ExecuteScalar()!;
    }

    // ---- SharedSmbConnector: blokada per serwer (Średnie z it. 92) ----

    /// <summary>Połączenie z serwerem „wolny” wisi do zwolnienia bramki — jak WNetAddConnection2 przy niedostępnym hoście.</summary>
    private sealed class SlowConnector(string baseDir) : ISmbConnector
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ISmbConnection> ConnectAsync(SmbShare share, SmbCredential credential, CancellationToken ct)
        {
            if (share.Server == "wolny") await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            return new Conn(Path.Combine(baseDir, share.Server, share.Share));
        }

        private sealed class Conn(string root) : ISmbConnection
        {
            public string LocalRoot { get; } = root;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact, Trait("BL", "BL-73"), Trait("NFR", "NFR-09")]
    public async Task Slow_server_does_not_block_connections_to_another_server()
    {
        var inner = new SlowConnector(_dir);
        using var shared = new SharedSmbConnector(inner);
        var cred = new SmbCredential("u", Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
        var slow = shared.ConnectAsync(UncPath.Parse(@"\\wolny\kopie"), cred, _ct).AsTask();
        await using (var fast = await shared.ConnectAsync(UncPath.Parse(@"\\nas\kopie"), cred, _ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), _ct))
            Assert.EndsWith(Path.Combine("nas", "kopie"), fast.LocalRoot, StringComparison.Ordinal); // przed it. 93: czekało na globalnej blokadzie
        Assert.False(slow.IsCompleted);
        inner.Release.SetResult();
        await using var s = await slow.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        Assert.Equal(1, shared.ActiveConnections);
    }

    [Fact, Trait("BL", "BL-73")]
    public async Task Cancelled_wait_for_a_busy_server_does_not_leak_the_gate()
    {
        var inner = new SlowConnector(_dir);
        using var shared = new SharedSmbConnector(inner);
        var cred = new SmbCredential("u", Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
        using var cts = new CancellationTokenSource();
        var first = shared.ConnectAsync(UncPath.Parse(@"\\wolny\a"), cred, cts.Token).AsTask();
        var second = shared.ConnectAsync(UncPath.Parse(@"\\wolny\b"), cred, cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        inner.Release.SetResult();
        await using var again = await shared.ConnectAsync(UncPath.Parse(@"\\wolny\a"), cred, _ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), _ct);
        Assert.Equal(1, shared.ActiveConnections);
    }
}
