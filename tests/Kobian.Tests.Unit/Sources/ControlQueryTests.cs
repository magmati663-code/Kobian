using System.Security.Cryptography;
using Kobian.Domain;
using Kobian.Providers.Sources.Erp;
using Kobian.Providers.Sources.MsSql;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Sources;

/// <summary>D5.11 / SEC-15.3 (BL-40, it. 123): zapytania kontrolne ERP przy backupie, zapis w dbMeta i porównanie po odtworzeniu. Sesje zastępcze (S5c) — prawdziwy SQL Server: W-51.</summary>
public sealed class ControlQueryTests : IDisposable
{
    private const string SqlRoot = @"C:\ProgramData\Kobian\staging";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kobian-ctl-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly string _db = "Firma_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    public ControlQueryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Dictionary<string, object?> R(params (string, object?)[] v) => FakeSqlServer.Row(v);

    private static ErpProfile Gt() => ErpProfileCatalog.LoadBuiltIn().Single(p => p.Id == "insert-gt");

    private sealed class ScriptedControls(Func<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> answer) : IMsSqlSession
    {
        public List<SqlCommandSpec> Calls { get; } = [];

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(SqlCommandSpec command, CancellationToken cancellationToken)
        {
            Calls.Add(command);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return answer((string)command.Parameters["@q"]);
        }

        public Task ExecuteAsync(SqlCommandSpec command, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private static ControlQuery Q(string id, string sql = "SELECT COUNT(*) AS n FROM tw__Towar", string tol = "range") => new(id, sql, tol);

    // ---------------------------------------------------------------- runner

    [Fact, Trait("D", "D5.11"), Trait("SEC", "SEC-15.3")]
    public async Task Runner_returns_counts_dates_and_group_keys_and_passes_database_only_as_parameter()
    {
        var sql = new ScriptedControls(_ => [R(("dok_Typ", 1), ("n", 7L), ("last", new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc))), R(("dok_Typ", 2), ("n", 3L), ("last", null))]);
        var r = await new ControlQueryRunner(sql).RunAsync(_db, [Q("gt-dok", "SELECT dok_Typ, COUNT(*) AS n, MAX(dok_DataWyst) AS last FROM dok__Dokument GROUP BY dok_Typ")], _ct);
        Assert.Empty(r.SkippedQueryIds);
        Assert.Equal(
            ["gt-dok|1|7", "gt-dok|1#last|2026-10-01T08:30:00Z", "gt-dok|2|3"],
            r.Values.Select(v => $"{v.QueryId}|{v.Key}|{v.Value}"));
        var call = Assert.Single(sql.Calls);
        Assert.Equal(ControlQueryRunner.WrapperText, call.Text);
        Assert.Equal(_db, call.Parameters["@db"]);
        Assert.DoesNotContain(_db, call.Text, StringComparison.Ordinal); // SEC-5.1: nazwa bazy wyłącznie jako parametr, cytowana QUOTENAME po stronie serwera
        Assert.Contains("QUOTENAME(@db)", call.Text, StringComparison.Ordinal);
        Assert.Contains("LOCK_TIMEOUT 5000", call.Text, StringComparison.Ordinal);
        Assert.Contains("READ COMMITTED", call.Text, StringComparison.Ordinal);
    }

    [Fact, Trait("D", "D5.11"), Trait("SEC", "SEC-15.3")]
    public async Task Text_from_data_columns_is_never_accepted_and_the_query_is_skipped()
    {
        var sql = new ScriptedControls(_ => [R(("nazwa", "Kowalski Sp. z o.o."), ("n", 1))]);
        var r = await new ControlQueryRunner(sql).RunAsync(_db, [Q("q1")], _ct);
        Assert.Empty(r.Values);
        Assert.Equal(["q1"], r.SkippedQueryIds);
    }

    [Fact, Trait("D", "D5.11"), Trait("SEC", "SEC-15.3")]
    public async Task Missing_count_floating_point_and_oversized_results_are_skipped()
    {
        var cases = new Func<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>[]
        {
            _ => [R(("x", 1))],                                        // brak kolumny n
            _ => [R(("n", 1.5))],                                      // liczba niecałkowita
            _ => [R(("n", null))],                                     // NULL
            _ => [R(("typ", 1), ("n", 1)), R(("typ", 1), ("n", 2))],   // zduplikowany klucz grupy
            _ => [.. Enumerable.Range(0, ControlQueryRunner.MaxRowsPerQuery + 1).Select(i => R(("typ", i), ("n", 1)))],
        };
        foreach (var answer in cases)
        {
            var r = await new ControlQueryRunner(new ScriptedControls(answer)).RunAsync(_db, [Q("q1")], _ct);
            Assert.Empty(r.Values);
            Assert.Equal(["q1"], r.SkippedQueryIds);
        }
    }

