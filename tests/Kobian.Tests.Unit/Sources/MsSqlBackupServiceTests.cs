using System.Security.Cryptography;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Backup;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;
using Kobian.Providers.Sources.MsSql;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Sources;

/// <summary>
/// BL-40 cz. 1: MsSqlBackupService na sesji zastępczej (S5c = bez kontenerów; prawdziwy SQL Server — kolejka W-23).
/// Dane wyłącznie syntetyczne; nazwy baz losowe.
/// </summary>
public sealed class MsSqlBackupServiceTests : IDisposable
{
    private const string SqlRoot = @"C:\ProgramData\Kobian\staging";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kobian-mssql-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly string _db = "Firma_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public MsSqlBackupServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private MsSqlBackupRequest Request(string runId = "run0000a1") => new(_db, _root, runId, SqlRoot)
    {
        ConflictWait = TimeSpan.Zero,
        ConflictPollInterval = TimeSpan.FromMilliseconds(1),
    };

    private FakeSqlServer Server() => new(_db, _root, SqlRoot);

    private bool StagingEmpty() => !Directory.EnumerateFileSystemEntries(_root).Any();

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.1"), Trait("D", "D5.7"), Trait("K", "K15.1")]
    public async Task Copy_only_backup_with_checksum_is_staged_described_and_removed()
    {
        var sql = Server();
        var svc = new MsSqlBackupService(sql);
        var staged = await svc.PrepareAsync(Request(), _ct);
        await using (staged)
        {
            Assert.True(File.Exists(staged.StagedFile));
            Assert.Equal(MsSqlVerifyMode.Checksum, staged.Verify); // wariant minimalny: brak CREATE DATABASE (D5.7)
            Assert.Equal("1", staged.DbMeta["copyOnly"]);
            Assert.Equal("1", staged.DbMeta["checksum"]);
            Assert.Equal("checksum", staged.DbMeta["verify"]);
            Assert.Equal(sql.LastSetUuid.ToString("D"), staged.DbMeta["backupSetUuid"]);
            Assert.Equal(sql.DiffBase.ToString("D"), staged.DbMeta["differentialBaseGuid"]); // AC-02.1: niezmieniony
            Assert.Empty(staged.Warnings);
            Assert.DoesNotContain(staged.DbMeta.Values, v => v.Contains(_root, StringComparison.Ordinal) || v.Contains("staging", StringComparison.OrdinalIgnoreCase));
        }

        Assert.True(staged.Removed);
        Assert.True(StagingEmpty()); // AC-02.1: brak *.bak (i katalogu uruchomienia) po zakończeniu
        var backup = Assert.Single(sql.Executed, c => c.Text == MsSqlBackupCommand.TemplateText);
        Assert.Equal(_db, backup.Parameters["@db"]); // nazwa wyłącznie jako parametr (SEC-5.1)
        Assert.StartsWith(SqlRoot + @"\run0000a1\", (string)backup.Parameters["@stagingPath"], StringComparison.Ordinal);
        Assert.DoesNotContain(sql.Executed, c => c.Text.Contains(_db, StringComparison.Ordinal));
        Assert.DoesNotContain(sql.Executed, c => c.Text == MsSqlBackupCommand.VerifyOnlyText);
    }

    [Fact, Trait("BL", "BL-40"), Trait("D", "D5.7")]
    public async Task Verifyonly_runs_on_the_same_file_when_login_has_create_database()
    {
        var sql = Server();
        sql.CanVerifyOnly = true;
        await using var staged = await new MsSqlBackupService(sql).PrepareAsync(Request(), _ct);
        Assert.Equal(MsSqlVerifyMode.VerifyOnly, staged.Verify);
        Assert.Equal("verifyonly", staged.DbMeta["verify"]);
        var backup = Assert.Single(sql.Executed, c => c.Text == MsSqlBackupCommand.TemplateText);
        var verify = Assert.Single(sql.Executed, c => c.Text == MsSqlBackupCommand.VerifyOnlyText);
        Assert.Equal(backup.Parameters["@stagingPath"], verify.Parameters["@stagingPath"]);
    }

    [Fact, Trait("BL", "BL-40"), Trait("D", "D5.7")]
    public async Task Failed_verifyonly_is_integrity_error_and_cleans_staging()
    {
        var sql = Server();
        sql.CanVerifyOnly = true;
        sql.VerifyErrors = [3241, 3013];
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlBackupService(sql).PrepareAsync(Request(), _ct));
        Assert.Equal(KbCode.SqlVerifyFailed, e.Code);
        Assert.Equal(ErrorClass.Integrity, e.ErrorClass);
        Assert.True(StagingEmpty());
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.3")]
    public async Task Missing_backup_permission_stops_before_backup_and_staging()
    {
        var sql = Server();
        sql.CanBackup = false;
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlBackupService(sql).PrepareAsync(Request(), _ct));
        Assert.Equal(KbCode.SqlBackupPermissionMissing, e.Code);
        Assert.Empty(sql.Executed);
        Assert.True(StagingEmpty());
    }

