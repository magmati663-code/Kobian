using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Crypto;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit.Crypto;

/// <summary>BL-24: pliki kluczy (D3.11) — wektory V-03/V-04 (ref_v1.py), downgrade KDF (SEC-13.2), klucz odzyskiwania.</summary>
public sealed class KeyFileTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "v1.json"))).RootElement;
    private static readonly byte[] Mk = Convert.FromHexString(Vectors.GetProperty("inputs").GetProperty("MK").GetString()!);

    private static string Str(string vec, string name) => Vectors.GetProperty(vec).GetProperty(name).GetString()!;

    private static string V03File()
    {
        var v = Vectors.GetProperty("V-03");
        return KeyFiles.WrapWithPassword(Mk, v.GetProperty("password").GetString()!, Convert.FromHexString(Str("V-03", "keyId")),
            DateTimeOffset.Parse(Str("V-03", "createdAt"), System.Globalization.CultureInfo.InvariantCulture),
            Convert.FromHexString(Str("V-03", "salt")), Convert.FromHexString(Str("V-03", "nonce")));
    }

    private static string Mutate(string file, Action<Dictionary<string, JsonElement>, Dictionary<string, JsonElement>> edit)
    {
        var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(file)!;
        var kdf = root["kdf"].ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(root["kdf"])! : new();
        edit(root, kdf);
        if (kdf.Count > 0) root["kdf"] = JsonSerializer.SerializeToElement(kdf);
        return JsonSerializer.Serialize(root);
    }

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private static void AssertUnwrapFails(Func<byte[]> act)
    {
        var ex = Assert.Throws<KobianException>(act);
        Assert.Equal(KbCode.KeyUnwrapFailed, ex.Code);
        Assert.Equal(ErrorClass.Security, ex.ErrorClass);
    }

    [Fact, Trait("V", "V-03"), Trait("BL", "BL-24")]
    public void V03_password_key_file_matches_reference_vector_and_unwraps()
    {
        var file = V03File();
        using var doc = JsonDocument.Parse(file);
        Assert.Equal(Str("V-03", "wrapped"), doc.RootElement.GetProperty("wrapped").GetString());
        Assert.False(doc.RootElement.GetProperty("revoked").GetBoolean());
        Assert.Equal(600000, doc.RootElement.GetProperty("kdf").GetProperty("iter").GetInt32());
        Assert.Equal(Mk, KeyFiles.UnwrapWithPassword(file, Vectors.GetProperty("V-03").GetProperty("password").GetString()!));
    }

    [Fact, Trait("V", "V-03"), Trait("BL", "BL-24")]
    public void V03_unwrap_accepts_the_reference_wrapped_blob_from_python()
    {
        // Plik zbudowany ręcznie z pól wektora (nie z kodu C#) — niezależna zgodność obu implementacji.
        var v = Vectors.GetProperty("V-03");
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["keyId"] = Str("V-03", "keyId"), ["type"] = "password",
            ["kdf"] = new Dictionary<string, object> { ["alg"] = "pbkdf2-sha256", ["iter"] = 600000, ["salt"] = Convert.ToBase64String(Convert.FromHexString(Str("V-03", "salt"))) },
            ["nonce"] = Convert.ToBase64String(Convert.FromHexString(Str("V-03", "nonce"))), ["wrapped"] = Str("V-03", "wrapped"),
            ["createdAt"] = Str("V-03", "createdAt"), ["revoked"] = false,
        });
        Assert.Equal(Mk, KeyFiles.UnwrapWithPassword(json, v.GetProperty("password").GetString()!));
    }

    [Fact, Trait("V", "V-04"), Trait("SEC", "SEC-13.2"), Trait("BL", "BL-24")]
    public void V04_lowered_iter_is_rejected_before_any_derivation()
    {
        var tampered = Mutate(V03File(), (_, kdf) => kdf["iter"] = J(1));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(tampered, "zażółć gęślą jaźń"));
    }

    [Theory, Trait("SEC", "SEC-13.2"), Trait("BL", "BL-24")]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(599_999)]
    [InlineData(10_000_001)]
    [InlineData(int.MaxValue)] // gdyby kod próbował derywacji, test by „zawisł” — odrzucenie jest przed PBKDF2
    public void Out_of_range_iter_is_rejected(int iter)
    {
        var tampered = Mutate(V03File(), (_, kdf) => kdf["iter"] = J(iter));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(tampered, "zażółć gęślą jaźń"));
    }

    [Fact, Trait("SEC", "SEC-13.2"), Trait("BL", "BL-24")]
    public void Raised_iter_inside_limits_fails_through_aad()
    {
        var tampered = Mutate(V03File(), (_, kdf) => kdf["iter"] = J(600_001));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(tampered, "zażółć gęślą jaźń"));
    }

    [Fact, Trait("SEC", "SEC-13.2"), Trait("BL", "BL-24")]
    public void Changed_salt_keyId_or_createdAt_fail_through_aad()
    {
        var pw = "zażółć gęślą jaźń";
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(Mutate(V03File(), (_, kdf) => kdf["salt"] = J(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))), pw));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(Mutate(V03File(), (r, _) => r["keyId"] = J(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)))), pw));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(Mutate(V03File(), (r, _) => r["createdAt"] = J("2001-01-01T00:00:00Z")), pw));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(Mutate(V03File(), (r, _) => r["extra"] = J("x")), pw)); // dodatkowe pole też wchodzi do AAD
    }

    [Fact, Trait("BL", "BL-24")]
    public void Wrong_password_wrong_type_revoked_and_garbage_all_give_the_same_error()
    {
        var file = V03File();
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(file, "zazolc gesla jazn"));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithRecoveryKey(file, RandomNumberGenerator.GetBytes(32)));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(Mutate(file, (r, _) => r["revoked"] = J(true)), "zażółć gęślą jaźń"));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(Mutate(file, (r, _) => r["wrapped"] = J("AAAA")), "zażółć gęślą jaźń"));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword("not json", "x"));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword("[]", "x"));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword("{}", "x"));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(new string(' ', 20_000), "x"));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(Mutate(file, (r, _) => r["kdf"] = J("x")), "x"));
    }

    [Fact, Trait("BL", "BL-24")]
    public void Password_roundtrip_uses_random_salt_and_nonce()
    {
        var mk = RandomNumberGenerator.GetBytes(32);
        var id = RandomNumberGenerator.GetBytes(16);
        var pw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
        var a = KeyFiles.WrapWithPassword(mk, pw, id, DateTimeOffset.UtcNow);
        var b = KeyFiles.WrapWithPassword(mk, pw, id, DateTimeOffset.UtcNow);
        Assert.NotEqual(a, b);
        Assert.Equal(mk, KeyFiles.UnwrapWithPassword(a, pw));
        Assert.Equal(mk, KeyFiles.UnwrapWithPassword(b, pw));
        Assert.DoesNotContain(pw, a, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(mk), a, StringComparison.Ordinal);
    }

    [Fact, Trait("BL", "BL-24")]
    public void Recovery_key_roundtrip_has_null_kdf_and_rejects_modified_type()
    {
        var mk = RandomNumberGenerator.GetBytes(32);
        var rk = RandomNumberGenerator.GetBytes(32);
        var file = KeyFiles.WrapWithRecoveryKey(mk, rk, RandomNumberGenerator.GetBytes(16), DateTimeOffset.UtcNow);
        using (var doc = JsonDocument.Parse(file))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("kdf").ValueKind);
            Assert.Equal("recovery", doc.RootElement.GetProperty("type").GetString());
        }
        Assert.Equal(mk, KeyFiles.UnwrapWithRecoveryKey(file, rk));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithRecoveryKey(file, RandomNumberGenerator.GetBytes(32)));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithRecoveryKey(Mutate(file, (r, _) => r["type"] = J("password")), rk));
        AssertUnwrapFails(() => KeyFiles.UnwrapWithPassword(file, "x"));
    }

    [Fact, Trait("BL", "BL-24")]
    public void Wrap_validates_argument_sizes_and_empty_password()
    {
        Assert.Throws<ArgumentException>(() => KeyFiles.WrapWithPassword(new byte[31], "x", new byte[16], DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => KeyFiles.WrapWithPassword(new byte[32], "", new byte[16], DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => KeyFiles.WrapWithPassword(new byte[32], "x", new byte[15], DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => KeyFiles.WrapWithRecoveryKey(new byte[32], new byte[31], new byte[16], DateTimeOffset.UtcNow));
    }

    [Fact, Trait("BL", "BL-24"), Trait("SEC", "SEC-3.3")]
    public void Loader_reads_password_file_without_newline_and_rejects_missing_inputs()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "kobian-kf-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
        Directory.CreateDirectory(tmp);
        try
        {
            var mk = RandomNumberGenerator.GetBytes(32);
            var repoId = RandomNumberGenerator.GetBytes(16);
            var keyId = RandomNumberGenerator.GetBytes(16);
            var pw = "zażółć-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            var kf = Path.Combine(tmp, "a.key");
            var pf = Path.Combine(tmp, "pw.txt");
            var hf = Path.Combine(tmp, "kobian.repo");
            using var direct = RepoKeys.Derive(mk, repoId);
            File.WriteAllText(kf, KeyFiles.WrapWithPassword(mk, pw, keyId, DateTimeOffset.UtcNow));
            File.WriteAllText(hf, RepoHeader.Create(direct, DateTimeOffset.UtcNow, [keyId]));
            File.WriteAllText(pf, pw + "\r\nignored second line\n");

            using var viaLoader = KeyFileLoader.Load(kf, pf, hf);
            Assert.Equal(direct.KData.ToArray(), viaLoader.KData.ToArray());

            AssertUnwrapFails(() => { KeyFileLoader.Load(kf, null, hf).Dispose(); return []; });
            AssertUnwrapFails(() => { KeyFileLoader.Load(kf, pf, null).Dispose(); return []; });
            File.WriteAllText(pf, "zle haslo");
            AssertUnwrapFails(() => { KeyFileLoader.Load(kf, pf, hf).Dispose(); return []; });
            File.WriteAllBytes(pf, new byte[2000]);
            AssertUnwrapFails(() => { KeyFileLoader.Load(kf, pf, hf).Dispose(); return []; });
            // klucz deweloperski (ADR-0002) nie jest już akceptowany
            File.WriteAllText(kf, "{\"format\":\"kobian-devkey-0\",\"repoId\":\"" + Convert.ToHexStringLower(repoId) + "\",\"masterKey\":\"" + Convert.ToHexStringLower(mk) + "\"}");
            Assert.Equal(KbCode.UnsupportedFeature, Assert.Throws<KobianException>(() => KeyFileLoader.Load(kf, pf, hf).Dispose()).Code);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }
}
