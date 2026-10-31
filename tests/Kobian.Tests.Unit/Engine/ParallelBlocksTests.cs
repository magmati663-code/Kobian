using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Engine.Restore;
using Kobian.Format.Metadata;
using Kobian.Providers.Storage.Local;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>Runda 9: równoległe przygotowanie bloków dużych plików daje DOKŁADNIE ten sam wynik logiczny co ścieżka sekwencyjna.</summary>
public sealed class ParallelBlocksTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-pb-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly RepoKeys _keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
    private readonly byte[] _host = RandomNumberGenerator.GetBytes(16);
    private string Src => Path.Combine(_tmp, "src");

    public ParallelBlocksTests() => Directory.CreateDirectory(Src);

    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    private void MakeSource()
    {
        var big = RandomNumberGenerator.GetBytes((9 << 20) + 17); // 3 bloki, ostatni częściowy
        var when = DateTime.UtcNow.AddDays(-10);
        void Put(string name, byte[] data)
        {
            File.WriteAllBytes(Path.Combine(Src, name), data);
            File.SetLastWriteTimeUtc(Path.Combine(Src, name), when);
        }
        Put("big1.bin", big);
        Put("big2.bin", big); // duplikat w tym samym przebiegu (dedup bloków nie-bazowych)
        Put("text.txt", System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Zażółć gęślą jaźń — kopia. ", 400_000)))); // ~11 MB, ściśliwy
        Put("exact.bin", RandomNumberGenerator.GetBytes(2 * BackupPipeline.ChunkSize)); // dokładnie 2 bloki
        Put("small.txt", "mały"u8.ToArray());
        Put("mixed.bin", [.. RandomNumberGenerator.GetBytes(BackupPipeline.ChunkSize), .. new byte[BackupPipeline.ChunkSize + 5]]); // losowy blok + zera
    }

    private sealed class FlakySource(string root, string failPath, int failAfterBytes) : ISourceTree
    {
        private readonly FileSystemSource _inner = new(root);
        public string DisplayRoot => _inner.DisplayRoot;
        public IEnumerable<SourceNode> List(string relativeDir) => _inner.List(relativeDir);

        public Stream OpenRead(string relativePath) =>
            relativePath == failPath ? new FailingStream(_inner.OpenRead(relativePath), failAfterBytes) : _inner.OpenRead(relativePath);
    }

    private sealed class FailingStream(Stream inner, int failAfter) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= failAfter) throw new IOException("symulowany błąd odczytu");
            var n = inner.Read(buffer, offset, count);
            _read += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private async Task<(BackupResult Result, SnapshotObject Snap, string Repo)> RunAsync(int depth, ISourceTree? source = null)
    {
        var repo = Path.Combine(_tmp, "repo" + depth + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(repo);
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", repo), CredentialRole.Backup, _ct);
        var r = await new BackupPipeline(_keys, new VerifiedUploader(s)) { BlockParallelism = depth }
            .RunAsync(new BackupRequest(source ?? new FileSystemSource(Src), "job", _host, Time: new FixedTime(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero)), RunId: new byte[16]), _ct);
        var snap = r.SnapId is null ? null! : await MetadataStore.LoadSnapshotAsync(s, _keys, r.SnapId, _ct);
        return (r, snap, repo);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Parallel_and_sequential_paths_produce_the_same_snapshot_and_restore_bit_exact()
    {
        MakeSource();
        var seq = await RunAsync(1);
        var par = await RunAsync(3);
        Assert.Equal(BackupStatus.Succeeded, seq.Result.Status);
        Assert.Equal(BackupStatus.Succeeded, par.Result.Status);
        Assert.Equal(seq.Snap.RootTreeId, par.Snap.RootTreeId); // to samo drzewo = te same identyfikatory bloków, rozmiary i kolejność
        Assert.Equal((seq.Result.Stats.Files, seq.Result.Stats.BytesRaw, seq.Result.Stats.BytesNew), (par.Result.Stats.Files, par.Result.Stats.BytesRaw, par.Result.Stats.BytesNew));

        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", par.Repo), CredentialRole.Backup, _ct);
        using var reader = new RepoReader(s, _keys);
        await reader.LoadIndexesAsync(par.Snap, _ct);
        var target = Path.Combine(_tmp, "restore");
        Directory.CreateDirectory(target);
        Assert.Equal(RestoreStatus.Succeeded, (await new RestoreExecutor(reader).RestoreAsync(par.Snap, target, null, _ct)).Status);
        foreach (var f in Directory.GetFiles(Src))
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(f)), SHA256.HashData(File.ReadAllBytes(Path.Combine(target, Path.GetFileName(f)))));
        Assert.Equal(CheckStatus.Succeeded, (await new RepoChecker(s, _keys).CheckSnapshotAsync(par.Snap.SnapId, _ct)).Status);
    }

    [Fact]
    public async Task Read_error_in_the_middle_of_a_big_file_skips_only_that_file()
    {
        MakeSource();
        var r = await RunAsync(3, new FlakySource(Src, "big1.bin", BackupPipeline.ChunkSize + 100));
        Assert.Equal(BackupStatus.Warning, r.Result.Status);
        Assert.Equal(["big1.bin"], r.Result.SkippedPaths);
        Assert.Equal(5, r.Result.Stats.Files); // pozostałe pliki
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", r.Repo), CredentialRole.Backup, _ct);
        Assert.Equal(CheckStatus.Succeeded, (await new RepoChecker(s, _keys).CheckSnapshotAsync(r.Snap.SnapId, _ct)).Status);
    }
}