    [Fact, Trait("D", "D5.11")]
    public async Task Server_error_and_timeout_skip_the_query_but_not_the_others()
    {
        var sql = new ScriptedControls(q => q.Contains("kh__", StringComparison.Ordinal) ? throw new MsSqlServerException([229]) : [R(("n", 5))]);
        var r = await new ControlQueryRunner(sql).RunAsync(_db, [Q("a"), Q("b", "SELECT COUNT(*) AS n FROM kh__Kontrahent"), Q("c")], _ct);
        Assert.Equal(["a", "c"], r.Values.Select(v => v.QueryId));
        Assert.Equal(["b"], r.SkippedQueryIds);

        var slow = new HangingSession();
        var t = await new ControlQueryRunner(slow) { QueryTimeout = TimeSpan.FromMilliseconds(50) }.RunAsync(_db, [Q("slow")], _ct);
        Assert.Equal(["slow"], t.SkippedQueryIds); // D5.11: przekroczenie limitu = pominięcie z ostrzeżeniem, nie błąd
    }

    private sealed class HangingSession : IMsSqlSession
    {
        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(SqlCommandSpec command, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return [];
        }

        public Task ExecuteAsync(SqlCommandSpec command, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact, Trait("D", "D5.11"), Trait("SEC", "SEC-15.3")]
    public async Task Unsafe_sql_never_reaches_the_server()
    {
        var sql = new ScriptedControls(_ => [R(("n", 1))]);
        var r = await new ControlQueryRunner(sql).RunAsync(_db, [Q("x1", "SELECT 1; DROP TABLE x"), Q("x2", "DELETE FROM x"), Q("x3", "SELECT 1 -- c"), Q("bad id!")], _ct);
        Assert.Empty(sql.Calls);
        Assert.Equal(4, r.SkippedQueryIds.Count);
    }

    [Fact, Trait("D", "D5.11")]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ControlQueryRunner(new HangingSession()).RunAsync(_db, [Q("q")], cts.Token));
    }

    [Fact, Trait("D", "D5.9")]
    public void Builtin_profiles_have_valid_control_queries_and_tolerances()
    {
        foreach (var p in ErpProfileCatalog.LoadBuiltIn())
            foreach (var q in p.ControlQueries)
            {
                Assert.True(ErpProfileCatalog.IsSafeControlSql(q.Sql), q.Id);
                Assert.True(ControlQueryRunner.IsValidQueryId(q.Id), q.Id);
            }
    }

    // ---------------------------------------------------------------- zapis w dbMeta

    [Fact, Trait("D", "D5.11")]
    public void Control_set_round_trips_and_rejects_malformed_metadata()
    {
        var records = new List<ControlRecord> { new("gt-tw", "_", "100", "102"), new("gt-dok", "1#last", "2026-10-01T08:30:00Z", null) };
        var json = ControlSet.Serialize(records);
        Assert.NotNull(json);
        Assert.True(ControlSet.TryParse(json, out var back));
        Assert.Equal(records, back);

        foreach (var bad in new[] { "", "{}", "[1]", "[[\"a\",\"b\",\"c\"]]", "[[\"a b\",\"k\",\"1\",null]]", "[[\"q\",\"k\",1,null]]", "[[[[[[1]]]]]]", "not json" })
            Assert.False(ControlSet.TryParse(bad, out _), bad);
        Assert.Null(ControlSet.Serialize([]));
        Assert.Null(ControlSet.Serialize(Enumerable.Range(0, ControlSet.MaxRecords + 1).Select(i => new ControlRecord("q", "k" + i, "1", "1")).ToList()));
    }

    [Fact, Trait("D", "D5.11")]
    public void Merge_pairs_before_and_after_and_keeps_missing_after_as_null()
    {
        var before = new[] { new ControlValue("q", "_", "10"), new ControlValue("q", "1", "4") };
        var after = new[] { new ControlValue("q", "_", "12") };
        var m = ControlSet.Merge(before, after);
        Assert.Equal([new ControlRecord("q", "_", "10", "12"), new ControlRecord("q", "1", "4", null)], m);
    }

    // ---------------------------------------------------------------- porównanie

    private static ErpProfile Profile(string tolerance) => new("p", 1, 1, "confirmed", new ErpProfileMatch(["dbo.x"], null), "single", [new ControlQuery("q", "SELECT COUNT(*) AS n FROM x", tolerance)], "k");

