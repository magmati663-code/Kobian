using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Local;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>D4.6/D4.7 na dostawcy lokalnym; dane syntetyczne w katalogu tymczasowym.</summary>
public sealed class LocalStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kobian-test-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public LocalStorageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ValueTask<IStorageSession> Open(CredentialRole role = CredentialRole.Backup) =>
        new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d1", "local", _root), role, _ct);

    private async Task<StatResult> Put(IStorageSession s, string path, byte[] data, WriteMode mode)
    {
        await using var w = await s.CreateAsync(path, data.Length, mode, 0, _ct);
        await w.WriteAsync(data, _ct);
        return await w.CommitAsync(_ct);
    }

    [Theory]
    [InlineData(WriteMode.StagedRename)]
    [InlineData(WriteMode.DirectFinal)]
    public async Task Write_and_read_back(WriteMode mode)
    {
        await using var s = await Open();
        var data = RandomNumberGenerator.GetBytes(10_000);
        var st = await Put(s, "data/ab/0123abcd.pack", data, mode);
        Assert.Equal(10_000, st.Length);
        await using var r = await s.OpenReadAsync("data/ab/0123abcd.pack", 0, null, _ct);
        using var ms = new MemoryStream();
        await r.CopyToAsync(ms, _ct);
        Assert.Equal(data, ms.ToArray());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "data", "ab"), "*.tmp-*"));
    }

    [Fact, Trait("AC", "AC-18.1")]
    public async Task Range_read_returns_exact_slice()
    {
        await using var s = await Open();
        var data = RandomNumberGenerator.GetBytes(4096);
        await Put(s, "data/00/pack1", data, WriteMode.DirectFinal);
        await using var r = await s.OpenReadAsync("data/00/pack1", 4096 - 16, 16, _ct);
        var buf = new byte[64];
        var n = await r.ReadAtLeastAsync(buf, 64, throwOnEndOfStream: false, _ct);
        Assert.Equal(16, n);
        Assert.Equal(data[^16..], buf[..16]);
    }

    [Theory]
    [InlineData(WriteMode.StagedRename)]
    [InlineData(WriteMode.DirectFinal)]
    public async Task Existing_object_is_never_overwritten(WriteMode mode)
    {
        await using var s = await Open();
        await Put(s, "index/aa.idx", [1, 2, 3], WriteMode.DirectFinal);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "index/aa.idx", [9, 9, 9], mode));
        Assert.Equal(KbCode.ObjectExists, ex.Code);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(_root, "index", "aa.idx"), _ct));
    }

    [Fact, Trait("AC", "AC-18.1")]
    public async Task DirectFinal_resumes_from_committed_offset()
    {
        await using var s = await Open();
        var data = RandomNumberGenerator.GetBytes(3000);
        var w = await s.CreateAsync("data/01/p", data.Length, WriteMode.DirectFinal, 0, _ct);
        await w.WriteAsync(data.AsMemory(0, 1000), _ct);
        await w.AbortAsync(_ct); // przerwanie: plik niekompletny zostaje (DirectFinal)
        await using var w2 = await s.CreateAsync("data/01/p", data.Length, WriteMode.DirectFinal, 1000, _ct);
        await w2.WriteAsync(data.AsMemory(1000), _ct);
        await w2.CommitAsync(_ct);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_root, "data", "01", "p"), _ct));
    }

    [Fact]
    public async Task Resume_with_mismatched_offset_requires_abandon()
    {
        await using var s = await Open();
        var w = await s.CreateAsync("data/01/q", 100, WriteMode.DirectFinal, 0, _ct);
        await w.WriteAsync(new byte[10], _ct);
        await w.AbortAsync(_ct);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await s.CreateAsync("data/01/q", 100, WriteMode.DirectFinal, 50, _ct));
        Assert.Equal(KbCode.SizeMismatch, ex.Code);
    }

    [Fact]
    public async Task Commit_with_short_write_fails_and_leaves_no_final_object()
    {
        await using var s = await Open();
        var w = await s.CreateAsync("snapshots/s1.snap", 100, WriteMode.StagedRename, 0, _ct);
        await w.WriteAsync(new byte[40], _ct);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await w.CommitAsync(_ct));
        Assert.Equal(KbCode.SizeMismatch, ex.Code);
        await w.AbortAsync(_ct);
        Assert.False(File.Exists(Path.Combine(_root, "snapshots", "s1.snap")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "snapshots")));
    }

    [Fact]
    public async Task Write_beyond_expected_length_is_rejected()
    {
        await using var s = await Open();
        await using var w = await s.CreateAsync("locks/l1.lock", 4, WriteMode.DirectFinal, 0, _ct);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await w.WriteAsync(new byte[5], _ct));
        Assert.Equal(KbCode.SizeMismatch, ex.Code);
    }

    [Fact, Trait("SEC", "D4.1")]
    public async Task Backup_session_cannot_delete()
    {
        await using var s = await Open(CredentialRole.Backup);
        await Put(s, "data/00/x", [1], WriteMode.DirectFinal);
        Assert.Equal(StorageCaps.None, s.Capabilities.Caps & StorageCaps.DeleteAllowed);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await s.DeleteAsync("data/00/x", _ct));
        Assert.True(File.Exists(Path.Combine(_root, "data", "00", "x")));
        await using var m = await Open(CredentialRole.Maintenance);
        await m.DeleteAsync("data/00/x", _ct);
        Assert.False(File.Exists(Path.Combine(_root, "data", "00", "x")));
    }

    [Theory, Trait("SEC", "D4.6")]
    [InlineData("../escape")]
    [InlineData("data/../../escape")]
    [InlineData("/abs")]
    [InlineData("Data/UPPER")]
    [InlineData("data//x")]
    [InlineData("data/./x")]
    [InlineData("data\\x")]
    [InlineData("C:/x")]
    [InlineData("data/x/")]
    [InlineData("")]
    public async Task Invalid_paths_are_rejected(string path)
    {
        await using var s = await Open();
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await s.StatAsync(path, _ct));
        Assert.Equal(KbCode.InvalidObjectName, ex.Code);
    }

    [Fact, Trait("SEC", "D8.9")]
    public async Task Link_inside_repository_is_refused()
    {
        var outside = Path.Combine(_root + "-outside");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "data"), outside);
            await using var s = await Open();
            var ex = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "data/00/x", [1], WriteMode.DirectFinal));
            Assert.Equal(KbCode.ReparsePointRefused, ex.Code);
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task List_returns_repo_objects_with_forward_slashes()
    {
        await using var s = await Open();
        await Put(s, "data/ab/p1", [1], WriteMode.DirectFinal);
        await Put(s, "index/i1.idx", [2], WriteMode.DirectFinal);
        await File.WriteAllTextAsync(Path.Combine(_root, "Desktop.ini"), "obcy plik", _ct);
        var all = new List<string>();
        await foreach (var st in s.ListAsync("", _ct)) all.Add(st.Path);
        all.Sort(StringComparer.Ordinal);
        Assert.Equal(["data/ab/p1", "index/i1.idx"], all);
    }

    [Fact]
    public async Task Missing_root_is_permanent_error()
    {
        var ex = await Assert.ThrowsAsync<StorageException>(async () =>
            await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", _root + "-nope"), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.DestinationNotFound, ex.Code);
    }
}
