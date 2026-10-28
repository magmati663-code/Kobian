using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Ftp;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>BL-34 / KOSZT-22: dostawca FTP/FTPS na serwerze w pamięci (D4.3, D4.6, D4.7, D4.8, D4.14); dane syntetyczne.</summary>
public sealed class FtpStorageTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeFtpServer _srv = new();
    private readonly List<TimeSpan> _delays = [];

    private FtpStorageProviderFactory Factory(string scheme = "ftps") =>
        new(_srv, scheme) { Delay = (d, _) => { _delays.Add(d); return Task.CompletedTask; } };

    private ValueTask<IStorageSession> Open(CredentialRole role = CredentialRole.Backup, string root = FakeFtpServer.Root) =>
        Factory().OpenAsync(new DestinationConfig("d1", "ftps", root, "file:ftp1"), role, _ct);

    private static async Task<StatResult> Put(IStorageSession s, string path, byte[] data, WriteMode mode, CancellationToken ct)
    {
        await using var w = await s.CreateAsync(path, data.Length, mode, 0, ct);
        await w.WriteAsync(data, ct);
        return await w.CommitAsync(ct);
    }

    [Theory, Trait("AC", "AC-18.1"), Trait("RD", "D4.7")]
    [InlineData(WriteMode.StagedRename)]
    [InlineData(WriteMode.DirectFinal)]
    public async Task Write_and_read_back(WriteMode mode)
    {
        await using var s = await Open();
        var data = RandomNumberGenerator.GetBytes(10_000);
        var st = await Put(s, "data/ab/0123abcd.pack", data, mode, _ct);
        Assert.Equal(10_000, st.Length);
        await using (var r = await s.OpenReadAsync("data/ab/0123abcd.pack", 0, null, _ct))
        {
            using var ms = new MemoryStream();
            await r.CopyToAsync(ms, _ct);
            Assert.Equal(data, ms.ToArray());
        }
        Assert.DoesNotContain(_srv.Paths(), p => p.Contains(".tmp-", StringComparison.Ordinal));
        if (mode == WriteMode.StagedRename)
        {
            // STOR do pliku tymczasowego, potem RNFR/RNTO — nigdy STOR bezpośrednio na nazwę docelową.
            Assert.DoesNotContain("STOR /kopie/data/ab/0123abcd.pack", _srv.Commands);
            Assert.Contains("RNTO /kopie/data/ab/0123abcd.pack", _srv.Commands);
        }
    }

    [Fact, Trait("RD", "D4.14")]
    public async Task Size_is_always_queried_in_binary_mode()
    {
        await using var s = await Open();
        await Put(s, "data/ab/t.pack", [1, 2, 3], WriteMode.StagedRename, _ct);
        var idx = _srv.Commands.Select((c, i) => (c, i)).Where(x => x.c.StartsWith("SIZE ", StringComparison.Ordinal)).Select(x => x.i).ToList();
        Assert.NotEmpty(idx);
        Assert.All(idx, i => Assert.Equal("TYPE I", _srv.Commands[i - 1]));
    }

    [Fact, Trait("AC", "AC-18.1"), Trait("SEC", "D4.7")]
    public async Task Never_overwrites_existing_object_although_server_would()
    {
        await using var s = await Open();
        await Put(s, "data/aa/x.pack", [1, 2, 3], WriteMode.StagedRename, _ct);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "data/aa/x.pack", [9, 9, 9], WriteMode.StagedRename, _ct));
        Assert.Equal(KbCode.ObjectExists, ex.Code);
        var ex2 = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "data/aa/x.pack", [9, 9, 9], WriteMode.DirectFinal, _ct));
        Assert.Equal(KbCode.ObjectExists, ex2.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, _srv.Read("/kopie/data/aa/x.pack"));
        Assert.Equal(1, _srv.Commands.Count(c => c.StartsWith("STOR ", StringComparison.Ordinal)));
    }

    [Fact, Trait("AC", "AC-18.1")]
    public async Task Object_created_before_commit_is_reported_as_exists_not_overwritten()
    {
        await using var s = await Open();
        await using var w = await s.CreateAsync("data/bb/y.pack", 3, WriteMode.StagedRename, 0, _ct);
        await w.WriteAsync(new byte[] { 7, 7, 7 }, _ct);
        _srv.PutFile("/kopie/data/bb/y.pack", [1]); // ktoś inny zdążył przed sprawdzeniem
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await w.CommitAsync(_ct));
        Assert.Equal(KbCode.ObjectExists, ex.Code);
        Assert.Equal(new byte[] { 1 }, _srv.Read("/kopie/data/bb/y.pack"));
        Assert.DoesNotContain(_srv.Commands, c => c.StartsWith("RNTO ", StringComparison.Ordinal));
    }

    [Fact, Trait("AC", "AC-18.1")]
    public async Task Rnto_refused_by_server_because_target_appeared_is_exists()
    {
        // Okno wyścigu między sprawdzeniem a RNTO: serwery odrzucające RNTO na istniejący cel (IIS, FileZilla) → KB-2301, nie błąd przejściowy.
        await using var s = await Open();
        await using var w = await s.CreateAsync("data/bc/z.pack", 2, WriteMode.StagedRename, 0, _ct);
        await w.WriteAsync(new byte[] { 5, 5 }, _ct);
        _srv.BeforeRnto = () => _srv.PutFile("/kopie/data/bc/z.pack", [4]);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await w.CommitAsync(_ct));
        Assert.Equal(KbCode.ObjectExists, ex.Code);
        Assert.Equal(new byte[] { 4 }, _srv.Read("/kopie/data/bc/z.pack"));
    }

    [Fact, Trait("SEC", "D4.1"), Trait("RD", "D4.6")]
    public async Task Backup_session_cannot_delete_maintenance_can()
    {
        _srv.PutFile("/kopie/data/cc/z.pack", [1]);
        await using (var b = await Open())
        {
            Assert.Equal(StorageCaps.None, b.Capabilities.Caps & StorageCaps.DeleteAllowed);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await b.DeleteAsync("data/cc/z.pack", _ct));
        }
        Assert.True(_srv.Exists("/kopie/data/cc/z.pack"));
        Assert.DoesNotContain(_srv.Commands, c => c.StartsWith("DELE ", StringComparison.Ordinal));
        await using var m = await Open(CredentialRole.Maintenance);
        await m.DeleteAsync("data/cc/z.pack", _ct);
        Assert.False(_srv.Exists("/kopie/data/cc/z.pack"));
        Assert.Equal([CredentialRole.Backup, CredentialRole.Maintenance], _srv.Roles);
    }

    [Fact, Trait("SEC", "D4.1")]
    public async Task Aborted_staged_write_leaves_tmp_for_maintenance_and_never_deletes()
    {
        await using var s = await Open();
        await using (var w = await s.CreateAsync("data/cd/a.pack", 10, WriteMode.StagedRename, 0, _ct))
        {
            await w.WriteAsync(new byte[3], _ct);
            await w.AbortAsync(_ct);
        }
        Assert.Contains(_srv.Paths(), p => p.StartsWith("/kopie/data/cd/a.pack.tmp-", StringComparison.Ordinal));
        Assert.False(_srv.Exists("/kopie/data/cd/a.pack"));
        Assert.DoesNotContain(_srv.Commands, c => c.StartsWith("DELE ", StringComparison.Ordinal));
    }

    [Theory, Trait("SEC", "D4.6")]
    [InlineData("relative/root")]
    [InlineData("")]
    [InlineData("/srv/../etc")]
    [InlineData("/srv/./repo")]
    public async Task Invalid_roots_rejected(string root)
    {
        await Assert.ThrowsAsync<StorageException>(async () => await Open(root: root));
        Assert.Equal(0, _srv.Connections);
    }

    [Theory, Trait("SEC", "D4.6")]
    [InlineData("../x")]
    [InlineData("/abs/x")]
    [InlineData("data//x")]
    public async Task Invalid_paths_rejected(string path)
    {
        await using var s = await Open();
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await s.StatAsync(path, _ct));
        Assert.Equal(KbCode.InvalidObjectName, ex.Code);
    }

    [Fact, Trait("SEC", "D8.9")]
    public async Task Link_in_path_refused_for_write_and_read()
    {
        _srv.PutLink("/kopie/data");
        await using var s = await Open();
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "data/ab/p.pack", [1], WriteMode.DirectFinal, _ct));
        Assert.Equal(KbCode.ReparsePointRefused, ex.Code);
        Assert.False(_srv.Exists("/kopie/data/ab/p.pack"));
        _srv.PutLink("/kopie/snapshots-link");
        var ex2 = await Assert.ThrowsAsync<StorageException>(async () => await s.OpenReadAsync("snapshots-link", 0, null, _ct));
        Assert.Equal(KbCode.ReparsePointRefused, ex2.Code);
    }

    [Fact]
    public async Task List_skips_foreign_files_and_links()
    {
        _srv.PutFile("/kopie/data/aa/ok.pack", [1, 2]);
        _srv.PutFile("/kopie/data/aa/Foreign File.txt", [1]);
        _srv.PutLink("/kopie/data/aa/link.pack");
        await using var s = await Open();
        var all = new List<string>();
        await foreach (var e in s.ListAsync("", _ct)) all.Add(e.Path);
        Assert.Equal(["data/aa/ok.pack"], all);
    }

    [Fact, Trait("RD", "D4.14")]
    public async Task Range_read_returns_requested_slice_via_rest()
    {
        var data = RandomNumberGenerator.GetBytes(1000);
        _srv.PutFile("/kopie/data/aa/r.pack", data);
        await using var s = await Open();
        await using (var r = await s.OpenReadAsync("data/aa/r.pack", 100, 50, _ct))
        {
            using var ms = new MemoryStream();
            await r.CopyToAsync(ms, _ct);
            Assert.Equal(data[100..150], ms.ToArray());
        }
        Assert.Contains("REST 100", _srv.Commands);
        // Strumień zamknięty → kanał sterowania wolny dla kolejnej komendy.
        Assert.NotNull(await s.StatAsync("data/aa/r.pack", _ct));
    }

    [Fact]
    public async Task Server_without_rest_has_no_range_read()
    {
        _srv.Features = FtpFeatures.Mlsd | FtpFeatures.Size;
        _srv.PutFile("/kopie/data/aa/q.pack", new byte[10]);
        await using var s = await Open();
        Assert.Equal(StorageCaps.None, s.Capabilities.Caps & StorageCaps.RangeRead);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await s.OpenReadAsync("data/aa/q.pack", 5, null, _ct));
    }

    [Fact, Trait("AC", "AC-04.5"), Trait("RD", "D4.7")]
    public async Task Connection_drop_mid_write_is_transient_and_directfinal_resumes_with_appe()
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
        Assert.Equal(1200, _srv.Read("/kopie/data/dd/big.pack")!.Length);
        await using var s2 = await Open();
        var st = await s2.StatAsync("data/dd/big.pack", _ct);
        await using var w2 = await s2.CreateAsync("data/dd/big.pack", data.Length, WriteMode.DirectFinal, st!.Length, _ct);
        Assert.Equal(1200, w2.CommittedOffset);
        await w2.WriteAsync(data.AsMemory((int)st.Length), _ct);
        await w2.CommitAsync(_ct);
        Assert.Equal(data, _srv.Read("/kopie/data/dd/big.pack"));
        Assert.Contains("APPE /kopie/data/dd/big.pack", _srv.Commands);
    }

    [Fact, Trait("RD", "D4.7")]
    public async Task Resume_with_wrong_offset_abandons_object_and_staged_resume_unsupported()
    {
        _srv.PutFile("/kopie/data/ee/o.pack", new byte[10]);
        await using var s = await Open();
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await s.CreateAsync("data/ee/o.pack", 100, WriteMode.DirectFinal, 5, _ct).AsTask());
        Assert.Equal(KbCode.SizeMismatch, ex.Code);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await s.CreateAsync("data/ee/p.pack", 100, WriteMode.StagedRename, 5, _ct).AsTask());
    }

    [Fact, Trait("RD", "D4.14")]
    public async Task Size_after_write_is_retried_up_to_three_times_then_fails()
    {
        await using var s = await Open();
        _srv.SizeLagReplies = 2; // serwer z asynchronicznym zapisem: dwie pierwsze odpowiedzi SIZE za małe
        await Put(s, "data/ff/a.pack", new byte[100], WriteMode.StagedRename, _ct);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], _delays);
        _delays.Clear();
        _srv.SizeLagReplies = 3;
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "data/ff/b.pack", new byte[100], WriteMode.StagedRename, _ct));
        Assert.Equal(KbCode.SizeMismatch, ex.Code);
        Assert.Equal(ErrorClass.Integrity, ex.ErrorClass);
        Assert.Equal(2, _delays.Count);
        Assert.False(_srv.Exists("/kopie/data/ff/b.pack"));
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
        Assert.False(_srv.Exists("/kopie/data/ff/s.pack"));
    }

    [Theory, Trait("RD", "D4.8")]
    [InlineData(552, KbCode.DestinationFull, ErrorClass.Permanent)]
    [InlineData(452, KbCode.DestinationFull, ErrorClass.Permanent)]
    [InlineData(550, KbCode.AccessDenied, ErrorClass.Permanent)]
    [InlineData(553, KbCode.InvalidObjectName, ErrorClass.Permanent)]
    [InlineData(451, KbCode.TransientIo, ErrorClass.Transient)]
    [InlineData(425, KbCode.TransientIo, ErrorClass.Transient)]
    public async Task Upload_refusals_are_classified(int code, string kb, ErrorClass cls)
    {
        await using var s = await Open();
        _srv.FailUploadWithCode = code;
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await s.CreateAsync("data/gg/f.pack", 1, WriteMode.StagedRename, 0, _ct).AsTask());
        Assert.Equal(kb, ex.Code);
        Assert.Equal(cls, ex.ErrorClass);
    }

    [Theory, Trait("RD", "D4.8")]
    [InlineData(421, "Too many connections from this IP", false, FtpFailure.SessionLimit)]
    [InlineData(421, "Service not available, closing control connection", false, FtpFailure.Transient)]
    [InlineData(425, null, false, FtpFailure.Transient)]
    [InlineData(426, null, false, FtpFailure.Transient)]
    [InlineData(450, null, false, FtpFailure.Transient)]
    [InlineData(451, null, true, FtpFailure.Transient)]
    [InlineData(530, null, false, FtpFailure.AuthRejected)]
    [InlineData(452, null, true, FtpFailure.NoSpace)]
    [InlineData(552, null, true, FtpFailure.NoSpace)]
    [InlineData(550, null, true, FtpFailure.PermissionDenied)]
    [InlineData(550, null, false, FtpFailure.NotFound)]
    [InlineData(553, null, true, FtpFailure.NameNotAllowed)]
    [InlineData(502, null, false, FtpFailure.PermissionDenied)]
    public void Reply_codes_follow_d48(int code, string? message, bool write, FtpFailure expected)
    {
        Assert.Equal(expected, FtpErrorMap.Classify(code, message, write));
    }

    [Theory, Trait("RD", "D4.8")]
    [InlineData(FtpFailure.AuthRejected, KbCode.AuthRejected, ErrorClass.Permanent)]
    [InlineData(FtpFailure.SessionLimit, KbCode.ServerSessionLimit, ErrorClass.Transient)]
    [InlineData(FtpFailure.TlsIdentityRejected, KbCode.TlsIdentityMismatch, ErrorClass.Security)]
    [InlineData(FtpFailure.TlsNotSupported, KbCode.FtpsNotSupported, ErrorClass.Permanent)]
    [InlineData(FtpFailure.InsecureNotConfirmed, KbCode.InsecureTransportNotConfirmed, ErrorClass.Security)]
    [InlineData(FtpFailure.Transient, KbCode.TransientIo, ErrorClass.Transient)]
    public async Task Connect_failures_map_to_kb_codes(FtpFailure failure, string kb, ErrorClass cls)
    {
        _srv.FailConnect = new FtpProtocolException(failure, "x");
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Open());
        Assert.Equal(kb, ex.Code);
        Assert.Equal(cls, ex.ErrorClass);
    }

    [Fact, Trait("SEC", "D4.11"), Trait("AC", "AC-12.2")]
    public async Task Tls_identity_rejection_carries_presented_spki_for_approval()
    {
        var id = new ServerIdentity("tls-spki", "SHA256:presentedPresentedPresentedPresentedPresent", "SPKI");
        _srv.FailConnect = new FtpProtocolException(FtpFailure.TlsIdentityRejected, "x") { PresentedIdentity = id };
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Open());
        Assert.Equal(KbCode.TlsIdentityMismatch, ex.Code);
        Assert.Equal(id, ex.PresentedIdentity);
    }

    [Fact, Trait("SEC", "D4.3")]
    public async Task Plain_ftp_without_insecure_flag_is_refused_before_connecting()
    {
        var ex = await Assert.ThrowsAsync<StorageException>(async () =>
            await Factory("ftp").OpenAsync(new DestinationConfig("d", "ftp", FakeFtpServer.Root, "file:ftp1"), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.InsecureTransportNotConfirmed, ex.Code);
        Assert.Equal(ErrorClass.Security, ex.ErrorClass);
        Assert.Equal(0, _srv.Connections); // żadne poświadczenie nie poszło otwartym tekstem
        var opts = new Dictionary<string, string> { ["insecure"] = "1" };
        await using var s = await Factory("ftp").OpenAsync(new DestinationConfig("d", "ftp", FakeFtpServer.Root, "file:ftp1", opts), CredentialRole.Backup, _ct);
        Assert.Equal(1, _srv.Connections);
    }

    [Fact]
    public async Task Capabilities_follow_server_features()
    {
        await using var s = await Open();
        var caps = s.Capabilities;
        Assert.Equal(StorageCaps.AtomicRename | StorageCaps.RenameAllowed | StorageCaps.ResumeUpload | StorageCaps.RangeRead | StorageCaps.Mtime | StorageCaps.FreeSpace, caps.Caps);
        Assert.Equal(2, caps.RecommendedParallelism);
        Assert.Equal(16L * 1024 * 1024, caps.RecommendedPackSize);
        Assert.Null(caps.ServerHash);
        Assert.Equal(1L << 40, await s.GetFreeSpaceAsync(_ct));
        Assert.Equal("tls-spki", s.PresentedIdentity!.Kind);
        _srv.Features = FtpFeatures.Size | FtpFeatures.HashSha256;
        _srv.PutFile("/kopie/data/hh/h.pack", [1, 2, 3]);
        await using var s2 = await Open(CredentialRole.Maintenance);
        Assert.NotEqual(StorageCaps.None, s2.Capabilities.Caps & StorageCaps.ServerSideHash);
        Assert.NotEqual(StorageCaps.None, s2.Capabilities.Caps & StorageCaps.DeleteAllowed);
        Assert.Equal(StorageCaps.None, s2.Capabilities.Caps & StorageCaps.FreeSpace);
        Assert.Null(await s2.GetFreeSpaceAsync(_ct));
        Assert.Equal(SHA256.HashData(new byte[] { 1, 2, 3 }), await s2.ServerHashAsync("data/hh/h.pack", HashAlgorithmName.SHA256, _ct));
        Assert.Null(await s2.ServerHashAsync("data/hh/h.pack", HashAlgorithmName.SHA512, _ct));
    }

    [Fact]
    public async Task Missing_root_is_destination_not_found_and_scheme_checked()
    {
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Open(root: "/nonexistent"));
        Assert.Equal(KbCode.DestinationNotFound, ex.Code);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Factory().OpenAsync(new DestinationConfig("d", "sftp", "/kopie"), CredentialRole.Backup, _ct));
        Assert.Throws<ArgumentException>(() => new FtpStorageProviderFactory(_srv, "sftp"));
    }
}
