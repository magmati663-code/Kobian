using System.Globalization;
using System.Text.Json;

namespace Kobian.Notifications.Events;

/// <summary>
/// Koperta CloudEvents 1.0, tryb strukturalny JSON (D9.6). Typy <c>pl.kobian.&lt;obszar&gt;.&lt;zdarzenie&gt;.v1</c>;
/// <c>data</c> bez nazw plików i sekretów (SEC-9.6); w trybie minimalnym bez <c>job.name</c>.
/// </summary>
public static class CloudEvent
{
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal)
    {
        "pl.kobian.run.finished.v1", "pl.kobian.alert.opened.v1", "pl.kobian.alert.reminder.v1", "pl.kobian.alert.resolved.v1",
        "pl.kobian.heartbeat.v1", "pl.kobian.report.ready.v1", "pl.kobian.audit.anchor.v1",
    };

    public const int MaxBytes = 64 * 1024;

    /// <param name="data">Pola danych; dozwolone wartości: string, long, int, bool, null, zagnieżdżone słowniki o tych samych typach.</param>
    public static byte[] Serialize(string id, string hostId, string type, DateTimeOffset time, string? subject, IReadOnlyDictionary<string, object?> data, bool minimal)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(hostId);
        ArgumentNullException.ThrowIfNull(data);
        if (!Types.Contains(type)) throw new ArgumentException("Unknown event type.", nameof(type));
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("specversion", "1.0");
            w.WriteString("id", id);
            w.WriteString("source", "urn:kobian:host:" + hostId);
            w.WriteString("type", type);
            w.WriteString("time", time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            w.WriteString("datacontenttype", "application/json");
            if (subject is not null) w.WriteString("subject", subject);
            w.WritePropertyName("data");
            WriteObject(w, data, minimal, depth: 0);
            w.WriteEndObject();
        }
        var bytes = ms.ToArray();
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("Event exceeds 64 KiB.");
        return bytes;
    }

    private static void WriteObject(Utf8JsonWriter w, IReadOnlyDictionary<string, object?> d, bool minimal, int depth)
    {
        if (depth > 4) throw new ArgumentException("Event data nested too deeply.");
        w.WriteStartObject();
        foreach (var (k, v) in d)
        {
            if (minimal && depth == 1 && k == "name") continue; // job.name w trybie minimalnym
            w.WritePropertyName(k);
            switch (v)
            {
                case null: w.WriteNullValue(); break;
                case string s: w.WriteStringValue(s); break;
                case long l: w.WriteNumberValue(l); break;
                case int i: w.WriteNumberValue(i); break;
                case bool b: w.WriteBooleanValue(b); break;
                case IReadOnlyDictionary<string, object?> nested: WriteObject(w, nested, minimal, depth + 1); break;
                default: throw new ArgumentException($"Unsupported data type for '{k}'.");
            }
        }
        w.WriteEndObject();
    }
}
