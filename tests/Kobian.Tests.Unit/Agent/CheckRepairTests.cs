using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.Contracts;
using Kobian.Api.V1;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Format.Pack;
using Kobian.Providers.Storage.Local;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// it. 109 — D8.10 / S6 `check --repair`: brakujący pack danych → snapshot „częściowo uszkodzony” z liczbą plików, brakujący pack drzew → „nieodtwarzalny”;
/// wynik w katalogu (damage, damaged_files, suspect → następna kopia pełna); repozytorium nietknięte; repair wymaga roli Administrator (KB-7011). Dane syntetyczne.
/// </summary>
public sealed class CheckRepairTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-repair-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _hosts = [];
    private CatalogWriter _writer = null!;
    private CatalogCheckService _check = null!;
    private string _snapId = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.Combine(_dir, n);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        File.WriteAllBytes(Path.Combine(P("src"), "a.bin"), RandomNumberGenerator.GetBytes(1_500_000)); // pierwszy plik, osobny pack (≥ 1 MiB)
        File.WriteAllText(Path.Combine(P("src"), "b.txt"), "plik b");
        File.WriteAllText(Path.Combine(P("src"), "c.txt"), "plik c");
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(P("wrapped.key"), prov.KeyFileJson);
        File.WriteAllText(P("kobian.repo"), prov.HeaderJson);
        File.WriteAllText(P("pw.txt"), pw + "\n");
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        var providers = new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() };
        var jobs = new CatalogJobService(Catalog, _writer, providers, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask);
        var def = "{\"version\":1,\"sources\":[" + J(P("src")) + "],\"destination\":{\"scheme\":\"local\",\"root\":" + J(P("repo")) + "},\"key\":{\"file\":" + J(P("wrapped.key"))
            + ",\"passwordFile\":" + J(P("pw.txt")) + ",\"repoHeader\":" + J(P("kobian.repo")) + "},\"packTargetSize\":1048576}";
        using (var c = CatalogBootstrap.Open(Catalog))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ('j1','Pliki','files',$d,x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');";
            cmd.Parameters.AddWithValue("$d", Encoding.UTF8.GetBytes(def));
            cmd.ExecuteNonQuery();
        }
        var b = await jobs.RunAsync("Pliki", _ct);
        Assert.Equal(JobResult.Succeeded, b.Result);
        _snapId = b.SnapshotId!;
        _check = new CatalogCheckService(Catalog, _writer, providers, TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    /// <summary>Pack zawierający (albo nie) bloki drzew — z indeksów repozytorium.</summary>
    private async Task<string> PackFile(bool withTrees)
    {
        using var keys = KeyFileLoader.Load(P("wrapped.key"), P("pw.txt"), P("kobian.repo"));
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", P("repo")), CredentialRole.Backup, _ct);
        var snap = await MetadataStore.LoadSnapshotAsync(s, keys, Convert.FromHexString(_snapId), _ct);
        var candidates = new List<(byte[] PackId, int Entries)>();
        foreach (var (indexId, _) in snap.Indexes)
        {
            var (idx, _) = await MetadataStore.LoadIndexAsync(s, keys, indexId, _ct);
            foreach (var pk in idx.Packs)
                if (pk.Entries.Any(e => e.BlobType == BlobType.Tree) == withTrees) candidates.Add((pk.PackId, pk.Entries.Count));
        }
        if (candidates.Count == 0) throw new InvalidOperationException("brak packa o żądanej zawartości");
        // Pack z najmniejszą liczbą bloków = początek a.bin (sam); kolejność packów w indeksie zależy od równoległego wysyłania.
        var pick = candidates.OrderBy(c => c.Entries).First();
        return Path.Combine(P("repo"), Kobian.Format.Repo.RepoPaths.Pack(pick.PackId).Replace('/', Path.DirectorySeparatorChar));
    }

    private (string? Damage, long? Files, long Suspect) Row()
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT damage, damaged_files, suspect FROM snap_snapshots WHERE snap_id=$s;";
        cmd.Parameters.AddWithValue("$s", _snapId);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetInt64(2));
    }

    [Fact, Trait("Plan", "D8.10"), Trait("Plan", "S6")]
    public async Task Missing_data_pack_makes_snapshot_partial_with_counted_files_and_forces_full()
    {
        var pack = await PackFile(withTrees: false);
        var hidden = pack + ".bak";
        File.Move(pack, hidden);
        var o = await _check.CheckAsync("Pliki", 0, null, repair: true, _ct);
        Assert.Equal(CheckStatus.Failed, o.Status);
        var d = Assert.Single(o.Damage!);
        Assert.Equal((SnapshotDamageState.Partial, 1L, 0L, 2L), (d.State, d.DamagedFiles, d.DamagedDirectories, d.IntactFiles));
        Assert.Equal(["a.bin"], d.Sample);
        Assert.Equal(("partial", (long?)1, 1L), Row());
        Assert.True(File.Exists(hidden)); // repozytorium nietknięte (append-only)
        Assert.False(File.Exists(pack));

        var plain = await _check.CheckAsync("Pliki", 0, null, _ct); // bez --repair — bez oceny
        Assert.Null(plain.Damage);
    }

    [Fact, Trait("Plan", "D8.10")]
    public async Task Missing_tree_pack_makes_snapshot_unrecoverable()
    {
        File.Move(await PackFile(withTrees: true), P("trees.bak"));
        var o = await _check.CheckAsync("Pliki", 0, null, repair: true, _ct);
        var d = Assert.Single(o.Damage!);
        Assert.Equal(SnapshotDamageState.Unrecoverable, d.State);
        Assert.Equal("unrecoverable", Row().Damage);
    }

    private string[] IndexFiles() => Directory.GetFiles(Path.Combine(P("repo"), "index"), "*.idx");

    [Fact, Trait("Plan", "D8.10"), Trait("Plan", "D3.10")]
    public async Task Lost_index_is_rebuilt_from_pack_trailers_and_restore_still_works_with_KB_1505()
    {
        var original = Assert.Single(IndexFiles());
        File.Move(original, P("lost-index.bak")); // indeks kopii zniknął, packi zostały
        var o = await _check.CheckAsync("Pliki", 0, null, repair: true, _ct);
        Assert.NotNull(o.Recovery);
        Assert.Equal((o.Recovery!.OrphanPacks, 0), (o.Recovery.RecoveredPacks, o.Recovery.UnreadablePacks));
        Assert.True(o.Recovery.RecoveredPacks >= 2);
        var rebuilt = Assert.Single(IndexFiles()); // nowy obiekt (append-only), inny identyfikator
        Assert.NotEqual(Path.GetFileName(original), Path.GetFileName(rebuilt));
        var d = Assert.Single(o.Damage!);
        Assert.Equal((SnapshotDamageState.Intact, true, 3L), (d.State, d.IndexRecovered, d.IntactFiles));

        Directory.CreateDirectory(P("out"));
        var restore = new CatalogRestoreService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, TimeProvider.System);
        var r = await restore.RestoreAsync(_snapId, null, P("out"), _ct);
        Assert.Equal((JobResult.Warning, KbCode.IndexRecoveredFromPacks), (r.Result, r.Code));
        Assert.Equal(File.ReadAllBytes(Path.Combine(P("src"), "a.bin")), File.ReadAllBytes(Path.Combine(P("out"), "a.bin")));

        var again = await _check.CheckAsync("Pliki", 0, null, repair: true, _ct); // drugi przebieg: nic do odzyskania
        Assert.Equal(0, again.Recovery!.OrphanPacks);
        Assert.Single(IndexFiles());
    }

    [Fact, Trait("Plan", "D8.10"), Trait("SEC", "SEC-3.3")]
    public async Task Foreign_or_corrupt_orphan_pack_is_not_trusted()
    {
        var orphanDir = Path.Combine(P("repo"), "data", "ab");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllBytes(Path.Combine(orphanDir, "ab" + new string('0', 30) + ".pack"), RandomNumberGenerator.GetBytes(4096)); // „pack” bez poprawnej stopki/klucza
        var o = await _check.CheckAsync("Pliki", 0, null, repair: true, _ct);
        Assert.Equal((1, 0, 1), (o.Recovery!.OrphanPacks, o.Recovery.RecoveredPacks, o.Recovery.UnreadablePacks));
        Assert.Null(o.Recovery.IndexId); // nic nie zapisano
        Assert.Single(IndexFiles());
    }

    [Fact, Trait("Plan", "D8.10")]
    public async Task Healthy_repository_reports_no_damage_and_leaves_catalog_unchanged()
    {
        var o = await _check.CheckAsync("Pliki", 0, null, repair: true, _ct);
        Assert.Equal(CheckStatus.Succeeded, o.Status);
        Assert.Empty(o.Damage!);
        Assert.Equal(((string?)null, (long?)null, 0L), Row());
    }

    [Theory, Trait("Plan", "D8.10"), Trait("SEC", "D2.9"), Trait("Category", "Integration")]
    [InlineData(AgentRole.Operator, true)]
    [InlineData(AgentRole.Administrator, false)]
    public async Task Repair_requires_administrator(AgentRole role, bool denied)
    {
        if (OperatingSystem.IsWindows()) return;
        var pipe = "kobian-test-" + Guid.NewGuid().ToString("N");
        var app = AgentHost.Create(new AgentHostOptions(pipe), new CatalogAgentBackend(Catalog, "t", check: _check), new FixedRoleResolver(role));
        _hosts.Add(app);
        await app.StartAsync(_ct);
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var sock = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                await sock.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(ApiConventions.UnixSocketPath(pipe)), ct);
                return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);
            },
        };
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost", new Grpc.Net.Client.GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
        var client = new Api.V1.Agent.AgentClient(channel);
        var headers = new Metadata { { ApiConventions.ClientRevisionHeader, ApiConventions.CurrentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
        if (denied)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() => client.CheckAsync(new CheckArgs { Job = "Pliki", Repair = true }, headers, cancellationToken: _ct).ResponseAsync);
            Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
            Assert.Equal(OverallState.Ok, (await client.CheckAsync(new CheckArgs { Job = "Pliki" }, headers, cancellationToken: _ct)).Result); // zwykły check — dozwolony
        }
        else
        {
            var r = await client.CheckAsync(new CheckArgs { Job = "Pliki", Repair = true }, headers, cancellationToken: _ct);
            Assert.Equal(OverallState.Ok, r.Result);
            Assert.Empty(r.Damage);
        }
    }
}
