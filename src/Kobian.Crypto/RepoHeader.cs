using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Domain;
using Kobian.Domain.Serialization;

namespace Kobian.Crypto;

/// <summary>Zweryfikowana zawartość <c>kobian.repo</c> (D3.11).</summary>
public sealed record RepoHeaderInfo(byte[] RepoId, int FormatVersion, IReadOnlyList<string> KeyIds, string CreatedAt);

/// <summary>
/// <c>kobian.repo</c> (D3.11, V-09, SEC-7.3): <c>{ "body": {...}, "mac": hex(HMAC-SHA256(K_mac, UTF-8(JCS(body)))) }</c>.
/// Lista <c>keys</c> w treści objętej MAC jest jedynym źródłem prawdy o ważnych kluczach (unieważnienie = usunięcie z listy, D8.3).
/// Weryfikacja: rozmiar → struktura → MAC w czasie stałym → dopiero potem <c>features</c>/<c>minReaderVersion</c> (KB-1002),
/// bo przed MAC żadnemu polu nie wolno ufać. Każdy błąd uwierzytelnienia/struktury → <see cref="KbCode.RepoHeaderInvalid"/> (bez wyroczni).
/// </summary>
public static class RepoHeader
{
    public const string Magic = "kobian-repo";
    public const int FormatVersion = 1;
    public const int ReaderVersion = 1;
    private const int MaxBytes = 64 * 1024; // SEC-3.3
    private const int MaxKeys = 64;

    /// <summary>Tworzy plik nagłówka. Parametry chunkera/packa/kompresji = wartości domyślne D3.11.</summary>
    public static string Create(RepoKeys keys, DateTimeOffset createdAt, IReadOnlyList<byte[]> keyIds)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(keyIds);
        if (keyIds.Count is 0 or > MaxKeys) throw new ArgumentException("1..64 key ids required.", nameof(keyIds));
        var created = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var body = BuildBody(Convert.ToHexStringLower(keys.RepoId), created, keyIds.Select(k =>
        {
            if (k.Length != RepoKeys.IdSize) throw new ArgumentException("keyId must be 16 bytes.", nameof(keyIds));
            return Convert.ToHexStringLower(k);
        }).ToList());
        var mac = keys.HeaderMac(Jcs.Canonicalize(body));
        return "{\"body\":" + body + ",\"mac\":\"" + Convert.ToHexStringLower(mac) + "\"}";
    }

    /// <summary>
    /// Odczytuje <c>repoId</c> BEZ weryfikacji MAC (potrzebny, by wyprowadzić klucze). Wynik jest niezaufany do czasu <see cref="Verify"/>.
    /// </summary>
    public static byte[] PeekRepoId(string json)
    {
        try
        {
            using var doc = Parse(json);
            return Convert.FromHexString(doc.RootElement.GetProperty("body").GetProperty("repoId").GetString()!) is { Length: RepoKeys.IdSize } id
                ? id : throw Fail();
        }
        catch (Exception ex) when (IsParseError(ex)) { throw Fail(ex); }
    }

    /// <summary>Weryfikuje MAC i strukturę; zwraca zawartość dopiero po poprawnym MAC.</summary>
    public static RepoHeaderInfo Verify(string json, RepoKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        try
        {
            using var doc = Parse(json);
            var root = doc.RootElement;
            if (root.EnumerateObject().Count() != 2) throw Fail();
            var body = root.GetProperty("body");
            if (body.ValueKind != JsonValueKind.Object) throw Fail();
            var mac = Convert.FromHexString(root.GetProperty("mac").GetString()!);
            if (!keys.VerifyHeaderMac(Jcs.Canonicalize(body), mac)) throw Fail();

            // Od tego miejsca treść jest uwierzytelniona.
            if (body.GetProperty("magic").GetString() != Magic) throw Fail();
            var version = body.GetProperty("formatVersion").GetInt32();
            var minReader = body.GetProperty("minReaderVersion").GetInt32();
            var features = body.GetProperty("features");
            if (features.ValueKind != JsonValueKind.Array) throw Fail();
            if (version != FormatVersion || minReader > ReaderVersion || features.GetArrayLength() > 0)
                throw new KobianException(KbCode.UnsupportedFeature, ErrorClass.Permanent, "The repository requires a feature this version does not support.");
            var repoId = Convert.FromHexString(body.GetProperty("repoId").GetString()!);
            if (repoId.Length != RepoKeys.IdSize || !CryptographicOperations.FixedTimeEquals(repoId, keys.RepoId)) throw Fail();
            var keyArr = body.GetProperty("keys");
            if (keyArr.ValueKind != JsonValueKind.Array || keyArr.GetArrayLength() is 0 or > MaxKeys) throw Fail();
            var ids = new List<string>();
            foreach (var k in keyArr.EnumerateArray())
            {
                var s = k.GetString()!;
                if (Convert.FromHexString(s).Length != RepoKeys.IdSize || !ids.TrueAdd(s.ToLowerInvariant())) throw Fail();
            }
            return new RepoHeaderInfo(repoId, version, ids, body.GetProperty("createdAt").GetString()!);
        }
        catch (Exception ex) when (IsParseError(ex)) { throw Fail(ex); }
    }

    /// <summary>Czy klucz jest ważny wg uwierzytelnionej listy (po <see cref="Verify"/>); klucze spoza listy są ignorowane (D3.11).</summary>
    public static bool IsKeyListed(RepoHeaderInfo info, ReadOnlySpan<byte> keyId)
        => info.KeyIds.Contains(Convert.ToHexStringLower(keyId), StringComparer.Ordinal);

    private static JsonDocument Parse(string json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaxBytes) throw Fail();
        var doc = JsonDocument.Parse(json.TrimStart('﻿'), new JsonDocumentOptions { MaxDepth = 8 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) { doc.Dispose(); throw Fail(); }
        return doc;
    }

    private static string BuildBody(string repoId, string createdAt, IReadOnlyList<string> keyIds)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("magic", Magic);
            w.WriteNumber("formatVersion", FormatVersion);
            w.WriteString("repoId", repoId);
            w.WriteString("createdAt", createdAt);
            w.WriteStartObject("chunker");
            w.WriteString("alg", "fixed");
            w.WriteNumber("size", 4_194_304);
            w.WriteNumber("smallFileMax", 1_048_576);
            w.WriteEndObject();
            w.WriteStartObject("pack");
            w.WriteNumber("targetSize", 16_777_216);
            w.WriteEndObject();
            w.WriteStartObject("compression");
            w.WriteString("default", "zstd");
            w.WriteNumber("level", 3);
            w.WriteEndObject();
            w.WriteStartArray("keys");
            foreach (var k in keyIds) w.WriteStringValue(k);
            w.WriteEndArray();
            w.WriteNumber("minReaderVersion", ReaderVersion);
            w.WriteStartArray("features");
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool IsParseError(Exception ex)
        => ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or ArgumentException or OverflowException;

    private static bool TrueAdd(this List<string> list, string item)
    {
        if (list.Contains(item, StringComparer.Ordinal)) return false;
        list.Add(item);
        return true;
    }

    private static KobianException Fail(Exception? inner = null)
        => inner is null
            ? new KobianException(KbCode.RepoHeaderInvalid, ErrorClass.Security, "The repository header is invalid.")
            : new KobianException(KbCode.RepoHeaderInvalid, ErrorClass.Security, "The repository header is invalid.", inner);
}
