using Kobian.Domain;

namespace Kobian.Application.Storage;

/// <summary>
/// Ścieżki obiektów repo względem korzenia celu (D4.6 zasada 4): ASCII <c>[a-z0-9./-]</c>, 1–200 znaków,
/// separator „/”, bez „.”/„..”, pustych segmentów, ścieżek absolutnych. Nazwy repo nie niosą danych źródła (AC-06.1).
/// </summary>
public static class StoragePath
{
    public const int MaxLength = 200;

    public static bool IsValid(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength) return false;
        foreach (var c in path)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '/' or '-')) return false;
        }
        if (path[0] == '/' || path[^1] == '/') return false;
        foreach (var seg in path.Split('/'))
        {
            if (seg.Length == 0 || seg == "." || seg == "..") return false;
        }
        return true;
    }

    /// <summary>Prefiks listowania: pusty (korzeń) albo poprawna ścieżka katalogu.</summary>
    public static bool IsValidPrefix(string? prefix) => prefix is not null && (prefix.Length == 0 || IsValid(prefix));

    public static void Require(string? path)
    {
        if (!IsValid(path))
            throw new StorageException(ErrorClass.Permanent, KbCode.InvalidObjectName, "Invalid repository object path.");
    }
}
