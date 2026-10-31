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

/// <summary>
/// Runda 6 planu optymalizacji: kopia przyrostowa nie czyta plików o niezmienionym rozmiarze i czasie modyfikacji (poprzedni snapshot daje bloki),
/// ale czyta wszystko, co mogło się zmienić — inny rozmiar/mtime, plik „świeży” (okno racy) i każde źródło bez wiarygodnych metadanych.
/// </summary>
public sealed class ParentTreeShortcutTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-pts-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly RepoKeys _keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
    private readonly byte[] _host = RandomNumberGenerator.GetBytes(16);
    private string Src => Path.Combine(_tmp, "src");
    private string Repo => Path.Combine(_tmp, "repo");
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-30);

    public ParentTreeShortcutTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Repo);
    }

    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    /// <summary>Źródło z licznikiem odczytów treści.</summary>
    private sealed class CountingSource(string root, bool reliable = true) : ISourceTree
    {
        private readonly FileSystemSource _inner = new(root);
        public List<string> Opened { get; } = [];
        public string DisplayRoot => _inner.DisplayRoot;
        public bool MetadataReliable => reliable;
        public IEnumerable<SourceNode> List(string relativeDir) => _inner.List(relativeDir);

        public Stream OpenRead(string relativePath)
        {
            Opened.Add(relativePath);
            return _inner.OpenRead(relativePath);
        }
    }

    private void Write(string rel, byte[] data, DateTime mtime)
    {
        var p = Path.Combine(Src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        File.SetLastWriteTimeUtc(p, mtime);
    }

    private void MakeSource()
    {
        Write("a.bin", RandomNumberGenerator.GetBytes(5_000_000), Old); // 2 bloki
        Write("d1/b.dat", RandomNumberGenerator.GetBytes(70_000), Old);
        Write("d1/d2/c.txt", "zawartość żółwia"u8.ToArray(), Old);
        Write("empty.txt", [], Old);
    }

    private async Task<IStorageSession> OpenAsync() =>
        await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", Repo), CredentialRole.Backup, _ct);

    private sealed record Run(BackupResult Result, long Reused, CountingSource Source);

    private async Task<Run> BackupAsync(byte[]? parent, bool reliable = true, TimeSpan? parentStartedAgo = null, bool useShortcut = true)
    {
        await using var s = await OpenAsync();
        var src = new CountingSource(Src, reliable);
        BaselineSet? baseline = null;
        ParentTree? pt = null;
        RepoReader? reader = null;
        var mode = SnapshotMode.Full;
        if (parent is not null)
        {
            var prev = await MetadataStore.LoadSnapshotAsync(s, _keys, parent, _ct);
            baseline = await BaselineSet.LoadResolvedAsync(prev.Indexes, new IndexResolver(s, _keys, cacheObjects: false), _ct);
            mode = SnapshotMode.Incremental;
            if (useShortcut)
            {
                reader = new RepoReader(s, _keys) { PackCacheBudget = 8 << 20 };
                await reader.LoadTreeIndexAsync(prev, _ct);
                pt = new ParentTree(prev.RootTreeId, DateTimeOffset.UtcNow - (parentStartedAgo ?? TimeSpan.FromDays(1)), reader.ReadTreeAsync);
            }
        }
        using (reader)
        {
            var p = new BackupPipeline(_keys, new VerifiedUploader(s));
            var r = await p.RunAsync(new BackupRequest(src, "job", _host, mode, null, parent, BaselineStream: baseline, ParentTree: pt), _ct);
            Assert.Equal(BackupStatus.Succeeded, r.Status);
            return new Run(r, p.LastReusedFiles, src);
        }
    }

    private async Task AssertRestoresAsync(byte[] snapId)
    {
        await using var s = await OpenAsync();
        var snap = await MetadataStore.LoadSnapshotAsync(s, _keys, snapId, _ct);
        using var reader = new RepoReader(s, _keys);
        await reader.LoadIndexesAsync(snap, _ct);
        var target = Path.Combine(_tmp, "restore-" + Convert.ToHexStringLower(snapId));
        Directory.CreateDirectory(target);
        Assert.Equal(RestoreStatus.Succeeded, (await new RestoreExecutor(reader).RestoreAsync(snap, target, null, _ct)).Status);
        foreach (var f in Directory.GetFiles(Src, "*", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(Path.Combine(target, Path.GetRelativePath(Src, f))));
        Assert.Equal(CheckStatus.Succeeded, (await new RepoChecker(s, _keys).CheckSnapshotAsync(snapId, _ct)).Status);
    }

    [Fact]
    public async Task Unchanged_tree_reads_no_file_content_and_uploads_nothing_but_restores_bit_exact()
    {
        MakeSource();
        var full = await BackupAsync(null);
        Assert.Equal(4, full.Source.Opened.Count);
        var inc = await BackupAsync(full.Result.SnapId);
        Assert.Empty(inc.Source.Opened); // żaden plik nie został otwarty
        Assert.Equal(4, inc.Reused);
        Assert.Equal(0L, inc.Result.Stats.BytesNew + inc.Result.Stats.PacksNew); // drzewa te same (ten sam id) — nic nowego do wysłania
        Assert.Equal(full.Result.Stats.Files, inc.Result.Stats.Files);
        Assert.Equal(full.Result.Stats.BytesRaw, inc.Result.Stats.BytesRaw);
        await AssertRestoresAsync(inc.Result.SnapId!);
    }

    [Fact]
    public async Task Changed_size_or_mtime_or_new_files_are_read_and_the_rest_is_reused()
    {
        MakeSource();
        var full = await BackupAsync(null);
        Write("d1/b.dat", RandomNumberGenerator.GetBytes(70_001), Old);            // inny rozmiar
        Write("d1/d2/c.txt", "zawartość żółwia"u8.ToArray(), Old.AddMinutes(5));   // ten sam rozmiar, inny mtime
        Write("nowy.txt", "nowy"u8.ToArray(), Old);                                  // nowy plik
        var inc = await BackupAsync(full.Result.SnapId);
        Assert.Equal(["d1/b.dat", "d1/d2/c.txt", "nowy.txt"], inc.Source.Opened.Order(StringComparer.Ordinal));
        Assert.Equal(2, inc.Reused); // a.bin i empty.txt
        await AssertRestoresAsync(inc.Result.SnapId!);
    }

    [Fact]
    public async Task Recently_modified_files_inside_the_racy_window_are_always_read()
    {
        MakeSource();
        var fresh = DateTime.UtcNow.AddSeconds(-10);
        Write("swiezy.txt", "dane"u8.ToArray(), fresh);
        var full = await BackupAsync(null);
        // poprzedni przebieg wystartował 1 s po mtime pliku (< 2 s): zmiana w tym samym znaczniku czasu byłaby niewidoczna → plik czytany ponownie
        var inc = await BackupAsync(full.Result.SnapId, parentStartedAgo: TimeSpan.FromSeconds(9));
        Assert.Equal(["swiezy.txt"], inc.Source.Opened);
        Assert.Equal(4, inc.Reused);
        // następny przebieg: poprzedni wystartował 9 s po mtime pliku → plik przestaje być „świeży”
        var later = await BackupAsync(inc.Result.SnapId, parentStartedAgo: TimeSpan.FromSeconds(1));
        Assert.Empty(later.Source.Opened);
        await AssertRestoresAsync(later.Result.SnapId!);
    }

    [Fact]
    public async Task Unreliable_sources_without_parent_tree_and_forced_content_check_read_everything()
    {
        MakeSource();
        var full = await BackupAsync(null);
        var remote = await BackupAsync(full.Result.SnapId, reliable: false);   // np. listing FTP z dokładnością do minuty
        Assert.Equal(4, remote.Source.Opened.Count);
        Assert.Equal(0, remote.Reused);
        var forced = await BackupAsync(full.Result.SnapId, useShortcut: false); // pełna weryfikacja treści (co kilka dni)
        Assert.Equal(4, forced.Source.Opened.Count);
    }

    [Fact]
    public async Task Same_size_and_mtime_content_change_is_missed_by_the_shortcut_and_caught_by_content_check()
    {
        // Znane, jawne ograniczenie skrótu (jak rsync/restic bez --ignore-inode): narzędzie przywracające mtime ukrywa zmianę. Zapewnia to okresowa pełna weryfikacja treści.
        MakeSource();
        var full = await BackupAsync(null);
        Write("d1/b.dat", RandomNumberGenerator.GetBytes(70_000), Old); // inna treść, ten sam rozmiar i mtime
        var shortcut = await BackupAsync(full.Result.SnapId);
        Assert.DoesNotContain("d1/b.dat", shortcut.Source.Opened);
        var verified = await BackupAsync(shortcut.Result.SnapId, useShortcut: false);
        Assert.Contains("d1/b.dat", verified.Source.Opened);
        await AssertRestoresAsync(verified.Result.SnapId!); // po pełnej weryfikacji repozytorium odpowiada dysku
    }
}
