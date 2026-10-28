using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Domain;
using Kobian.Domain.Serialization;

namespace Kobian.Crypto;

/// <summary>
/// Pliki kluczy <c>keys/&lt;keyId&gt;.key</c> (D3.11, BL-24): opakowanie klucza głównego <c>MK</c> przez KEK
/// (PBKDF2-HMAC-SHA256 z hasła albo klucz odzyskiwania 32 B bez KDF) algorytmem AES-256-GCM.
/// AAD opakowania = <c>UTF-8(JCS(obiekt bez "wrapped" i "revoked"))</c> — zmiana <c>iter</c>, <c>salt</c>, <c>type</c>
/// albo <c>keyId</c> uniemożliwia odszyfrowanie (SEC-13.2, V-04). Hasło: UTF-8 bez normalizacji (V-03).
/// Wszystkie błędy odblokowania → <see cref="KbCode.KeyUnwrapFailed"/> (bez rozróżniania przyczyn — brak wyroczni).
/// </summary>
public static class KeyFiles
{
    public const int Pbkdf2Iterations = 600_000;
    /// <summary>Dolny próg: plik z niższym <c>iter</c> jest odrzucany PRZED jakąkolwiek derywacją (SEC-13.2).</summary>
    public const int MinIterations = 600_000;
    /// <summary>Górny próg (DoS przez złośliwy plik klucza).</summary>
    public const int MaxIterations = 10_000_000;
    public const int SaltSize = 32;
    public const string TypePassword = "password";
    public const string TypeRecovery = "recovery";
    private const string KdfAlg = "pbkdf2-sha256";
    internal const int MaxFileBytes = 16 * 1024; // SEC-3.3: plik klucza jest mały

    public static string WrapWithPassword(ReadOnlySpan<byte> masterKey, string password, ReadOnlySpan<byte> keyId, DateTimeOffset createdAt)
        => WrapWithPassword(masterKey, password, keyId, createdAt, RandomNumberGenerator.GetBytes(SaltSize), RandomNumberGenerator.GetBytes(Nonces.Size));

