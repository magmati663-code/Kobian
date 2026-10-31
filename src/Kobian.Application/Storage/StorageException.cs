using Kobian.Domain;

namespace Kobian.Application.Storage;

/// <summary>Jedyny typ wyjątku zgłaszany przez dostawców magazynu (D4.6 zasada 2, mapa D4.8).</summary>
public sealed class StorageException : Exception
{
    public StorageException(ErrorClass errorClass, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorClass = errorClass;
        Code = code;
    }

    public StorageException()
        : this(ErrorClass.Permanent, KbCode.Internal, "Storage error")
    {
    }

    public StorageException(string message)
        : this(ErrorClass.Permanent, KbCode.Internal, message)
    {
    }

    public StorageException(string message, Exception innerException)
        : this(ErrorClass.Permanent, KbCode.Internal, message, innerException)
    {
    }

    public ErrorClass ErrorClass { get; }

    public string Code { get; }

    public bool IsTransient => ErrorClass == ErrorClass.Transient;

    /// <summary>Tożsamość przedstawiona przez serwer przy odmowie pinningu (KB-2104) — do pokazania użytkownikowi w ProbeDestination (D4.11). Publiczny odcisk, nie sekret.</summary>
    public ServerIdentity? PresentedIdentity { get; init; }
}
