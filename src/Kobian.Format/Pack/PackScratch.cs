namespace Kobian.Format.Pack;

/// <summary>
/// Bufory robocze <see cref="PackWriter"/> (jawny tekst ramki i ramka zaszyfrowana) współdzielone przez packi jednego przebiegu: rosną do największego bloku
/// i zostają. Nie jest bezpieczny wątkowo — jeden potok = jeden obiekt. Zawartość jawna jest zerowana przez PackWriter po każdym bloku.
/// </summary>
public sealed class PackScratch
{
    private byte[] _plain = [];
    private byte[] _sealed = [];

    public byte[] Plain(int length) => _plain.Length >= length ? _plain : _plain = new byte[length];

    public byte[] Sealed(int length) => _sealed.Length >= length ? _sealed : _sealed = new byte[length];
}
