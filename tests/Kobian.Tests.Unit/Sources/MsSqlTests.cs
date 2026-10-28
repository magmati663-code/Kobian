using Kobian.Domain;
using Kobian.Providers.Sources.Erp;
using Kobian.Providers.Sources.MsSql;
using Xunit;

namespace Kobian.Tests.Unit.Sources;

/// <summary>R2.5 bez serwera SQL (S5c=nie): budowanie poleceń, skrypt uprawnień, mapowanie błędów, wykrywanie ERP.
/// Wykonanie na prawdziwym SQL Server — kolejka Windows (W-3).</summary>
public sealed class MsSqlTests
{
    [Fact, Trait("AC", "AC-02.1"), Trait("SEC", "SEC-5.1")]
    public void Backup_command_keeps_database_name_out_of_sql_text()
    {
        var name = "ERP]; DROP DATABASE x; --";
        var spec = MsSqlBackupCommand.FullCopyOnly(name, @"C:\ProgramData\Kobian\staging\r1\a.bak", BackupImpactProfile.Default);
        Assert.DoesNotContain("DROP", spec.Text, StringComparison.Ordinal);
        Assert.Contains("QUOTENAME(@db)", spec.Text, StringComparison.Ordinal);
        Assert.Contains("COPY_ONLY, CHECKSUM", spec.Text, StringComparison.Ordinal);
        Assert.Equal(name, spec.Parameters["@db"]);
        Assert.Same(DBNullValue.Instance, spec.Parameters["@bufferCount"]);
    }

