using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Jobs;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Providers.Storage.Local;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// RD-73 cz. 2 (it. 119, D4.16 pkt 2–3; PROWIZORYCZNIE Qb63): Agent otwiera N sesji Backup do wysyłki packów (N = wyuczony limit celu albo
/// RecommendedParallelism; Local = 2), sesje dodatkowe przez fabrykę, odmowa otwarcia (KB-2112) = sygnał AIMD i kopia na mniejszej liczbie sesji,
/// limit zapisywany w <c>repo_dest_parallelism</c>. Cel lokalny w katalogu tymczasowym, dane syntetyczne.
/// </summary>
public sealed class ParallelUploadJobTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-pujob-" + Guid.NewGuid().ToString("N"));
    private CatalogWriter _writer = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.Combine(_dir, n);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("src"));
        Directory.CreateDirectory(P("repo"));
        for (var i = 0; i < 6; i++) File.WriteAllBytes(Path.Combine(P("src"), $"f{i}.bin"), RandomNumberGenerator.GetBytes(900_000 + i));
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = Kobian.Crypto.RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(P("wrapped.key"), prov.KeyFileJson);
        File.WriteAllText(P("kobian.repo"), prov.HeaderJson);
        File.WriteAllText(P("pw.txt"), pw + "\n");
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        var def = $$"""{"version":1,"sources":[{{J(P("src"))}}],"destination":{"scheme":"local","root":{{J(P("repo"))}}},"key":{"file":{{J(P("wrapped.key"))}},"passwordFile":{{J(P("pw.txt"))}},"repoHeader":{{J(P("kobian.repo"))}}},"packTargetSize":1048576}""";
        Exec("INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ('j1','Biuro','files',$d,x'00',1,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');",
            ("$d", Encoding.UTF8.GetBytes(def)));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private string DestId => (string)Scalar("SELECT destination_id FROM repo_repositories LIMIT 1;")!;

    private CatalogJobService Jobs(CountingFactory f, bool parallel = true) =>
        new(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = f }, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask)
        {
            UploadParallelism = parallel ? new CatalogParallelismStore(Catalog, _writer, TimeProvider.System) : null,
        };

    [Fact, Trait("Plan", "D4.16"), Trait("RD", "RD-73")]
    public async Task Local_destination_uploads_packs_on_two_sessions_and_the_snapshot_verifies()
    {
        var f = new CountingFactory();
        var jobs = Jobs(f);
        var outcome = await jobs.RunAsync("Biuro", _ct);
        Assert.Equal(JobResult.Succeeded, outcome.Result); // V2 po commit przeszło — wszystkie packi z indeksu są w repo
        Assert.Equal(2, jobs.LastUploadSessions);
        Assert.Equal(3, f.Opens); // blokada + główna + 1 dodatkowa
        Assert.Equal(f.Opens, f.Disposed); // sesja dodatkowa zamknięta
        Assert.Equal(2, f.Probe.Max); // wysyłki packów faktycznie się nakładały, nie więcej niż limit
        Assert.True(Directory.GetFiles(P("repo"), "*", SearchOption.AllDirectories).Count(p => p.Contains($"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) >= 4);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM repo_dest_parallelism;")); // na rekomendowanej — nic do zapamiętania

        Assert.Equal(JobResult.Succeeded, (await jobs.RunAsync("Biuro", _ct)).Result); // przyrost na tej samej puli
    }

    [Fact, Trait("Plan", "D4.16"), Trait("RD", "RD-73")]
    public async Task Without_store_the_job_stays_sequential()
    {
        var f = new CountingFactory();
        var jobs = Jobs(f, parallel: false);
        Assert.Equal(JobResult.Succeeded, (await jobs.RunAsync("Biuro", _ct)).Result);
        Assert.Equal(0, jobs.LastUploadSessions);
        Assert.Equal(2, f.Opens);
        Assert.Equal(1, f.Probe.Max);
    }

    [Fact, Trait("Plan", "D4.16"), Trait("RD", "RD-73")]
    public async Task Refused_extra_session_is_a_limit_signal_and_the_lower_limit_is_learned()
    {
        var f = new CountingFactory { RefuseOpenNumber = 3 };
        var jobs = Jobs(f);
        Assert.Equal(JobResult.Succeeded, (await jobs.RunAsync("Biuro", _ct)).Result);
        Assert.Equal(1, jobs.LastUploadSessions); // kopia poszła na jednej sesji
        Assert.Equal(1, f.Probe.Max);
        Assert.Equal(1L, Scalar($"SELECT parallelism FROM repo_dest_parallelism WHERE destination_id='{DestId}';"));

        // następne uruchomienie startuje od wyuczonego limitu: bez próby otwarcia sesji dodatkowej
        var g = new CountingFactory();
        var again = Jobs(g);
        Assert.Equal(JobResult.Succeeded, (await again.RunAsync("Biuro", _ct)).Result);
        Assert.Equal(1, again.LastUploadSessions);
        Assert.Equal(2, g.Opens);
    }

    /// <summary>Fabryka Local licząca otwarcia/zamknięcia; sesje mierzą wysyłki packów w toku; opcjonalnie n-te otwarcie odmawia jak serwer z limitem sesji.</summary>
    private sealed class CountingFactory : IStorageProviderFactory
    {
        private readonly LocalStorageProviderFactory _inner = new();
        private int _opens;
        private int _disposed;

        public int RefuseOpenNumber { get; init; }

        public Probe Probe { get; } = new();

        public int Opens => Volatile.Read(ref _opens);

        public int Disposed => Volatile.Read(ref _disposed);

        public string Scheme => "local";

        public async ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _opens);
            if (n == RefuseOpenNumber) throw new StorageException(ErrorClass.Transient, KbCode.ServerSessionLimit, "Destination refused a new session (simulated).");
            return new ProbeSession(await _inner.OpenAsync(config, role, ct), Probe, () => Interlocked.Increment(ref _disposed));
        }
    }

    private sealed class Probe
    {
        private int _now;

        public int Max;

        public void Enter()
        {
            var n = Interlocked.Increment(ref _now);
            int m;
            while ((m = Volatile.Read(ref Max)) < n && Interlocked.CompareExchange(ref Max, n, m) != m) { }
        }

        public void Leave() => Interlocked.Decrement(ref _now);
    }

    private sealed class ProbeSession(IStorageSession inner, Probe probe, Action disposed) : IStorageSession
    {
        private int _busy;

        public CredentialRole Role => inner.Role;

        public ProviderCapabilities Capabilities => inner.Capabilities;

        public ServerIdentity? PresentedIdentity => inner.PresentedIdentity;

        public ValueTask<StatResult?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

        public IAsyncEnumerable<StatResult> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);

        public ValueTask<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct) => inner.OpenReadAsync(path, offset, length, ct);

        public async ValueTask<IObjectWriter> CreateAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct)
        {
            if (!path.StartsWith("data/", StringComparison.Ordinal)) return await inner.CreateAsync(path, expectedLength, mode, resumeFrom, ct);
            if (Interlocked.Exchange(ref _busy, 1) == 1) throw new InvalidOperationException("session used concurrently"); // sesja jednowątkowa (D4.10)
            probe.Enter();
            await Task.Delay(40, ct);
            return new W(await inner.CreateAsync(path, expectedLength, mode, resumeFrom, ct), () => { probe.Leave(); Volatile.Write(ref _busy, 0); });
        }

        public ValueTask<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);

        public ValueTask DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

        public ValueTask<byte[]?> ServerHashAsync(string path, HashAlgorithmName algorithm, CancellationToken ct) => inner.ServerHashAsync(path, algorithm, ct);

        public async ValueTask DisposeAsync()
        {
            disposed();
            await inner.DisposeAsync();
        }

        private sealed class W(IObjectWriter w, Action done) : IObjectWriter
        {
            private int _done;

            public long CommittedOffset => w.CommittedOffset;

            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => w.WriteAsync(data, ct);

            public async ValueTask<StatResult> CommitAsync(CancellationToken ct)
            {
                try { return await w.CommitAsync(ct); }
                finally { Done(); }
            }

            public async ValueTask AbortAsync(CancellationToken ct)
            {
                try { await w.AbortAsync(ct); }
                finally { Done(); }
            }

            public async ValueTask DisposeAsync()
            {
                Done();
                await w.DisposeAsync();
            }

            private void Done()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0) done();
            }
        }
    }
}
