using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;
using Kobian.Format.Pack;
using Kobian.Format.Repo;
using Kobian.Providers.Storage.Local;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>Runda 5: odczyt pojedynczej ramki przez zakres bajtów daje to samo co odczyt całego packa i wykrywa naruszenie ramki.</summary>
public sealed class RangeReadTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-rr-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly RepoKeys _keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
    private string Src => Path.Combine(_tmp, "src");
    private string Repo => Path.Combine(_tmp, "repo");

    public RangeReadTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Repo);
    }

    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    private async Task<(IStorageSession Session, SnapshotObject Snap)> BackupAsync()
    {
        for (var d = 0; d < 6; d++)
        {
            Directory.CreateDirectory(Path.Combine(Src, "d" + d, "sub"));
            for (var f = 0; f < 5; f++) File.WriteAllBytes(Path.Combine(Src, "d" + d, "sub", $"f{f}.bin"), RandomNumberGenerator.GetBytes(2000 + (d * 1000) + f));
        }
        var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", Repo), CredentialRole.Backup, _ct);
        var r = await new BackupPipeline(_keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(new FileSystemSource(Src), "job", RandomNumberGenerator.GetBytes(16)), _ct);
        return (s, await MetadataStore.LoadSnapshotAsync(s, _keys, r.SnapId!, _ct));
    }

    private static async Task<List<byte[]>> TreeIdsAsync(RepoReader reader, byte[] root, CancellationToken ct)
    {
        var ids = new List<byte[]>();
        var stack = new Stack<byte[]>([root]);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            ids.Add(id);
            foreach (var e in await reader.ReadTreeAsync(id, ct))
                if (e.SubtreeId is { } sub) stack.Push(sub);
        }
        return ids;
    }

    [Fact]
    public async Task Range_reader_returns_the_same_trees_and_blocks_as_the_whole_pack_reader()
    {
        var (s, snap) = await BackupAsync();
        await using var session = s;
        using var whole = new RepoReader(s, _keys);
        await whole.LoadIndexesAsync(snap, _ct);
        using var range = new RepoReader(s, _keys) { UseRangeReads = true, PackCacheBudget = 1 };
        await range.LoadIndexesAsync(snap, _ct);

        var ids = await TreeIdsAsync(whole, snap.RootTreeId, _ct);
        Assert.True(ids.Count >= 13); // korzeń + 6 × (katalog + podkatalog)
        foreach (var id in ids)
            Assert.Equal(await whole.ReadBlockAsync(id, BlobType.Tree, _ct), await range.ReadBlockAsync(id, BlobType.Tree, _ct));
        var fileChunk = (await whole.ReadTreeAsync(ids[^1], _ct)).First(e => e.Chunks is { Count: > 0 }).Chunks![0];
        Assert.Equal(await whole.ReadBlockAsync(fileChunk, BlobType.Data, _ct), await range.ReadBlockAsync(fileChunk, BlobType.Data, _ct));

        // czytnik tylko z blokami drzew (wyspecjalizowany indeks) czyta drzewa tak samo
        using var treesOnly = new RepoReader(s, _keys) { UseRangeReads = true };
        await treesOnly.LoadTreeIndexAsync(snap, _ct);
        Assert.Equal(await whole.ReadBlockAsync(snap.RootTreeId, BlobType.Tree, _ct), await treesOnly.ReadBlockAsync(snap.RootTreeId, BlobType.Tree, _ct));
        Assert.False(treesOnly.TryLocate(fileChunk, out _)); // bloków danych nie zna
    }

    [Fact]
    public async Task Tampering_with_a_frame_is_detected_by_the_range_reader()
    {
        var (s, snap) = await BackupAsync();
        await using var session = s;
        using var whole = new RepoReader(s, _keys);
        await whole.LoadIndexesAsync(snap, _ct);
        Assert.True(whole.TryLocate(snap.RootTreeId, out var loc));
        var packFile = Path.Combine(Repo, RepoPaths.Pack(loc!.PackId).Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(packFile, _ct);
        bytes[loc.Entry.Offset + 4 + 5] ^= 0x01; // jeden bit w ramce korzenia drzewa
        await File.WriteAllBytesAsync(packFile, bytes, _ct);

        using var range = new RepoReader(s, _keys) { UseRangeReads = true };
        await range.LoadIndexesAsync(snap, _ct);
        var ex = await Assert.ThrowsAsync<KobianException>(() => range.ReadTreeAsync(snap.RootTreeId, _ct));
        Assert.Equal(KbCode.AuthenticationFailed, ex.Code);
    }

    [Fact]
    public async Task Frame_moved_to_another_position_is_rejected()
    {
        var (s, snap) = await BackupAsync();
        await using var session = s;
        using var whole = new RepoReader(s, _keys);
        await whole.LoadIndexesAsync(snap, _ct);
        var ids = await TreeIdsAsync(whole, snap.RootTreeId, _ct);
        // dwie różne ramki: odczyt „ramki B” spod położenia „ramki A” musi się nie powieść (pozycja i chunkId są w danych dodatkowych AEAD)
        Assert.True(whole.TryLocate(ids[0], out var a));
        Assert.True(whole.TryLocate(ids[1], out var b));
        var swapped = new BlockLocation(a!.PackId, a.PackSize, a.PackSha256, a.Entry with { Offset = b!.Entry.Offset, FrameLength = b.Entry.FrameLength, RawLength = b.Entry.RawLength }, a.EntryIndex);
        if (!a.PackId.AsSpan().SequenceEqual(b.PackId)) return; // różne packi — scenariusz nie dotyczy (zwykle wszystkie w jednym)
        await using var rs = await s.OpenReadAsync(RepoPaths.Pack(swapped.PackId), swapped.Entry.Offset, 4 + swapped.Entry.FrameLength, _ct);
        var buf = new byte[4 + swapped.Entry.FrameLength];
        await rs.ReadExactlyAsync(buf, _ct);
        var key = _keys.DerivePackKey(swapped.PackId);
        using var cipher = new Kobian.Crypto.AesGcmCipher(key);
        Assert.Throws<KobianException>(() => Kobian.Format.Pack.PackReader.OpenFrame(cipher, _keys.RepoId, swapped.PackId, swapped.EntryIndex, swapped.Entry, buf[4..]));
    }
}
