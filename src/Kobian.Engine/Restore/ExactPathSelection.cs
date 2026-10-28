using Kobian.Domain;
using Kobian.Format.Metadata;

namespace Kobian.Engine.Restore;

/// <summary>
/// Wybór dokładnych ścieżek odtwarzania (it. 80, Qb34): każda ścieżka = plik albo folder z całą zawartością, porównanie porządkowe, bez wzorców.
/// Wspólny dla wykonawcy (<see cref="RestoreExecutor"/>) i planera (<see cref="RestorePlanner"/>, it. 98) — ten sam wybór w podglądzie i w odtworzeniu.
/// </summary>
public sealed class ExactPathSelection
{
    public const int MaxPaths = 1000;

    private readonly HashSet<string> _paths;

    public ExactPathSelection(HashSet<string> normalized) => _paths = normalized ?? throw new ArgumentNullException(nameof(normalized));

    /// <summary>Dopasowane korzenie wyboru (ścieżka istnieje w kopii).</summary>
    public HashSet<string> Matched { get; } = new(StringComparer.Ordinal);

    /// <summary>Walidacja i normalizacja (KB-5003 dla pustej/za długiej listy i niepoprawnej ścieżki — SplitPath jak w przeglądarce).</summary>
    public static HashSet<string> Normalize(IReadOnlyCollection<string> exactPaths)
    {
        ArgumentNullException.ThrowIfNull(exactPaths);
        if (exactPaths.Count is 0 or > MaxPaths) throw new KobianException(KbCode.SnapshotPathNotFound, ErrorClass.Permanent, "Invalid restore path list.");
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in exactPaths)
        {
            var parts = SnapshotBrowser.SplitPath(p);
            if (parts.Count == 0) throw new KobianException(KbCode.SnapshotPathNotFound, ErrorClass.Permanent, "Invalid restore path.");
            normalized.Add(string.Join('/', parts));
        }
        return normalized;
    }

    /// <summary>Ścieżka pod wybraną (albo równa) — odtwarzana w całości; zapamiętuje dopasowany korzeń.</summary>
    public bool Covered(string rel)
    {
        for (var r = rel; r.Length > 0; r = r.LastIndexOf('/') is var i and >= 0 ? r[..i] : "")
        {
            if (_paths.Contains(r))
            {
                Matched.Add(r);
                return true;
            }
        }
        return false;
    }

    /// <summary>Folder leży NA drodze do wybranej ścieżki (trzeba wejść, ale nie odtwarzać całości).</summary>
    public bool OnPath(string rel) => _paths.Any(x => x.StartsWith(rel + "/", StringComparison.Ordinal));
}

/// <summary>Reguły folderu docelowego wspólne dla wykonawcy i planera (D8.9): korzeń istnieje i nie jest dowiązaniem; nazwa z kopii nie wychodzi poza korzeń.</summary>
public static class RestoreTarget
{
    /// <summary>Pełna ścieżka korzenia; brak → KB-5001, dowiązanie → KB-5104.</summary>
    public static string OpenRoot(string targetRoot)
    {
        ArgumentNullException.ThrowIfNull(targetRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot));
        var info = new DirectoryInfo(root);
        if (!info.Exists) throw new KobianException(KbCode.RestoreTargetAccessDenied, ErrorClass.Permanent, "Restore target does not exist.");
        if (info.LinkTarget is not null) throw new KobianException(KbCode.RestoreReparseRefused, ErrorClass.Security, "Restore target is a link.");
        return root;
    }

    /// <summary>Nazwa z repo jest niezaufana: walidacja segmentu i kontrola, że cel leży pod korzeniem i ma dokładnie tę nazwę (inaczej KB-5101).</summary>
    public static bool TryChild(string root, string dir, string name, out string target) =>
        TryChild(root, dir, name, OperatingSystem.IsWindows(), out target);

    /// <summary>
    /// Jak wyżej; <paramref name="windowsNames"/> = true dodaje reguły nazw NTFS z <see cref="WindowsPathMapper"/> (R1–R5): odrzuca „:” (strumień ADS w istniejącym pliku),
    /// nazwy urządzeń (CON, NUL, COM1…), znaki zabronione i końcową kropkę/spację. Nazwy z prawdziwego NTFS ich nie zawierają, więc odrzucenie nie gubi danych;
    /// element trafia do raportu (KB-5101), nic nie jest zmieniane po cichu (D8.9).
    /// </summary>
    public static bool TryChild(string root, string dir, string name, bool windowsNames, out string target)
    {
        target = "";
        try
        {
            TreeCodec.ValidateName(name);
            if (windowsNames && WindowsPathMapper.MapComponent(name).Outcome != MapOutcome.Accepted) return false;
            var full = Path.GetFullPath(Path.Join(dir, name));
            if (!PathBoundary.IsUnder(root, full) || Path.GetFileName(full) != name) return false;
            target = full;
            return true;
        }
        catch (KobianException)
        {
            return false;
        }
    }
}
