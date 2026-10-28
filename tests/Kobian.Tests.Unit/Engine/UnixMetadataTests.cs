using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Engine.Restore;
using Kobian.Format.Metadata;
using Kobian.Providers.Storage.Local;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>Linux/macOS: typ wpisu z lstat (FIFO/gniazdo pomijane zamiast blokować kopię), uprawnienia POSIX w drzewie (klucz 16) i ich odtworzenie bez setuid/setgid.</summary>
public sealed class UnixMetadataTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-unix-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public UnixMetadataTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var d in Directory.GetDirectories(_tmp, "*", SearchOption.AllDirectories))
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_tmp, recursive: true);
    }

    [Fact]
    public void Tree_codec_round_trips_unix_mode_and_rejects_out_of_range()
    {
        var entries = new[] { new TreeEntry("skrypt.sh", EntryKind.File, 3, 1, Chunks: [], UnixMode: 0x1ED), new TreeEntry("bez", EntryKind.File, 0, 1, Chunks: []) };
        var back = TreeCodec.Decode(TreeCodec.Encode(entries));
        Assert.Equal((uint?)0x1ED, back.Single(e => e.Name == "skrypt.sh").UnixMode);
        Assert.Null(back.Single(e => e.Name == "bez").UnixMode);
        Assert.Throws<KobianException>(() => TreeCodec.Encode([new TreeEntry("x", EntryKind.File, 0, UnixMode: 0x1000)]));
    }

    [Fact]
    public void Mode_bits_classify_file_types()
    {
        Assert.Equal(UnixFileType.Fifo, UnixFileStat.Make(0x11A4, 0, 0).Type);
        Assert.Equal(UnixFileType.Socket, UnixFileStat.Make(0xC1ED, 0, 0).Type);
        Assert.Equal((UnixFileType.Regular, 0x9EDu), (UnixFileStat.Make(0x89ED, 0, 0).Type, UnixFileStat.Make(0x89ED, 0, 0).Mode));
    }

    [Fact, Trait("Category", "Unix")]
    public async Task Fifo_and_socket_are_skipped_and_permissions_survive_backup_and_restore()
    {
        if (OperatingSystem.IsWindows()) return;
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(Path.Combine(src, "bin"));
        File.WriteAllText(Path.Combine(src, "bin", "run.sh"), "#!/bin/sh\necho ok\n");
        File.SetUnixFileMode(Path.Combine(src, "bin", "run.sh"), (UnixFileMode)0x9ED); // 04755 — setuid nie może wrócić
        File.WriteAllText(Path.Combine(src, "tajne.txt"), "x");
        File.SetUnixFileMode(Path.Combine(src, "tajne.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using (var p = Process.Start("mkfifo", Path.Combine(src, "kolejka"))!) await p.WaitForExitAsync(_ct);
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        sock.Bind(new UnixDomainSocketEndPoint(Path.Combine(src, "gniazdo")));
        Directory.CreateDirectory(Path.Combine(src, "tylko-odczyt"));
        File.WriteAllText(Path.Combine(src, "tylko-odczyt", "a.txt"), "a");
        File.SetUnixFileMode(Path.Combine(src, "tylko-odczyt"), (UnixFileMode)0x16D); // 0555

        Assert.Equal(UnixFileType.Fifo, UnixFileStat.TryLStat(Path.Combine(src, "kolejka"))!.Value.Type);
        var repo = Path.Combine(_tmp, "repo");
        Directory.CreateDirectory(repo);
        using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", repo), CredentialRole.Backup, _ct);
        var run = new BackupPipeline(keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(new FileSystemSource(src), "job", new byte[16]), _ct);
        var r = await run.WaitAsync(TimeSpan.FromSeconds(60), _ct); // przed poprawką: open() na FIFO wisiał bez końca
        Assert.Equal(BackupStatus.Warning, r.Status);

        var snap = await MetadataStore.LoadSnapshotAsync(s, keys, r.SnapId!, _ct);
        using var reader = new RepoReader(s, keys);
        await reader.LoadIndexesAsync(snap, _ct);
        var dst = Path.Combine(_tmp, "dst");
        Directory.CreateDirectory(dst);
        var rr = await new RestoreExecutor(reader).RestoreAsync(snap, dst, null, _ct);
        Assert.Equal(RestoreStatus.Succeeded, rr.Status);
        Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(Path.Combine(dst, "bin", "run.sh"))); // 0755, bez setuid
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dst, "tajne.txt")));
        Assert.Equal((UnixFileMode)0x16D, File.GetUnixFileMode(Path.Combine(dst, "tylko-odczyt")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(dst, "tylko-odczyt", "a.txt")));
        Assert.False(File.Exists(Path.Combine(dst, "kolejka")) || File.Exists(Path.Combine(dst, "gniazdo")));
    }

    [Fact, Trait("Category", "Unix")]
    public async Task Restore_as_root_gives_files_the_owner_of_a_non_root_target_folder()
    {
        if (!UnixFileStat.IsRoot()) return; // tylko gdy testy działają jako root (CI w kontenerze, usługa)
        var src = Path.Combine(_tmp, "src2");
        Directory.CreateDirectory(Path.Combine(src, "a", "b"));
        File.WriteAllText(Path.Combine(src, "a", "b", "plik.txt"), "x");
        var repo = Path.Combine(_tmp, "repo2");
        Directory.CreateDirectory(repo);
        using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", repo), CredentialRole.Backup, _ct);
        var r = await new BackupPipeline(keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(new FileSystemSource(src), "job", new byte[16]), _ct);
        var snap = await MetadataStore.LoadSnapshotAsync(s, keys, r.SnapId!, _ct);
        using var reader = new RepoReader(s, keys);
        await reader.LoadIndexesAsync(snap, _ct);
        var dst = Path.Combine(_tmp, "dst2");
        Directory.CreateDirectory(dst);
        Assert.True(UnixFileStat.TryLChown(dst, 54321, 54322));
        var rr = await new RestoreExecutor(reader) { InheritTargetOwner = true }.RestoreAsync(snap, dst, "a/b/*", _ct);
        Assert.Equal(RestoreStatus.Succeeded, rr.Status);
        foreach (var p in new[] { Path.Combine(dst, "a"), Path.Combine(dst, "a", "b"), Path.Combine(dst, "a", "b", "plik.txt") })
            Assert.Equal((54321u, 54322u), (UnixFileStat.TryLStat(p)!.Value.Uid, UnixFileStat.TryLStat(p)!.Value.Gid));
    }
}
