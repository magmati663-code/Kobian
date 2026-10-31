using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Engine.Restore;
using Kobian.Format.Metadata;
using Kobian.Providers.Storage.Ftp;
using Kobian.Providers.Storage.WebDav;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>
/// Cel WebDAV: prawdziwy adapter HTTP (<see cref="HttpWebDavConnector"/>) wobec serwera WebDAV w procesie na 127.0.0.1 — zapis bez nadpisania (PUT + MOVE Overwrite: F),
/// odczyt zakresowy, katalogi, mapowanie błędów HTTP na KB, TLS z pinem SPKI (bez hasła przed weryfikacją certyfikatu) i pełny backup → restore przez WebDAV.
/// </summary>
public sealed class WebDavStorageTests : IAsyncDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly LoopbackWebDavServer _srv = new();

    public WebDavStorageTests() => _srv.AddCollection("/kopie");

    public ValueTask DisposeAsync() => _srv.DisposeAsync();

    private static DestinationConfig Cfg(int port, string root = "/kopie", bool insecure = true, params (string K, string V)[] extra)
    {
        var o = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = port.ToString(CultureInfo.InvariantCulture),
            ["connect_timeout_s"] = "5",
        };
        if (insecure) o["insecure"] = "1";
        foreach (var (k, v) in extra) o[k] = v;
        return new DestinationConfig("d", "webdav", root, "file:dav1", o);
    }

    private FluentFtpAdapterTests.CountingCredentials Creds(string? secret = null) => new() { User = _srv.User, Secret = secret ?? _srv.Secret };

    private ValueTask<IStorageSession> Open(CredentialRole role = CredentialRole.Backup, string root = "/kopie", string? secret = null) =>
        new WebDavStorageProviderFactory(new HttpWebDavConnector(Creds(secret))).OpenAsync(Cfg(_srv.Port, root), role, _ct);

    private async Task<StatResult> Put(IStorageSession s, string path, byte[] data, WriteMode mode = WriteMode.StagedRename)
    {
        await using var w = await s.CreateAsync(path, data.Length, mode, 0, _ct);
        await w.WriteAsync(data, _ct);
        return await w.CommitAsync(_ct);
    }

    private async Task<byte[]> Read(IStorageSession s, string path, long offset = 0, long? length = null)
    {
        await using var r = await s.OpenReadAsync(path, offset, length, _ct);
        using var ms = new MemoryStream();
        await r.CopyToAsync(ms, _ct);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(WriteMode.StagedRename)]
    [InlineData(WriteMode.DirectFinal)]
    public async Task Write_read_back_with_range_and_list(WriteMode mode)
    {
        await using var s = await Open();
        var data = RandomNumberGenerator.GetBytes(100_000);
        var st = await Put(s, "data/ab/0123abcd.pack", data, mode);
        Assert.Equal(100_000, st.Length);
        Assert.Equal(data, await Read(s, "data/ab/0123abcd.pack"));
        Assert.Equal(data[1000..1500], await Read(s, "data/ab/0123abcd.pack", 1000, 500));
        Assert.Equal(data, _srv.File("/kopie/data/ab/0123abcd.pack"));
        Assert.DoesNotContain(_srv.Paths(), p => p.Contains(".tmp-", StringComparison.Ordinal));
        await Put(s, "kobian.repo", [1, 2, 3]);
        var listed = new List<string>();
        await foreach (var e in s.ListAsync("", _ct)) listed.Add(e.Path);
        Assert.Equal(["data/ab/0123abcd.pack", "kobian.repo"], listed.Order(StringComparer.Ordinal));
        var dir = await s.StatAsync("data/ab", _ct);
        Assert.True(dir!.IsDirectory);
        Assert.Null(await s.StatAsync("data/zz/brak.pack", _ct));
        Assert.Equal((StorageCaps)0, s.Capabilities.Caps & (StorageCaps.ResumeUpload | StorageCaps.DeleteAllowed | StorageCaps.OverwriteAllowed));
    }

    [Fact]
    public async Task Never_overwrites_existing_object()
    {
        await using var s = await Open();
        await Put(s, "data/aa/x.pack", [1, 2, 3]);
        foreach (var mode in new[] { WriteMode.StagedRename, WriteMode.DirectFinal })
        {
            var ex = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "data/aa/x.pack", [9, 9, 9], mode));
            Assert.Equal(KbCode.ObjectExists, ex.Code);
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, _srv.File("/kopie/data/aa/x.pack"));
    }

    [Fact]
    public async Task Rename_race_is_refused_by_server_not_overwritten()
    {
        await using var s = await Open();
        await using var w = await s.CreateAsync("data/bb/y.pack", 3, WriteMode.StagedRename, 0, _ct);
        await w.WriteAsync(new byte[] { 7, 7, 7 }, _ct);
        _srv.BeforeMove = () => _srv.AddFile("/kopie/data/bb/y.pack", [1]); // ktoś inny zdążył między sprawdzeniem a MOVE
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await w.CommitAsync(_ct));
        Assert.Equal(KbCode.ObjectExists, ex.Code);
        Assert.Equal(new byte[] { 1 }, _srv.File("/kopie/data/bb/y.pack"));
    }

    [Fact]
    public async Task Direct_write_race_uses_if_none_match()
    {
        await using var s = await Open();
        await using var w = await s.CreateAsync("data/cc/z.pack", 2, WriteMode.DirectFinal, 0, _ct);
        await w.WriteAsync(new byte[] { 5, 5 }, _ct);
        _srv.AddFile("/kopie/data/cc/z.pack", [1]);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await w.CommitAsync(_ct));
        Assert.Equal(KbCode.ObjectExists, ex.Code);
        Assert.Equal(new byte[] { 1 }, _srv.File("/kopie/data/cc/z.pack"));
    }

    [Fact]
    public async Task Abort_before_commit_leaves_nothing_on_server()
    {
        await using var s = await Open();
        await using (var w = await s.CreateAsync("data/dd/a.pack", 3, WriteMode.StagedRename, 0, _ct))
        {
            await w.WriteAsync(new byte[] { 1 }, _ct);
            await w.AbortAsync(_ct);
        }
        Assert.DoesNotContain(_srv.Paths(), p => p.EndsWith(".pack", StringComparison.Ordinal) || p.Contains(".tmp-", StringComparison.Ordinal));
        Assert.DoesNotContain(_srv.Requests, r => r.StartsWith("PUT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wrong_password_missing_root_and_file_as_root_are_mapped()
    {
        var auth = await Assert.ThrowsAsync<StorageException>(async () => await Open(secret: "zle-haslo-1234"));
        Assert.Equal(KbCode.AuthRejected, auth.Code);
        Assert.Equal(ErrorClass.Permanent, auth.ErrorClass);
        var missing = await Assert.ThrowsAsync<StorageException>(async () => await Open(root: "/brak"));
        Assert.Equal(KbCode.DestinationNotFound, missing.Code);
        _srv.AddFile("/plik", [1]);
        var file = await Assert.ThrowsAsync<StorageException>(async () => await Open(root: "/plik"));
        Assert.Equal(KbCode.DestinationNotFound, file.Code);
    }

    [Theory]
    [InlineData(507, "KB-2201")]
    [InlineData(413, "KB-2116")]
    [InlineData(403, "KB-2105")]
    [InlineData(503, "KB-2112")]
    [InlineData(500, "KB-2001")]
    public async Task Upload_errors_are_classified(int status, string code)
    {
        await using var s = await Open();
        _srv.FailNextPut = status;
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "data/ee/a.pack", [1, 2]));
        Assert.Equal(code, ex.Code);
        Assert.Equal(status is 503 or 500 ? ErrorClass.Transient : ErrorClass.Permanent, ex.ErrorClass);
    }

    [Fact]
    public async Task Servers_ignoring_range_and_if_none_match_or_redirecting_collections_still_work()
    {
        _srv.IgnoreRange = true;
        _srv.IgnoreIfNoneMatch = true;
        _srv.RedirectCollectionsWithoutSlash = true;
        _srv.AbsoluteHrefs = true;
        await using var s = await Open();
        var data = RandomNumberGenerator.GetBytes(5000);
        await Put(s, "data/ff/b.pack", data);
        Assert.Equal(data[4000..], await Read(s, "data/ff/b.pack", 4000));
        Assert.True((await s.StatAsync("data/ff", _ct))!.IsDirectory);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await Put(s, "data/ff/b.pack", [1], WriteMode.DirectFinal));
        Assert.Equal(KbCode.ObjectExists, ex.Code); // sprawdzenie przed PUT działa także bez If-None-Match po stronie serwera
        var listed = new List<string>();
        await foreach (var e in s.ListAsync("data", _ct)) listed.Add(e.Path);
        Assert.Equal(["data/ff/b.pack"], listed);
    }

    [Fact]
    public async Task Root_with_spaces_and_polish_letters_is_encoded()
    {
        _srv.AddCollection("/dav/Kopie zażółć");
        _srv.AddCollection("/dav");
        await using var s = await Open(root: "/dav/Kopie zażółć");
        await Put(s, "kobian.repo", [4, 2]);
        Assert.Equal(new byte[] { 4, 2 }, _srv.File("/dav/Kopie zażółć/kobian.repo"));
        Assert.Equal(new byte[] { 4, 2 }, await Read(s, "kobian.repo"));
    }

    [Fact]
    public async Task Delete_only_in_maintenance_and_never_a_collection()
    {
        await using (var b = await Open())
        {
            await Put(b, "data/gg/c.pack", [1]);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await b.DeleteAsync("data/gg/c.pack", _ct));
        }
        await using var m = await Open(CredentialRole.Maintenance);
        Assert.NotEqual((StorageCaps)0, m.Capabilities.Caps & StorageCaps.DeleteAllowed);
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await m.DeleteAsync("data/gg", _ct));
        Assert.Equal(KbCode.InvalidObjectName, ex.Code);
        Assert.NotNull(_srv.File("/kopie/data/gg/c.pack"));
        await m.DeleteAsync("data/gg/c.pack", _ct);
        Assert.Null(_srv.File("/kopie/data/gg/c.pack"));
    }

    [Fact]
    public async Task Quota_is_reported_as_free_space_when_server_supports_rfc_4331()
    {
        await using (var none = await Open())
        {
            Assert.Null(await none.GetFreeSpaceAsync(_ct));
        }
        _srv.QuotaAvailable = 123_456_789;
        await using var s = await Open();
        Assert.NotEqual((StorageCaps)0, s.Capabilities.Caps & StorageCaps.FreeSpace);
        Assert.Equal(123_456_789, await s.GetFreeSpaceAsync(_ct));
    }

    [Fact]
    public async Task Https_is_default_so_plain_server_fails_without_sending_password()
    {
        var f = new WebDavStorageProviderFactory(new HttpWebDavConnector(Creds()));
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(Cfg(_srv.Port, insecure: false), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.FtpsNotSupported, ex.Code);
        Assert.False(_srv.AuthorizationSeen);
    }

    private static X509Certificate2 ServerCert()
    {
        using var c = FluentFtpAdapterTests.SelfSigned("nas.example");
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pkcs12), null);
    }

    [Fact, Trait("SEC", "D4.11")]
    public async Task Self_signed_https_requires_spki_pin_and_password_is_sent_only_after_trust()
    {
        using var cert = ServerCert();
        await using var tls = new LoopbackWebDavServer(cert);
        tls.AddCollection("/kopie");
        var f = new WebDavStorageProviderFactory(new HttpWebDavConnector(new FluentFtpAdapterTests.CountingCredentials { User = tls.User, Secret = tls.Secret }));
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(Cfg(tls.Port, insecure: false), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.TlsIdentityMismatch, ex.Code);
        Assert.Equal(ErrorClass.Security, ex.ErrorClass);
        Assert.Equal(TlsSpkiPin.Fingerprint(cert), ex.PresentedIdentity!.Fingerprint);
        Assert.False(tls.AuthorizationSeen);

        var wrong = await Assert.ThrowsAsync<StorageException>(async () =>
            await f.OpenAsync(Cfg(tls.Port, insecure: false, extra: ("spki", "SHA256:" + new string('A', 43))), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.TlsIdentityMismatch, wrong.Code);
        Assert.False(tls.AuthorizationSeen);

        await using var s = await f.OpenAsync(Cfg(tls.Port, insecure: false, extra: ("spki", TlsSpkiPin.Fingerprint(cert))), CredentialRole.Backup, _ct);
        Assert.Equal(TlsSpkiPin.Fingerprint(cert), s.PresentedIdentity!.Fingerprint);
        await Put(s, "kobian.repo", [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, tls.File("/kopie/kobian.repo"));
    }

    [Fact]
    public void Options_default_to_https_443_and_validate_input()
    {
        var o = WebDavOptions.Parse(new DestinationConfig("d", "webdav", "/k", null, new Dictionary<string, string> { ["host"] = "cloud.example" }));
        Assert.Equal((true, 443), (o.Tls, o.Port));
        Assert.Equal("https://cloud.example/", o.BaseUri.ToString());
        var v6 = WebDavOptions.Parse(new DestinationConfig("d", "webdav", "/k", null, new Dictionary<string, string> { ["host"] = "::1", ["insecure"] = "1" }));
        Assert.Equal("http://[::1]/", v6.BaseUri.ToString());
        Assert.Throws<StorageException>(() => WebDavOptions.Parse(new DestinationConfig("d", "webdav", "/k", null, new Dictionary<string, string> { ["host"] = "a/b" })));
        Assert.Throws<StorageException>(() => WebDavOptions.Parse(new DestinationConfig("d", "webdav", "/k", null, new Dictionary<string, string> { ["host"] = "h", ["spki"] = "md5:x" })));
    }

    [Fact]
    public void Multistatus_parser_refuses_dtd_and_ignores_failed_propstat()
    {
        const string evil = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><d:multistatus xmlns:d="DAV:"><d:response><d:href>/a</d:href></d:response></d:multistatus>""";
        var ex = Assert.Throws<StorageException>(() => MultiStatusParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(evil))));
        Assert.Equal(KbCode.TransientIo, ex.Code);
        const string ok = """
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/k/a%20b.pack</d:href>
                <d:propstat><d:prop><d:getcontentlength>12</d:getcontentlength><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                <d:propstat><d:prop><d:quota-available-bytes/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>
              </d:response>
              <d:response><d:href>https://h/k/sub/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
              <d:response><d:href>/k/forbidden</d:href>
                <d:propstat><d:prop><d:getcontentlength>1</d:getcontentlength></d:prop><d:status>HTTP/1.1 403 Forbidden</d:status></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        var list = MultiStatusParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(ok)));
        Assert.Equal(2, list.Count);
        Assert.Equal(("/k/a b.pack", false, 12L), (list[0].Path, list[0].IsCollection, list[0].Length!.Value));
        Assert.Equal(("/k/sub", true), (list[1].Path, list[1].IsCollection));
    }

    [Fact, Trait("V", "V1")]
    public async Task Backup_then_restore_through_webdav_is_bit_identical()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "kobian-dav-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
        var src = Path.Combine(tmp, "src");
        var dst = Path.Combine(tmp, "dst");
        Directory.CreateDirectory(Path.Combine(src, "docs"));
        Directory.CreateDirectory(dst);
        try
        {
            File.WriteAllBytes(Path.Combine(src, "big.bin"), RandomNumberGenerator.GetBytes((3 * 1024 * 1024) + 7));
            File.WriteAllText(Path.Combine(src, "docs", "notatka.txt"), "zażółć gęślą jaźń");
            using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
            await using var s = await Open();
            var r = await new BackupPipeline(keys, new VerifiedUploader(s)).RunAsync(
                new BackupRequest(new FileSystemSource(src), "job", new byte[16], PackTargetSize: 1024 * 1024), _ct);
            Assert.Equal(BackupStatus.Succeeded, r.Status);
            var snap = await MetadataStore.LoadSnapshotAsync(s, keys, r.SnapId!, _ct);
            using var reader = new RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap, _ct);
            var rr = await new RestoreExecutor(reader).RestoreAsync(snap, dst, null, _ct);
            Assert.Equal(RestoreStatus.Succeeded, rr.Status);
            Assert.Equal(File.ReadAllBytes(Path.Combine(src, "big.bin")), File.ReadAllBytes(Path.Combine(dst, "big.bin")));
            Assert.Equal("zażółć gęślą jaźń", File.ReadAllText(Path.Combine(dst, "docs", "notatka.txt")));
            Assert.DoesNotContain(_srv.Paths(), p => p.Contains(".tmp-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
