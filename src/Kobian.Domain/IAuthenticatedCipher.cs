namespace Kobian.Domain;

/// <summary>
/// Port szyfru AEAD (implementacja: <c>Kobian.Crypto.AesGcmCipher</c>). Umieszczony w Domain, aby
/// <c>Kobian.Format</c> (parsery) nie zależał od <c>Kobian.Crypto</c> — reguły referencji D2.7.
/// Szyfrogram = ciphertext ‖ tag (16 B).
/// </summary>
public interface IAuthenticatedCipher : IDisposable
{
    int TagSize { get; }

    void Seal(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData, Span<byte> ciphertextAndTag);

    /// <summary>Zwraca false, jeśli tag się nie zgadza (wyjście jest wtedy wyzerowane).</summary>
    bool TryOpen(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> associatedData, Span<byte> plaintext);
}
