using System.Text;

namespace Kobian.Providers.Sources.MsSql;

/// <summary>
/// Bezpieczne składanie tekstu T-SQL wyłącznie dla SKRYPTÓW pokazywanych administratorowi (AC-02.3).
/// Zapytania wykonywane przez Kobian używają parametrów i QUOTENAME po stronie serwera (SEC-5.1).
/// </summary>
public static class TSql
{
    public const int SysnameMaxLength = 128;

    /// <summary>Odpowiednik QUOTENAME(x, '[') z walidacją sysname: 1–128 znaków, bez znaków sterujących.</summary>
    public static string QuoteName(string identifier)
    {
        RequireSysname(identifier, nameof(identifier));
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    /// <summary>Literał N'…' z podwojonym apostrofem.</summary>
    public static string NLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (HasControlChars(value)) throw new ArgumentException("Control characters are not allowed.", nameof(value));
        return "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    public static void RequireSysname(string? name, string paramName)
    {
        if (string.IsNullOrEmpty(name) || name.Length > SysnameMaxLength || HasControlChars(name))
            throw new ArgumentException("Invalid SQL Server identifier (sysname).", paramName);
    }

    private static bool HasControlChars(string s)
    {
        foreach (var r in s.EnumerateRunes())
        {
            if (Rune.IsControl(r)) return true;
        }
        return false;
    }
}
