using System.Buffers.Binary;

namespace Kobian.Crypto;

/// <summary>Reguły nonce AES-256-GCM (D3.9).</summary>
public static class Nonces
{
    public const int Size = 12;
    public const int TagSize = 16;

    /// <summary>Nonce blobu i w packu: <c>00 00 00 00 ‖ u64be(i)</c>.</summary>
    public static void Blob(ulong index, Span<byte> destination)
    {
        if (destination.Length != Size) throw new ArgumentException("Nonce must be 12 bytes.", nameof(destination));
        // Wartość zarezerwowana dla trailera (FF…FF) nie może zostać wytworzona dla blobu: górne 4 B zawsze 0.
        destination[..4].Clear();
        BinaryPrimitives.WriteUInt64BigEndian(destination[4..], index);
    }

    /// <summary>Nonce trailera packa: 12 × 0xFF.</summary>
    public static void Trailer(Span<byte> destination)
    {
        if (destination.Length != Size) throw new ArgumentException("Nonce must be 12 bytes.", nameof(destination));
        destination.Fill(0xFF);
    }
}
