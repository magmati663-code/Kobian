using Kobian.Domain;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Kontrola poziomu poprawek .NET przy starcie Agenta (BL-62, it. 80): runtime i współdzielony ASP.NET Core (Kestrel Agenta) muszą być ≥ 10.0.13
/// (krytyczne CVE ASP.NET Core w 10.0.12 — biuletyn 2026-10-13). Niższy poziom NIE zatrzymuje kopii (utrata ochrony byłaby gorsza),
/// tylko podnosi stan Agenta do WARNING z KB-7013 (GetStatus, pulpit, `kobian status`) i zostawia zdarzenie w core_events. PROWIZORYCZNIE Qb37.
/// </summary>
public static class RuntimeGuard
{
    public static readonly Version MinimumRuntime = new(10, 0, 13);

    /// <summary>KB-7013 albo null. <paramref name="aspNetVersions"/> puste = brak współdzielonego ASP.NET Core (wdrożenie samodzielne) — oceniany sam runtime.</summary>
    public static string? Evaluate(Version runtime, IEnumerable<Version> aspNetVersions)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(aspNetVersions);
        if (runtime < MinimumRuntime) return KbCode.RuntimeUnpatched;
        var asp = aspNetVersions.Where(v => v.Major == runtime.Major).ToList();
        return asp.Count > 0 && asp.Max()! < MinimumRuntime ? KbCode.RuntimeUnpatched : null;
    }

    /// <summary>Wersje Microsoft.AspNetCore.App obok bieżącego runtime (układ katalogu dotnet: shared/&lt;framework&gt;/&lt;wersja&gt;).</summary>
    public static IReadOnlyList<Version> InstalledAspNetVersions()
    {
        var dir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var shared = dir is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(dir));
        if (shared is null) return [];
        var asp = Path.Combine(shared, "Microsoft.AspNetCore.App");
        if (!Directory.Exists(asp)) return [];
        return [.. Directory.GetDirectories(asp).Select(Path.GetFileName).Select(v => Version.TryParse(v, out var x) ? x : null).OfType<Version>()];
    }

    /// <summary>Ocena bieżącego procesu (Program.cs).</summary>
    public static string? EvaluateCurrent() => Evaluate(Environment.Version, InstalledAspNetVersions());
}
