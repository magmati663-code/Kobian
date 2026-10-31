using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kobian.Domain.Serialization;

/// <summary>
/// Kanoniczny JSON (RFC 8785 JCS) dla podzbioru bez liczb zmiennoprzecinkowych (D3.11, D9.9):
/// klucze sortowane po jednostkach UTF-16, minimalne ucieczki, liczby całkowite w zakresie IEEE-754 (|n| ≤ 2^53).
/// Liczba z częścią ułamkową lub wykładnikiem → <see cref="FormatException"/> (świadome ograniczenie podzbioru).
/// </summary>
public static class Jcs
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    public static byte[] Canonicalize(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(element, sb, 0);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] Canonicalize(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        return Canonicalize(doc.RootElement);
    }

    private static void Write(JsonElement e, StringBuilder sb, int depth)
    {
        if (depth > 32) throw new FormatException("JSON too deep.");
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var props = e.EnumerateObject().ToList();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in props)
                    if (!names.Add(p.Name)) throw new FormatException("Duplicate property name.");
                var first = true;
                foreach (var p in props.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(p.Name, sb);
                    sb.Append(':');
                    Write(p.Value, sb, depth + 1);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var f = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!f) sb.Append(',');
                    f = false;
                    Write(item, sb, depth + 1);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(e.GetString()!, sb);
                break;
            case JsonValueKind.Number:
                if (!e.TryGetInt64(out var n) || n > MaxSafeInteger || n < -MaxSafeInteger)
                    throw new FormatException("Only safe integers are supported in canonical JSON subset.");
                sb.Append(n.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            default:
                throw new FormatException("Unsupported JSON value.");
        }
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
