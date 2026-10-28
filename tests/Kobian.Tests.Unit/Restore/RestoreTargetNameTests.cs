using Kobian.Engine.Restore;
using Xunit;

namespace Kobian.Tests.Unit.Restore;

/// <summary>D8.9: nazwy z repo są niezaufane — reguły NTFS (R1–R5) działają także w <see cref="RestoreTarget.TryChild(string, string, string, bool, out string)"/>, nie tylko w mapperze.</summary>
public sealed class RestoreTargetNameTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "kobian-name-test");

    [Theory, Trait("SEC", "D8.9"), Trait("R", "R1")]
    [InlineData("plik.txt:ads")]
    [InlineData("a:b")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("a<b")]
    [InlineData("koniec.")]
    [InlineData("koniec ")]
    public void Windows_rules_reject_streams_devices_and_forbidden_characters(string name) =>
        Assert.False(RestoreTarget.TryChild(Root, Root, name, windowsNames: true, out _));

    [Theory, Trait("SEC", "D8.9")]
    [InlineData("plik.txt")]
    [InlineData("Raport 2026 (1).xlsx")]
    [InlineData("console")]
    [InlineData("zażółć.txt")]
    public void Windows_rules_accept_ordinary_names(string name)
    {
        Assert.True(RestoreTarget.TryChild(Root, Root, name, windowsNames: true, out var target));
        Assert.Equal(Path.Combine(Root, name), target);
    }

    [Theory, Trait("SEC", "D8.9")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Traversal_is_rejected_regardless_of_platform_rules(string name) =>
        Assert.False(RestoreTarget.TryChild(Root, Root, name, windowsNames: false, out _));

    [Fact, Trait("SEC", "D8.9")]
    public void Without_windows_rules_a_colon_is_a_legal_name_on_other_platforms()
    {
        if (OperatingSystem.IsWindows()) return; // na Windows „:” jest zawsze odrzucane przez system plików
        Assert.True(RestoreTarget.TryChild(Root, Root, "a:b", windowsNames: false, out var target));
        Assert.Equal(Path.Combine(Root, "a:b"), target);
    }
}
