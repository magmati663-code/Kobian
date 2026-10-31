using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>6.9 / R2.6.3: kontrast tokenów stanów i tekstu ≥ 4.5:1 (WCAG 1.4.3), ikona na dysku ≥ 3:1 (1.4.11) — w obu motywach.</summary>
public sealed class ThemeContrastTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public static TheoryData<string, string, string, double> Pairs()
    {
        var d = new TheoryData<string, string, string, double>();
        foreach (var t in new[] { "Light", "Dark" })
        {
            foreach (var s in new[] { "Protected", "Warning", "AtRisk", "Paused" })
            {
                d.Add(t, $"Kb.Color.{s}.Text", $"Kb.Color.{s}.Bg", 4.5);
                d.Add(t, $"Kb.Color.{s}.Text", "Kb.Color.Surface", 4.5);
                d.Add(t, $"Kb.Color.{s}.Solid", $"Kb.Color.{s}.Bg", 3.0);
            }
            d.Add(t, "Kb.Color.Text", "Kb.Color.Surface", 4.5);
            d.Add(t, "Kb.Color.Text", "Kb.Color.Bg", 4.5);
            d.Add(t, "Kb.Color.TextMuted", "Kb.Color.Surface", 4.5);
            d.Add(t, "Kb.Color.TextMuted", "Kb.Color.Bg", 4.5);
            d.Add(t, "Kb.Color.AccentText", "Kb.Color.Accent", 4.5);
            d.Add(t, "Kb.Color.Accent", "Kb.Color.Surface", 4.5);
            d.Add(t, "Kb.Color.NavText", "Kb.Color.NavBg", 4.5);
            d.Add(t, "Kb.Color.NavMuted", "Kb.Color.NavBg", 4.5);
            d.Add(t, "Kb.Color.NavText", "Kb.Color.NavSelected", 4.5);
        }
        return d;
    }

    [Theory, MemberData(nameof(Pairs)), Trait("NFR", "NFR-13")]
    public Task Contrast(string theme, string fg, string bg, double min) => UiSession.Session.Dispatch(() =>
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var ratio = Ratio(Get(fg, variant), Get(bg, variant));
        Assert.True(ratio >= min, $"{theme}: {fg} na {bg} = {ratio:F2}:1 < {min}:1");
    }, _ct);

    /// <summary>Biały glif ikony na dysku stanu (ProtectionBadge) ≥ 3:1.</summary>
    [Theory, InlineData("Light"), InlineData("Dark")]
    public Task Icon_glyph_on_solid(string theme) => UiSession.Session.Dispatch(() =>
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        foreach (var s in new[] { "Protected", "Warning", "AtRisk", "Paused" })
            Assert.True(Ratio(Colors.White, Get($"Kb.Color.{s}.Solid", variant)) >= 3.0, $"{theme} {s}");
    }, _ct);

    private static Color Get(string key, ThemeVariant v) =>
        Application.Current!.TryGetResource(key, v, out var r) && r is Color c ? c : throw new KeyNotFoundException(key);

    internal static double Ratio(Color a, Color b)
    {
        var (l1, l2) = (Lum(a), Lum(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static double Lum(Color c)
    {
        static double Ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Ch(c.R)) + (0.7152 * Ch(c.G)) + (0.0722 * Ch(c.B));
    }
}
