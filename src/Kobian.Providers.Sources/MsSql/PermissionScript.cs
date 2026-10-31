using System.Globalization;
using System.Text;

namespace Kobian.Providers.Sources.MsSql;

/// <summary>Warianty uprawnień (D5.7).</summary>
public enum PermissionVariant
{
    /// <summary>db_backupoperator + VIEW SERVER STATE + SELECT na msdb.dbo.backupset/backupmediafamily.</summary>
    Minimal = 1,
    /// <summary>Jak Minimal + CREATE DATABASE w master (RESTORE VERIFYONLY/HEADERONLY).</summary>
    WithVerify = 2,
}

/// <summary>
/// Generator skryptu uprawnień (AC-02.3, D5.7): administrator SQL uruchamia go sam — Kobian nie prosi o hasło „sa” (SEC-5.3).
/// Login domyślny: konto wirtualne usługi <c>NT SERVICE\KobianAgent</c> (D2.12).
/// </summary>
public static class PermissionScript
{
    public const string DefaultLogin = @"NT SERVICE\KobianAgent";

    public static string Generate(IEnumerable<string> databases, PermissionVariant variant, string login = DefaultLogin)
    {
        ArgumentNullException.ThrowIfNull(databases);
        var dbs = databases.Distinct(StringComparer.Ordinal).ToList();
        if (dbs.Count == 0) throw new ArgumentException("At least one database is required.", nameof(databases));
        var l = TSql.QuoteName(login);
        var sb = new StringBuilder();
        sb.AppendLine("-- Kobian Backup: skrypt uprawnień (" + (variant == PermissionVariant.Minimal ? "wariant minimalny" : "wariant z weryfikacją VERIFYONLY") + ")");
        sb.AppendLine("-- Uruchom jako administrator SQL Server. Przejrzyj przed wykonaniem.");
        sb.AppendLine("USE [master];");
        sb.AppendLine(CultureInfo.InvariantCulture, $"IF SUSER_ID({TSql.NLiteral(login)}) IS NULL CREATE LOGIN {l} FROM WINDOWS;");
        sb.AppendLine(CultureInfo.InvariantCulture, $"GRANT VIEW SERVER STATE TO {l};");
        if (variant == PermissionVariant.WithVerify)
        {
            sb.AppendLine("-- Uwaga (D5.7): CREATE DATABASE pozwala tworzyć bazy (ryzyko zapełnienia dysku) — decyzja administratora.");
            sb.AppendLine(CultureInfo.InvariantCulture, $"IF USER_ID({TSql.NLiteral(login)}) IS NULL CREATE USER {l} FOR LOGIN {l};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"GRANT CREATE DATABASE TO {l};");
        }
        sb.AppendLine("USE [msdb];");
        sb.AppendLine(CultureInfo.InvariantCulture, $"IF USER_ID({TSql.NLiteral(login)}) IS NULL CREATE USER {l} FOR LOGIN {l};");
        sb.AppendLine(CultureInfo.InvariantCulture, $"GRANT SELECT ON [dbo].[backupset] TO {l};");
        sb.AppendLine(CultureInfo.InvariantCulture, $"GRANT SELECT ON [dbo].[backupmediafamily] TO {l};");
        foreach (var db in dbs)
        {
            var q = TSql.QuoteName(db);
            sb.AppendLine(CultureInfo.InvariantCulture, $"USE {q};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"IF USER_ID({TSql.NLiteral(login)}) IS NULL CREATE USER {l} FOR LOGIN {l};");
            sb.AppendLine(CultureInfo.InvariantCulture, $"ALTER ROLE [db_backupoperator] ADD MEMBER {l};");
        }
        return sb.ToString();
    }
}