    private static ControlCheckResult Cmp(string tol, string before, string? after, string? restored, string key = "_") =>
        ControlVerifier.Compare(Profile(tol), [new ControlRecord("q", key, before, after)], restored is null ? [] : [new ControlValue("q", key, restored)]);

    [Theory, Trait("D", "D5.11")]
    [InlineData("range", "100", "103", "100", true)]
    [InlineData("range", "100", "103", "103", true)]
    [InlineData("range", "100", "103", "99", false)]   // odtworzona baza ma mniej niż w chwili kopii
    [InlineData("range", "100", "103", "104", false)]  // więcej niż kiedykolwiek w trakcie kopii
    [InlineData("range", "100", null, "5000", true)]   // bez pomiaru „po” brak górnej granicy
    [InlineData("range", "100", null, "99", false)]
    [InlineData("range", "100", "103", null, false)]   // grupa zniknęła = 0 wierszy
    [InlineData("exact", "7", "7", "7", true)]
    [InlineData("exact", "7", "7", "8", false)]
    [InlineData("exact", "7", "8", "7", false)]        // baza zmieniła się w trakcie kopii — „exact” nie jest spełnione (ControlComparison)
    [InlineData("approx", "10000", null, "10010", true)]
    [InlineData("approx", "10000", null, "10011", false)]
    public void Number_tolerances(string tol, string before, string? after, string? restored, bool expectedPass) =>
        Assert.Equal(expectedPass, Cmp(tol, before, after, restored).Passed);

    [Fact, Trait("D", "D5.11")]
    public void Exact_without_after_measurement_is_unchecked_not_failed()
    {
        var r = Cmp("exact", "7", null, "9");
        Assert.True(r.Passed);
        Assert.Equal((0, 1), (r.Checked, r.Unchecked));
    }

    [Fact, Trait("D", "D5.11")]
    public void Dates_must_not_go_backwards_and_skipped_or_unknown_queries_are_unchecked()
    {
        Assert.True(Cmp("range", "2026-10-01T08:30:00Z", null, "2026-10-02T00:00:00Z", "1#last").Passed);
        Assert.False(Cmp("range", "2026-10-01T08:30:00Z", null, "2026-09-30T00:00:00Z", "1#last").Passed);
        Assert.False(Cmp("range", "2026-10-01T08:30:00Z", null, null, "1#last").Passed);

        var rec = new[] { new ControlRecord("q", "_", "5", "5"), new ControlRecord("zzz", "_", "5", "5") };
        var r = ControlVerifier.Compare(Profile("range"), rec, [], ["q"]);
        Assert.Equal((0, 2), (r.Checked, r.Unchecked)); // q pominięte przy odtwarzaniu, zzz nieznane profilowi
        Assert.True(r.Passed);
    }

