namespace Kobian.Domain;

/// <summary>
/// Kontrola „ścieżka leży pod korzeniem” wspólna dla źródła, celu lokalnego i folderu odtwarzania. <c>Path.TrimEndingDirectorySeparator</c> NIE obcina korzenia
/// (<c>D:\</c>, <c>/</c>), więc prosty <c>root + separator</c> dawał <c>D:\\</c> i odrzucał każdą ścieżkę: kopia całego dysku, cel na pendrivie w korzeniu
/// albo odtworzenie do <c>D:\</c> kończyły się „Path escapes root”.
/// </summary>
public static class PathBoundary
{
    /// <summary>Prefiks katalogu: korzeń z dokładnie jednym separatorem na końcu.</summary>
    public static string Prefix(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    }

    /// <summary><paramref name="full"/> (po <c>Path.GetFullPath</c>) leży ściśle POD <paramref name="root"/> (nie jest nim samym). Porównanie porządkowe, jak dotąd.</summary>
    public static bool IsUnder(string root, string full) =>
        full.StartsWith(Prefix(root), StringComparison.Ordinal) && full.Length > Prefix(root).Length;
}
