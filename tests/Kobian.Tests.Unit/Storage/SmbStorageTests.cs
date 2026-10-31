using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Smb;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>
/// BL-73 (it. 92): dostawca „smb” na fałszywym konektorze (udział = katalog tymczasowy). Natywny WNetAddConnection2 — kolejka W-35 (Windows).
/// Dane i poświadczenia syntetyczne, generowane w teście.
/// </summary>
public sealed class SmbStorageTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "kobian-smb-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bkSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
    private readonly string _mtSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));

    public SmbStorageTests() => Directory.CreateDirectory(Path.Combine(_base, "nas", "kopie", "repo1"));

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    /// <summary>Konektor testowy: <c>\\serwer\udział</c> → <c>&lt;base&gt;/serwer/udział</c>; liczy połączenia i rozłączenia, może zwrócić błąd Win32.</summary>
    private sealed class FakeConnector(string baseDir) : ISmbConnector
    {
        public int Connects;
        public int Disconnects;
        public int? FailWith;
        public List<SmbCredential> Seen { get; } = [];

        public ValueTask<ISmbConnection> ConnectAsync(SmbShare share, SmbCredential credential, CancellationToken ct)
        {
            Connects++;
            Seen.Add(credential);
            if (FailWith is { } e) throw new SmbConnectException(e, "fake failure");
            var root = Path.Combine(baseDir, share.Server, share.Share);
            if (share.RelativePath.Length > 0) root = Path.Combine(root, share.RelativePath.Replace('\\', Path.DirectorySeparatorChar));
            return ValueTask.FromResult<ISmbConnection>(new Conn(this, root));
        }

        private sealed class Conn(FakeConnector owner, string root) : ISmbConnection
        {
            public string LocalRoot { get; } = root;

            public ValueTask DisposeAsync()
            {
                owner.Disconnects++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeCredentials(string bk, string mt) : ISmbCredentialProvider
    {
        public int Calls;
        public bool Throw;

        public ValueTask<SmbCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("store unavailable");
            return ValueTask.FromResult(role == CredentialRole.Maintenance ? new SmbCredential(@"FIRMA\kobian-mt", mt) : new SmbCredential(@"FIRMA\kobian-bk", bk));
        }
    }

    private (SmbStorageProviderFactory Factory, FakeConnector Connector, FakeCredentials Credentials) Sut()
    {
        var c = new FakeConnector(_base);
        var cr = new FakeCredentials(_bkSecret, _mtSecret);
        return (new SmbStorageProviderFactory(c, cr), c, cr);
    }

    private static DestinationConfig Dest(string root = @"\\nas\kopie\repo1", string? secretRef = "file:nas") => new("d-smb", "smb", root, secretRef);

    // ---- UncPath ----

    [Theory]
    [InlineData(@"\\nas\kopie", "nas", "kopie", "")]
    [InlineData(@"\\nas.firma.local\kopie\a\b", "nas.firma.local", "kopie", @"a\b")]
    [InlineData("//nas/kopie/repo/", "nas", "kopie", "repo")]
    [InlineData(@"\\192.168.10.5\backup$\x", "192.168.10.5", "backup$", "x")]
    public void Unc_path_parses_server_share_and_relative_path(string root, string server, string share, string rel)
    {
        var s = UncPath.Parse(root);
        Assert.Equal(server, s.Server);
        Assert.Equal(share, s.Share);
        Assert.Equal(rel, s.RelativePath);
        Assert.StartsWith(@"\\" + server + @"\" + share, s.RootUnc, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, KbCode.DestinationNotFound)]
    [InlineData("", KbCode.DestinationNotFound)]
    [InlineData(@"C:\kopie", KbCode.DestinationNotFound)]
    [InlineData(@"\\?\C:\kopie", KbCode.DestinationNotFound)]
    [InlineData(@"\\?\UNC\nas\kopie", KbCode.DestinationNotFound)]
    [InlineData(@"\\.\pipe\kobian", KbCode.DestinationNotFound)]
    [InlineData(@"\\nas", KbCode.DestinationNotFound)]
    [InlineData(@"\\[::1]\kopie", KbCode.DestinationNotFound)]
    [InlineData(@"\\nas:445\kopie", KbCode.DestinationNotFound)]
    [InlineData(@"\\-nas\kopie", KbCode.DestinationNotFound)]
    [InlineData(@"\\300.1.1.1\kopie", KbCode.DestinationNotFound)]
    [InlineData("\\\\nas\\kopie\\a\u0001b", KbCode.DestinationNotFound)]
    [InlineData(@"\\nas\kopie\..\inne", KbCode.InvalidObjectName)]
    [InlineData(@"\\nas\kopie\.", KbCode.InvalidObjectName)]
    [InlineData(@"\\nas\kopie\plik:strumien", KbCode.InvalidObjectName)]
    [InlineData(@"\\nas\IPC$", KbCode.InvalidObjectName)]
    [InlineData(@"\\nas\admin$\x", KbCode.InvalidObjectName)]
    [InlineData(@"\\nas\C$\kopie", KbCode.InvalidObjectName)]
    [InlineData(@"\\nas\kopie\CON", KbCode.InvalidObjectName)]
    [InlineData(@"\\nas\kopie\nul.txt", KbCode.InvalidObjectName)]
    [InlineData(@"\\nas\kopie\katalog.", KbCode.InvalidObjectName)]
    [InlineData(@"\\nas\kopie\katalog ", KbCode.InvalidObjectName)]
    [InlineData(@"\\nas\kopie\a*b", KbCode.InvalidObjectName)]
    public void Unc_path_rejects_unsafe_or_malformed_roots(string? root, string code)
    {
        var ex = Assert.Throws<StorageException>(() => UncPath.Parse(root));
        Assert.Equal(code, ex.Code);
        if (root is { Length: > 3 }) Assert.DoesNotContain(root, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unc_path_reports_ipv4_server_for_the_kerberos_warning()
    {
        Assert.True(UncPath.ServerIsIpAddress(UncPath.Parse(@"\\10.0.0.7\kopie")));
        Assert.False(UncPath.ServerIsIpAddress(UncPath.Parse(@"\\nas.firma.local\kopie")));
    }

    [Fact]
    public void Unc_path_rejects_overlong_roots()
    {
        var ex = Assert.Throws<StorageException>(() => UncPath.Parse(@"\\nas\kopie\" + new string('a', UncPath.MaxLength)));
        Assert.Equal(KbCode.DestinationNotFound, ex.Code);
    }

    // ---- mapa błędów D4.8 ----

    [Theory, Trait("Decision", "D4.8")]
    [InlineData(1219, KbCode.SmbCredentialConflict, ErrorClass.Permanent)]
    [InlineData(1326, KbCode.AuthRejected, ErrorClass.Permanent)]
    [InlineData(86, KbCode.AuthRejected, ErrorClass.Permanent)]
    [InlineData(2202, KbCode.AuthRejected, ErrorClass.Permanent)]
    [InlineData(1909, KbCode.AccountLockedOrExpired, ErrorClass.Permanent)]
    [InlineData(1330, KbCode.AccountLockedOrExpired, ErrorClass.Permanent)]
    [InlineData(1907, KbCode.AccountLockedOrExpired, ErrorClass.Permanent)]
    [InlineData(5, KbCode.AccessDenied, ErrorClass.Permanent)]
    [InlineData(53, KbCode.DestinationNotFound, ErrorClass.Permanent)]
    [InlineData(67, KbCode.DestinationNotFound, ErrorClass.Permanent)]
    [InlineData(112, KbCode.DestinationFull, ErrorClass.Permanent)]
    [InlineData(39, KbCode.DestinationFull, ErrorClass.Permanent)]
    [InlineData(1238, KbCode.ServerSessionLimit, ErrorClass.Transient)]
    [InlineData(64, KbCode.TransientIo, ErrorClass.Transient)]
    [InlineData(59, KbCode.TransientIo, ErrorClass.Transient)]
    [InlineData(121, KbCode.TransientIo, ErrorClass.Transient)]
    [InlineData(1231, KbCode.TransientIo, ErrorClass.Transient)]
    public void Win32_connect_errors_map_to_d48_codes(int win32, string code, ErrorClass cls)
    {
        var ex = SmbErrorMap.MapConnect(win32);
        Assert.Equal(code, ex.Code);
        Assert.Equal(cls, ex.ErrorClass);
        Assert.Equal(cls == ErrorClass.Transient, SmbErrorMap.IsRetryable(win32));
    }

    // ---- dostawca ----

    [Fact, Trait("AC", "AC-04.3")]
    public async Task Session_writes_reads_and_closes_the_connection_on_dispose()
    {
        var (f, c, _) = Sut();
        var data = RandomNumberGenerator.GetBytes(5000);
        await using (var s = await f.OpenAsync(Dest(), CredentialRole.Backup, _ct))
        {
            Assert.Equal(1, c.Connects);
            Assert.Equal(0, c.Disconnects);
            await using (var w = await s.CreateAsync("data/ab/pack1", data.Length, WriteMode.StagedRename, 0, _ct))
            {
                await w.WriteAsync(data, _ct);
                await w.CommitAsync(_ct);
            }
            await using var r = await s.OpenReadAsync("data/ab/pack1", 0, null, _ct);
            using var ms = new MemoryStream();
            await r.CopyToAsync(ms, _ct);
            Assert.Equal(data, ms.ToArray());
            Assert.Equal(SmbStorageProviderFactory.Parallelism, s.Capabilities.RecommendedParallelism);
        }
        Assert.Equal(1, c.Disconnects); // AC-04.3: połączenie zamknięte po zadaniu
        Assert.True(File.Exists(Path.Combine(_base, "nas", "kopie", "repo1", "data", "ab", "pack1")));
    }

    [Fact, Trait("Decision", "D4.1")]
    public async Task Each_role_connects_with_its_own_credential_and_backup_never_deletes()
    {
        var (f, c, _) = Sut();
        await using (var s = await f.OpenAsync(Dest(), CredentialRole.Backup, _ct))
        {
            await using (var w = await s.CreateAsync("snapshots/s1", 3, WriteMode.DirectFinal, 0, _ct))
            {
                await w.WriteAsync(new byte[] { 1, 2, 3 }, _ct);
                await w.CommitAsync(_ct);
            }
            Assert.False(s.Capabilities.Caps.HasFlag(StorageCaps.DeleteAllowed));
            await Assert.ThrowsAnyAsync<Exception>(async () => await s.DeleteAsync("snapshots/s1", _ct));
        }
        await using (var m = await f.OpenAsync(Dest(), CredentialRole.Maintenance, _ct))
        {
            Assert.True(m.Capabilities.Caps.HasFlag(StorageCaps.DeleteAllowed));
            await m.DeleteAsync("snapshots/s1", _ct);
        }
        Assert.Equal([@"FIRMA\kobian-bk", @"FIRMA\kobian-mt"], c.Seen.Select(x => x.User));
        Assert.Equal(_bkSecret, c.Seen[0].Password);
        Assert.Equal(_mtSecret, c.Seen[1].Password);
        Assert.False(File.Exists(Path.Combine(_base, "nas", "kopie", "repo1", "snapshots", "s1")));
    }

    [Fact]
    public async Task Existing_object_is_never_overwritten_on_the_share()
    {
        var (f, _, _) = Sut();
        await using var s = await f.OpenAsync(Dest(), CredentialRole.Backup, _ct);
        await using (var w = await s.CreateAsync("keys/k1", 1, WriteMode.DirectFinal, 0, _ct))
        {
            await w.WriteAsync(new byte[] { 7 }, _ct);
            await w.CommitAsync(_ct);
        }
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await s.CreateAsync("keys/k1", 1, WriteMode.StagedRename, 0, _ct));
        Assert.Equal(KbCode.ObjectExists, ex.Code);
    }

    [Fact]
    public async Task Missing_secret_reference_is_refused_before_any_connection()
    {
        var (f, c, cr) = Sut();
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(Dest(secretRef: null), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.AuthRejected, ex.Code);
        Assert.Equal(0, c.Connects);
        Assert.Equal(0, cr.Calls);
    }

    [Fact]
    public async Task Invalid_root_is_refused_before_credentials_are_read()
    {
        var (f, c, cr) = Sut();
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(Dest(@"\\nas\kopie\..\x"), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.InvalidObjectName, ex.Code);
        Assert.Equal(0, cr.Calls);
        Assert.Equal(0, c.Connects);
    }

    [Fact, Trait("AC", "AC-01.2")]
    public async Task Rejected_password_is_permanent_after_exactly_one_attempt_and_leaks_nothing()
    {
        var (f, c, _) = Sut();
        c.FailWith = 1326;
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(Dest(), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.AuthRejected, ex.Code);
        Assert.Equal(ErrorClass.Permanent, ex.ErrorClass);
        Assert.Equal(1, c.Connects); // dokładnie jedna próba logowania — bez ponowień (blokada konta)
        Assert.DoesNotContain(_bkSecret, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("kobian-bk", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credential_store_failure_becomes_auth_rejected()
    {
        var (f, c, cr) = Sut();
        cr.Throw = true;
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(Dest(), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.AuthRejected, ex.Code);
        Assert.Equal(0, c.Connects);
    }

    [Fact]
    public async Task Missing_folder_on_share_fails_and_disconnects()
    {
        var (f, c, _) = Sut();
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(Dest(@"\\nas\kopie\brak"), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.DestinationNotFound, ex.Code);
        Assert.Equal(1, c.Connects);
        Assert.Equal(1, c.Disconnects);
    }

    [Fact]
    public async Task Root_that_is_a_link_is_refused_and_disconnects()
    {
        if (OperatingSystem.IsWindows()) return; // dowiązanie katalogu na Windows wymaga uprawnień — sprawdzane w W-35
        var target = Path.Combine(_base, "gdzie-indziej");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.Combine(_base, "nas", "kopie", "link"), target);
        var (f, c, _) = Sut();
        var ex = await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(Dest(@"\\nas\kopie\link"), CredentialRole.Backup, _ct));
        Assert.Equal(KbCode.ReparsePointRefused, ex.Code);
        Assert.Equal(1, c.Disconnects);
    }

    // ---- wspólne połączenia ----

    [Fact]
    public async Task Parallel_sessions_share_one_connection_until_the_last_is_released()
    {
        var inner = new FakeConnector(_base);
        using var shared = new SharedSmbConnector(inner);
        var f = new SmbStorageProviderFactory(shared, new FakeCredentials(_bkSecret, _mtSecret));
        var a = await f.OpenAsync(Dest(), CredentialRole.Backup, _ct);
        var b = await f.OpenAsync(Dest(@"\\NAS\kopie\repo1"), CredentialRole.Backup, _ct);
        Assert.Equal(1, inner.Connects);
        Assert.Equal(1, shared.ActiveConnections);
        await a.DisposeAsync();
        await a.DisposeAsync(); // podwójne zwolnienie nie zrywa cudzej sesji
        Assert.Equal(0, inner.Disconnects);
        await b.StatAsync("kobian.repo", _ct);
        await b.DisposeAsync();
        Assert.Equal(1, inner.Disconnects);
        Assert.Equal(0, shared.ActiveConnections);
    }

    [Fact, Trait("Decision", "D7.4")]
    public async Task Second_account_to_the_same_server_fails_fast_with_1219()
    {
        var inner = new FakeConnector(_base);
        using var shared = new SharedSmbConnector(inner);
        var f = new SmbStorageProviderFactory(shared, new FakeCredentials(_bkSecret, _mtSecret));
        await using (var bk = await f.OpenAsync(Dest(), CredentialRole.Backup, _ct))
        {
            var ex = await Assert.ThrowsAsync<StorageException>(async () => await f.OpenAsync(Dest(), CredentialRole.Maintenance, _ct));
            Assert.Equal(KbCode.SmbCredentialConflict, ex.Code);
            Assert.Equal(1, inner.Connects); // system nie był wołany drugi raz
        }
        await using (var mt = await f.OpenAsync(Dest(), CredentialRole.Maintenance, _ct))
            Assert.Equal(2, inner.Connects);
    }

    [Fact]
    public async Task Shared_connector_connects_to_the_share_root_and_maps_the_subfolder()
    {
        var inner = new FakeConnector(_base);
        using var shared = new SharedSmbConnector(inner);
        await using var lease = await shared.ConnectAsync(UncPath.Parse(@"\\nas\kopie\repo1"), new SmbCredential("u", _bkSecret), _ct);
        Assert.Equal(Path.Combine(_base, "nas", "kopie", "repo1"), lease.LocalRoot);
    }
}
