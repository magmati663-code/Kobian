using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;
using Kobian.Format.Repo;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>RD-48: wysyłka packu wprost z bufora strumienia (bez ToArray) — na celu dokładnie bajty packu, bez zapasu pojemności bufora.</summary>
public sealed class PackUploadNoCopyTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly RepoKeys _keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
    private readonly string _src = Path.Combine(Path.GetTempPath(), "kobian-rd48-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));

    public PackUploadNoCopyTests() => Directory.CreateDirectory(_src);

    public void Dispose()
    {
        _keys.Dispose();
        if (Directory.Exists(_src)) Directory.Delete(_src, recursive: true);
    }

    [Fact, Trait("RD", "RD-48"), Trait("D", "D4.14")]
    public async Task Upload_of_memory_slice_writes_only_the_slice_and_returns_its_hash()
    {
        var store = new MemoryStorage();
        var backing = RandomNumberGenerator.GetBytes(10_000);
        var slice = new ReadOnlyMemory<byte>(backing, 0, 7_777); // jak GetBuffer(): pojemność > długość
        var sha = await new VerifiedUploader(store.Open()).UploadAsync("data/x", slice, _ct);

        Assert.Equal(slice.ToArray(), store.Objects["data/x"]);
        Assert.Equal(SHA256.HashData(slice.Span), sha);
    }

    [Fact, Trait("RD", "RD-48")]
    public async Task Byte_array_overload_is_equivalent()
    {
        var store = new MemoryStorage(atomicRename: false);
        var data = RandomNumberGenerator.GetBytes(4_097);
        var sha = await new VerifiedUploader(store.Open()).UploadAsync("data/y", data, _ct);
        Assert.Equal(data, store.Objects["data/y"]);
        Assert.Equal(SHA256.HashData(data), sha);
    }

    [Fact, Trait("RD", "RD-48"), Trait("D", "D3.14")]
    public async Task Packs_on_target_match_index_size_and_hash_exactly()
    {
        // pliki o rozmiarach niewyrównanych → bufor MemoryStream ma zapas pojemności przy każdym packu
        for (var i = 0; i < 6; i++)
            await File.WriteAllBytesAsync(Path.Combine(_src, $"f{i}.bin"), RandomNumberGenerator.GetBytes(300_000 + (i * 12_345) + 7), _ct);
        var store = new MemoryStorage();
        var s = store.Open();
        var r = await new BackupPipeline(_keys, new VerifiedUploader(s)).RunAsync(
            new BackupRequest(new FileSystemSource(_src), "job-rd48", RandomNumberGenerator.GetBytes(16), PackTargetSize: 512 * 1024), _ct);
        Assert.Equal(BackupStatus.Succeeded, r.Status);

        var snap = await MetadataStore.LoadSnapshotAsync(s, _keys, r.SnapId!, _ct);
        var (idx, _) = await MetadataStore.LoadIndexAsync(s, _keys, snap.Indexes[0].IndexId, _ct);
        Assert.True(idx.Packs.Count >= 2);
        foreach (var pk in idx.Packs)
        {
            var obj = store.Objects[RepoPaths.Pack(pk.PackId)];
            Assert.Equal(pk.Size, obj.LongLength);
            Assert.Equal(pk.Sha256, SHA256.HashData(obj));
        }
    }
}
