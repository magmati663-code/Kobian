using System.Globalization;
using System.Text;
using System.Text.Json;
using Kobian.Providers.Sources.MsSql;

namespace Kobian.Providers.Sources.Erp;

/// <summary>Wartość kontrolna zapisana w <c>dbMeta.controls</c>: przed BACKUP i po (<see langword="null"/> = zapytanie po kopii pominięte).</summary>
public sealed record ControlRecord(string QueryId, string Key, string Before, string? After);

/// <summary>Wynik porównania po odtworzeniu (V4, D5.11) — wyłącznie identyfikatory zapytań i liczniki, bez wartości z bazy klienta.</summary>
public sealed record ControlCheckResult(int Checked, int Unchecked, IReadOnlyList<string> MismatchedQueryIds)
{
    public bool Passed => MismatchedQueryIds.Count == 0;
}

/// <summary>Serializacja klucza <c>controls</c> klucza 14 (płaska mapa tekstowa; limit 32 KiB na wartość — SnapshotCodec) i łączenie pomiarów przed/po.</summary>
public static class ControlSet
{
    public const string MetaKey = "controls";
    public const int MaxRecords = 512;
    public const int MaxSerializedBytes = 30 * 1024;

    public static IReadOnlyList<ControlRecord> Merge(IReadOnlyList<ControlValue> before, IReadOnlyList<ControlValue> after)
    {
        var afterByKey = after.GroupBy(v => (v.QueryId, v.Key)).ToDictionary(g => g.Key, g => g.First().Value);
        return before.Select(b => new ControlRecord(b.QueryId, b.Key, b.Value, afterByKey.GetValueOrDefault((b.QueryId, b.Key)))).ToList();
    }

    /// <summary>JSON <c>[[q,k,before,after|null],…]</c>; <see langword="null"/>, gdy brak rekordów albo wynik przekracza limit (wtedy kontroli nie zapisujemy).</summary>
    public static string? Serialize(IReadOnlyList<ControlRecord> records)
    {
        if (records.Count == 0 || records.Count > MaxRecords) return null;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var r in records)
            {
                w.WriteStartArray();
                w.WriteStringValue(r.QueryId);
                w.WriteStringValue(r.Key);
                w.WriteStringValue(r.Before);
                if (r.After is null) w.WriteNullValue();
                else w.WriteStringValue(r.After);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        return ms.Length > MaxSerializedBytes ? null : Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Odczyt z metadanych snapshotu (dane niezaufane — walidacja kształtu i limitów; błąd = brak kontroli).</summary>
    public static bool TryParse(string? json, out IReadOnlyList<ControlRecord> records)
    {
        records = [];
        if (string.IsNullOrEmpty(json) || json.Length > MaxSerializedBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > MaxRecords) return false;
            var list = new List<ControlRecord>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 4) return false;
                var q = e[0]; var k = e[1]; var b = e[2]; var a = e[3];
                if (q.ValueKind != JsonValueKind.String || k.ValueKind != JsonValueKind.String || b.ValueKind != JsonValueKind.String
                    || a.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
                var qid = q.GetString()!;
                var key = k.GetString()!;
                if (!ControlQueryRunner.IsValidQueryId(qid) || key.Length is 0 or > ControlQueryRunner.MaxKeyLength + 5) return false;
                list.Add(new ControlRecord(qid, key, b.GetString()!, a.GetString()));
            }
            records = list;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Porównanie wartości kontrolnych po odtworzeniu z zapisanymi przy backupie (D5.11): tolerancje <c>range</c>/<c>exact</c>/<c>approx</c> z profilu, daty — maksimum ≥ wzorzec.</summary>
public static class ControlVerifier
{
    public static ControlCheckResult Compare(ErpProfile profile, IReadOnlyList<ControlRecord> records, IReadOnlyList<ControlValue> restored, IReadOnlyList<string>? skippedQueryIds = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var tolerance = profile.ControlQueries.ToDictionary(q => q.Id, q => q.Tolerance, StringComparer.Ordinal);
        var now = restored.GroupBy(v => (v.QueryId, v.Key)).ToDictionary(g => g.Key, g => g.First().Value);
        var skipped = new HashSet<string>(skippedQueryIds ?? [], StringComparer.Ordinal);
        int ok = 0, unchecked_ = 0;
        var bad = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (skipped.Contains(r.QueryId) || !tolerance.TryGetValue(r.QueryId, out var tol)) { unchecked_++; continue; }
            now.TryGetValue((r.QueryId, r.Key), out var restoredText);
            bool? match;
            if (r.Key.EndsWith(ControlQueryRunner.LastSuffix, StringComparison.Ordinal))
            {
                match = TryDate(r.Before, out var reference) ? ControlComparison.DateMatches(reference, TryDate(restoredText, out var d) ? d : null) : null;
            }
            else
            {
                match = CompareNumber(tol, r, restoredText);
            }
            if (match is null) unchecked_++;
            else if (match.Value) ok++;
            else { ok++; bad.Add(r.QueryId); }
        }
        return new ControlCheckResult(ok, unchecked_, bad.ToList());
    }

    private static bool? CompareNumber(string tolerance, ControlRecord r, string? restoredText)
    {
        if (!long.TryParse(r.Before, NumberStyles.Integer, CultureInfo.InvariantCulture, out var before)) return null;
        long after = before;
        var hasAfter = r.After is not null && long.TryParse(r.After, NumberStyles.Integer, CultureInfo.InvariantCulture, out after);
        var restored = 0L; // grupa, której po odtworzeniu nie ma = 0 wierszy
        if (restoredText is not null && !long.TryParse(restoredText, NumberStyles.Integer, CultureInfo.InvariantCulture, out restored)) return null;
        switch (tolerance)
        {
            case "range":
                return hasAfter ? ControlComparison.Matches("range", before, after, restored) : restored >= before; // bez pomiaru „po” brak górnej granicy
            case "exact":
                return hasAfter ? ControlComparison.Matches("exact", before, after, restored) : null; // „exact” wymaga obu pomiarów (baza nie zmieniała się w trakcie)
            case "approx":
                return ControlComparison.Matches("approx", before, before, restored);
            default:
                return null;
        }
    }

    private static bool TryDate(string? s, out DateTimeOffset? value)
    {
        value = null;
        if (s is null) return false;
        if (!DateTimeOffset.TryParseExact(s, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)) return false;
        value = d;
        return true;
    }
}