    [Fact, Trait("D", "D5.11"), Trait("SEC", "SEC-15.3")]
    public void Result_carries_query_ids_only_never_values()
    {
        var r = Cmp("range", "100", "103", "1");
        Assert.Equal(["q"], r.MismatchedQueryIds);
        Assert.DoesNotContain("100", string.Join(",", r.MismatchedQueryIds), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- backup

    private MsSqlBackupRequest Request(bool detect = true, string? profile = null) => new(_db, _root, "run0000c1", SqlRoot, ErpProfile: profile)
    {
        ConflictWait = TimeSpan.Zero,
        ConflictPollInterval = TimeSpan.FromMilliseconds(1),
        DetectErp = detect,
    };

    private FakeSqlServer GtServer()
    {
        var sql = new FakeSqlServer(_db, _root, SqlRoot);
        foreach (var t in new[] { "dok__Dokument", "dok_Pozycja", "tw__Towar", "kh__Kontrahent", "adr__Ewid" }) sql.ErpTables.Add(("dbo", t));
        sql.ControlResponder = (q, nth) => q switch
        {
            _ when q.Contains("tw__Towar", StringComparison.Ordinal) => [R(("n", nth == 1 ? 100 : 103))],
            _ when q.Contains("kh__Kontrahent", StringComparison.Ordinal) => [R(("n", 40))],
            _ => [R(("dok_Typ", 1), ("n", nth == 1 ? 7 : 8), ("last", new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc)))],
        };
        return sql;
    }

    [Fact, Trait("D", "D5.11"), Trait("D5", "D5.9"), Trait("BL", "BL-40")]
    public async Task Backup_detects_erp_runs_controls_before_and_after_and_records_them_in_dbmeta()
    {
        var sql = GtServer();
        await using var staged = await new MsSqlBackupService(sql).PrepareAsync(Request(), _ct);
        Assert.Equal("insert-gt", staged.DbMeta["erpProfile"]);
        Assert.Empty(staged.Warnings);
        Assert.True(ControlSet.TryParse(staged.DbMeta[ControlSet.MetaKey], out var records));
        Assert.Contains(new ControlRecord("gt-tw", "_", "100", "103"), records);
        Assert.Contains(new ControlRecord("gt-kh", "_", "40", "40"), records);
        Assert.Contains(new ControlRecord("gt-dok", "1", "7", "8"), records);
        Assert.Contains(new ControlRecord("gt-dok", "1#last", "2026-10-14T09:00:00Z", "2026-10-14T09:00:00Z"), records);
        // kolejność: wykrywanie → kontrola PRZED → BACKUP → kontrola PO; wszystkie zapytania tylko-odczytowe, nazwa bazy jako parametr
        Assert.Equal(6, sql.ControlCalls.Count);
        Assert.All(sql.ControlCalls, c => Assert.Equal(_db, c.Parameters["@db"]));
        var detect = Assert.Single(sql.DetectCalls);
        Assert.Equal(_db, detect.Parameters["@db"]);
        Assert.DoesNotContain(_db, detect.Text, StringComparison.Ordinal);
        Assert.Contains("QUOTENAME(@db)", detect.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(staged.DbMeta.Values, v => v.Contains("Kowalski", StringComparison.Ordinal));
    }

    [Fact, Trait("D", "D5.11"), Trait("D5", "D5.9")]
    public async Task Database_without_erp_schema_runs_no_control_queries_and_has_no_controls_key()
    {
        var sql = new FakeSqlServer(_db, _root, SqlRoot);
        await using var staged = await new MsSqlBackupService(sql).PrepareAsync(Request(), _ct);
        Assert.Empty(sql.ControlCalls);
        Assert.False(staged.DbMeta.ContainsKey(ControlSet.MetaKey));
        Assert.False(staged.DbMeta.ContainsKey("erpProfile"));
        Assert.Empty(staged.Warnings);
    }

    [Fact, Trait("D", "D5.11")]
    public async Task Detection_is_off_unless_requested_and_an_explicit_profile_id_is_honoured()
    {
        var off = GtServer();
        await using (var s1 = await new MsSqlBackupService(off).PrepareAsync(Request(detect: false), _ct))
        {
            Assert.Empty(off.DetectCalls);
            Assert.Empty(off.ControlCalls);
            Assert.False(s1.DbMeta.ContainsKey(ControlSet.MetaKey));
        }

        var explicitProfile = GtServer();
        await using var s2 = await new MsSqlBackupService(explicitProfile).PrepareAsync(Request(detect: false, profile: "insert-gt") with { RunId = "run0000c2" }, _ct);
        Assert.Empty(explicitProfile.DetectCalls); // bez wykrywania
        Assert.True(s2.DbMeta.ContainsKey(ControlSet.MetaKey));
    }

    [Fact, Trait("D", "D5.11"), Trait("AC", "AC-02.1")]
    public async Task Missing_select_permission_only_warns_with_kb_3010_and_the_backup_is_still_valid()
    {
        var sql = GtServer();
        sql.ControlResponder = (_, _) => throw new MsSqlServerException([229]);
        await using var staged = await new MsSqlBackupService(sql).PrepareAsync(Request(), _ct);
        Assert.Equal(KbCode.SqlControlQuerySkipped, Assert.Single(staged.Warnings).Code);
        Assert.False(staged.DbMeta.ContainsKey(ControlSet.MetaKey));
        Assert.True(File.Exists(staged.StagedFile));
        Assert.Equal("insert-gt", staged.DbMeta["erpProfile"]);
    }

    [Fact, Trait("D", "D5.11")]
    public async Task Failed_backup_after_controls_still_cleans_staging()
    {
        var sql = GtServer();
        sql.BackupErrors = [3201];
        await Assert.ThrowsAsync<KobianException>(() => new MsSqlBackupService(sql).PrepareAsync(Request(), _ct));
        Assert.False(Directory.EnumerateFileSystemEntries(_root).Any());
    }

    [Fact, Trait("D", "D5.9")]
    public void Detection_query_text_depends_only_on_the_number_of_names()
    {
        var d = new ErpDetector(ErpProfileCatalog.LoadBuiltIn());
        var text = d.BuildDatabaseQuery();
        foreach (var n in d.TableNames) Assert.DoesNotContain(n, text, StringComparison.Ordinal);
        Assert.Contains("@n" + d.TableNames.Count + " sysname", text, StringComparison.Ordinal);
        Assert.Equal(d.TableNames.Count, d.BuildParameters().Count);
    }
}
