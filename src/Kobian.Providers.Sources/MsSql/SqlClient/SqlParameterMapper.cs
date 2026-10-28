using System.Data;
using Microsoft.Data.SqlClient;

namespace Kobian.Providers.Sources.MsSql.SqlClient;

/// <summary>
/// Mapowanie parametrów <see cref="SqlCommandSpec"/> na <see cref="SqlParameter"/> z jawnym typem (SEC-5.1: bez AddWithValue —
/// brak niejawnej konwersji varchar/nvarchar i rozjazdu planów). Obsługiwany jest wyłącznie zamknięty zbiór typów.
/// </summary>
public static class SqlParameterMapper
{
    public static SqlParameter Map(string name, object? value)
    {
        if (!IsValidName(name)) throw new ArgumentException("Invalid SQL parameter name.", nameof(name));
        return value switch
        {
            null or DBNullValue => new SqlParameter(name, SqlDbType.Int) { Value = DBNull.Value },
            string s => new SqlParameter(name, SqlDbType.NVarChar, s.Length <= 4000 ? Math.Max(s.Length, 1) : -1) { Value = s },
            int i => new SqlParameter(name, SqlDbType.Int) { Value = i },
            long l => new SqlParameter(name, SqlDbType.BigInt) { Value = l },
            short sh => new SqlParameter(name, SqlDbType.SmallInt) { Value = sh },
            bool b => new SqlParameter(name, SqlDbType.Bit) { Value = b },
            Guid g => new SqlParameter(name, SqlDbType.UniqueIdentifier) { Value = g },
            DateTime dt => new SqlParameter(name, SqlDbType.DateTime2) { Value = dt },
            DateTimeOffset dto => new SqlParameter(name, SqlDbType.DateTimeOffset) { Value = dto },
            byte[] bytes => new SqlParameter(name, SqlDbType.VarBinary, bytes.Length <= 8000 ? Math.Max(bytes.Length, 1) : -1) { Value = bytes },
            _ => throw new ArgumentException("Unsupported SQL parameter type: " + value.GetType().Name, nameof(value)),
        };
    }

    /// <summary>Nazwa parametru: <c>@</c> + identyfikator ASCII (litera/podkreślenie, potem litery/cyfry/podkreślenia), ≤ 128 znaków.</summary>
    public static bool IsValidName(string? name)
    {
        if (name is null || name.Length is < 2 or > 128 || name[0] != '@') return false;
        if (!(char.IsAsciiLetter(name[1]) || name[1] == '_')) return false;
        for (var i = 2; i < name.Length; i++)
            if (!(char.IsAsciiLetterOrDigit(name[i]) || name[i] == '_')) return false;
        return true;
    }
}
