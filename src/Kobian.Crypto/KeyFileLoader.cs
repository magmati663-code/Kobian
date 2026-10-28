using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kobian.Domain;

namespace Kobian.Crypto;

/// <summary>
/// Ładuje <see cref="RepoKeys"/> z opakowanego pliku klucza <c>keys/*.key</c> (D3.11): wymaga hasła (plik) i nagłówka <c>kobian.repo</c>.
/// <c>repoId</c> pochodzi wyłącznie z nagłówka (BL-24, it. 48: usunięto klucz deweloperski ADR-0002 i jawne <c>--repo-id</c> ADR-0003).
/// Po odblokowaniu MAC nagłówka jest weryfikowany kluczem <c>K_mac</c> (SEC-7.3), a <c>keyId</c> klucza musi być na uwierzytelnionej liście <c>body.keys</c>
/// (unieważnione klucze odpadają, D3.11/D8.3).
/// </summary>
public static class KeyFileLoader
{
    private const int MaxPasswordFileBytes = 1024;

    public static RepoKeys Load(string keyFilePath, string? passwordFilePath, string? repoHeaderPath)
    {
        // SEC-3.3: rozmiar sprawdzany PRZED wczytaniem (wcześniej limit działał dopiero w Unwrap, po ReadAllText dowolnie dużego pliku).
        if (new FileInfo(keyFilePath).Length > KeyFiles.MaxFileBytes) throw new KobianException(KbCode.KeyUnwrapFailed, ErrorClass.Security, "Invalid key file.");
        var text = File.ReadAllText(keyFilePath, Encoding.UTF8);
        string? keyIdHex = null;
        var wrapped = false;
        try
        {
            using var doc = JsonDocument.Parse(text.TrimStart('﻿'), new JsonDocumentOptions { MaxDepth = 8 });
            wrapped = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("wrapped", out _);
            if (wrapped && doc.RootElement.TryGetProperty("keyId", out var kid) && kid.ValueKind == JsonValueKind.String) keyIdHex = kid.GetString();
        }
        catch (JsonException)
        {
            // niepoprawny JSON: ta sama odpowiedź co dla nieznanego formatu
        }
        if (!wrapped) throw new KobianException(KbCode.UnsupportedFeature, ErrorClass.Permanent, "Invalid key file.");
        if (passwordFilePath is null) throw new KobianException(KbCode.KeyUnwrapFailed, ErrorClass.Security, "Password source is required for a wrapped key file.");
        if (repoHeaderPath is null) throw new KobianException(KbCode.KeyUnwrapFailed, ErrorClass.Security, "Repository header (kobian.repo) is required.");
        var headerJson = File.ReadAllText(repoHeaderPath, Encoding.UTF8);
        var repoId = RepoHeader.PeekRepoId(headerJson); // niezaufane do czasu Verify

        var pwText = ReadPasswordFile(passwordFilePath);
        var mk = KeyFiles.UnwrapWithPassword(text, pwText);
        try
        {
            var keys = RepoKeys.Derive(mk, repoId);
            try
            {
                var info = RepoHeader.Verify(headerJson, keys);
                if (keyIdHex is null || !info.KeyIds.Contains(keyIdHex.ToLowerInvariant(), StringComparer.Ordinal))
                    throw new KobianException(KbCode.KeyUnwrapFailed, ErrorClass.Security, "Key is not listed in the repository header.");
                return keys;
            }
            catch { keys.Dispose(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(mk); }
    }

    /// <summary>Pierwsza linia pliku (bez BOM, ≤ 1 KiB). Hasło jako string zostaje do GC (znane ograniczenie, Niskie).</summary>
    public static string ReadPasswordFile(string passwordFilePath)
    {
        var pwBytes = File.ReadAllBytes(passwordFilePath);
        try
        {
            if (pwBytes.Length > MaxPasswordFileBytes) throw new KobianException(KbCode.KeyUnwrapFailed, ErrorClass.Security, "Password file too large.");
            var span = pwBytes.AsSpan();
            if (span.StartsWith("﻿"u8)) span = span[3..];
            var nl = span.IndexOfAny((byte)'\r', (byte)'\n');
            return Encoding.UTF8.GetString(nl >= 0 ? span[..nl] : span);
        }
        finally { CryptographicOperations.ZeroMemory(pwBytes); }
    }
}
