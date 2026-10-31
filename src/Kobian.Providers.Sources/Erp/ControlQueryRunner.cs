using System.Globalization;
using Kobian.Providers.Sources.MsSql;

namespace Kobian.Providers.Sources.Erp;

/// <summary>Jedna zmierzona wartość kontrolna (D5.11): liczba całkowita albo data UTC ISO-8601 (<c>yyyy-MM-ddTHH:mm:ssZ</c>) — nigdy tekst z kolumn danych (SEC-15.3).</summary>
public sealed record ControlValue(string QueryId, string Key, string Value);

/// <summary>Wynik uruchomienia zapytań kontrolnych: wartości + identyfikatory zapytań pominiętych (limit czasu, brak uprawnień, niedozwolony wynik).</summary>
public sealed record ControlRunResult(IReadOnlyList<ControlValue> Values, IReadOnlyList<string> SkippedQueryIds);

/// <summary>
/// D5.11 / SEC-15.3: wykonuje zapytania kontrolne profilu ERP na wskazanej bazie — <c>READ COMMITTED</c>, <c>LOCK_TIMEOUT 5000</c>, limit 30 s per zapytanie.
/// Zapytanie pochodzi wyłącznie z profilu (dane wbudowane, zwalidowane: jedna instrukcja SELECT bez komentarzy), nazwa bazy idzie jako parametr
/// i jest cytowana po stronie serwera (<c>QUOTENAME</c>). Konwencja wyniku: kolumna <c>n</c> (liczba, wymagana), opcjonalnie <c>last</c> (data),
/// pozostałe kolumny = klucz grupy (tylko liczby/daty). Przekroczenie czasu, błąd serwera albo wynik spoza konwencji = zapytanie POMINIĘTE
/// (ostrzeżenie KB-3010, nie błąd kopii).
/// </summary>
public sealed class ControlQueryRunner(IMsSqlSession session)
{
    public const int MaxRowsPerQuery = 256;
    public const int MaxKeyLength = 64;
    public const string CountColumn = "n";
    public const string LastColumn = "last";
    public const string LastSuffix = "#last";
    public const string NoGroupKey = "_";

    public const string WrapperText = """
        DECLARE @sql nvarchar(max) = N'SET LOCK_TIMEOUT 5000; SET TRANSACTION ISOLATION LEVEL READ COMMITTED; USE ' + QUOTENAME(@db) + N'; ' + @q;
        EXEC sp_executesql @sql;
        """;

    /// <summary>D5.11: limit czasu jednego zapytania (domyślnie 30 s).</summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<ControlRunResult> RunAsync(string database, IReadOnlyList<ControlQuery> queries, CancellationToken ct)
    {
        TSql.RequireSysname(database, nameof(database));
        ArgumentNullException.ThrowIfNull(queries);
        var values = new List<ControlValue>();
        var skipped = new List<string>();
        foreach (var q in queries)
        {
            if (!ErpProfileCatalog.IsSafeControlSql(q.Sql) || !IsValidQueryId(q.Id))
            {
                skipped.Add(q.Id);
                continue;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(QueryTimeout);
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
            try
            {
                rows = await session.QueryAsync(new SqlCommandSpec(WrapperText,
                    new Dictionary<string, object>(StringComparer.Ordinal) { ["@db"] = database, ["@q"] = q.Sql }), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                skipped.Add(q.Id); // limit czasu — pominięcie kontroli, nie błąd kopii
                continue;
            }
            catch (MsSqlServerException)
            {
                skipped.Add(q.Id); // brak uprawnień SELECT (229), LOCK_TIMEOUT (1222), brak tabeli (208) — wyłącznie pominięcie
                continue;
            }
            if (TryExtract(q.Id, rows, out var extracted)) values.AddRange(extracted);
            else skipped.Add(q.Id);
        }
        return new ControlRunResult(values, skipped);
    }

    public static bool IsValidQueryId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 32 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal static bool TryExtract(string queryId, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, out List<ControlValue> values)
    {
        values = [];
        if (rows.Count > MaxRowsPerQuery) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            long? n = null;
            string? last = null;
            var groups = new List<(string Name, string Value)>();
            foreach (var (name, raw) in row)
            {
                if (string.Equals(name, CountColumn, StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryInteger(raw, out var nn)) return false;
                    n = nn;
                }
                else if (string.Equals(name, LastColumn, StringComparison.OrdinalIgnoreCase))
                {
                    if (raw is null) continue;
                    if (!TryDate(raw, out last)) return false;
                }
                else
                {
                    if (raw is null) groups.Add((name.ToLowerInvariant(), "~"));
                    else if (TryInteger(raw, out var gv)) groups.Add((name.ToLowerInvariant(), gv.ToString(CultureInfo.InvariantCulture)));
                    else if (TryDate(raw, out var gd)) groups.Add((name.ToLowerInvariant(), gd!));
                    else return false; // tekst z kolumn danych nigdy nie trafia do kopii (SEC-15.3)
                }
            }
            if (n is null) return false;
            var key = groups.Count == 0 ? NoGroupKey : string.Join('|', groups.OrderBy(g => g.Name, StringComparer.Ordinal).Select(g => g.Value));
            if (key.Length > MaxKeyLength || !seen.Add(key)) return false;
            values.Add(new ControlValue(queryId, key, n.Value.ToString(CultureInfo.InvariantCulture)));
            if (last is not null) values.Add(new ControlValue(queryId, key + LastSuffix, last));
        }
        return true;
    }

    private static bool TryInteger(object? raw, out long value)
    {
        switch (raw)
        {
            case byte or sbyte or short or ushort or int or uint or long:
                value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                return true;
            case ulong u when u <= long.MaxValue:
                value = (long)u;
                return true;
            case decimal d when d == decimal.Truncate(d) && d >= long.MinValue && d <= long.MaxValue:
                value = (long)d;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static bool TryDate(object raw, out string? iso)
    {
        DateTimeOffset? d = raw switch
        {
            DateTimeOffset o => o.ToUniversalTime(),
            DateTime t => new DateTimeOffset(t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc)),
            _ => null,
        };
        iso = d?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return iso is not null;
    }
}
