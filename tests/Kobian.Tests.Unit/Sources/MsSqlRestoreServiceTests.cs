using Kobian.Domain;
using Kobian.Providers.Sources.Erp;
using Kobian.Providers.Sources.MsSql;
using Xunit;

namespace Kobian.Tests.Unit.Sources;

/// <summary>SCR-08 (it. 83, 8.4, AC-32.1/32.2): odtworzenie bazy MSSQL do NOWEJ bazy na porcie IMsSqlSession — serwer zastępczy ze skryptem odpowiedzi.</summary>
public sealed class MsSqlRestoreServiceTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);
    private const string Bak = @"C:\ProgramData\Kobian\staging\r1\Firma.bak";

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ScriptSql : IMsSqlSession
    {
        public bool Exists { get; set; }
        public bool CanCreate { get; set; } = true;
        public string? DataPath { get; set; } = @"D:\SQLData";
        public string? LogPath { get; set; } = @"L:\SQLLogs\";
        public List<(string Logical, string Type)> Files { get; } = [("Firma", "D"), ("Firma_2", "D"), ("Firma_log", "L")];
        public int[]? RestoreErrors { get; set; }
        public List<SqlCommandSpec> Calls { get; } = [];
        public Func<SqlCommandSpec, IReadOnlyList<IReadOnlyDictionary<string, object?>>>? Controls { get; set; }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(SqlCommandSpec c, CancellationToken ct)
        {
            Calls.Add(c);
            static IReadOnlyDictionary<string, object?> Row(params (string, object?)[] v) => v.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<IReadOnlyDictionary<string, object?>> r = c.Text switch
            {
                MsSqlRestoreService.ExistsText => [Row(("exists_flag", Exists ? 1 : 0))],
                MsSqlRestoreService.CanCreateText => [Row(("can_create", CanCreate ? 1 : 0))],
                MsSqlRestoreService.DefaultPathsText => [Row(("data_path", DataPath), ("log_path", LogPath))],
                MsSqlRestoreService.FileListText => [.. Files.Select(f => Row(("LogicalName", f.Logical), ("Type", f.Type), ("PhysicalName", @"C:\inne\" + f.Logical)))],
                ControlQueryRunner.WrapperText => Controls?.Invoke(c) ?? [],
                _ => throw new InvalidOperationException(c.Text),
            };
            return Task.FromResult(r);
        }

        public Task ExecuteAsync(SqlCommandSpec c, CancellationToken ct)
        {
            Calls.Add(c);
            if (c.Text.StartsWith("RESTORE DATABASE", StringComparison.Ordinal) && RestoreErrors is { } e) throw new MsSqlServerException(e);
            return Task.CompletedTask;
        }
    }

    [Fact, Trait("AC", "AC-32.1"), Trait("Plan", "8.4")]
    public async Task Restores_to_new_dated_name_with_moves_checksum_and_trustworthy_off()
    {
        var sql = new ScriptSql();
        var r = await new MsSqlRestoreService(sql, new Clock()).RestoreAsync(new MsSqlRestoreRequest(Bak, "Firma"), _ct);
        Assert.Equal("Firma_restore_20261014", r.TargetDatabase);
        Assert.Equal([@"D:\SQLData\Firma_restore_20261014.mdf", @"D:\SQLData\Firma_restore_20261014_1.ndf", @"L:\SQLLogs\Firma_restore_20261014_log.ldf"], r.Moves.Select(m => m.PhysicalPath));
        var texts = sql.Calls.Select(c => c.Text).ToList();
        Assert.True(texts.IndexOf(MsSqlBackupCommand.VerifyOnlyText) < texts.FindIndex(t => t.StartsWith("RESTORE DATABASE", StringComparison.Ordinal))); // najpierw VERIFYONLY
        var restore = sql.Calls.Single(c => c.Text.StartsWith("RESTORE DATABASE", StringComparison.Ordinal));
        Assert.Equal("RESTORE DATABASE @db FROM DISK = @path WITH MOVE @l0 TO @p0, MOVE @l1 TO @p1, MOVE @l2 TO @p2, CHECKSUM, RECOVERY, STATS = 5;", restore.Text);
        Assert.DoesNotContain("REPLACE", restore.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Firma_restore_20261014", restore.Parameters["@db"]);
        Assert.Equal("Firma_log", restore.Parameters["@l2"]);
        Assert.Contains(sql.Calls, c => c.Text == MsSqlRestoreService.TrustworthyOffText);
    }

    [Fact, Trait("AC", "AC-32.2")]
    public async Task Existing_target_is_never_overwritten_and_nothing_is_executed()
    {
        var sql = new ScriptSql { Exists = true };
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlRestoreService(sql, new Clock()).RestoreAsync(new MsSqlRestoreRequest(Bak, "Firma", "Firma"), _ct));
        Assert.Equal(KbCode.SqlRestoreTargetExists, e.Code);
        Assert.DoesNotContain(sql.Calls, c => c.Text.StartsWith("RESTORE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("perm", "KB-3502")]
    [InlineData("filestream", "KB-3503")]
    [InlineData("nopaths", "KB-3504")]
    [InlineData("nolog", "KB-3202")]
    [InlineData("badname", "KB-0103")]
    [InlineData("badpath", "KB-0103")]
    [InlineData("checksum", "KB-3201")]
    public async Task Refusals_and_errors_map_to_codes(string c, string code)
    {
        var sql = new ScriptSql();
        var req = new MsSqlRestoreRequest(Bak, "Firma");
        switch (c)
        {
            case "perm": sql.CanCreate = false; break;
            case "filestream": sql.Files.Add(("Firma_fs", "S")); break;
            case "nopaths": sql.DataPath = null; break;
            case "nolog": sql.Files.RemoveAt(2); break;
            case "badname": req = req with { TargetDatabase = "x\u0001y" }; break;
            case "badpath": req = req with { DataDirectory = "/var/sql" }; break;
            case "checksum": sql.RestoreErrors = [3013, 3043]; break;
        }
        if (c == "badpath")
        {
            var e0 = await Assert.ThrowsAsync<KobianException>(() => new MsSqlRestoreService(sql, new Clock()).RestoreAsync(req, _ct));
            Assert.Equal(code, e0.Code);
            return;
        }
        var e = await Assert.ThrowsAsync<KobianException>(() => new MsSqlRestoreService(sql, new Clock()).RestoreAsync(req, _ct));
        Assert.Equal(c == "checksum" ? KbCode.SqlPageChecksumError : code, e.Code);
        if (c != "checksum") Assert.DoesNotContain(sql.Calls, x => x.Text.StartsWith("RESTORE DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void File_names_come_only_from_target_name_never_from_backup_paths()
    {
        var files = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["LogicalName"] = "a", ["Type"] = "D" },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["LogicalName"] = "b", ["Type"] = "L" },
        };
        var moves = MsSqlRestoreService.PlanMoves(files, "Firma ąę]x", @"D:\d", @"D:\l");
        Assert.Equal([@"D:\d\Firma____x.mdf", @"D:\l\Firma____x_log.ldf"], moves.Select(m => m.PhysicalPath));
    }

    private static ErpProfile InsertGt() => ErpProfileCatalog.LoadBuiltIn().Single(p => p.Id == "insert-gt");

    private static IReadOnlyList<ControlRecord> GtRecords() =>
    [
        new("gt-tw", "_", "100", "102"),
        new("gt-kh", "_", "40", "40"),
    ];

    [Fact, Trait("D", "D5.11"), Trait("AC", "AC-32.1")]
    public async Task Control_queries_run_on_the_restored_database_and_matching_counts_pass()
    {
        var sql = new ScriptSql
        {
            Controls = c => ((string)c.Parameters["@q"]).Contains("tw__Towar", StringComparison.Ordinal)
                ? [FakeRow(("n", 101))]
                : ((string)c.Parameters["@q"]).Contains("kh__Kontrahent", StringComparison.Ordinal) ? [FakeRow(("n", 40))] : [],
        };
        var r = await new MsSqlRestoreService(sql, new Clock()).RestoreAsync(new MsSqlRestoreRequest(Bak, "Firma") { Profile = InsertGt(), Controls = GtRecords() }, _ct);
        Assert.NotNull(r.ControlCheck);
        Assert.True(r.ControlCheck!.Passed);
        // zapytania poszły na NOWĄ bazę, po RESTORE i TRUSTWORTHY OFF — nigdy na bazie produkcyjnej o nazwie źródłowej
        var ctl = sql.Calls.Where(c => c.Text == ControlQueryRunner.WrapperText).ToList();
        Assert.NotEmpty(ctl);
        Assert.All(ctl, c => Assert.Equal("Firma_restore_20261014", c.Parameters["@db"]));
        var texts = sql.Calls.Select(c => c.Text).ToList();
        Assert.True(texts.IndexOf(MsSqlRestoreService.TrustworthyOffText) < texts.IndexOf(ControlQueryRunner.WrapperText));
    }

    [Fact, Trait("D", "D5.11"), Trait("AC", "AC-32.1"), Trait("D8", "D8.4")]
    public async Task Logically_empty_restored_database_is_reported_by_query_id_without_values()
    {
        var sql = new ScriptSql { Controls = c => [FakeRow(("n", 0))] }; // baza poprawna technicznie, ale pusta
        var r = await new MsSqlRestoreService(sql, new Clock()).RestoreAsync(new MsSqlRestoreRequest(Bak, "Firma") { Profile = InsertGt(), Controls = GtRecords() }, _ct);
        Assert.False(r.ControlCheck!.Passed);
        Assert.Equal(["gt-kh", "gt-tw"], r.ControlCheck.MismatchedQueryIds);
    }

    [Fact, Trait("D", "D5.11")]
    public async Task Restore_without_recorded_controls_runs_no_control_queries()
    {
        var sql = new ScriptSql();
        var r = await new MsSqlRestoreService(sql, new Clock()).RestoreAsync(new MsSqlRestoreRequest(Bak, "Firma"), _ct);
        Assert.Null(r.ControlCheck);
        Assert.DoesNotContain(sql.Calls, c => c.Text == ControlQueryRunner.WrapperText);
    }

    private static Dictionary<string, object?> FakeRow(params (string, object?)[] v) => v.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);
}
