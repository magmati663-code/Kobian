using Kobian.Domain;

namespace Kobian.Application.Jobs;

/// <summary>Problem walidacji zwracany przez <c>UpsertJob(validate_only)</c> — jedno źródło reguł dla UI, CLI i importu JSON (D6.7).</summary>
public sealed record ValidationIssue(string Field, string KbCode, string MessageKey);

/// <summary>Polityka retencji (D3.7, D3.12). Okresy w dniach; RetentionLock — snapshoty młodsze są nienaruszalne.</summary>
public sealed record RetentionPolicy(
    int KeepLast = 0,
    int KeepDaily = 0,
    int KeepWeekly = 0,
    int KeepMonthly = 0,
    int KeepYearly = 0,
    TimeSpan KeepWithin = default,
    TimeSpan RetentionLock = default)
{
    public bool HasAnyRule => KeepLast > 0 || KeepDaily > 0 || KeepWeekly > 0 || KeepMonthly > 0 || KeepYearly > 0 || KeepWithin > TimeSpan.Zero;

    /// <summary>Najkrótszy okres zachowania wynikający z reguł czasowych (null, gdy tylko KeepLast).</summary>
    public TimeSpan? ShortestTimeRetention()
    {
        var spans = new List<TimeSpan>();
        // it. 86 (fuzzing gRPC): duże wartości z API przepełniały int (365 × n) i TimeSpan — liczenie w double z nasyceniem.
        static TimeSpan Days(double d) => d >= TimeSpan.MaxValue.TotalDays ? TimeSpan.MaxValue : TimeSpan.FromDays(d);
        if (KeepDaily > 0) spans.Add(Days(KeepDaily));
        if (KeepWeekly > 0) spans.Add(Days(7.0 * KeepWeekly));
        if (KeepMonthly > 0) spans.Add(Days(28.0 * KeepMonthly));
        if (KeepYearly > 0) spans.Add(Days(365.0 * KeepYearly));
        if (KeepWithin > TimeSpan.Zero) spans.Add(KeepWithin);
        return spans.Count == 0 ? null : spans.Min();
    }
}

