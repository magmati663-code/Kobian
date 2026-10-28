using System.Security.Cryptography;
using Kobian.Crypto;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;
using Kobian.Providers.Sources.MsSql;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>Plik .bak ze stagingu jako wejście potoku (tryb Database, klucz 14) — dane syntetyczne, bez SQL Server (S5c).</summary>
public sealed class DatabaseSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-db-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public DatabaseSnapshotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static MsSqlBackupInfo Info(bool checksum = true) => new(
        "SRV01\\SQLEXPRESS", "FirmaTest", MsSqlBackupKind.Full, true, checksum, Guid.NewGuid(), null, 41000000012300001m, 41000000012500001m, 41000000010000001m,
        DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), "subiekt-gt");

    [Fact, Trait("D", "D5.10"), Trait("D", "R2.3.5")]
    public async Task Staged_bak_becomes_database_snapshot_with_dbMeta_and_no_staging_path()
    {
        var staged = Path.Combine(_dir, "run-abc", Guid.NewGuid().ToString("N") + ".bak");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        var content = RandomNumberGenerator.GetBytes((5 * 1024 * 1024) + 17);
        await File.WriteAllBytesAsync(staged, content, _ct);
        var info = Info();

        using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
        var store = new MemoryStorage();
        var s = store.Open();
        var src = new StagedFileSource(staged, DbSnapshotMeta.EntryName, "mssql://SRV01/FirmaTest");
        var r = await new BackupPipeline(keys, new VerifiedUploader(s)).RunAsync(
            new BackupRequest(src, "job-db", RandomNumberGenerator.GetBytes(16), SnapshotMode.Database, DbMeta: DbSnapshotMeta.Build(info)), _ct);

        Assert.Equal(BackupStatus.Succeeded, r.Status);
        var snap = await MetadataStore.LoadSnapshotAsync(s, keys, r.SnapId!, _ct);
        Assert.Equal(SnapshotMode.Database, snap.Mode);
        Assert.Equal(info.BackupSetUuid, DbSnapshotMeta.BackupSetUuid(snap.DbMeta));
        Assert.Equal("41000000012500001", snap.DbMeta!["lastLsn"]);
        Assert.DoesNotContain(snap.SourceRoots, x => x.Contains("run-abc", StringComparison.Ordinal));
        Assert.DoesNotContain(snap.DbMeta.Values, v => v.Contains(_dir, StringComparison.Ordinal));

        using var reader = new RepoReader(s, keys);
        await reader.LoadIndexesAsync(snap, _ct);
        var tree = await reader.ReadTreeAsync(snap.RootTreeId, _ct);
        var e = Assert.Single(tree);
        Assert.Equal(DbSnapshotMeta.EntryName, e.Name);
        Assert.Equal(content.Length, e.Size);
        Assert.Equal(SHA256.HashData(content), e.FileSha256);
    }

    [Fact, Trait("D", "D5.10")]
    public void Backup_without_checksum_is_refused_and_entry_name_must_be_segment()
    {
        Assert.Throws<InvalidOperationException>(() => DbSnapshotMeta.Build(Info(checksum: false)));
        Assert.Throws<ArgumentException>(() => new StagedFileSource(Path.Combine(_dir, "x"), "../x", "d"));
    }
}
