using System.Text;

namespace Kobian.Providers.Sources.Erp;

/// <summary>Wynik wykrywania dla jednej bazy (AC-30.x).</summary>
public sealed record ErpDetection(ErpProfile Profile, bool Confirmed);

/// <summary>
/// Wykrywanie ERP po schemacie (D5.9): jedno tylko-odczytowe zapytanie per baza o obecność tabel z reguł,
/// nazwy jako parametry; dopasowanie jako czysta funkcja.
/// </summary>
public sealed class ErpDetector
{
    private readonly IReadOnlyList<ErpProfile> _profiles;

    public ErpDetector(IReadOnlyList<ErpProfile> profiles)
    {
        _profiles = profiles;
        TableNames = profiles.SelectMany(p => p.Match.AllTables.Concat(p.Match.AnyTables ?? []))
            .Select(t => TableRef.Parse(t).Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Nazwy tabel do parametrów @n1…@nN.</summary>
    public IReadOnlyList<string> TableNames { get; }

    /// <summary>Zapytanie z R2.5.4 z liczbą parametrów równą liczbie nazw.</summary>
    public string BuildQuery()
    {
        var sb = new StringBuilder("SELECT s.name AS schema_name, t.name AS table_name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.name IN (");
        for (var i = 1; i <= TableNames.Count; i++)
        {
            if (i > 1) sb.Append(", ");
            sb.Append("@n").Append(i);
        }
        return sb.Append(");").ToString();
    }

    // Szablon ze stałych elementów; podstawiane są wyłącznie ciągi @n<liczba> (nigdy dane użytkownika) — SEC-5.1.
    private const string DatabaseQueryTemplate =
        "DECLARE @sql nvarchar(max) = N'SELECT s.name AS schema_name, t.name AS table_name FROM ' + QUOTENAME(@db) + N'.sys.tables t JOIN ' + QUOTENAME(@db) + N'.sys.schemas s ON s.schema_id = t.schema_id WHERE t.name IN ({names})'; "
        + "EXEC sp_executesql @sql, N'{decl}', {pass};";

    /// <summary>
    /// Wariant dla bazy wskazanej parametrem <c>@db</c> (cytowana po stronie serwera <c>QUOTENAME</c>): metadane <c>sys.tables</c>/<c>sys.schemas</c> tej bazy,
    /// nazwy tabel jako parametry <c>@n1…@nN</c> przekazane do <c>sp_executesql</c>. Tekst zależy tylko od liczby nazw (SEC-5.1).
    /// </summary>
    public string BuildDatabaseQuery()
    {
        var names = string.Join(", ", Enumerable.Range(1, TableNames.Count).Select(i => "@n" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var decl = string.Join(", ", Enumerable.Range(1, TableNames.Count).Select(i => "@n" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " sysname"));
        var pass = string.Join(", ", Enumerable.Range(1, TableNames.Count).Select(i => "@n" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " = @n" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return DatabaseQueryTemplate.Replace("{names}", names, StringComparison.Ordinal).Replace("{decl}", decl, StringComparison.Ordinal).Replace("{pass}", pass, StringComparison.Ordinal);
    }

    public IReadOnlyDictionary<string, object> BuildParameters() =>
        TableNames.Select((n, i) => (Key: "@n" + (i + 1), Value: (object)n)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    /// <summary>Dopasowanie: wszystkie <c>allTables</c> obecne (schemat i nazwa porównywane bez rozróżniania wielkości liter — domyślna kolacja CI), oraz ≥ 1 z <c>anyTables</c> jeśli podane. Wynik według priorytetu malejąco.</summary>
    public IReadOnlyList<ErpDetection> Match(IEnumerable<(string Schema, string Table)> presentTables)
    {
        var present = new HashSet<string>(presentTables.Select(t => t.Schema + "." + t.Table), StringComparer.OrdinalIgnoreCase);
        return _profiles
            .Where(p => p.Match.AllTables.All(t => present.Contains(TableRef.Parse(t).ToString()))
                        && (p.Match.AnyTables is null || p.Match.AnyTables.Count == 0 || p.Match.AnyTables.Any(t => present.Contains(TableRef.Parse(t).ToString()))))
            .OrderByDescending(p => p.Priority).ThenBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new ErpDetection(p, p.Status == "confirmed"))
            .ToList();
    }
}