    [Fact, Trait("NFR", "NFR-07")]
    public void Low_impact_profile_sets_buffercount_and_maxtransfer()
    {
        var spec = MsSqlBackupCommand.FullCopyOnly("Firma", @"D:\s\x.bak", BackupImpactProfile.LowImpact);
        Assert.Equal(8, spec.Parameters["@bufferCount"]);
        Assert.Equal(1_048_576, spec.Parameters["@maxTransfer"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    public void Invalid_database_names_rejected(string name) =>
        Assert.Throws<ArgumentException>(() => MsSqlBackupCommand.FullCopyOnly(name, @"C:\s\x.bak", BackupImpactProfile.Default));

    [Fact]
    public void Relative_staging_path_rejected() =>
        Assert.Throws<ArgumentException>(() => MsSqlBackupCommand.FullCopyOnly("db", @"staging\x.bak", BackupImpactProfile.Default));

    [Theory, Trait("SEC", "SEC-5.1")]
    [InlineData("Firma", "[Firma]")]
    [InlineData("a]b", "[a]]b]")]
    [InlineData("]]", "[]]]]]")]
    [InlineData("Zażółć", "[Zażółć]")]
    public void QuoteName_escapes_brackets(string input, string expected) => Assert.Equal(expected, TSql.QuoteName(input));

    [Fact]
    public void QuoteName_rejects_over_128_chars() => Assert.Throws<ArgumentException>(() => TSql.QuoteName(new string('a', 129)));

    [Fact]
    public void NLiteral_doubles_apostrophes() => Assert.Equal("N'O''Brien'", TSql.NLiteral("O'Brien"));

    [Fact, Trait("AC", "AC-02.3"), Trait("SEC", "SEC-5.3")]
    public void Minimal_permission_script_has_no_create_database_and_no_sa()
    {
        var script = PermissionScript.Generate(["Firma_A", "x]y"], PermissionVariant.Minimal);
        Assert.Contains("ALTER ROLE [db_backupoperator] ADD MEMBER [NT SERVICE\\KobianAgent];", script, StringComparison.Ordinal);
        Assert.Contains("USE [x]]y];", script, StringComparison.Ordinal);
        Assert.Contains("GRANT VIEW SERVER STATE", script, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON [dbo].[backupset]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE DATABASE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sysadmin", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'sa'", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("AC", "AC-02.3")]
    public void Verify_variant_adds_create_database_in_master_only()
    {
        var script = PermissionScript.Generate(["Firma"], PermissionVariant.WithVerify);
        var master = script.IndexOf("USE [master];", StringComparison.Ordinal);
        var msdb = script.IndexOf("USE [msdb];", StringComparison.Ordinal);
        var grant = script.IndexOf("GRANT CREATE DATABASE", StringComparison.Ordinal);
        Assert.True(master < grant && grant < msdb);
        Assert.DoesNotContain("dbcreator", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(18456, "KB-3002", ErrorClass.Permanent)]
    [InlineData(262, "KB-3003", ErrorClass.Permanent)]
    [InlineData(-2, "KB-3001", ErrorClass.Transient)]
    [InlineData(3043, "KB-3201", ErrorClass.Integrity)]
    [InlineData(99999, "KB-0001", ErrorClass.Permanent)]
    public void Sql_errors_map_to_kb_codes(int number, string code, ErrorClass cls) => Assert.Equal((code, cls), SqlErrorMap.Map(number));

    [Fact]
    public void Checksum_error_wins_over_generic_terminating_error() =>
        Assert.Equal(KbCode.SqlPageChecksumError, SqlErrorMap.MapMany([3013, 3043]).Code);

    [Theory]
    [InlineData(17, SqlSupport.Supported)]
    [InlineData(14, SqlSupport.Supported)]
    [InlineData(13, SqlSupport.Conditional)]
    [InlineData(12, SqlSupport.Conditional)]
    [InlineData(11, SqlSupport.Unsupported)]
    public void Version_policy_D5_8(int major, SqlSupport expected) => Assert.Equal(expected, SqlVersionPolicy.Classify(major));

    [Fact]
    public void Queries_avoid_functions_missing_in_sql_2014()
    {
        foreach (var q in new[] { MsSqlQueries.D01Server, MsSqlQueries.D02Databases, MsSqlQueries.D03Permissions, MsSqlQueries.D04Tde, MsSqlQueries.P01ConflictingOperations, MsSqlQueries.P03LastFull, MsSqlQueries.P03DifferentialBase })
        {
            Assert.DoesNotContain("STRING_AGG", q, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("STRING_SPLIT", q, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TRIM(", q, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class ErpDetectionTests
{
    private static readonly ErpDetector Detector = new(ErpProfileCatalog.LoadBuiltIn());

    [Fact]
    public void Built_in_profiles_load_and_validate() =>
        Assert.Equal(["comarch-optima-company", "comarch-optima-config", "insert-gt"], ErpProfileCatalog.LoadBuiltIn().Select(p => p.Id).ToArray());

    [Fact, Trait("AC", "AC-30.1")]
    public void Subiekt_gt_schema_is_detected()
    {
        var tables = new[] { ("dbo", "dok__Dokument"), ("dbo", "dok_Pozycja"), ("dbo", "tw__Towar"), ("dbo", "kh__Kontrahent"), ("dbo", "adr__Ewid"), ("dbo", "inna") };
        var m = Detector.Match(tables);
        Assert.Single(m);
        Assert.Equal("insert-gt", m[0].Profile.Id);
        Assert.True(m[0].Confirmed);
    }

    [Fact, Trait("AC", "AC-30.1")]
    public void Optima_config_detected_with_schema_case_insensitive()
    {
        var m = Detector.Match([("cdn", "bazy")]);
        Assert.Equal("comarch-optima-config", Assert.Single(m).Profile.Id);
    }

    [Fact]
    public void Partial_schema_is_not_detected() =>
        Assert.Empty(Detector.Match([("dbo", "dok__Dokument"), ("dbo", "tw__Towar")]));

    [Fact]
    public void Same_table_name_in_wrong_schema_is_not_detected() =>
        Assert.Empty(Detector.Match([("dbo", "Bazy")]));

    [Fact, Trait("SEC", "SEC-5.1")]
    public void Detection_query_uses_one_parameter_per_table_name()
    {
        var q = Detector.BuildQuery();
        var p = Detector.BuildParameters();
        Assert.Equal(Detector.TableNames.Count, p.Count);
        foreach (var name in Detector.TableNames) Assert.DoesNotContain(name, q, StringComparison.Ordinal);
        Assert.Contains("@n" + p.Count + ")", q, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"id":"x","version":1,"priority":1,"status":"confirmed","match":{"allTables":["t"],"anyTables":null},"consistencyGroup":"g","controlQueries":[{"id":"q","sql":"DELETE FROM t","tolerance":"exact"}],"restoreNotesKey":"k"}""")]
    [InlineData("""{"id":"x","version":1,"priority":1,"status":"confirmed","match":{"allTables":["t"],"anyTables":null},"consistencyGroup":"g","controlQueries":[{"id":"q","sql":"SELECT 1; DROP TABLE t","tolerance":"exact"}],"restoreNotesKey":"k"}""")]
    [InlineData("""{"id":"x","version":1,"priority":1,"status":"maybe","match":{"allTables":["t"],"anyTables":null},"consistencyGroup":"g","controlQueries":[],"restoreNotesKey":"k"}""")]
    [InlineData("""{"id":"x","version":1,"priority":1,"status":"confirmed","match":{"allTables":[],"anyTables":null},"consistencyGroup":"g","controlQueries":[],"restoreNotesKey":"k"}""")]
    [InlineData("""{"id":"x","version":1,"priority":1,"status":"confirmed","match":{"allTables":["t"],"anyTables":null},"consistencyGroup":"g","controlQueries":[],"restoreNotesKey":"k","extra":1}""")]
    public void Invalid_profiles_are_rejected(string json)
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.ThrowsAny<Exception>(() => ErpProfileCatalog.Parse(ms));
    }
}
