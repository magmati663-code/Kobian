using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kobian.Crypto;

/// <summary>
/// Klucz odzyskiwania wg K14.1: 32 B klucza ‖ CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, big-endian) = 34 B
/// → 55 znaków Base32 (RFC 4648, bez dopełnienia) w 14 grupach (13 × 4 + 1 × 3). CRC wykrywa literówki i nie zmienia entropii.
/// </summary>
public static class RecoveryKey
{
    public const int KeyLength = 32;
    public const int EncodedLength = 55;
    public const int GroupCount = 14;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Koduje klucz do postaci grupowanej „ABCD-EFGH-…-XYZ”.</summary>
    public static string Encode(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLength) throw new ArgumentException("Recovery key must be 32 bytes.", nameof(key));
        Span<byte> buf = stackalloc byte[KeyLength + 2];
        key.CopyTo(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf[KeyLength..], Crc16Ccitt(key));
        var raw = Base32(buf);
        CryptographicOperations.ZeroMemory(buf);
        var sb = new StringBuilder(EncodedLength + GroupCount - 1);
        for (var i = 0; i < raw.Length; i += 4)
        {
            if (i > 0) sb.Append('-');
            sb.Append(raw, i, Math.Min(4, raw.Length - i));
        }
        return sb.ToString();
    }

    /// <summary>Grupy do wyświetlenia (14).</summary>
    public static string[] Groups(string encoded) => encoded.Split('-');

    /// <summary>
    /// Dekoduje tekst wpisany przez człowieka: ignoruje spacje i myślniki, wielkość liter; mapuje 0→O, 1→I
    /// (alfabet ich nie zawiera). Zwraca false przy złej długości, znaku spoza alfabetu lub niezgodnym CRC.
    /// </summary>
    public static bool TryDecode(string? text, Span<byte> key)
    {
        if (text is null || key.Length != KeyLength) return false;
        Span<char> chars = stackalloc char[EncodedLength];
        var n = 0;
        foreach (var c0 in text)
        {
            if (c0 is ' ' or '-' or '\t') continue;
            var c = char.ToUpperInvariant(c0) switch { '0' => 'O', '1' => 'I', var x => x };
            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0 || n >= EncodedLength) return false;
            chars[n++] = c;
        }
        if (n != EncodedLength) return false;
        Span<byte> buf = stackalloc byte[KeyLength + 2];
        ulong acc = 0;
        int bits = 0, o = 0;
        foreach (var c in chars)
        {
            acc = (acc << 5) | (uint)Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                buf[o++] = (byte)(acc >> bits);
            }
        }
        // 55 × 5 = 275 bitów = 34 B + 3 bity dopełnienia, które muszą być zerowe (kanoniczność).
        if (o != KeyLength + 2 || (acc & ((1UL << bits) - 1)) != 0) return false;
        var ok = BinaryPrimitives.ReadUInt16BigEndian(buf[KeyLength..]) == Crc16Ccitt(buf[..KeyLength]);
        if (ok) buf[..KeyLength].CopyTo(key);
        CryptographicOperations.ZeroMemory(buf);
        return ok;
    }

    /// <summary>Losuje 2 różne indeksy grup do potwierdzenia przepisania (AC-28.1).</summary>
    public static (int First, int Second) PickConfirmationGroups()
    {
        var a = RandomNumberGenerator.GetInt32(GroupCount);
        int b;
        do b = RandomNumberGenerator.GetInt32(GroupCount); while (b == a);
        return a < b ? (a, b) : (b, a);
    }

    /// <summary>Porównanie przepisanej grupy (bez rozróżniania wielkości liter, 0/O, 1/I) w czasie stałym.</summary>
    public static bool ConfirmGroup(string encoded, int groupIndex, string typed)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        var expected = Groups(encoded)[groupIndex];
        var norm = (typed ?? "").Trim().ToUpperInvariant().Replace('0', 'O').Replace('1', 'I');
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(norm));
    }

    internal static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    private static string Base32(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int acc = 0, bits = 0;
        foreach (var b in data)
        {
            acc = (acc << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(acc >> bits) & 31]);
            }
        }
        if (bits > 0) sb.Append(Alphabet[(acc << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
