namespace Kobian.Domain;

/// <summary>Klasy błędów (4.7, D3.14): decydują o ponowieniach i stanie uruchomienia.</summary>
public enum ErrorClass
{
    /// <summary>Przejściowy — można ponowić (sieć, blokada).</summary>
    Transient = 1,
    /// <summary>Trwały — ponowienie nie pomoże bez działania użytkownika.</summary>
    Permanent = 2,
    /// <summary>Integralność — dane uszkodzone lub niekompletne.</summary>
    Integrity = 3,
    /// <summary>Bezpieczeństwo — podejrzenie manipulacji, błąd uwierzytelnienia.</summary>
    Security = 4,
}

/// <summary>
/// Wyjątek domenowy z kodem z rejestru <c>docs/kb-codes.yaml</c> (D1.8).
/// Komunikat nie może zawierać sekretów ani danych klienta (NFR-11, SEC-11.5).
/// </summary>
public sealed class KobianException : Exception
{
    public KobianException(string code, ErrorClass errorClass, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Code = code;
        ErrorClass = errorClass;
    }

    public KobianException(string code, ErrorClass errorClass, string message, Exception inner)
        : base(message, inner)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Code = code;
        ErrorClass = errorClass;
    }

    public KobianException()
        : this(KbCode.Internal, ErrorClass.Permanent, "Internal error")
    {
    }

    public KobianException(string message)
        : this(KbCode.Internal, ErrorClass.Permanent, message)
    {
    }

    public KobianException(string message, Exception innerException)
        : this(KbCode.Internal, ErrorClass.Permanent, message, innerException)
    {
    }

    /// <summary>Kod KB-xxxx.</summary>
    public string Code { get; }

    public ErrorClass ErrorClass { get; }

    public override string ToString() => $"{Code} [{ErrorClass}] {Message}";
}
