using System.Reflection;
using System.Text.RegularExpressions;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit;

/// <summary>D1.8: rejestr docs/kb-codes.yaml i stałe KbCode muszą być zgodne; każdy wpis ma PL i EN.</summary>
public sealed partial class KbRegistryTests
{
    private static string RegistryPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kobian.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "docs", "kb-codes.yaml");
    }

    [GeneratedRegex(@"^- code: (KB-\d{4})\s*$", RegexOptions.Multiline)]
    private static partial Regex CodeLine();

    [Fact]
    public void KbCode_constants_match_registry_exactly()
    {
        var yaml = File.ReadAllText(RegistryPath());
        var registry = CodeLine().Matches(yaml).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(registry.Count, registry.Distinct(StringComparer.Ordinal).Count());
        var constants = typeof(KbCode).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(constants.Count, constants.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(registry.OrderBy(x => x, StringComparer.Ordinal), constants.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_registry_entry_has_class_and_both_languages()
    {
        var blocks = File.ReadAllText(RegistryPath()).Split("\n- code: ").Skip(1).ToArray();
        Assert.NotEmpty(blocks);
        foreach (var b in blocks)
        {
            Assert.Matches(@"\n  class: (transient|permanent|integrity|security)\s", b);
            Assert.Matches(@"\n  message\.pl: "".+""", b);
            Assert.Matches(@"\n  message\.en: "".+""", b);
            Assert.Matches(@"\n  action: "".+""", b);
            Assert.Matches(@"\n  action\.en: "".+""", b);
        }
    }
}
