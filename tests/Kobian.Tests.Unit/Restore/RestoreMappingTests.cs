using Kobian.Domain;
using Kobian.Engine.Restore;
using Xunit;

namespace Kobian.Tests.Unit.Restore;

/// <summary>D8.9 reguły R1–R6, R9 (R7/R8 — wymagają Windows: kolejka W-5).</summary>
public sealed class RestoreMappingTests
{
    private static MappedPath Map(params string[] c) => new WindowsPathMapper(@"C:\Odtworzone").Map(c);

    [Theory, Trait("SEC", "D8.9"), Trait("R", "R1")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("plik.txt:ads")]
    [InlineData("a\u0001b")]
    [InlineData("a\0")]
    public void R1_forbidden_characters_are_rejected(string name)
    {
        var r = Map("Dane", name);
        Assert.Equal(MapOutcome.Rejected, r.Outcome);
        Assert.Equal(KbCode.RestoreNameInvalid, r.KbCode);
    }

    [Theory, Trait("R", "R2")]
    [InlineData("..")]
    [InlineData(".")]
    public void R2_dot_components_are_rejected(string name) =>
        Assert.Equal(MapOutcome.Rejected, Map(name, "x.txt").Outcome);

    [Theory, Trait("R", "R3")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("com1", "_com1")]
    [InlineData("LPT9.log", "_LPT9.log")]
    [InlineData("COM\u00B9", "_COM\u00B9")]
    [InlineData("CON .txt", "_CON .txt")]
    [InlineData("console", "console")]
    [InlineData("COM10", "COM10")]
    public void R3_reserved_names_are_prefixed(string name, string expected) =>
        Assert.Equal(expected, WindowsPathMapper.MapComponent(name).TargetPath);

    [Theory, Trait("R", "R4"), Trait("R", "R5")]
    [InlineData("raport.", "raport_")]
    [InlineData("raport ", "raport_")]
    [InlineData("a<b>c\"d|e?f*g", "a_b_c_d_e_f_g")]
    [InlineData("Zażółć gęślą jaźń 📁.txt", "Zażółć gęślą jaźń 📁.txt")]
    public void R4_R5_replacements(string name, string expected) =>
        Assert.Equal(expected, WindowsPathMapper.MapComponent(name).TargetPath);

    [Fact]
    public void Renamed_paths_are_reported_never_silent()
    {
        var r = Map("Dane", "nul.txt");
        Assert.Equal(MapOutcome.Renamed, r.Outcome);
        Assert.Equal(@"C:\Odtworzone\Dane\_nul.txt", r.TargetPath);
        Assert.NotEmpty(r.Notes);
    }

    [Fact, Trait("R", "R6")]
    public void R6_too_long_component_or_path_rejected()
    {
        Assert.Equal(KbCode.RestorePathTooLong, Map(new string('a', 256)).KbCode);
        var deep = Enumerable.Repeat(new string('d', 200), 170).ToArray();
        Assert.Equal(KbCode.RestorePathTooLong, Map(deep).KbCode);
    }

    [Fact, Trait("AC", "AC-08.7"), Trait("R", "R9")]
    public void R9_case_collisions_get_suffix_and_same_dir_is_reused()
    {
        var m = new WindowsPathMapper(@"D:\R");
        Assert.Equal(@"D:\R\Dane\Plik.txt", m.Map(["Dane", "Plik.txt"]).TargetPath);
        var second = m.Map(["Dane", "plik.txt"]);
        Assert.Equal(MapOutcome.Renamed, second.Outcome);
        Assert.Equal(@"D:\R\Dane\plik (2).txt", second.TargetPath);
        Assert.Equal(@"D:\R\Dane\Inny.txt", m.Map(["Dane", "Inny.txt"]).TargetPath);
        // katalog o nazwie różniącej się wielkością liter to osobny katalog z sufiksem
        Assert.Equal(@"D:\R\dane (2)\x", m.Map(["dane", "x"]).TargetPath);
        Assert.Equal(@"D:\R\dane (2)\y", m.Map(["dane", "y"]).TargetPath);
    }

    [Fact]
    public void Relative_target_root_is_refused() =>
        Assert.Throws<ArgumentException>(() => new WindowsPathMapper(@"Odtworzone"));
}

public sealed class RestoreRangePlannerTests
{
    [Fact]
    public void Adjacent_and_near_frames_are_merged_far_ones_split()
    {
        var frames = new[] { new FrameRef(2, 2000, 100), new FrameRef(0, 64, 100), new FrameRef(1, 164, 100), new FrameRef(3, 5_000_000, 50) };
        var r = RestoreRangePlanner.MergeAdjacent(frames);
        Assert.Equal(2, r.Count);
        Assert.Equal(64, r[0].Offset);
        Assert.Equal(2100 - 64, r[0].Length);
        Assert.Equal([0, 1, 2], r[0].FrameIndexes);
        Assert.Equal([3], r[1].FrameIndexes);
    }

    [Fact]
    public void Overlapping_frames_are_rejected() =>
        Assert.Throws<ArgumentException>(() => RestoreRangePlanner.MergeAdjacent([new FrameRef(0, 0, 100), new FrameRef(1, 50, 100)]));

    [Fact]
    public void Without_range_read_densest_packs_go_first()
    {
        var need = new Dictionary<string, IReadOnlyList<FrameRef>>
        {
            ["aa"] = [new FrameRef(0, 64, 10)],
            ["bb"] = [new FrameRef(0, 64, 10), new FrameRef(1, 78, 10), new FrameRef(2, 92, 10)],
        };
        var steps = RestoreRangePlanner.OrderPacks(need, rangeRead: false);
        Assert.Equal(["bb", "aa"], steps.Select(s => s.PackId));
        Assert.Equal(-1, steps[0].Ranges.Single().Length);
        Assert.Equal(["aa", "bb"], RestoreRangePlanner.OrderPacks(need, rangeRead: true).Select(s => s.PackId));
    }
}
