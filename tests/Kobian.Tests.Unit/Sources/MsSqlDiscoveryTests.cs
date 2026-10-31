using Kobian.Domain;
using Kobian.Providers.Sources.MsSql;
using Xunit;

namespace Kobian.Tests.Unit.Sources;

/// <summary>BL-40 cz. 3: discovery MSSQL (R2.5.2 D-01/D-02/D-03, D5.7/D5.8) — tylko odczyt, limity, odporność na brak dostępu do pojedynczej bazy.</summary>
public sealed class MsSqlDiscoveryTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.3"), Trait("D", "D5.7")]
    public async Task Lists_online_databases_sorted_with_permissions_and_warnings()
    {
        var sql = new ScriptedSql();
        sql.Databases.Add(Db("Zeta", recovery: "SIMPLE"));
        sql.Databases.Add(Db("Alfa", recovery: "FULL", logWait: "LOG_BACKUP", encrypted: true, bytes: 8L << 20));
        sql.Databases.Add(Db("Beta", recovery: "FULL", logWait: "NOTHING"));
        sql.Perms["Alfa"] = (true, true);
        sql.Perms["Beta"] = (false, false);
        sql.Perms["Zeta"] = (true, false);

        var r = await new MsSqlDiscoveryService(sql).DiscoverAsync(_ct);

        Assert.Equal(["Alfa", "Beta", "Zeta"], r.Databases.Select(d => d.Name));
        Assert.Empty(r.Warnings);
        Assert.False(r.Truncated);
        Assert.Equal(16, r.Server.Major);
        Assert.True(r.Server.Express);
        Assert.Equal(SqlSupport.Supported, r.Server.Support);
        var alfa = r.Databases[0];
        Assert.True(alfa.CanBackup && alfa.CanVerifyOnly && alfa.Encrypted);
        Assert.Equal(8L << 20, alfa.DataBytes);
        Assert.Equal([KbCode.SqlTdeCertificateMissing, KbCode.SqlLogGrowing], alfa.Warnings);
        Assert.Equal([KbCode.SqlBackupPermissionMissing], r.Databases[1].Warnings);
        Assert.False(r.Databases[1].CanBackup);
        Assert.Empty(r.Databases[2].Warnings);
        // Tylko odczyt: żadnego ExecuteAsync; D-03 jako stały szablon z parametrem @db (nazwa nigdy w tekście — SEC-5.1).
        Assert.Equal(0, sql.Executes);
        Assert.All(sql.Seen, c => Assert.Contains(c.Text, new[] { MsSqlQueries.D01Server, MsSqlQueries.D02Databases, MsSqlQueries.D03Permissions }));
        Assert.Equal(["Alfa", "Beta", "Zeta"], sql.Seen.Where(c => c.Text == MsSqlQueries.D03Permissions).Select(c => (string)c.Parameters["@db"]));
    }

    [Theory, Trait("BL", "BL-40"), Trait("D", "D5.8")]
    [InlineData(13, true)]
    [InlineData(12, true)]
    [InlineData(17, false)]
    public async Task Conditional_versions_warn(int major, bool warn)
    {
        var r = await new MsSqlDiscoveryService(new ScriptedSql { Major = major }).DiscoverAsync(_ct);
        Assert.Equal(warn, r.Warnings.Contains(KbCode.SqlVersionUnsupported));
        Assert.Equal(warn ? SqlSupport.Conditional : SqlSupport.Supported, r.Server.Support);
    }

    [Fact, Trait("BL", "BL-40"), Trait("D", "D5.8")]
    public async Task Unsupported_version_fails_before_listing_databases()
    {
        var sql = new ScriptedSql { Major = 11 };
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlDiscoveryService(sql).DiscoverAsync(_ct));
        Assert.Equal(KbCode.SqlVersionUnsupported, e.Code);
        Assert.Single(sql.Seen);
    }

    [Fact, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.1")]
    public async Task Names_outside_sysname_rule_are_skipped()
    {
        var sql = new ScriptedSql();
        sql.Databases.Add(Db("ok"));
        sql.Databases.Add(Db("zła\u0001nazwa"));
        sql.Databases.Add(Db(new string('x', 129)));
        sql.Databases.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = null });
        var r = await new MsSqlDiscoveryService(sql).DiscoverAsync(_ct);
        Assert.Equal(["ok"], r.Databases.Select(d => d.Name));
    }

    [Theory, Trait("BL", "BL-40")]
    [InlineData(916)]
    [InlineData(4060)]
    [InlineData(229)]
    public async Task No_access_to_one_database_marks_it_without_permission(int error)
    {
        var sql = new ScriptedSql();
        sql.Databases.Add(Db("a"));
        sql.Databases.Add(Db("b"));
        sql.PermErrors["a"] = error;
        var r = await new MsSqlDiscoveryService(sql).DiscoverAsync(_ct);
        Assert.False(r.Databases[0].CanBackup);
        Assert.Contains(KbCode.SqlBackupPermissionMissing, r.Databases[0].Warnings);
        Assert.True(r.Databases[1].CanBackup);
    }

    [Theory, Trait("BL", "BL-40")]
    [InlineData(-2, "KB-3001")]
    [InlineData(10054, "KB-3001")]
    [InlineData(-2146893019, "KB-3004")]
    public async Task Lost_connection_or_tls_during_permissions_fails_whole_discovery(int error, string code)
    {
        var sql = new ScriptedSql();
        sql.Databases.Add(Db("a"));
        sql.PermErrors["a"] = error;
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlDiscoveryService(sql).DiscoverAsync(_ct));
        Assert.Equal(code, e.Code);
    }

    [Theory, Trait("BL", "BL-40")]
    [InlineData(18456, "KB-3002")]
    [InlineData(53, "KB-3001")]
    public async Task Server_errors_on_connect_map_to_kb_codes(int error, string code)
    {
        var sql = new ScriptedSql { ServerError = error };
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlDiscoveryService(sql).DiscoverAsync(_ct));
        Assert.Equal(code, e.Code);
        Assert.DoesNotContain("Firma", e.Message, StringComparison.Ordinal);
    }

    [Fact, Trait("BL", "BL-40"), Trait("SEC", "SEC-3.3")]
    public async Task Database_count_is_limited_and_reported_as_truncated()
    {
        var sql = new ScriptedSql();
        for (var i = 0; i < MsSqlDiscoveryService.MaxDatabases + 7; i++) sql.Databases.Add(Db($"db{i:D4}"));
        var r = await new MsSqlDiscoveryService(sql).DiscoverAsync(_ct);
        Assert.True(r.Truncated);
        Assert.Equal(MsSqlDiscoveryService.MaxDatabases, r.Databases.Count);
        Assert.Equal(MsSqlDiscoveryService.MaxDatabases, sql.Seen.Count(c => c.Text == MsSqlQueries.D03Permissions));
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Server_text_is_sanitized_and_bounded()
    {
        var sql = new ScriptedSql { Edition = "Developer Edition\r\n" + new string('e', 300) };
        var r = await new MsSqlDiscoveryService(sql).DiscoverAsync(_ct);
        Assert.Equal(128, r.Server.Edition.Length);
        Assert.DoesNotContain('\n', r.Server.Edition);
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Cancellation_is_observed_between_databases()
    {
        using var cts = new CancellationTokenSource();
        var sql = new ScriptedSql { OnPermissions = cts.Cancel };
        sql.Databases.Add(Db("a"));
        sql.Databases.Add(Db("b"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MsSqlDiscoveryService(sql).DiscoverAsync(cts.Token));
        Assert.Equal(1, sql.Seen.Count(c => c.Text == MsSqlQueries.D03Permissions));
    }

    private static Dictionary<string, object?> Db(string name, string recovery = "SIMPLE", string logWait = "NOTHING", bool encrypted = false, long bytes = 4096) =>
        new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["recovery_model_desc"] = recovery,
            ["log_reuse_wait_desc"] = logWait,
            ["is_encrypted"] = encrypted,
            ["is_read_only"] = false,
            ["compatibility_level"] = (byte)150,
            ["data_bytes"] = bytes,
        };

    /// <summary>SQL Server zastępczy dla discovery (S5c = bez kontenerów); prawdziwy serwer — W-26.</summary>
    internal sealed class ScriptedSql : IMsSqlSession
    {
        public int Major { get; set; } = 16;

        public string Edition { get; set; } = "Express Edition (64-bit)";

        public int? ServerError { get; set; }

        public Action? OnPermissions { get; set; }

        public List<Dictionary<string, object?>> Databases { get; } = [];

        public Dictionary<string, (bool Backup, bool Verify)> Perms { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> PermErrors { get; } = new(StringComparer.Ordinal);

        public List<SqlCommandSpec> Seen { get; } = [];

        public int Executes { get; private set; }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(SqlCommandSpec command, CancellationToken cancellationToken)
        {
            Seen.Add(command);
            if (ServerError is { } se) throw new MsSqlServerException([se]);
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
            switch (command.Text)
            {
                case MsSqlQueries.D01Server:
                    rows = [new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["major"] = Major, ["version"] = Major + ".0.4135.5", ["edition"] = Edition, ["engine_edition"] = 4, ["is_clustered"] = 0, ["is_hadr"] = 0,
                    }];
                    break;
                case MsSqlQueries.D02Databases:
                    rows = Databases;
                    break;
                case MsSqlQueries.D03Permissions:
                    OnPermissions?.Invoke();
                    var db = (string)command.Parameters["@db"];
                    if (PermErrors.TryGetValue(db, out var err)) throw new MsSqlServerException([err]);
                    var (b, v) = Perms.TryGetValue(db, out var p) ? p : (true, false);
                    rows = [new Dictionary<string, object?>(StringComparer.Ordinal) { ["can_backup_db"] = b ? 1 : 0, ["can_verifyonly"] = v ? 1 : 0 }];
                    break;
                default:
                    throw new InvalidOperationException("unexpected query");
            }
            return Task.FromResult(rows);
        }

        public Task ExecuteAsync(SqlCommandSpec command, CancellationToken cancellationToken)
        {
            Executes++;
            throw new InvalidOperationException("discovery must not execute commands");
        }
    }
}
