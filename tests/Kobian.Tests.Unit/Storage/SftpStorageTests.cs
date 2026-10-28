using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Sftp;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>BL-33: dostawca SFTP na serwerze w pamięci (D4.4, D4.6, D4.7, D4.8); dane syntetyczne.</summary>
public sealed class SftpStorageTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeSftpServer _srv = new();

    private ValueTask<IStorageSession> Open(CredentialRole role = CredentialRole.Backup, string root = FakeSftpServer.Root) =>
        new SftpStorageProviderFactory(_srv).OpenAsync(new DestinationConfig("d1", "sftp", root), role, _ct);

    private static async Task<StatResult> Put(IStorageSession s, string path, byte[] data, WriteMode mode, CancellationToken ct)
    {
        await using var w = await s.CreateAsync(path, data.Length, mode, 0, ct);
        await w.WriteAsync(data, ct);
        return await w.CommitAsync(ct);
    }

    [Theory, Trait("AC", "AC-18.1")]
    [InlineData(WriteMode.StagedRename)]
    [InlineData(WriteMode.DirectFinal)]
    public async Task Write_and_read_back(WriteMode mode)
    {
        await using var s = await Open();
        var data = RandomNumberGenerator.GetBytes(10_000);
        var st = await Put(s, "data/ab/0123abcd.pack", data, mode, _ct);
        Assert.Equal(10_000, st.Length);
        await using var r = await s.OpenReadAsync("data/ab/0123abcd.pack", 0, null, _ct);
        using var ms = new MemoryStream();
        await r.CopyToAsync(ms, _ct);
        Assert.Equal(data, ms.ToArray());
        Assert.DoesNotContain(_srv.Paths(), p => p.Contains(".tmp-", StringComparison.Ordinal));
    }

    [Fact, Trait("AC", "AC-18.1")]
    public async Task Never_overwrites_existing_object()
    {
        await using var s = await Open();
        await Put(s, "data/aa/x.pack", [1, 2, 3], WriteMode.StagedRename, _ct);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "data/aa/x.pack", [9, 9, 9], WriteMode.StagedRename, _ct));
        Assert.Equal(KbCode.ObjectExists, ex.Code);
        var ex2 = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "data/aa/x.pack", [9, 9, 9], WriteMode.DirectFinal, _ct));
        Assert.Equal(KbCode.ObjectExists, ex2.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, _srv.Read("/srv/repo/data/aa/x.pack"));
    }

    [Fact, Trait("AC", "AC-18.1")]
    public async Task Rename_race_is_reported_as_exists_not_overwritten()
    {
        await using var s = await Open();
        await using var w = await s.CreateAsync("data/bb/y.pack", 3, WriteMode.StagedRename, 0, _ct);
        await w.WriteAsync(new byte[] { 7, 7, 7 }, _ct);
        _srv.PutFile("/srv/repo/data/bb/y.pack", [1]); // ktoś inny zdążył
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await w.CommitAsync(_ct));
        Assert.Equal(KbCode.ObjectExists, ex.Code);
        Assert.Equal(new byte[] { 1 }, _srv.Read("/srv/repo/data/bb/y.pack"));
    }

    [Fact, Trait("SEC", "D4.1")]
    public async Task Backup_session_cannot_delete_maintenance_can()
    {
        _srv.PutFile("/srv/repo/data/cc/z.pack", [1]);
        await using (var b = await Open())
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await b.DeleteAsync("data/cc/z.pack", _ct));
        Assert.True(_srv.Exists("/srv/repo/data/cc/z.pack"));
        await using var m = await Open(CredentialRole.Maintenance);
        await m.DeleteAsync("data/cc/z.pack", _ct);
        Assert.False(_srv.Exists("/srv/repo/data/cc/z.pack"));
    }

    [Theory, Trait("SEC", "D4.6")]
    [InlineData("../x")]
    [InlineData("/abs/x")]
    [InlineData("data//x")]
    [InlineData("DATA/x")]
    public async Task Invalid_paths_rejected(string path)
    {
        await using var s = await Open();
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await s.StatAsync(path, _ct));
        Assert.Equal(KbCode.InvalidObjectName, ex.Code);
    }

    [Theory, Trait("SEC", "D4.6")]
    [InlineData("relative/root")]
    [InlineData("")]
    [InlineData("/srv/../etc")]
    [InlineData("/srv/./repo")]
    public async Task Invalid_roots_rejected(string root)
    {
        await Assert.ThrowsAsync<StorageException>(async () => await Open(root: root));
    }

    [Fact, Trait("SEC", "D8.9")]
    public async Task Symlink_in_path_refused_for_write_and_read()
    {
        _srv.PutSymlink("/srv/repo/data");
        await using var s = await Open();
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "data/ab/p.pack", [1], WriteMode.DirectFinal, _ct));
        Assert.Equal(KbCode.ReparsePointRefused, ex.Code);
        Assert.False(_srv.Exists("/srv/repo/data/ab/p.pack"));
        _srv.PutSymlink("/srv/repo/snapshots-link");
        var ex2 = await Assert.ThrowsAsync<StorageException>(async () => await s.OpenReadAsync("snapshots-link", 0, null, _ct));
        Assert.Equal(KbCode.ReparsePointRefused, ex2.Code);
    }

    [Fact]
    public async Task List_skips_foreign_files_and_symlinks()
    {
        _srv.PutFile("/srv/repo/data/aa/ok.pack", [1, 2]);
        _srv.PutFile("/srv/repo/data/aa/Foreign File.txt", [1]);
        _srv.PutSymlink("/srv/repo/data/aa/link.pack");
        await using var s = await Open();
        var all = new List<string>();
        await foreach (var e in s.ListAsync("", _ct)) all.Add(e.Path);
        Assert.Equal(["data/aa/ok.pack"], all);
    }

    [Fact]
    public async Task Range_read_returns_requested_slice()
    {
        var data = RandomNumberGenerator.GetBytes(1000);
        _srv.PutFile("/srv/repo/data/aa/r.pack", data);
        await using var s = await Open();
        await using var r = await s.OpenReadAsync("data/aa/r.pack", 100, 50, _ct);
        using var ms = new MemoryStream();
        await r.CopyToAsync(ms, _ct);
        Assert.Equal(data[100..150], ms.ToArray());
    }

    [Fact, Trait("AC", "AC-04.5")]
    public async Task Connection_drop_mid_write_is_transient_and_directfinal_resumes()
    {
        var data = RandomNumberGenerator.GetBytes(3000);
        await using (var s1 = await Open())
        {
            _srv.DropAfterBytes = 1200;
            await using var w = await s1.CreateAsync("data/dd/big.pack", data.Length, WriteMode.DirectFinal, 0, _ct);
            var ex = await Assert.ThrowsAsync<StorageException>(async () => await w.WriteAsync(data, _ct));
            Assert.Equal(ErrorClass.Transient, ex.ErrorClass);
            Assert.Equal(KbCode.TransientIo, ex.Code);
        }
        Assert.Equal(1200, _srv.Read("/srv/repo/data/dd/big.pack")!.Length);
        await using var s2 = await Open();
        var st = await s2.StatAsync("data/dd/big.pack", _ct);
        await using var w2 = await s2.CreateAsync("data/dd/big.pack", data.Length, WriteMode.DirectFinal, st!.Length, _ct);
        await w2.WriteAsync(data.AsMemory((int)st.Length), _ct);
        await w2.CommitAsync(_ct);
        Assert.Equal(data, _srv.Read("/srv/repo/data/dd/big.pack"));
    }

    [Fact]
    public async Task Resume_with_wrong_offset_abandons_object()
    {
        _srv.PutFile("/srv/repo/data/ee/o.pack", new byte[10]);
        await using var s = await Open();
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await s.CreateAsync("data/ee/o.pack", 100, WriteMode.DirectFinal, 5, _ct).AsTask());
        Assert.Equal(KbCode.SizeMismatch, ex.Code);
    }

    [Fact]
    public async Task Write_beyond_expected_length_rejected_and_short_object_fails_commit()
    {
        await using var s = await Open();
        await using var w = await s.CreateAsync("data/ff/s.pack", 4, WriteMode.StagedRename, 0, _ct);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await w.WriteAsync(new byte[5], _ct));
        Assert.Equal(KbCode.SizeMismatch, ex.Code);
        await w.WriteAsync(new byte[3], _ct);
        var ex2 = await Assert.ThrowsAsync<StorageException>(async () => await w.CommitAsync(_ct));
        Assert.Equal(KbCode.SizeMismatch, ex2.Code);
        Assert.False(_srv.Exists("/srv/repo/data/ff/s.pack"));
    }

    [Theory, Trait("AC", "AC-04.6")]
    [InlineData(SftpStatus.NoSpaceOnFilesystem, KbCode.DestinationFull, ErrorClass.Permanent)]
    [InlineData(SftpStatus.QuotaExceeded, KbCode.DestinationFull, ErrorClass.Permanent)]
    [InlineData(SftpStatus.PermissionDenied, KbCode.AccessDenied, ErrorClass.Permanent)]
    [InlineData(SftpStatus.NoConnection, KbCode.TransientIo, ErrorClass.Transient)]
    [InlineData(SftpStatus.SessionLimit, KbCode.ServerSessionLimit, ErrorClass.Transient)]
    public async Task Open_failures_are_classified(SftpStatus status, string code, ErrorClass cls)
    {
        await using var s = await Open();
        _srv.FailOpenWithStatus = status;
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await s.CreateAsync("data/gg/f.pack", 1, WriteMode.StagedRename, 0, _ct).AsTask());
        Assert.Equal(code, ex.Code);
        Assert.Equal(cls, ex.ErrorClass);
    }

    [Fact, Trait("AC", "AC-04.6")]
    public async Task Session_limit_and_auth_rejection_map_to_kb_codes()
    {
        _srv.OpenSessionLimit = 0;
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Open());
        Assert.Equal(KbCode.ServerSessionLimit, ex.Code);
        Assert.Equal(ErrorClass.Transient, ex.ErrorClass);
        var mapped = SftpErrorMap.Map(new SftpProtocolException(SftpStatus.AuthRejected, "x"));
        Assert.Equal(KbCode.AuthRejected, mapped.Code);
        Assert.Equal(ErrorClass.Permanent, mapped.ErrorClass);
    }

    [Fact]
    public async Task Missing_root_is_destination_not_found()
    {
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Open(root: "/srv/nonexistent"));
        Assert.Equal(KbCode.DestinationNotFound, ex.Code);
    }

    [Fact]
    public async Task Capabilities_and_free_space_follow_server_features()
    {
        await using var s = await Open();
        Assert.NotEqual(StorageCaps.None, s.Capabilities.Caps & StorageCaps.FreeSpace);
        Assert.Equal(StorageCaps.None, s.Capabilities.Caps & StorageCaps.DeleteAllowed);
        Assert.Equal(1L << 40, await s.GetFreeSpaceAsync(_ct));
        Assert.Equal("ssh-ed25519", s.PresentedIdentity!.Kind);
        _srv.Features = SftpFeatures.None;
        await using var s2 = await Open(CredentialRole.Maintenance);
        Assert.Equal(StorageCaps.None, s2.Capabilities.Caps & StorageCaps.FreeSpace);
        Assert.NotEqual(StorageCaps.None, s2.Capabilities.Caps & StorageCaps.DeleteAllowed);
        Assert.Null(await s2.GetFreeSpaceAsync(_ct));
    }

    [Fact]
    public async Task Wrong_scheme_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new SftpStorageProviderFactory(_srv).OpenAsync(new DestinationConfig("d", "local", "/srv/repo"), CredentialRole.Backup, _ct));
    }
}
