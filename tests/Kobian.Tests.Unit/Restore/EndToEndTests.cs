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

namespace Kobian.Tests.Unit.Restore;

/// <summary>
/// Etap E2E (tools/ci.sh, cecha Category=E2E): pełny cykl życia repozytorium na dostawcy Local —
/// backup pełny → zmiany źródła (modyfikacja, nowy plik, usunięcie) → backup przyrostowy → odtworzenie OBU migawek,
/// każda bit w bit względem stanu źródła z chwili backupu (V1, AC-07.1). Dane wyłącznie syntetyczne, katalog tymczasowy.
/// </summary>
[Trait("Category", "E2E")]
public sealed class EndToEndTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-e2e-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly byte[] _host = new byte[16];

    private string P(string name) => Path.Combine(_tmp, name);

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
    }

    private static void AssertSameTree(string expected, string actual)
    {
        var fe = Directory.GetFiles(expected, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(expected, p)).Order(StringComparer.Ordinal).ToList();
        var fa = Directory.GetFiles(actual, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(actual, p)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(fe, fa);
        foreach (var f in fe)
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(Path.Combine(expected, f))), SHA256.HashData(File.ReadAllBytes(Path.Combine(actual, f))));
    }

    [Fact, Trait("AC", "AC-07.1"), Trait("AC", "AC-08.1"), Trait("V", "V1")]
    public async Task Full_then_incremental_backup_restores_both_generations_bit_identical()
    {
        var src = P("src");
        Directory.CreateDirectory(Path.Combine(src, "a", "b"));
        Directory.CreateDirectory(P("repo"));
        File.WriteAllBytes(Path.Combine(src, "big.bin"), RandomNumberGenerator.GetBytes((6 * 1024 * 1024) + 11));
        File.WriteAllText(Path.Combine(src, "a", "keep.txt"), "bez zmian");
        File.WriteAllText(Path.Combine(src, "a", "b", "edit.txt"), "wersja 1");
        File.WriteAllText(Path.Combine(src, "a", "b", "gone.txt"), "do usunięcia");
        File.WriteAllBytes(Path.Combine(src, "empty"), []);

        using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
        await using var s = await new LocalStorageProviderFactory().OpenAsync(new DestinationConfig("d", "local", P("repo")), CredentialRole.Backup, _ct);
        var pipeline = new BackupPipeline(keys, new VerifiedUploader(s));

        var full = await pipeline.RunAsync(new BackupRequest(new FileSystemSource(src), "job", _host, PackTargetSize: 2 * 1024 * 1024), _ct);
        Assert.Equal(BackupStatus.Succeeded, full.Status);
        CopyTree(src, P("state1"));
        var snap1 = await MetadataStore.LoadSnapshotAsync(s, keys, full.SnapId!, _ct);

        // zmiany źródła: modyfikacja, nowy plik, usunięcie
        File.WriteAllText(Path.Combine(src, "a", "b", "edit.txt"), "wersja 2 — dłuższa");
        File.SetLastWriteTimeUtc(Path.Combine(src, "a", "b", "edit.txt"), new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(src, "nowy.txt"), "nowy");
        File.Delete(Path.Combine(src, "a", "b", "gone.txt"));

        var baseline = new List<BaselineIndex>();
        foreach (var (id, _) in snap1.Indexes)
        {
            var (idx, sha) = await MetadataStore.LoadIndexAsync(s, keys, id, _ct);
            baseline.Add(new BaselineIndex(id, sha, idx));
        }
        var inc = await pipeline.RunAsync(new BackupRequest(new FileSystemSource(src), "job", _host, SnapshotMode.Incremental, baseline, full.SnapId, PackTargetSize: 2 * 1024 * 1024), _ct);
        Assert.Equal(BackupStatus.Succeeded, inc.Status);
        // dedup: duży niezmieniony plik nie jest ponownie wysyłany
        Assert.True(inc.Stats.BytesNew < 1024 * 1024, $"BytesNew={inc.Stats.BytesNew}");
        CopyTree(src, P("state2"));
        var snap2 = await MetadataStore.LoadSnapshotAsync(s, keys, inc.SnapId!, _ct);

        foreach (var (snap, expected, name) in new[] { (snap1, "state1", "r1"), (snap2, "state2", "r2") })
        {
            using var reader = new RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap, _ct);
            Directory.CreateDirectory(P(name));
            var r = await new RestoreExecutor(reader).RestoreAsync(snap, P(name), null, _ct);
            Assert.Equal(RestoreStatus.Succeeded, r.Status);
            AssertSameTree(P(expected), P(name));
        }
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(P("r2"), "a", "b", "edit.txt")));
    }
}