    /// <summary>Wariant deterministyczny — wyłącznie do wektora V-03 (nie używać w kodzie produktu).</summary>
    internal static string WrapWithPassword(ReadOnlySpan<byte> masterKey, string password, ReadOnlySpan<byte> keyId, DateTimeOffset createdAt,
        ReadOnlySpan<byte> salt, ReadOnlySpan<byte> nonce)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (salt.Length != SaltSize) throw new ArgumentException("salt must be 32 bytes.", nameof(salt));
        var kek = DeriveKek(password, salt, Pbkdf2Iterations);
        try { return Wrap(masterKey, kek, keyId, createdAt, TypePassword, salt, nonce); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Opakowanie przez klucz odzyskiwania (32 B bezpośrednio jako KEK, D3.5/D3.11).</summary>
    public static string WrapWithRecoveryKey(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> recoveryKey, ReadOnlySpan<byte> keyId, DateTimeOffset createdAt)
    {
        if (recoveryKey.Length != RepoKeys.KeySize) throw new ArgumentException("Recovery key must be 32 bytes.", nameof(recoveryKey));
        return Wrap(masterKey, recoveryKey, keyId, createdAt, TypeRecovery, default, RandomNumberGenerator.GetBytes(Nonces.Size));
    }

    public static byte[] UnwrapWithPassword(string json, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return Unwrap(json, TypePassword, (salt, iter) => DeriveKek(password, salt, iter));
    }

    public static byte[] UnwrapWithRecoveryKey(string json, ReadOnlySpan<byte> recoveryKey)
    {
        if (recoveryKey.Length != RepoKeys.KeySize) throw new ArgumentException("Recovery key must be 32 bytes.", nameof(recoveryKey));
        var copy = recoveryKey.ToArray();
        try { return Unwrap(json, TypeRecovery, (_, _) => (byte[])copy.Clone()); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    /// <summary>
    /// Zwraca plik klucza z <c>revoked:true</c> (D8.3). AAD nie obejmuje <c>revoked</c>, więc opakowanie pozostaje spójne;
    /// <c>Unwrap*</c> odrzuca takie pliki. Prawdziwym unieważnieniem jest usunięcie keyId z uwierzytelnionej listy w <c>kobian.repo</c>.
    /// </summary>
    public static string MarkRevoked(string json)
    {
        try
        {
            if (json is null || Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw Fail();
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("wrapped", out _)) throw Fail();
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name == "revoked") continue;
                    p.WriteTo(w);
                }
                w.WriteBoolean("revoked", true);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw Fail(ex); }
    }

    private static byte[] DeriveKek(string password, ReadOnlySpan<byte> salt, int iterations)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(pw, salt, iterations, HashAlgorithmName.SHA256, RepoKeys.KeySize); }
        finally { CryptographicOperations.ZeroMemory(pw); }
    }

    private static string Wrap(ReadOnlySpan<byte> mk, ReadOnlySpan<byte> kek, ReadOnlySpan<byte> keyId, DateTimeOffset createdAt,
        string type, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> nonce)
    {
        if (mk.Length != RepoKeys.KeySize) throw new ArgumentException("MK must be 32 bytes.", nameof(mk));
        if (keyId.Length != RepoKeys.IdSize) throw new ArgumentException("keyId must be 16 bytes.", nameof(keyId));
        if (nonce.Length != Nonces.Size) throw new ArgumentException("nonce must be 12 bytes.", nameof(nonce));
        var created = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var head = BuildObject(Convert.ToHexStringLower(keyId), type, salt.IsEmpty ? null : salt.ToArray(), nonce.ToArray(), created, null);
        var aad = Jcs.Canonicalize(head);
        var sealedKey = new byte[mk.Length + Nonces.TagSize];
        using (var cipher = new AesGcmCipher(kek)) cipher.Seal(nonce, mk, aad, sealedKey);
        return BuildObject(Convert.ToHexStringLower(keyId), type, salt.IsEmpty ? null : salt.ToArray(), nonce.ToArray(), created, sealedKey);
    }

    /// <summary>Bez <paramref name="wrapped"/> zwraca obiekt do AAD; z nim — pełny plik (z <c>revoked:false</c>).</summary>
    private static string BuildObject(string keyId, string type, byte[]? salt, byte[] nonce, string createdAt, byte[]? wrapped)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("keyId", keyId);
            w.WriteString("type", type);
            if (salt is null) w.WriteNull("kdf");
            else
            {
                w.WriteStartObject("kdf");
                w.WriteString("alg", KdfAlg);
                w.WriteNumber("iter", Pbkdf2Iterations);
                w.WriteString("salt", Convert.ToBase64String(salt));
                w.WriteEndObject();
            }
            w.WriteString("nonce", Convert.ToBase64String(nonce));
            w.WriteString("createdAt", createdAt);
            if (wrapped is not null)
            {
                w.WriteString("wrapped", Convert.ToBase64String(wrapped));
                w.WriteBoolean("revoked", false);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static byte[] Unwrap(string json, string expectedType, Func<byte[], int, byte[]> kekFor)
    {
        try
        {
            if (json is null || Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw Fail();
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw Fail();
            if (r.GetProperty("type").GetString() != expectedType) throw Fail();
            if (r.TryGetProperty("revoked", out var rev) && rev.ValueKind != JsonValueKind.False) throw Fail();
            var nonce = Convert.FromBase64String(r.GetProperty("nonce").GetString()!);
            var wrapped = Convert.FromBase64String(r.GetProperty("wrapped").GetString()!);
            if (nonce.Length != Nonces.Size || wrapped.Length != RepoKeys.KeySize + Nonces.TagSize) throw Fail();

            byte[] salt = [];
            var iter = 0;
            var kdf = r.GetProperty("kdf");
            if (expectedType == TypePassword)
            {
                if (kdf.ValueKind != JsonValueKind.Object || kdf.GetProperty("alg").GetString() != KdfAlg) throw Fail();
                iter = kdf.GetProperty("iter").GetInt32();
                // Downgrade (SEC-13.2): parametr poza progami → odrzucenie zanim wykonamy jakąkolwiek derywację.
                if (iter < MinIterations || iter > MaxIterations) throw Fail();
                salt = Convert.FromBase64String(kdf.GetProperty("salt").GetString()!);
                if (salt.Length != SaltSize) throw Fail();
            }
            else if (kdf.ValueKind != JsonValueKind.Null) throw Fail();

            var aad = Jcs.Canonicalize(StripForAad(r));
            var kek = kekFor(salt, iter);
            try
            {
                var mk = new byte[RepoKeys.KeySize];
                using var cipher = new AesGcmCipher(kek);
                if (!cipher.TryOpen(nonce, wrapped, aad, mk)) throw Fail();
                return mk;
            }
            finally { CryptographicOperations.ZeroMemory(kek); }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            throw Fail(ex);
        }
    }

    private static string StripForAad(JsonElement root)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name is "wrapped" or "revoked") continue;
                p.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static KobianException Fail(Exception? inner = null)
        => inner is null
            ? new KobianException(KbCode.KeyUnwrapFailed, ErrorClass.Security, "Cannot unlock the repository key.")
            : new KobianException(KbCode.KeyUnwrapFailed, ErrorClass.Security, "Cannot unlock the repository key.", inner);
}
