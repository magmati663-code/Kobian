using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Domain;
using Kobian.Domain.Serialization;

namespace Kobian.Crypto;

/// <summary>Uwierzytelniona treść pliku blokady (D3.13). Czas w UTC z dokładnością do sekundy.</summary>
public sealed record RepoLockInfo(
    string LockId,
    string HostId,
    string InstallationId,
    bool Exclusive,
    DateTimeOffset CreatedAt,
    DateTimeOffset RenewedAt,
    int TtlSec);

/// <summary>
/// Plik <c>locks/&lt;lockId&gt;.lock</c> (D3.13): płaski obiekt JSON <c>{magic, lockId, hostId, installationId, kind, createdAt, renewedAt, ttlSec, mac}</c>,
/// <c>mac</c> = hex(HMAC-SHA256(K_mac, UTF-8(JCS(obiekt bez „mac”)))). Pole <c>magic</c> = „kobian-lock” rozdziela domenę MAC od nagłówka repo
/// (<c>kobian.repo</c> ma inny kształt i <c>magic</c> = „kobian-repo”), więc tego samego K_mac nie da się użyć do podstawienia jednego za drugie.
/// Weryfikacja: rozmiar → JSON bez duplikatów pól → MAC w czasie stałym → dopiero potem pola (SEC-3.3). Każdy błąd = <see cref="KbCode.RepoLockInvalid"/>.
/// </summary>
public static class RepoLockFile
{
    public const string Magic = "kobian-lock";
    public const int MaxBytes = 4096;
    public const int MinTtlSec = 60;
    public const int MaxTtlSec = 86_400;

    public static string Create(RepoKeys keys, RepoLockInfo info)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(info);
        RequireHex(info.LockId, 16, nameof(info));
        RequireHex(info.HostId, 16, nameof(info));
        RequireHex(info.InstallationId, 16, nameof(info));
        if (info.TtlSec is < MinTtlSec or > MaxTtlSec) throw new ArgumentOutOfRangeException(nameof(info), "ttlSec out of range.");
        var body = Body(info);
        var mac = keys.HeaderMac(Jcs.Canonicalize(body));
        return body[..^1] + ",\"mac\":\"" + Convert.ToHexStringLower(mac) + "\"}";
    }

    /// <summary>Weryfikuje plik; <paramref name="expectedLockId"/> = identyfikator z nazwy pliku (przeniesienie pliku pod inną nazwę jest odrzucane).</summary>
    public static RepoLockInfo Verify(ReadOnlySpan<byte> utf8, RepoKeys keys, string expectedLockId)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (utf8.Length is 0 or > MaxBytes) throw Fail();
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Fail();
            var names = new HashSet<string>(StringComparer.Ordinal);
            string? macHex = null;
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var p in root.EnumerateObject())
                {
                    if (!names.Add(p.Name)) throw Fail();
                    if (p.Name == "mac") { macHex = p.Value.GetString(); continue; }
                    p.WriteTo(w);
                }
                w.WriteEndObject();
            }
            if (macHex is null || macHex.Length != 64 || names.Count != 9) throw Fail();
            var mac = Convert.FromHexString(macHex);
            if (!keys.VerifyHeaderMac(Jcs.Canonicalize(Encoding.UTF8.GetString(ms.ToArray())), mac)) throw Fail();

            // Od tego miejsca treść jest uwierzytelniona.
            if (root.GetProperty("magic").GetString() != Magic) throw Fail();
            var lockId = root.GetProperty("lockId").GetString()!;
            if (!string.Equals(lockId, expectedLockId, StringComparison.Ordinal)) throw Fail();
            var kind = root.GetProperty("kind").GetString();
            if (kind is not ("shared" or "exclusive")) throw Fail();
            var ttl = root.GetProperty("ttlSec").GetInt32();
            if (ttl is < MinTtlSec or > MaxTtlSec) throw Fail();
            var info = new RepoLockInfo(lockId, root.GetProperty("hostId").GetString()!, root.GetProperty("installationId").GetString()!, kind == "exclusive",
                Time(root.GetProperty("createdAt").GetString()), Time(root.GetProperty("renewedAt").GetString()), ttl);
            RequireHex(info.LockId, 16, "lockId");
            RequireHex(info.HostId, 16, "hostId");
            RequireHex(info.InstallationId, 16, "installationId");
            return info;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw Fail(ex);
        }
    }

    private static string Body(RepoLockInfo i)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("magic", Magic);
            w.WriteString("lockId", i.LockId);
            w.WriteString("hostId", i.HostId);
            w.WriteString("installationId", i.InstallationId);
            w.WriteString("kind", i.Exclusive ? "exclusive" : "shared");
            w.WriteString("createdAt", Iso(i.CreatedAt));
            w.WriteString("renewedAt", Iso(i.RenewedAt));
            w.WriteNumber("ttlSec", i.TtlSec);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(string? s) =>
        DateTimeOffset.ParseExact(s ?? throw Fail(), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void RequireHex(string? s, int bytes, string name)
    {
        if (s is null || s.Length != bytes * 2 || !s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            throw new ArgumentException("Expected lowercase hex identifier.", name);
    }

    private static KobianException Fail() =>
        new(KbCode.RepoLockInvalid, ErrorClass.Security, "Repository lock file is not authentic or malformed.");

    private static KobianException Fail(Exception inner) =>
        new(KbCode.RepoLockInvalid, ErrorClass.Security, "Repository lock file is not authentic or malformed.", inner);
}
