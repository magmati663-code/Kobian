using System.Xml.Linq;
using Xunit;

namespace Kobian.Tests.Architecture;

/// <summary>
/// D2.7 / R2.2.1 (K22.1: 17 projektów produkcyjnych). Reguły sprawdzane statycznie na plikach .csproj,
/// bo projekty Windows (net10.0-windows) nie mogą być referowane przez test net10.0.
/// </summary>
public sealed class ReferenceRulesTests
{
    // Dozwolone BEZPOŚREDNIE referencje projektów (biała lista — tabela R2.2.1).
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["Domain"] = [],
        ["Format"] = ["Domain"],
        ["Crypto"] = ["Domain"],
        ["Application"] = ["Domain"],
        ["Engine"] = ["Domain", "Format", "Crypto", "Application"],
        ["Catalog.Sqlite"] = ["Application", "Domain"],
        ["Scheduler"] = ["Application", "Domain"],
        ["Providers.Storage"] = ["Application", "Domain"],
        ["Providers.Sources"] = ["Application", "Domain"],
        ["Platform.Windows"] = ["Application", "Domain"],
        ["Notifications"] = ["Application", "Domain"],
        ["Api.Contracts"] = [],
        ["Agent"] = ["Domain", "Format", "Crypto", "Application", "Engine", "Catalog.Sqlite", "Scheduler",
                     "Providers.Storage", "Providers.Sources", "Platform.Windows", "Notifications", "Api.Contracts"],
        ["Worker"] = ["Domain", "Application", "Platform.Windows"],
        ["Cli"] = ["Api.Contracts", "Domain"],
        ["UI"] = ["Api.Contracts", "Domain"],
        ["Restore.Standalone"] = ["Domain", "Format", "Crypto", "Engine", "Providers.Storage", "Platform.Windows"],
    };

    // Zakazy PRZECHODNIE (kolumna „Nie może referować”).
    private static readonly Dictionary<string, string[]> ForbiddenTransitive = new(StringComparer.Ordinal)
    {
        ["Engine"] = ["Catalog.Sqlite", "Agent", "UI", "Api.Contracts"],
        ["Cli"] = ["Engine", "Providers.Storage", "Providers.Sources", "Platform.Windows", "Catalog.Sqlite"],
        ["UI"] = ["Engine", "Providers.Storage", "Providers.Sources", "Platform.Windows", "Catalog.Sqlite"],
        ["Restore.Standalone"] = ["Agent", "Catalog.Sqlite", "Scheduler", "Notifications"],
        ["Agent"] = ["UI", "Cli"],
    };

    // Pakiety zakazane przechodnio w artefakcie DR (US-43).
    private static readonly string[] RestoreForbiddenPackagePrefixes = ["Microsoft.AspNetCore", "Microsoft.Data.Sqlite", "Grpc."];

    private static readonly string SrcDir = Path.Combine(RepoRoot.Find(), "src");

    private static Dictionary<string, XDocument> LoadProjects()
    {
        var result = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var dir in Directory.GetDirectories(SrcDir, "Kobian.*"))
        {
            var name = Path.GetFileName(dir)["Kobian.".Length..];
            var csproj = Path.Combine(dir, Path.GetFileName(dir) + ".csproj");
            Assert.True(File.Exists(csproj), $"brak {csproj}");
            result[name] = XDocument.Load(csproj);
        }
        return result;
    }

    private static string[] ProjectRefs(XDocument doc) =>
        doc.Descendants("ProjectReference")
           .Select(e => (string?)e.Attribute("Include") ?? "")
           .Select(p => Path.GetFileNameWithoutExtension(p.Replace('\\', '/')))
           .Select(n => n.StartsWith("Kobian.", StringComparison.Ordinal) ? n["Kobian.".Length..] : n)
           .ToArray();

    private static string[] PackageRefs(XDocument doc) =>
        doc.Descendants("PackageReference").Select(e => (string?)e.Attribute("Include") ?? "")
           .Concat(doc.Descendants("FrameworkReference").Select(e => (string?)e.Attribute("Include") ?? ""))
           .ToArray();

    private static HashSet<string> Closure(Dictionary<string, XDocument> all, string start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(ProjectRefs(all[start]));
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!seen.Add(p) || !all.TryGetValue(p, out var doc)) continue;
            foreach (var r in ProjectRefs(doc)) stack.Push(r);
        }
        return seen;
    }

    [Fact]
    public void Solution_has_exactly_the_17_production_projects_from_R2_2_1()
    {
        var names = LoadProjects().Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(Allowed.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(), names);
    }

    [Fact]
    public void Direct_project_references_follow_whitelist()
    {
        var all = LoadProjects();
        var violations = new List<string>();
        foreach (var (name, doc) in all)
            foreach (var r in ProjectRefs(doc))
                if (!Allowed[name].Contains(r, StringComparer.Ordinal))
                    violations.Add($"{name} -> {r}");
        Assert.Empty(violations);
    }

    [Fact]
    public void Forbidden_transitive_references_are_absent()
    {
        var all = LoadProjects();
        var violations = new List<string>();
        foreach (var (name, forbidden) in ForbiddenTransitive)
        {
            var closure = Closure(all, name);
            violations.AddRange(forbidden.Where(closure.Contains).Select(f => $"{name} ->* {f}"));
        }
        Assert.Empty(violations);
    }

    [Fact]
    public void Restore_standalone_has_no_aspnetcore_sqlite_or_grpc_packages_transitively()
    {
        var all = LoadProjects();
        var projects = Closure(all, "Restore.Standalone").Append("Restore.Standalone");
        var bad = projects.SelectMany(p => PackageRefs(all[p]).Select(pkg => (p, pkg)))
            .Where(x => RestoreForbiddenPackagePrefixes.Any(pre => x.pkg.StartsWith(pre, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.p}: {x.pkg}").ToArray();
        Assert.Empty(bad);
    }

    [Fact]
    public void No_project_reference_cycles()
    {
        var all = LoadProjects();
        foreach (var name in all.Keys)
            Assert.DoesNotContain(name, Closure(all, name));
    }
}
