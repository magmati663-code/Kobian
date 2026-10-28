using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kobian.Providers.Sources.Erp;

/// <summary>Tabela w regule profilu: schemat (domyślnie dbo) + nazwa.</summary>
public sealed record TableRef(string Schema, string Name)
{
    public static TableRef Parse(string s)
    {
        ArgumentException.ThrowIfNullOrEmpty(s);
        var dot = s.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? new TableRef("dbo", s) : new TableRef(s[..dot], s[(dot + 1)..]);
    }

    public override string ToString() => Schema + "." + Name;
}

public sealed record ControlQuery(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sql")] string Sql,
    [property: JsonPropertyName("tolerance")] string Tolerance);

public sealed record ErpProfileMatch(
    [property: JsonPropertyName("allTables")] IReadOnlyList<string> AllTables,
    [property: JsonPropertyName("anyTables")] IReadOnlyList<string>? AnyTables);

/// <summary>Profil ERP jako dane (D5.9): <c>profiles/&lt;id&gt;.json</c>.</summary>
public sealed record ErpProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("match")] ErpProfileMatch Match,
    [property: JsonPropertyName("consistencyGroup")] string ConsistencyGroup,
    [property: JsonPropertyName("controlQueries")] IReadOnlyList<ControlQuery> ControlQueries,
    [property: JsonPropertyName("restoreNotesKey")] string RestoreNotesKey);

public static class ErpProfileCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    /// <summary>Wczytuje profile wbudowane (zasoby). Profil niepoprawny = wyjątek przy starcie (fail fast; testowane).</summary>
    public static IReadOnlyList<ErpProfile> LoadBuiltIn()
    {
        var asm = typeof(ErpProfileCatalog).Assembly;
        var result = new List<ErpProfile>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(".Erp.Profiles.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            result.Add(Parse(s));
        }
        return result;
    }

    public static ErpProfile Parse(Stream json)
    {
        var p = JsonSerializer.Deserialize<ErpProfile>(json, Options) ?? throw new InvalidDataException("Empty profile.");
        Validate(p);
        return p;
    }

    /// <summary>D5.11: jedna instrukcja SELECT, bez średnika i komentarzy (używane przy wczytaniu profilu i tuż przed wykonaniem).</summary>
    public static bool IsSafeControlSql(string? sql)
    {
        if (sql is null) return false;
        var t = sql.Trim();
        return t.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase) && !t.Contains(';', StringComparison.Ordinal)
            && !t.Contains("--", StringComparison.Ordinal) && !t.Contains("/*", StringComparison.Ordinal);
    }

    private static void Validate(ErpProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Id) || p.Version < 1) throw new InvalidDataException("Profile id/version invalid.");
        if (p.Match?.AllTables is null || p.Match.AllTables.Count == 0) throw new InvalidDataException($"Profile {p.Id}: allTables required.");
        if (p.Status is not ("confirmed" or "to-verify")) throw new InvalidDataException($"Profile {p.Id}: status must be confirmed|to-verify.");
        foreach (var q in p.ControlQueries ?? [])
        {
            // Zapytania kontrolne: tylko SELECT agregujący, jedna instrukcja (D5.11) — obrona przed błędną edycją profilu.
            if (!IsSafeControlSql(q.Sql)) throw new InvalidDataException($"Profile {p.Id}: control query {q.Id} must be a single SELECT.");
            if (!ControlQueryRunner.IsValidQueryId(q.Id)) throw new InvalidDataException($"Profile {p.Id}: control query id {q.Id} invalid.");
            if (q.Tolerance is not ("range" or "exact" or "approx")) throw new InvalidDataException($"Profile {p.Id}: control query {q.Id} tolerance invalid.");
        }
    }
}