/// <summary>Źródła lista ścieżek (Windows) — S1.</summary>
public static class JobValidator
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 256;

    /// <summary>S1: brak nakładania (rodzic/dziecko lub duplikat) w jednym zadaniu — KB-0102. Porównanie bez rozróżniania wielkości liter (NTFS).</summary>
    public static IReadOnlyList<ValidationIssue> ValidateSources(IReadOnlyList<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var issues = new List<ValidationIssue>();
        if (sources.Count == 0)
        {
            issues.Add(new ValidationIssue("sources", KbCode.SourcesNotReadable, "Wizard.Sources.Empty"));
            return issues;
        }
        var norm = sources.Select(Normalize).ToList();
        for (var i = 0; i < norm.Count; i++)
            for (var j = 0; j < norm.Count; j++)
                if (i != j && ((norm[i] == norm[j] && i < j) || IsAncestor(norm[i], norm[j])))
                    issues.Add(new ValidationIssue($"sources[{j}]", KbCode.SourcesOverlap, "Wizard.Sources.Overlap"));
        return issues;
    }

    /// <summary>S4: ≥ 12 znaków, ≤ 256, nie z listy popularnych, ≠ nazwa użytkownika/komputera (SEC-6.5; bez reguł złożoności — NIST SP 800-63B).</summary>
    public static IReadOnlyList<ValidationIssue> ValidatePassword(string password, string? userName, string? machineName, ICommonPasswordList common)
    {
        ArgumentNullException.ThrowIfNull(common);
        var weak = password is null
                   || password.Length < MinPasswordLength
                   || password.Length > MaxPasswordLength
                   || common.Contains(password)
                   || (!string.IsNullOrEmpty(userName) && password.Contains(userName, StringComparison.OrdinalIgnoreCase))
                   || (!string.IsNullOrEmpty(machineName) && password.Contains(machineName, StringComparison.OrdinalIgnoreCase))
                   || password.Distinct().Count() < 4;
        return weak ? [new ValidationIssue("password", KbCode.PasswordWeak, "Wizard.Password.Weak")] : [];
    }

    /// <summary>S5: co najmniej 1 reguła; retentionLock ≤ najkrótszy okres zachowania (KB-0401).</summary>
    public static IReadOnlyList<ValidationIssue> ValidateRetention(RetentionPolicy p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var issues = new List<ValidationIssue>();
        if (!p.HasAnyRule || p.KeepLast < 0 || p.KeepDaily < 0 || p.KeepWeekly < 0 || p.KeepMonthly < 0 || p.KeepYearly < 0 || p.RetentionLock < TimeSpan.Zero)
            issues.Add(new ValidationIssue("retention", KbCode.RetentionInvalid, "Wizard.Retention.Invalid"));
        var shortest = p.ShortestTimeRetention();
        if (p.RetentionLock > TimeSpan.Zero && (shortest is null || p.RetentionLock > shortest))
            issues.Add(new ValidationIssue("retention.retentionLock", KbCode.RetentionInvalid, "Wizard.Retention.LockTooLong"));
        return issues;
    }

    private static string Normalize(string p)
    {
        // „..”, „.” i prefiks \\?\ są sprowadzane do postaci kanonicznej — inaczej C:\Data i C:\Data\..\Data\sub nie były wykrywane jako nakładanie (KB-0102).
        // Poza Windows GetFullPath działa na separatorach '/', więc najpierw ujednolicamy do '\\' tylko dla postaci Windows.
        var s = p.Replace('/', '\\');
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) s = @"\\" + s[8..];
        else if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        s = CanonicalSegments(s);
        return s.TrimEnd('\\').ToUpperInvariant() + "\\";
    }

    /// <summary>Usuwa segmenty „.” i rozwiązuje „..” (bez dostępu do systemu plików; działa też dla ścieżek Windows sprawdzanych na Linuksie).</summary>
    private static string CanonicalSegments(string s)
    {
        var unc = s.StartsWith(@"\\", StringComparison.Ordinal);
        var parts = new List<string>();
        foreach (var seg in s.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == ".." && parts.Count > 1) parts.RemoveAt(parts.Count - 1);
            else if (seg != "..") parts.Add(seg);
        }
        return (unc ? @"\\" : "") + string.Join('\\', parts);
    }

    private static bool IsAncestor(string parent, string child) =>
        child.Length > parent.Length && child.StartsWith(parent, StringComparison.Ordinal);
}

/// <summary>Lista popularnych haseł (SEC-6.5). Źródło pełnej listy — po weryfikacji licencji (audyt zależności).</summary>
public interface ICommonPasswordList
{
    bool Contains(string password);
}

/// <summary>
/// Minimalna wbudowana lista (tymczasowa, własna — bez zewnętrznego źródła o niezweryfikowanej licencji).
/// Porównanie bez wielkości liter i po usunięciu cyfr/znaków na końcu („Haslo123456!” → „haslo”).
/// </summary>
public sealed class BuiltInCommonPasswords : ICommonPasswordList
{
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "haslo", "hasło", "qwerty", "qwertyuiop", "qwertyuiopas", "asdfghjkl", "zaq12wsx", "admin", "administrator",
        "letmein", "welcome", "witaj", "kobian", "backup", "kopia", "polska", "iloveyou", "monkey", "dragon", "abc", "abcdef",
        "abcdefghijkl", "zxcvbnm", "passw0rd", "p@ssw0rd", "football", "superman", "michal", "kasia", "marcin", "tomek",
    };

    public bool Contains(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var core = password.Trim().TrimEnd('!', '@', '#', '$', '%', '.', '?', '*').TrimEnd("0123456789".ToCharArray());
        var digitsOnly = password.All(char.IsAsciiDigit);
        return digitsOnly || Words.Contains(password) || Words.Contains(core);
    }
}
