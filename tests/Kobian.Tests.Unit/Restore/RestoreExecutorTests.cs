using System.Security.Cryptography;
using System.Text;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Engine.Restore;
using Kobian.Format.Metadata;
using Kobian.Format.Repo;
using Kobian.Providers.Storage.Local;
using Xunit;

namespace Kobian.Tests.Unit.Restore;

/// <summary>BL-43 / BL-44: backup → restore bit w bit (V1), brak nadpisywania, filtr, wykrycie podmiany packa, kobian-restore offline.</summary>
public sealed class RestoreExecutorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-rx-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly byte[] _mk = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _repoId = RandomNumberGenerator.GetBytes(16);

    public RestoreExecutorTests()
    {
        Directory.CreateDirectory(Path.Combine(Src, "docs", "deep"));
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(Dst);
        File.WriteAllBytes(Path.Combine(Src, "big.bin"), RandomNumberGenerator.GetBytes((9 * 1024 * 1024) + 5));
        File.WriteAllText(Path.Combine(Src, "docs", "raport.xlsx"), "xlsx");
        File.WriteAllText(Path.Combine(Src, "docs", "notatka.txt"), "zażółć gęślą jaźń");
        File.WriteAllBytes(Path.Combine(Src, "docs", "deep", "pusty"), []);
        File.SetLastWriteTimeUtc(Path.Combine(Src, "docs", "notatka.txt"), new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    private string Src => Path.Combine(_tmp, "src");

    private string Repo => Path.Combine(_tmp, "repo");

    private string Dst => Path.Combine(_tmp, "dst");

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private async Task<(IStorageSession S, RepoKeys K, SnapshotObject Snap)> Backup(long packTarget = 2 * 1024 * 1024)
    {
        var keys = RepoKeys.Derive(_mk, _repoId);
        var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", Repo), CredentialRole.Backup, _ct);
        var r = await new BackupPipeline(keys, new VerifiedUploader(s)).RunAsync(new BackupRequest(new FileSystemSource(Src), "job", new byte[16], PackTargetSize: packTarget), _ct);
        Assert.Equal(BackupStatus.Succeeded, r.Status);
        return (s, keys, await MetadataStore.LoadSnapshotAsync(s, keys, r.SnapId!, _ct));
    }

    private static void AssertSameTree(string a, string b)
    {
        var fa = Directory.GetFiles(a, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(a, p)).Order(StringComparer.Ordinal).ToList();
        var fb = Directory.GetFiles(b, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(b, p)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(fa, fb);
        foreach (var f in fa) Assert.Equal(SHA256.HashData(File.ReadAllBytes(Path.Combine(a, f))), SHA256.HashData(File.ReadAllBytes(Path.Combine(b, f))));
    }

    [Fact, Trait("BL", "BL-43"), Trait("AC", "AC-07.1"), Trait("V", "V1")]
    public async Task Backup_then_restore_is_bit_identical_with_mtime()
    {
        var (s, keys, snap) = await Backup();
        await using (s)
        using (keys)
        {
            using var reader = new RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap, _ct);
            var r = await new RestoreExecutor(reader).RestoreAsync(snap, Dst, null, _ct);
            Assert.Equal(RestoreStatus.Succeeded, r.Status);
            Assert.Equal(4, r.Files);
            AssertSameTree(Src, Dst);
            Assert.Equal(new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(Dst, "docs", "notatka.txt")));
        }
    }

    [Fact, Trait("RD", "RD-60"), Trait("Plan", "D8.8")]
    public async Task Progress_is_reported_per_file_and_no_partial_files_remain()
    {
        var (s, keys, snap) = await Backup();
        await using (s)
        using (keys)
        {
            using var reader = new RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap, _ct);
            var reports = new List<(long Files, long Bytes)>();
            var r = await new RestoreExecutor(reader) { Progress = (f, b) => reports.Add((f, b)) }.RestoreAsync(snap, Dst, null, _ct);
            Assert.Equal(RestoreStatus.Succeeded, r.Status);
            Assert.Equal(Enumerable.Range(1, 4).Select(i => (long)i), reports.Select(x => x.Files)); // rośnie o 1, kończy się na liczbie plików
            Assert.Equal(r.Bytes, reports[^1].Bytes);
            Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second.Bytes >= p.First.Bytes));
            Assert.Empty(Directory.GetFiles(Dst, "*" + RestoreExecutor.PartialSuffix, SearchOption.AllDirectories));
        }
    }

    [Fact, Trait("D", "D8.9"), Trait("KB", "KB-5105")]
    public async Task Existing_files_are_never_overwritten_and_filter_limits_scope()
    {
        var (s, keys, snap) = await Backup();
        await using (s)
        using (keys)
        {
            Directory.CreateDirectory(Path.Combine(Dst, "docs"));
            File.WriteAllText(Path.Combine(Dst, "docs", "raport.xlsx"), "MOJE");
            using var reader = new RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap, _ct);
            var r = await new RestoreExecutor(reader).RestoreAsync(snap, Dst, "*.xlsx", _ct);
            Assert.Equal(RestoreStatus.Warning, r.Status);
            Assert.Equal("MOJE", File.ReadAllText(Path.Combine(Dst, "docs", "raport.xlsx")));
            Assert.Contains(r.Issues, i => i.Code == KbCode.RestoreTargetExists && i.RelativePath == "docs/raport.xlsx");
            Assert.False(File.Exists(Path.Combine(Dst, "big.bin")));
            Assert.False(File.Exists(Path.Combine(Dst, "docs", "notatka.txt")));
        }
    }

    [Fact, Trait("SEC", "RD-76")]
    public async Task Resume_keeps_foreign_oversized_partial_and_removes_own_small_partial()
    {
        var (s, keys, snap) = await Backup();
        await using (s)
        using (keys)
        {
            using var reader = new RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap, _ct);
            var foreign = Path.Combine(Dst, "docs", "notatka.txt" + RestoreExecutor.PartialSuffix);
            var own = Path.Combine(Dst, "docs", "raport.xlsx" + RestoreExecutor.PartialSuffix);
            Directory.CreateDirectory(Path.Combine(Dst, "docs"));
            File.WriteAllBytes(foreign, new byte[4096]); // większy niż notatka.txt — nie może być naszym zapisem
            File.WriteAllText(own, "xl"); // krótszy niż raport.xlsx — niedokończony zapis z poprzedniej próby
            var r = await new RestoreExecutor(reader) { Journal = new AllStartedJournal() }.RestoreAsync(snap, Dst, null, _ct);
            Assert.True(File.Exists(foreign));
            Assert.Equal(4096, new FileInfo(foreign).Length);
            Assert.Contains(r.Issues, i => i.Code == KbCode.RestoreTargetExists && i.RelativePath == "docs/notatka.txt");
            Assert.Equal("xlsx", File.ReadAllText(Path.Combine(Dst, "docs", "raport.xlsx")));
            Assert.False(File.Exists(own));
        }
    }

    private sealed class AllStartedJournal : IRestoreJournal
    {
        public bool IsRestored(string rel) => false;

        public bool WasStarted(string rel) => true;

        public ValueTask StartedAsync(string rel, long size, int chunks) => ValueTask.CompletedTask;

        public ValueTask FinishedAsync(string rel, RestoreFileState state) => ValueTask.CompletedTask;
    }

    private sealed class AllRestoredJournal : IRestoreJournal
    {
        public bool IsRestored(string rel) => true;

        public bool WasStarted(string rel) => true;

        public ValueTask StartedAsync(string rel, long size, int chunks) => ValueTask.CompletedTask;

        public ValueTask FinishedAsync(string rel, RestoreFileState state) => ValueTask.CompletedTask;
    }

    /// <summary>Dziennik twierdzi „wszystko odtworzone”, ale folder docelowy został wyczyszczony: pliki muszą wrócić, a nie zostać zaliczone z pamięci.</summary>
    [Fact, Trait("D", "D8.8")]
    public async Task Journal_claim_of_restored_files_is_not_trusted_when_they_are_missing_on_disk()
    {
        var (s, keys, snap) = await Backup();
        await using (s)
        using (keys)
        {
            using var reader = new RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap, _ct);
            var r = await new RestoreExecutor(reader) { Journal = new AllRestoredJournal() }.RestoreAsync(snap, Dst, null, _ct);
            Assert.Equal(RestoreStatus.Succeeded, r.Status);
            Assert.Equal(0, r.Resumed); // nic nie było na dysku → nic nie „wznowiono”
            AssertSameTree(Src, Dst);
        }
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("KB", "KB-5202")]
    public async Task Tampered_pack_fails_restore_without_leaving_partial_file()
    {
        var (s, keys, snap) = await Backup(packTarget: 1); // każdy blok we własnym packu — uszkodzenie dotyka tylko pliku, którego blok leży w tym packu
        await using (s)
        using (keys)
        {
            using var reader = new RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap, _ct);
            // uszkadzamy pack z PIERWSZYM blokiem big.bin (pack bloku 4 MiB; drzewa i małe pliki leżą w innych packach)
            var big = (await reader.ReadTreeAsync(snap.RootTreeId, _ct)).Single(x => x.Name == "big.bin");
            Assert.True(reader.TryLocate(big.Chunks![0], out var loc));
            var pack = Path.Combine(Repo, RepoPaths.Pack(loc!.PackId).Replace('/', Path.DirectorySeparatorChar));
            var b = File.ReadAllBytes(pack);
            b[200] ^= 0x01;
            File.WriteAllBytes(pack, b);
            using var reader2 = new RepoReader(s, keys);
            await reader2.LoadIndexesAsync(snap, _ct);
            var r = await new RestoreExecutor(reader2).RestoreAsync(snap, Dst, null, _ct);
            Assert.Equal(RestoreStatus.Failed, r.Status);
            Assert.Empty(Directory.GetFiles(Dst, "*" + RestoreExecutor.PartialSuffix, SearchOption.AllDirectories)); // nazwa testu obiecuje to od początku; wcześniej nie była sprawdzana
            // uszkodzony pack dotyczy tylko big.bin: pozostałe pliki (inne packi) mają wrócić, a nie zostać porzucone po pierwszym błędzie
            Assert.Contains(r.Issues, i => i.RelativePath == "big.bin" && i.Code is KbCode.RestorePackMissing or KbCode.RestoreHashMismatch);
            Assert.False(File.Exists(Path.Combine(Dst, "big.bin")));
            Assert.True(File.Exists(Path.Combine(Dst, "docs", "notatka.txt")), string.Join("; ", r.Issues.Select(i => i.Code + ":" + i.RelativePath)) + " | " + string.Join(",", Directory.GetFiles(Dst, "*", SearchOption.AllDirectories)));
            Assert.Equal("zażółć gęślą jaźń", await File.ReadAllTextAsync(Path.Combine(Dst, "docs", "notatka.txt"), _ct));
            Assert.True(File.Exists(Path.Combine(Dst, "docs", "deep", "pusty")));
        }
    }

    /// <summary>Klucz hasłowy + nagłówek kobian.repo zgodne z <see cref="_mk"/>/<see cref="_repoId"/> (BL-24); zwraca ścieżki (klucz, hasło, nagłówek).</summary>
    private (string Key, string Pw, string Header) WrapKeys(string pw, string tag)
    {
        var keyId = RandomNumberGenerator.GetBytes(16);
        using var keys = RepoKeys.Derive(_mk, _repoId);
        var now = DateTimeOffset.UtcNow;
        var key = Path.Combine(_tmp, tag + ".key");
        var pwFile = Path.Combine(_tmp, tag + ".txt");
        var header = Path.Combine(_tmp, tag + ".repo");
        File.WriteAllText(key, KeyFiles.WrapWithPassword(_mk, pw, keyId, now), Encoding.UTF8);
        File.WriteAllText(pwFile, pw + "\n", Encoding.UTF8);
        File.WriteAllText(header, RepoHeader.Create(keys, now, [keyId]), Encoding.UTF8);
        return (key, pwFile, header);
    }

    [Fact, Trait("BL", "BL-44"), Trait("D", "D8.3")]
    public async Task Standalone_kobian_restore_lists_and_restores_latest()
    {
        var (s, keys, _) = await Backup();
        await s.DisposeAsync();
        keys.Dispose();
        var (keyFile, pwFile, header) = WrapKeys("zażółć-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)), "a");
        var o = new StringWriter();
        var e = new StringWriter();
        var lc = await OfflineRestore.RunAsync(["list", "--repo", Repo, "--key-file", keyFile, "--password-file", pwFile, "--repo-header", header], new LocalStorageProviderFactory(), o, e, _ct);
        Assert.True(lc == 0, e.ToString());
        Assert.Contains(" job ", o.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await OfflineRestore.RunAsync(["restore", "--repo", Repo, "--key-file", keyFile, "--password-file", pwFile, "--repo-header", header, "--to", Dst], new LocalStorageProviderFactory(), o, e, _ct));
        AssertSameTree(Src, Dst);
        Assert.Equal(64, await OfflineRestore.RunAsync(["restore", "--repo", Repo], new LocalStorageProviderFactory(), o, e, _ct));
        // bez hasła = składnia (64); jawny --repo-id i klucz deweloperski nie istnieją (ADR-0002/0003 usunięte)
        Assert.Equal(64, await OfflineRestore.RunAsync(["restore", "--repo", Repo, "--key-file", keyFile, "--repo-header", header, "--to", Dst], new LocalStorageProviderFactory(), o, e, _ct));
        // klucz nie z tego repozytorium (inny MK) → odrzucony (MAC nagłówka / unwrap)
        var (otherKey, otherPw, _) = WrapKeys("inne-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)), "b");
        var otherMk = RandomNumberGenerator.GetBytes(32);
        var otherPassword = (await File.ReadAllTextAsync(otherPw, _ct)).Trim();
        var otherWrapped = KeyFiles.WrapWithPassword(otherMk, otherPassword, RandomNumberGenerator.GetBytes(16), DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(otherKey, otherWrapped, Encoding.UTF8, _ct);
        Assert.Equal(2, await OfflineRestore.RunAsync(["restore", "--repo", Repo, "--key-file", otherKey, "--password-file", otherPw, "--repo-header", header, "--to", Path.Combine(_tmp, "x")], new LocalStorageProviderFactory(), new StringWriter(), new StringWriter(), _ct));
    }

    [Fact, Trait("BL", "BL-24"), Trait("BL", "BL-44"), Trait("D", "D3.11")]
    public async Task Standalone_kobian_restore_rejects_wrong_password_without_leaking_it()
    {
        var (s, keys, _) = await Backup();
        await s.DisposeAsync();
        keys.Dispose();
        var pw = "zażółć-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var (keyFile, pwFile, header) = WrapKeys(pw, "pw");
        var e = new StringWriter();
        Assert.Equal(0, await OfflineRestore.RunAsync(["restore", "--repo", Repo, "--key-file", keyFile, "--password-file", pwFile, "--repo-header", header, "--to", Dst], new LocalStorageProviderFactory(), new StringWriter(), e, _ct));
        AssertSameTree(Src, Dst);
        await File.WriteAllTextAsync(pwFile, "bledne", Encoding.UTF8, _ct);
        Assert.Equal(2, await OfflineRestore.RunAsync(["restore", "--repo", Repo, "--key-file", keyFile, "--password-file", pwFile, "--repo-header", header, "--to", Path.Combine(_tmp, "y")], new LocalStorageProviderFactory(), new StringWriter(), e, _ct));
        Assert.DoesNotContain(pw, e.ToString(), StringComparison.Ordinal);
    }
}
