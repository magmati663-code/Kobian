using System.Text;
using Kobian.Domain;

namespace Kobian.Engine.Restore;

public enum MapOutcome
{
    Accepted,
    Renamed,
    Rejected,
}

/// <summary>Wynik mapowania (zawsze raportowany, gdy ≠ Accepted — „nic po cichu”, D8.9).</summary>
public sealed record MappedPath(MapOutcome Outcome, string? TargetPath, string? KbCode, IReadOnlyList<string> Notes);

/// <summary>
/// D8.9 <c>MapPath</c> — czysta część reguł R1–R6 i R9 dla celu Windows (ścieżki z „\”). R7 (FAT32) i R8 (punkty ponownej analizy)
/// wymagają systemu plików — port <see cref="IRestoreTargetProbe"/>; implementacja w Platform.Windows (CreateFile + GetFinalPathNameByHandle).
/// Nazwy nie są normalizowane Unicode (R2.3.5); konflikty porównywane OrdinalIgnoreCase.
/// </summary>
public sealed class WindowsPathMapper
{
    public const int MaxPathUtf16 = 32_767;
    public const int MaxComponentUtf16 = 255;
    private static readonly HashSet<string> Reserved = BuildReserved();
    private readonly string _targetRoot;
    // R9: per katalog docelowy (ścieżka zmapowana, OrdinalIgnoreCase) → nazwy już przydzielone.
    private readonly Dictionary<string, Dictionary<string, string>> _assigned = new(StringComparer.OrdinalIgnoreCase);

    public WindowsPathMapper(string targetRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetRoot);
        if (!(targetRoot.Length >= 3 && char.IsAsciiLetter(targetRoot[0]) && targetRoot[1] == ':' && targetRoot[2] == '\\') && !targetRoot.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Target root must be an absolute Windows path.", nameof(targetRoot));
        _targetRoot = targetRoot.TrimEnd('\\');
    }

    /// <summary>Mapuje ścieżkę względną z drzewa snapshotu (składniki w kolejności od korzenia).</summary>
    public MappedPath Map(IReadOnlyList<string> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Count == 0) return Reject(KbCode.RestoreNameInvalid, "empty path");
        var notes = new List<string>();
        var renamed = false;
        var current = _targetRoot;
        for (var i = 0; i < components.Count; i++)
        {
            var name = components[i];
            var r = MapComponent(name);
            if (r.Outcome == MapOutcome.Rejected) return r with { Notes = [.. notes, .. r.Notes] };
            var mapped = r.TargetPath!;
            if (r.Outcome == MapOutcome.Renamed)
            {
                renamed = true;
                notes.AddRange(r.Notes);
            }
            // R9: per katalog docelowy: nazwa (po R1–R5, porównanie Ordinal) → nazwa przydzielona; kolizja tylko wielkością liter → „ (2)”.
            if (!_assigned.TryGetValue(current, out var dir)) _assigned[current] = dir = new Dictionary<string, string>(StringComparer.Ordinal);
            if (dir.TryGetValue(mapped, out var already))
            {
                mapped = already;
            }
            else
            {
                var assigned = mapped;
                if (dir.Values.Any(v => string.Equals(v, assigned, StringComparison.OrdinalIgnoreCase)))
                {
                    assigned = Suffix(mapped, dir.Values);
                    notes.Add($"R9: '{mapped}' -> '{assigned}'");
                    renamed = true;
                }
                dir[mapped] = assigned;
                mapped = assigned;
            }
            if (mapped.Length > MaxComponentUtf16) return Reject(KbCode.RestorePathTooLong, "R6: component > 255");
            current = current + "\\" + mapped;
        }
        if (current.Length + 4 > MaxPathUtf16) return Reject(KbCode.RestorePathTooLong, "R6: path > 32767"); // + prefiks \\?\
        return new MappedPath(renamed ? MapOutcome.Renamed : MapOutcome.Accepted, current, null, notes);
    }

    /// <summary>Reguły R1–R5 dla jednego składnika.</summary>
    public static MappedPath MapComponent(string name)
    {
        if (string.IsNullOrEmpty(name)) return Reject(KbCode.RestoreNameInvalid, "R1: empty");
        foreach (var ch in name)
        {
            if (ch is '\\' or '/' or ':' or '\0' || ch < 0x20) return Reject(KbCode.RestoreNameInvalid, "R1: forbidden character");
        }
        if (name is "." or "..") return Reject(KbCode.RestoreNameInvalid, "R2: dot component");

        var notes = new List<string>();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(ch is '<' or '>' or '"' or '|' or '?' or '*' ? '_' : ch);
        }
        if (!sb.Equals(name)) notes.Add("R5: invalid Windows characters replaced");
        if (sb[^1] is '.' or ' ')
        {
            sb[^1] = '_';
            notes.Add("R4: trailing dot/space replaced");
        }
        var result = sb.ToString();
        if (IsReserved(result))
        {
            result = "_" + result;
            notes.Add("R3: reserved device name");
        }
        return new MappedPath(notes.Count == 0 ? MapOutcome.Accepted : MapOutcome.Renamed, result, null, notes);
    }

    public static bool IsReserved(string name)
    {
        // Nazwa bazowa przed pierwszą kropką, bez końcowych spacji (Windows ignoruje je przy rozpoznawaniu urządzeń).
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return Reserved.Contains(stem);
    }

    private static string Suffix(string name, IEnumerable<string> taken)
    {
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n.ToString(System.Globalization.CultureInfo.InvariantCulture)}){ext}";
            if (!taken.Any(t => string.Equals(t, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    private static MappedPath Reject(string code, string note) => new(MapOutcome.Rejected, null, code, [note]);

    private static HashSet<string> BuildReserved()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        foreach (var p in new[] { "COM", "LPT" })
        {
            for (var i = 1; i <= 9; i++) s.Add(p + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            s.Add(p + "\u00B9");
            s.Add(p + "\u00B2");
            s.Add(p + "\u00B3");
        }
        return s;
    }
}

/// <summary>Port do sprawdzeń R7/R8 na systemie plików celu (implementacja Windows w Platform.Windows).</summary>
public interface IRestoreTargetProbe
{
    /// <summary>Typ systemu plików wolumenu (np. „NTFS”, „FAT32”, „exFAT”).</summary>
    string FileSystemName(string path);

    /// <summary>Czy istniejący składnik ścieżki jest punktem ponownej analizy (R8).</summary>
    bool IsReparsePoint(string path);
}