    [Theory, Trait("BL", "BL-40")]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Database_missing_or_not_online_is_permanent_error(bool exists, bool renamed)
    {
        var sql = Server();
        sql.DatabaseListed = exists;
        var req = renamed ? Request() with { Database = _db.ToUpperInvariant() } : Request();
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlBackupService(sql).PrepareAsync(req, _ct));
        Assert.Equal(KbCode.SqlDatabaseUnavailable, e.Code);
        Assert.Equal(ErrorClass.Permanent, e.ErrorClass);
        Assert.DoesNotContain(_db, e.Message, StringComparison.OrdinalIgnoreCase); // NFR-11
    }

    [Theory, Trait("BL", "BL-40"), Trait("D", "D5.8")]
    [InlineData(11, false, false)]
    [InlineData(13, true, true)]
    [InlineData(16, true, false)]
    public async Task Version_policy_blocks_unsupported_and_warns_conditional(int major, bool proceeds, bool warns)
    {
        var sql = Server();
        sql.Major = major;
        var svc = new MsSqlBackupService(sql);
        if (!proceeds)
        {
            var e = await Assert.ThrowsAsync<KobianException>(() => svc.PrepareAsync(Request(), _ct));
            Assert.Equal(KbCode.SqlVersionUnsupported, e.Code);
            Assert.Empty(sql.Executed);
            return;
        }

        await using var staged = await svc.PrepareAsync(Request(), _ct);
        Assert.Equal(warns, staged.Warnings.Any(w => w.Code == KbCode.SqlVersionUnsupported));
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.2")]
    public async Task Conflicting_operation_beyond_wait_skips_without_overlap()
    {
        var sql = Server();
        sql.ConflictPolls = int.MaxValue;
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlBackupService(sql).PrepareAsync(
            Request() with { ConflictWait = TimeSpan.FromMilliseconds(30) }, _ct));
        Assert.Equal(KbCode.SqlConcurrentOperation, e.Code);
        Assert.True(sql.ConflictQueries >= 2); // czekał i sprawdzał ponownie
        Assert.Empty(sql.Executed); // brak nakładania się operacji
        Assert.True(StagingEmpty());
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.2")]
    public async Task Conflicting_operation_that_ends_within_wait_lets_backup_proceed()
    {
        var sql = Server();
        sql.ConflictPolls = 2;
        await using var staged = await new MsSqlBackupService(sql).PrepareAsync(Request() with { ConflictWait = TimeSpan.FromMinutes(1) }, _ct);
        Assert.Equal(3, sql.ConflictQueries);
        Assert.Single(sql.Executed, c => c.Text == MsSqlBackupCommand.TemplateText);
    }

    [Fact, Trait("BL", "BL-40"), Trait("D", "D5.10")]
    public async Task Insufficient_staging_space_is_refused_before_backup()
    {
        var sql = Server();
        sql.DataBytes = 1_000_000;
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlBackupService(sql, new FixedVolume(1_099_999)).PrepareAsync(Request(), _ct));
        Assert.Equal(KbCode.SqlStagingSpace, e.Code);
        Assert.Empty(sql.Executed);

        await using var ok = await new MsSqlBackupService(sql, new FixedVolume(1_100_000)).PrepareAsync(Request("run0000a2"), _ct);
        await using var unknown = await new MsSqlBackupService(sql, new FixedVolume(null)).PrepareAsync(Request("run0000a3"), _ct);
    }

    [Theory, Trait("BL", "BL-40"), Trait("AC", "AC-02.1")]
    [InlineData(false, true, "D")]
    [InlineData(true, false, "D")]
    [InlineData(true, true, "I")]
    public async Task Backup_set_that_is_not_full_copy_only_with_checksum_is_rejected(bool copyOnly, bool checksums, string type)
    {
        var sql = Server();
        sql.SetCopyOnly = copyOnly;
        sql.SetChecksums = checksums;
        sql.SetType = type;
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlBackupService(sql).PrepareAsync(Request(), _ct));
        Assert.Equal(KbCode.SqlBackupSetMismatch, e.Code);
        Assert.Equal(ErrorClass.Integrity, e.ErrorClass);
        Assert.True(StagingEmpty());
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.1")]
    public async Task Backup_set_is_matched_by_device_not_by_latest_in_msdb()
    {
        var sql = Server();
        sql.RegisterSet = false; // BACKUP przeszedł, ale w msdb nie ma zestawu dla naszego pliku (np. inny „najnowszy”)
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlBackupService(sql).PrepareAsync(Request(), _ct));
        Assert.Equal(KbCode.SqlBackupSetMismatch, e.Code);
        Assert.True(StagingEmpty());
    }

    [Theory, Trait("BL", "BL-40"), Trait("R", "R2.5.7")]
    [InlineData(3043, "KB-3201", ErrorClass.Integrity)]
    [InlineData(262, "KB-3003", ErrorClass.Permanent)]
    [InlineData(-2, "KB-3001", ErrorClass.Transient)]
    public async Task Backup_errors_map_to_kb_codes_and_clean_staging(int number, string code, ErrorClass cls)
    {
        var sql = Server();
        sql.BackupErrors = [number, 3013];
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlBackupService(sql).PrepareAsync(Request(), _ct));
        Assert.Equal(code, e.Code);
        Assert.Equal(cls, e.ErrorClass);
        Assert.DoesNotContain(_db, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SqlRoot, e.Message, StringComparison.Ordinal);
        Assert.True(StagingEmpty());
    }

    [Fact, Trait("BL", "BL-40"), Trait("R", "R2.5.7")]
    public async Task Tde_and_foreign_full_backup_during_run_are_warnings()
    {
        var sql = Server();
        sql.Encrypted = true;
        sql.ChangeDiffBaseOnBackup = true;
        await using var staged = await new MsSqlBackupService(sql).PrepareAsync(Request(), _ct);
        Assert.Contains(staged.Warnings, w => w.Code == KbCode.SqlTdeCertificateMissing);
        Assert.Contains(staged.Warnings, w => w.Code == KbCode.SqlForeignFullBackup);
    }

    [Fact, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.1")]
    public async Task Invalid_inputs_are_rejected_before_contacting_server()
    {
        var sql = Server();
        var svc = new MsSqlBackupService(sql);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.PrepareAsync(Request("../x"), _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.PrepareAsync(Request() with { SqlStagingRoot = "var/staging" }, _ct)); // względna; „/var/…” = SQL Server na Linuksie (poprawna)
        await Assert.ThrowsAsync<ArgumentException>(() => svc.PrepareAsync(Request() with { Database = "a\u0001b" }, _ct));
        Assert.Equal(0, sql.Queries);

        Directory.CreateDirectory(Path.Combine(_root, "run0000a1")); // katalog uruchomienia już istnieje — nie nadpisujemy
        var e = await Assert.ThrowsAsync<KobianException>(() => svc.PrepareAsync(Request(), _ct));
        Assert.Equal(KbCode.Internal, e.Code);
        Assert.True(Directory.Exists(Path.Combine(_root, "run0000a1")));
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.1"), Trait("Category", "E2E")]
    public async Task Staged_backup_goes_through_pipeline_and_restores_bit_for_bit()
    {
        var sql = Server();
        using var keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
        var store = new MemoryStorage();
        var s = store.Open();
        byte[] content;
        BackupResult r;
        await using (var staged = await new MsSqlBackupService(sql).PrepareAsync(Request(), _ct))
        {
            content = await File.ReadAllBytesAsync(staged.StagedFile, _ct);
            var src = new StagedFileSource(staged.StagedFile, DbSnapshotMeta.EntryName, "mssql://srv/" + _db);
            r = await new BackupPipeline(keys, new VerifiedUploader(s)).RunAsync(
                new BackupRequest(src, "job-db", RandomNumberGenerator.GetBytes(16), SnapshotMode.Database, DbMeta: staged.DbMeta), _ct);
        }

        Assert.Equal(BackupStatus.Succeeded, r.Status);
        Assert.True(StagingEmpty());
        var snap = await MetadataStore.LoadSnapshotAsync(s, keys, r.SnapId!, _ct);
        Assert.Equal("checksum", snap.DbMeta!["verify"]);
        using var reader = new RepoReader(s, keys);
        await reader.LoadIndexesAsync(snap, _ct);
        var entry = Assert.Single(await reader.ReadTreeAsync(snap.RootTreeId, _ct));
        Assert.Equal(SHA256.HashData(content), entry.FileSha256);
    }

    private sealed class FixedVolume(long? free) : IStagingVolume
    {
        public long? FreeBytes(string stagingRoot) => free;
    }
}
