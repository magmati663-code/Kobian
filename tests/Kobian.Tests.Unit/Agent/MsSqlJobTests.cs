using System.Security.Cryptography;
using System.Text;
using Kobian.Agent.Jobs;
using Kobian.Agent.Secrets;
using Kobian.Application.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql;
using Kobian.Providers.Sources.MsSql.SqlClient;
using Kobian.Providers.Storage.Local;
using Kobian.Providers.Storage.Sftp;
using Kobian.Tests.Unit.TestDoubles;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// BL-40 cz. 2: zadanie „mssql” w Agencie — definicja v1 z sekcją „database”, CatalogJobService: MsSqlBackupService → StagedFileSource →
/// BackupPipeline (tryb Database) → V2 → sprzątanie stagingu. SQL Server zastępczy (S5c = nie); prawdziwy serwer — W-24.
/// </summary>
public sealed class MsSqlJobTests : IAsyncLifetime
{
    private const string SqlRoot = @"C:\ProgramData\Kobian\staging";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-mssqljob-" + Guid.NewGuid().ToString("N"));
    private readonly string _db = "Firma_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    private CatalogWriter _writer = null!;
    private string Catalog => Path.Combine(_dir, "catalog.db");
    private string P(string n) => Path.Combine(_dir, n);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(P("repo"));
        Directory.CreateDirectory(P("staging"));
        var pw = "haslo-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var prov = Kobian.Crypto.RepoProvisioner.Create(pw, DateTimeOffset.UtcNow);
        File.WriteAllText(P("wrapped.key"), prov.KeyFileJson);
        File.WriteAllText(P("kobian.repo"), prov.HeaderJson);
        File.WriteAllText(P("pw.txt"), pw + "\n");
        CatalogBootstrap.Bootstrap(Catalog, TimeProvider.System);
        _writer = new CatalogWriter(Catalog);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private string Database(string extra = "") =>
        $$"""{"server":"srv01\\SQLEXPRESS","name":{{J(_db)}},"staging":{{J(P("staging"))}},"sqlStaging":{{J(SqlRoot)}},"conflictWaitMinutes":0{{extra}}}""";

    private string Definition(string? database = null) =>
        $$"""{"version":1,"database":{{database ?? Database()}},"destination":{"scheme":"local","root":{{J(P("repo"))}}},"key":{"file":{{J(P("wrapped.key"))}},"passwordFile":{{J(P("pw.txt"))}},"repoHeader":{{J(P("kobian.repo"))}}},"packTargetSize":1048576}""";

    private void AddJob(string name, string def)
    {
        using var c = CatalogBootstrap.Open(Catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,time_zone,misfire,created_at,updated_at) VALUES ($i,$n,'database',$d,x'00',1,'UTC','skip','2026-10-14T00:00:00Z','2026-10-14T00:00:00Z');";
        cmd.Parameters.AddWithValue("$i", "j-" + name);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$d", Encoding.UTF8.GetBytes(def));
        cmd.ExecuteNonQuery();
    }

    private CatalogJobService Service(FakeSqlServer? sql) =>
        new(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask)
        {
            MsSqlSessions = sql is null ? null : (_, _) => ValueTask.FromResult<IMsSqlSession>(sql),
        };

