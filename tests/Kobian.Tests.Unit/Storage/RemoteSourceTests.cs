using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Jobs;
using Kobian.Application.Storage;
using Kobian.Cli.Commands;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Engine.Restore;
using Kobian.Format.Metadata;
using Kobian.Providers.Storage.Local;
using Kobian.Providers.Storage.Secrets;
using Kobian.Providers.Storage.Sftp;
using Kobian.Providers.Storage.WebDav;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>
/// Serwer jako ŹRÓDŁO kopii (SFTP, WebDAV) i kilka źródeł w jednym zadaniu: każde źródło to folder w kopii; odczyt nie wychodzi poza korzeń; dowiązania pomijane;
/// zerwane połączenie kończy kopię błędem (a nie „udaną” kopią z pominiętymi plikami). Poświadczenia z kreatora zapisywane w magazynie sekretów Agenta.
/// </summary>
public sealed class RemoteSourceTests : IAsyncDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-rsrc-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly LoopbackWebDavServer _dav = new();

    public RemoteSourceTests() => Directory.CreateDirectory(_tmp);

    public async ValueTask DisposeAsync()
    {
        await _dav.DisposeAsync();
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private async Task<IStorageSession> Dav(string root) =>
        await new WebDavStorageProviderFactory(new HttpWebDavConnector(new FluentFtpAdapterTests.CountingCredentials { User = _dav.User, Secret = _dav.Secret }))
            .OpenAsync(new DestinationConfig("s", "webdav", root, "file:x", new Dictionary<string, string>
            {
                ["host"] = "127.0.0.1", ["port"] = _dav.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["insecure"] = "1",
            }), CredentialRole.Backup, _ct);

    [Fact]
    public async Task Local_folder_webdav_and_sftp_sources_back_up_into_separate_folders_and_restore_bit_for_bit()
    {
        var local = Path.Combine(_tmp, "Dokumenty");
        Directory.CreateDirectory(Path.Combine(local, "umowy"));
        File.WriteAllText(Path.Combine(local, "umowy", "a.txt"), "lokalny");
        _dav.AddCollection("/udzial");
        _dav.AddCollection("/udzial/Zdjęcia 2026");
        var photo = RandomNumberGenerator.GetBytes(300_000);
        _dav.AddFile("/udzial/Zdjęcia 2026/plaża.jpg", photo);
        _dav.AddFile("/udzial/notatka.txt", "z serwera WebDAV"u8.ToArray());
        var sftp = new FakeSftpServer();
        sftp.PutFile(FakeSftpServer.Root + "/raporty/q3.csv", "kwota;1\n"u8.ToArray());
        sftp.PutSymlink(FakeSftpServer.Root + "/link");

        await using var dav = await Dav("/udzial");
        await using var sf = await new SftpStorageProviderFactory(sftp).OpenAsync(new DestinationConfig("s2", "sftp", FakeSftpServer.Root), CredentialRole.Backup, _ct);
        var tree = new CompositeSourceTree([
            (local, new FileSystemSource(local)),
            ("webdav://127.0.0.1/udzial", new RemoteSourceTree((IRemoteSourceReader)dav, "webdav://127.0.0.1/udzial")),
            ("sftp://nas" + FakeSftpServer.Root, new RemoteSourceTree((IRemoteSourceReader)sf, "sftp://nas" + FakeSftpServer.Root)),
        ]);
        Assert.Equal(["Dokumenty", "udzial", "repo"], tree.FolderNames);

        var repo = Path.Combine(_tmp, "repo-dst");
        Directory.CreateDirectory(repo);
        using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", repo), CredentialRole.Backup, _ct);
        var r = await new BackupPipeline(keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(tree, "job", new byte[16]), _ct);
        Assert.Equal(BackupStatus.Warning, r.Status); // dowiązanie na serwerze SFTP pominięte z ostrzeżeniem
        var snap = await MetadataStore.LoadSnapshotAsync(s, keys, r.SnapId!, _ct);
        Assert.Equal(3, snap.SourceRoots.Count);

        using var reader = new RepoReader(s, keys);
        await reader.LoadIndexesAsync(snap, _ct);
        var dst = Path.Combine(_tmp, "odtw");
        Directory.CreateDirectory(dst);
        var rr = await new RestoreExecutor(reader).RestoreAsync(snap, dst, null, _ct);
        Assert.Equal(RestoreStatus.Succeeded, rr.Status);
        Assert.Equal("lokalny", File.ReadAllText(Path.Combine(dst, "Dokumenty", "umowy", "a.txt")));
        Assert.Equal(photo, File.ReadAllBytes(Path.Combine(dst, "udzial", "Zdjęcia 2026", "plaża.jpg")));
        Assert.Equal("z serwera WebDAV", File.ReadAllText(Path.Combine(dst, "udzial", "notatka.txt")));
        Assert.Equal("kwota;1\n", File.ReadAllText(Path.Combine(dst, "repo", "raporty", "q3.csv")));
        Assert.False(File.Exists(Path.Combine(dst, "repo", "link")));
    }

    [Fact]
    public async Task Source_reader_never_leaves_the_root_and_maps_errors()
    {
        _dav.AddCollection("/udzial");
        await using var dav = await Dav("/udzial");
        var reader = (IRemoteSourceReader)dav;
        foreach (var bad in new[] { "..", "a/../..", "/etc", "a//b", "a\\b" })
            Assert.Equal(KbCode.InvalidObjectName, (await Assert.ThrowsAsync<StorageException>(async () => await reader.ListDirectoryAsync(bad, _ct))).Code);
        var tree = new RemoteSourceTree(reader, "x");
        Assert.Throws<IOException>(() => tree.OpenRead("brak.txt")); // brak pliku = pominięcie elementu, nie awaria kopii

        // Serwer znika w trakcie kopii: błąd kopii (przejściowy, ponawiany), nie „udana” kopia z pominiętymi plikami.
        var gone = new LoopbackWebDavServer();
        gone.AddCollection("/udzial");
        await using var s2 = await new WebDavStorageProviderFactory(new HttpWebDavConnector(new FluentFtpAdapterTests.CountingCredentials { User = gone.User, Secret = gone.Secret }))
            .OpenAsync(new DestinationConfig("s", "webdav", "/udzial", "file:x", new Dictionary<string, string>
            {
                ["host"] = "127.0.0.1", ["port"] = gone.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["insecure"] = "1", ["connect_timeout_s"] = "2",
            }), CredentialRole.Backup, _ct);
        await gone.DisposeAsync();
        var ex = Assert.Throws<KobianException>(() => new RemoteSourceTree((IRemoteSourceReader)s2, "y").List("").ToList());
        Assert.Equal(ErrorClass.Transient, ex.ErrorClass);
    }

    [Fact]
    public async Task Credential_typed_in_wizard_is_stored_owner_only_and_read_by_providers()
    {
        var dir = Path.Combine(_tmp, "secrets");
        var store = new CredentialStore(dir);
        var reference = store.Save("nas-kopie", "kopie", "Haslo z Zażółć 123!");
        Assert.Equal("file:nas-kopie", reference);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "nas-kopie.json")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        }
        var ftp = await new FileFtpCredentialProvider(dir).ResolveAsync(reference, CredentialRole.Maintenance, _ct);
        Assert.Equal(("kopie", "Haslo z Zażółć 123!"), (ftp.UserName, ftp.Password));
        store.Save("nas-kopie", "kopie", "nowe-haslo-2026"); // zmiana hasła = zastąpienie wpisu
        Assert.Equal("nowe-haslo-2026", (await new FileFtpCredentialProvider(dir).ResolveAsync(reference, CredentialRole.Backup, _ct)).Password);
        Assert.Throws<KobianException>(() => store.Save("../x", "u", "p"));
        Assert.Throws<KobianException>(() => store.Save("ok", "", "p"));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Equal("nas.firma.pl-jan.kowalski", CredentialStore.SuggestName("NAS.firma.pl", "jan.kowalski"));
    }

    [Theory]
    [InlineData("sftp://nas.firma.pl/srv/dane", "sftp", "nas.firma.pl", null, "/srv/dane")]
    [InlineData("webdav://cloud.example:8443/remote.php/dav/files/jan/Zdj%C4%99cia", "webdav", "cloud.example", 8443, "/remote.php/dav/files/jan/Zdjęcia")]
    [InlineData("ftps://192.168.1.20", "ftps", "192.168.1.20", null, "/")]
    public void Cli_parses_server_sources(string value, string scheme, string host, int? port, string root)
    {
        var s = SourceServer.TryParse(value, "file:nas", null)!;
        Assert.Equal((scheme, host, port, root, "file:nas"), (s.Scheme, s.Host, s.Port, s.Root, s.SecretRef));
        Assert.Null(SourceServer.TryParse(@"C:\Dane", null, null));
        Assert.Null(SourceServer.TryParse("/home/jan", null, null));
        Assert.Throws<ArgumentException>(() => SourceServer.TryParse("sftp://jan:haslo@nas/x", null, null)); // hasło w adresie — nigdy
    }

    [Fact]
    public void Definition_with_folders_and_server_round_trips()
    {
        var json = """{"version":1,"sources":["/home/jan/Dokumenty",{"scheme":"webdav","root":"/dav/zdjecia","host":"cloud.example","secretRef":"file:dav"}],"destination":{"scheme":"local","root":"/mnt/usb"},"key":{"file":"/k"}}""";
        var def = JobDefinition.Parse("files", Encoding.UTF8.GetBytes(json));
        Assert.Equal(2, def.Sources.Count);
        Assert.Equal("/home/jan/Dokumenty", def.Sources[0].Path);
        Assert.Equal(("webdav", "cloud.example", "file:dav"), (def.Sources[1].Remote!.Scheme, def.Sources[1].Remote!.Host, def.Sources[1].Remote!.SecretRef));
        Assert.Equal(def, JobDefinition.Parse("files", Encoding.UTF8.GetBytes(json)));
        var noCred = """{"version":1,"sources":[{"scheme":"sftp","root":"/x","host":"h","hostKey":"SHA256:AAAA"}],"destination":{"scheme":"local","root":"/mnt/usb"},"key":{"file":"/k"}}""";
        Assert.Throws<KobianException>(() => JobDefinition.Parse("files", Encoding.UTF8.GetBytes(noCred)));
    }
}
