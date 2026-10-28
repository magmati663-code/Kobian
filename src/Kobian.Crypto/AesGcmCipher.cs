using System.Security.Cryptography;
using Kobian.Domain;

namespace Kobian.Crypto;

/// <summary>AES-256-GCM z tagiem 16 B (D3.4, D3.9). Klucz kopiowany i zerowany przy Dispose.</summary>
public sealed class AesGcmCipher : IAuthenticatedCipher
{
    private readonly AesGcm _aes;

    public AesGcmCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length != RepoKeys.KeySize) throw new ArgumentException("AES-256 key must be 32 bytes.", nameof(key));
        _aes = new AesGcm(key, Nonces.TagSize);
    }

    public int TagSize => Nonces.TagSize;

    public void Seal(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData, Span<byte> ciphertextAndTag)
    {
        if (ciphertextAndTag.Length != plaintext.Length + TagSize)
            throw new ArgumentException("Output must be plaintext length + 16.", nameof(ciphertextAndTag));
        _aes.Encrypt(nonce, plaintext, ciphertextAndTag[..plaintext.Length], ciphertextAndTag[plaintext.Length..], associatedData);
    }

    public bool TryOpen(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> associatedData, Span<byte> plaintext)
    {
        if (ciphertextAndTag.Length < TagSize || plaintext.Length != ciphertextAndTag.Length - TagSize)
            return false;
        try
        {
            _aes.Decrypt(nonce, ciphertextAndTag[..^TagSize], ciphertextAndTag[^TagSize..], plaintext, associatedData);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return false;
        }
    }

    public void Dispose() => _aes.Dispose();
}