    private (string State, string? Code, long? Mode) LastRun()
    {
        using var c = CatalogBootstrap.Open(Catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT r.state, r.kb_code, s.mode FROM run_runs r LEFT JOIN snap_snapshots s ON s.snap_id = r.snapshot_id ORDER BY r.rowid DESC LIMIT 1;";
        using var rd = cmd.ExecuteReader();
        Assert.True(rd.Read());
        return (rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetInt64(2));
    }

    private bool StagingEmpty() => !Directory.EnumerateFileSystemEntries(P("staging")).Any();

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.1"), Trait("Category", "E2E")]
    public async Task Mssql_job_backs_up_database_as_database_snapshot_and_cleans_staging()
    {
        AddJob("ERP", Definition());
        var sql = new FakeSqlServer(_db, P("staging"), SqlRoot);
        var outcome = await Service(sql).RunAsync("ERP", _ct);

        Assert.Equal(JobResult.Succeeded, outcome.Result);
        Assert.NotNull(outcome.SnapshotId);
        var (state, code, mode) = LastRun();
        Assert.Equal("Succeeded", state);
        Assert.Null(code);
        Assert.Equal(3L, mode); // SnapshotMode.Database
        Assert.True(StagingEmpty(), "AC-02.1: brak *.bak w stagingu po kopii");
        Assert.Contains(sql.Executed, e => e.Text == MsSqlBackupCommand.TemplateText);

        // Drugie uruchomienie: przyrost dedup względem poprzedniego snapshotu bazy, nadal tryb Database.
        var second = await Service(new FakeSqlServer(_db, P("staging"), SqlRoot)).RunAsync("ERP", _ct);
        Assert.Equal(JobResult.Succeeded, second.Result);
        Assert.Equal(3L, LastRun().Mode);
        Assert.True(StagingEmpty());
    }

    [Fact, Trait("BL", "BL-40"), Trait("D", "D5.8")]
    public async Task Preparation_warning_makes_run_warning_with_code()
    {
        AddJob("ERP", Definition());
        var sql = new FakeSqlServer(_db, P("staging"), SqlRoot) { Major = 13 }; // SQL 2016: warunkowo wspierany → KB-3401 ostrzeżenie
        var outcome = await Service(sql).RunAsync("ERP", _ct);
        Assert.Equal(JobResult.Warning, outcome.Result);
        Assert.Equal(KbCode.SqlVersionUnsupported, outcome.Code);
        Assert.Equal(("Warning", (string?)KbCode.SqlVersionUnsupported), (LastRun().State, LastRun().Code));
        Assert.NotNull(outcome.SnapshotId);
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.1")]
    public async Task Sql_error_fails_run_with_kb_code_and_leaves_no_staged_file()
    {
        AddJob("ERP", Definition());
        var sql = new FakeSqlServer(_db, P("staging"), SqlRoot) { BackupErrors = [3013, 3043] };
        var outcome = await Service(sql).RunAsync("ERP", _ct);
        Assert.Equal(JobResult.Failed, outcome.Result);
        Assert.Equal(KbCode.SqlPageChecksumError, outcome.Code);
        Assert.Equal("Failed", LastRun().State);
        Assert.Empty(Directory.EnumerateFiles(P("staging"), "*.bak", SearchOption.AllDirectories));
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-02.3")]
    public async Task Missing_backup_permission_fails_with_kb3003()
    {
        AddJob("ERP", Definition());
        var outcome = await Service(new FakeSqlServer(_db, P("staging"), SqlRoot) { CanBackup = false }).RunAsync("ERP", _ct);
        Assert.Equal((JobResult.Failed, (string?)KbCode.SqlBackupPermissionMissing), (outcome.Result, outcome.Code));
    }

    [Fact]
    public async Task Agent_without_mssql_factory_refuses_mssql_jobs()
    {
        AddJob("ERP", Definition());
        var outcome = await Service(null).RunAsync("ERP", _ct);
        Assert.Equal((JobResult.Failed, (string?)KbCode.JobDefinitionInvalid), (outcome.Result, outcome.Code));
    }

    [Fact]
    public void Definition_parses_database_section()
    {
        var def = JobDefinition.Parse("database", Encoding.UTF8.GetBytes(Definition(Database(""","lowImpact":true,"strict":true"""))));
        Assert.Equal(JobDefinition.MsSqlSourceMarker, def.Source);
        var db = Assert.IsType<MsSqlSourceDefinition>(def.MsSql);
        Assert.Equal(_db, db.Database);
        Assert.Equal(@"srv01\SQLEXPRESS", db.Connection.Server);
        Assert.Equal(MsSqlAuthentication.Integrated, db.Connection.Authentication);
        Assert.Null(db.Connection.UserName);
        Assert.True(db.Connection.Strict);
        Assert.Equal(BackupImpactProfile.LowImpact, db.Profile);
        Assert.Equal(TimeSpan.Zero, db.ConflictWait);
        Assert.Equal(SqlRoot, db.SqlStagingRoot);
        Assert.DoesNotContain(_db, db.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("srv01", db.ToString(), StringComparison.Ordinal);

        var sqlAuth = JobDefinition.Parse("database", Encoding.UTF8.GetBytes(Definition(Database(""","auth":"sql","secretRef":"file:erp-sql" """))));
        Assert.Equal(MsSqlAuthentication.SqlLogin, sqlAuth.MsSql!.Connection.Authentication);
        Assert.Equal("file:erp-sql", sqlAuth.MsSql.SecretRef);
        Assert.Null(JobDefinition.Parse("files", Encoding.UTF8.GetBytes(
            $$$"""{"version":1,"sources":[{{{J(P("repo"))}}}],"destination":{"scheme":"local","root":{{{J(P("repo"))}}}},"key":{"file":{{{J(P("wrapped.key"))}}}}}""")).MsSql);
    }

    [Theory, Trait("SEC", "SEC-5.6")]
    [InlineData(""","auth":"sql" """)]                                   // login SQL bez secretRef
    [InlineData(""","secretRef":"file:x" """)]                            // integrated z secretRef
    [InlineData(""","auth":"ntlm" """)]
    [InlineData(""","password":"x" """)]                                  // nieznane pole — sekrety nie wchodzą do definicji
    [InlineData(""","allowLocalhostUntrusted":true """)]                  // zgoda tylko dla localhost
    [InlineData(""","certificate":"relative.cer" """)]
    [InlineData(""","conflictWaitMinutes":100000 """)]
    [InlineData(""","lowImpact":"yes" """)]
    public void Invalid_database_section_is_rejected(string extra)
    {
        // conflictWaitMinutes jest już w sekcji bazowej — dla tego przypadku sekcja bez wartości bazowej (bez duplikatu klucza).
        var section = extra.Contains("conflictWaitMinutes", StringComparison.Ordinal)
            ? $$"""{"server":"srv","name":"Firma","staging":{{J(P("staging"))}},"sqlStaging":{{J(SqlRoot)}}{{extra}}}"""
            : Database(extra);
        var e = Assert.Throws<KobianException>(() => JobDefinition.Parse("database", Encoding.UTF8.GetBytes(Definition(section))));
        Assert.Equal(KbCode.JobDefinitionInvalid, e.Code);
    }

    [Theory, Trait("SEC", "SEC-5.1")]
    [InlineData("""{"server":"np:srv","name":"Firma","staging":"C:\\s"}""")]
    [InlineData("""{"server":"srv;Trust Server Certificate=true","name":"Firma","staging":"C:\\s"}""")]
    [InlineData("""{"server":"srv","name":"","staging":"C:\\s"}""")]
    [InlineData("""{"server":"srv","name":"Firma","staging":"/tmp/s"}""")]          // staging nie-Windows bez sqlStaging
    [InlineData("""{"server":"srv","name":"Firma","staging":"C:\\s","sqlStaging":"/tmp"}""")]
    [InlineData("""{"server":"srv","staging":"C:\\s"}""")]
    public void Injecting_or_incomplete_database_section_is_rejected(string section)
    {
        var e = Assert.Throws<KobianException>(() => JobDefinition.Parse("database", Encoding.UTF8.GetBytes(Definition(section))));
        Assert.Equal(KbCode.JobDefinitionInvalid, e.Code);
    }

    [Fact]
    public void Database_job_must_not_have_sources_and_files_job_needs_them()
    {
        var both = Definition().Replace("\"database\":", $$"""
            "sources":[{{J(P("repo"))}}],"database":
            """, StringComparison.Ordinal);
        Assert.Throws<KobianException>(() => JobDefinition.Parse("database", Encoding.UTF8.GetBytes(both)));
        Assert.Throws<KobianException>(() => JobDefinition.Parse("files", Encoding.UTF8.GetBytes(Definition())));
        Assert.Throws<KobianException>(() => JobDefinition.Parse("mssql", Encoding.UTF8.GetBytes(Definition())));
        Assert.Throws<KobianException>(() => JobDefinition.Parse("database", Encoding.UTF8.GetBytes(Definition(Database(""","engine":"mysql" """)))));
        Assert.NotNull(JobDefinition.Parse("database", Encoding.UTF8.GetBytes(Definition(Database(""","engine":"mssql" """)))).MsSql);
    }

    [Fact]
    public async Task Integrated_factory_builds_sqlclient_session()
    {
        var def = JobDefinition.Parse("database", Encoding.UTF8.GetBytes(Definition())).MsSql!;
        var session = await AgentMsSql.CreateAsync(def, new FakeSecrets(null), _ct);
        await using var s = Assert.IsType<SqlClientMsSqlSession>(session);
    }

    [Theory, Trait("SEC", "NFR-11")]
    [InlineData(false)] // brak wpisu
    [InlineData(true)]  // wpis z kluczem prywatnym zamiast hasła
    public async Task Sql_login_factory_refuses_missing_or_keyonly_credentials(bool hasEntry)
    {
        var def = JobDefinition.Parse("database", Encoding.UTF8.GetBytes(Definition(Database(""","auth":"sql","secretRef":"file:erp-sql" """)))).MsSql!;
        var secrets = new FakeSecrets(hasEntry ? new SftpCredential("kobian_backup", null, [1, 2, 3], null) : null);
        var e = await Assert.ThrowsAsync<KobianException>(async () => await AgentMsSql.CreateAsync(def, secrets, _ct));
        Assert.Equal(KbCode.SqlLoginFailed, e.Code);
        Assert.DoesNotContain("erp-sql", e.Message, StringComparison.Ordinal);
    }

    [Fact, Trait("SEC", "NFR-11")]
    public async Task Sql_login_factory_uses_entry_user_and_rereads_password_on_connect()
    {
        var def = JobDefinition.Parse("database", Encoding.UTF8.GetBytes(Definition(Database(""","auth":"sql","secretRef":"file:erp-sql" """)))).MsSql!;
        var secrets = new FakeSecrets(new SftpCredential("kobian_backup", "t3st-" + Guid.NewGuid().ToString("N"), null, null));
        var session = await AgentMsSql.CreateAsync(def, secrets, _ct);
        await using var s = Assert.IsType<SqlClientMsSqlSession>(session);
        Assert.Equal(1, secrets.Calls);
    }

    private sealed class FakeSecrets(SftpCredential? cred) : ISftpCredentialProvider
    {
        public int Calls { get; private set; }

        public ValueTask<SftpCredential> ResolveAsync(string secretRef, CredentialRole role, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(CredentialRole.Backup, role);
            return cred is null ? throw new SftpProtocolException(SftpStatus.AuthRejected, "missing") : ValueTask.FromResult(cred);
        }
    }

    [Fact, Trait("SEC", "RD-39"), Trait("BL", "BL-40")]
    public async Task Run_refuses_sql_server_outside_allow_list_before_connecting()
    {
        AddJob("ERP", Definition()); // serwer srv01\SQLEXPRESS
        var opened = 0;
        var svc = new CatalogJobService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask)
        {
            MsSqlSessions = (_, _) =>
            {
                opened++;
                return ValueTask.FromResult<IMsSqlSession>(new FakeSqlServer(_db, P("staging"), SqlRoot));
            },
            AllowedSqlServers = Kobian.Providers.Sources.MsSql.SqlServerAllowList.LocalOnly,
        };
        var outcome = await svc.RunAsync("ERP", _ct);
        Assert.Equal((JobResult.Failed, (string?)KbCode.SqlServerNotAllowed), (outcome.Result, outcome.Code));
        Assert.Equal(0, opened);
        Assert.Equal("Failed", LastRun().State);

        var allowed = new CatalogJobService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, new byte[16], TimeProvider.System, (_, _) => Task.CompletedTask)
        {
            MsSqlSessions = (_, _) => ValueTask.FromResult<IMsSqlSession>(new FakeSqlServer(_db, P("staging"), SqlRoot)),
            AllowedSqlServers = Kobian.Providers.Sources.MsSql.SqlServerAllowList.Parse("SRV01"),
        };
        Assert.Equal(JobResult.Succeeded, (await allowed.RunAsync("ERP", _ct)).Result);
    }

    /// <summary>Serwer zastępczy dla odtwarzania: odpowiada na zapytania MsSqlRestoreService, w chwili VERIFYONLY sprawdza obecność .bak w stagingu Agenta.</summary>
    private sealed class RestoreSql(string localRoot, string sqlRoot) : IMsSqlSession
    {
        public bool Exists { get; set; }
        public long BakBytesAtVerify { get; private set; } = -1;
        public List<SqlCommandSpec> Calls { get; } = [];
        public Func<string, int>? ControlCount { get; set; }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(SqlCommandSpec c, CancellationToken ct)
        {
            Calls.Add(c);
            static IReadOnlyDictionary<string, object?> Row(params (string, object?)[] v) => v.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<IReadOnlyDictionary<string, object?>> r = c.Text switch
            {
                Kobian.Providers.Sources.Erp.ControlQueryRunner.WrapperText => ControlCount is null ? [] : [Row(("n", ControlCount((string)c.Parameters["@q"])))],
                MsSqlRestoreService.ExistsText => [Row(("exists_flag", Exists ? 1 : 0))],
                MsSqlRestoreService.CanCreateText => [Row(("can_create", 1))],
                MsSqlRestoreService.DefaultPathsText => [Row(("data_path", @"D:\SQLData"), ("log_path", @"D:\SQLLogs"))],
                MsSqlRestoreService.FileListText => [Row(("LogicalName", "Firma"), ("Type", "D")), Row(("LogicalName", "Firma_log"), ("Type", "L"))],
                _ => throw new InvalidOperationException(c.Text),
            };
            return Task.FromResult(r);
        }

        public Task ExecuteAsync(SqlCommandSpec c, CancellationToken ct)
        {
            Calls.Add(c);
            if (c.Text == MsSqlBackupCommand.VerifyOnlyText)
            {
                var sqlPath = (string)c.Parameters["@stagingPath"];
                var local = Path.Combine(localRoot, sqlPath[(sqlRoot.Length + 1)..].Replace('\\', Path.DirectorySeparatorChar));
                BakBytesAtVerify = File.Exists(local) ? new FileInfo(local).Length : 0;
            }
            return Task.CompletedTask;
        }
    }

    [Fact, Trait("BL", "BL-68"), Trait("AC", "AC-32.1")]
    public async Task Database_snapshot_restores_to_new_database_through_staging_and_cleans_up()
    {
        AddJob("ERP", Definition());
        var backup = await Service(new FakeSqlServer(_db, P("staging"), SqlRoot)).RunAsync("ERP", _ct);
        Assert.Equal(JobResult.Succeeded, backup.Result);
        var sql = new RestoreSql(P("staging"), SqlRoot);
        var svc = new CatalogRestoreService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, TimeProvider.System)
        {
            MsSqlSessions = (_, _) => ValueTask.FromResult<IMsSqlSession>(sql),
        };
        var r = await svc.RestoreDatabaseAsync("latest", null, _ct);
        Assert.Equal(JobResult.Succeeded, r.Outcome.Result);
        Assert.StartsWith(_db + "_restore_", r.TargetDatabase, StringComparison.Ordinal);
        Assert.True(sql.BakBytesAtVerify > 0, ".bak w stagingu w chwili VERIFYONLY");
        Assert.Contains(sql.Calls, c => c.Text.StartsWith("RESTORE DATABASE", StringComparison.Ordinal) && (string)c.Parameters["@db"] == r.TargetDatabase);
        Assert.True(StagingEmpty(), "staging sprzątnięty po odtworzeniu");
        Assert.Equal("Succeeded", LastRun().State);

        // istniejąca baza → KB-3501, nic nie nadpisane, staging pusty
        var sql2 = new RestoreSql(P("staging"), SqlRoot) { Exists = true };
        var svc2 = new CatalogRestoreService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, TimeProvider.System)
        {
            MsSqlSessions = (_, _) => ValueTask.FromResult<IMsSqlSession>(sql2),
        };
        var dup = await svc2.RestoreDatabaseAsync("latest", _db, _ct);
        Assert.Equal((JobResult.Failed, (string?)KbCode.SqlRestoreTargetExists), (dup.Outcome.Result, dup.Outcome.Code));
        Assert.DoesNotContain(sql2.Calls, c => c.Text.StartsWith("RESTORE", StringComparison.Ordinal));
        Assert.True(StagingEmpty());

        // serwer spoza listy dozwolonych — przed jakimkolwiek połączeniem
        var svc3 = new CatalogRestoreService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, TimeProvider.System)
        {
            MsSqlSessions = (_, _) => throw new InvalidOperationException("nie powinno się połączyć"),
            AllowedSqlServers = Kobian.Providers.Sources.MsSql.SqlServerAllowList.LocalOnly,
        };
        Assert.Equal(KbCode.SqlServerNotAllowed, (await svc3.RestoreDatabaseAsync("latest", null, _ct)).Outcome.Code);
        Assert.Equal(KbCode.OperationNotAvailable, (await new CatalogRestoreService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory>(), TimeProvider.System).RestoreDatabaseAsync("latest", null, _ct)).Outcome.Code);
    }

    private sealed class TinyVolume : IStagingVolume
    {
        public long? FreeBytes(string stagingRoot) => 10;
    }

    [Fact, Trait("BL", "BL-68")]
    public async Task Database_restore_checks_staging_space_before_downloading()
    {
        AddJob("ERP", Definition());
        Assert.Equal(JobResult.Succeeded, (await Service(new FakeSqlServer(_db, P("staging"), SqlRoot)).RunAsync("ERP", _ct)).Result);
        var opened = 0;
        var svc = new CatalogRestoreService(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, TimeProvider.System)
        {
            MsSqlSessions = (_, _) =>
            {
                opened++;
                return ValueTask.FromResult<IMsSqlSession>(new RestoreSql(P("staging"), SqlRoot));
            },
            StagingVolume = new TinyVolume(),
        };
        var r = await svc.RestoreDatabaseAsync("latest", null, _ct);
        Assert.Equal((JobResult.Failed, (string?)KbCode.SqlStagingSpace), (r.Outcome.Result, r.Outcome.Code));
        Assert.Equal(0, opened);
        Assert.True(StagingEmpty());
    }

    private FakeSqlServer GtServer(bool selectAllowed = true)
    {
        var sql = new FakeSqlServer(_db, P("staging"), SqlRoot);
        foreach (var t in new[] { "dok__Dokument", "dok_Pozycja", "tw__Towar", "kh__Kontrahent", "adr__Ewid" }) sql.ErpTables.Add(("dbo", t));
        sql.ControlResponder = (q, _) => selectAllowed
            ? [FakeSqlServer.Row(("n", q.Contains("tw__Towar", StringComparison.Ordinal) ? 100 : q.Contains("kh__Kontrahent", StringComparison.Ordinal) ? 40 : 7))]
            : throw new MsSqlServerException([229]);
        return sql;
    }

    private static int GtCount(string q) => q.Contains("tw__Towar", StringComparison.Ordinal) ? 100 : q.Contains("kh__Kontrahent", StringComparison.Ordinal) ? 40 : 7;

    private CatalogRestoreService RestoreService(RestoreSql sql) =>
        new(Catalog, _writer, new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() }, TimeProvider.System)
        {
            MsSqlSessions = (_, _) => ValueTask.FromResult<IMsSqlSession>(sql),
        };

    [Fact, Trait("BL", "BL-40"), Trait("D", "D5.11"), Trait("AC", "AC-32.1")]
    public async Task Erp_controls_recorded_at_backup_pass_after_restore_and_flag_a_logically_empty_copy()
    {
        AddJob("ERP", Definition());
        var backup = await Service(GtServer()).RunAsync("ERP", _ct);
        Assert.Equal((JobResult.Succeeded, (string?)null), (backup.Result, backup.Code)); // wykryty profil insert-gt, zapytania wykonane, brak ostrzeżeń

        var good = new RestoreSql(P("staging"), SqlRoot) { ControlCount = GtCount };
        var ok = await RestoreService(good).RestoreDatabaseAsync("latest", null, _ct);
        Assert.Equal((JobResult.Succeeded, (string?)null), (ok.Outcome.Result, ok.Outcome.Code));
        Assert.Contains(good.Calls, c => c.Text == Kobian.Providers.Sources.Erp.ControlQueryRunner.WrapperText && (string)c.Parameters["@db"] == ok.TargetDatabase);

        var empty = new RestoreSql(P("staging"), SqlRoot) { ControlCount = _ => 0 }; // baza poprawna technicznie, ale pusta
        var bad = await RestoreService(empty).RestoreDatabaseAsync("latest", _db + "_pusta", _ct);
        Assert.Equal((JobResult.Warning, (string?)KbCode.SqlRestoreControlMismatch), (bad.Outcome.Result, bad.Outcome.Code));
        Assert.Equal(_db + "_pusta", bad.TargetDatabase); // baza istnieje — decyzja należy do administratora
        Assert.Equal("Warning", LastRun().State);
        Assert.True(StagingEmpty());
    }

    [Fact, Trait("BL", "BL-40"), Trait("D", "D5.11")]
    public async Task Backup_without_select_permission_is_a_warning_kb_3010_and_restore_runs_no_controls()
    {
        AddJob("ERP", Definition());
        var backup = await Service(GtServer(selectAllowed: false)).RunAsync("ERP", _ct);
        Assert.Equal((JobResult.Warning, (string?)KbCode.SqlControlQuerySkipped), (backup.Result, backup.Code));

        var sql = new RestoreSql(P("staging"), SqlRoot) { ControlCount = _ => 0 };
        var r = await RestoreService(sql).RestoreDatabaseAsync("latest", null, _ct);
        Assert.Equal(JobResult.Succeeded, r.Outcome.Result); // brak zapisanych kontroli = nic do porównania
        Assert.DoesNotContain(sql.Calls, c => c.Text == Kobian.Providers.Sources.Erp.ControlQueryRunner.WrapperText);
    }
}
