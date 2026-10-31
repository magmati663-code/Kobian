using Kobian.Agent.Jobs;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>BL-26: rotacja V3 wg tygodnia ISO i wybór „najgorszego” kodu raportu (czyste funkcje CatalogCheckService).</summary>
public sealed class CatalogCheckServiceTests
{
    [Fact, Trait("BL", "BL-26"), Trait("V", "V3")]
    public void Rotation_is_the_iso_week_and_advances_by_one_each_week_across_year_end()
    {
        var mon = new DateTimeOffset(2026, 12, 28, 10, 0, 0, TimeSpan.Zero); // tydzień ISO 53 roku 2026
        Assert.Equal((2026L * 53) + 53, CatalogCheckService.RotationFor(mon));
        Assert.Equal(CatalogCheckService.RotationFor(mon), CatalogCheckService.RotationFor(mon.AddDays(6))); // ta sama doba tygodnia → ta sama próbka
        var next = CatalogCheckService.RotationFor(mon.AddDays(7)); // 2027-W01
        Assert.Equal((2027L * 53) + 1, next);
        Assert.Equal(CatalogCheckService.RotationFor(mon) + 1, next);
    }

    private static CheckReport Report(params string[] codes) =>
        new(CheckStatus.Failed, 2, 1, 1, 1, 0, 0, [], [.. codes.Select(c => new CheckIssue(c, "pack:00", null))], false);

    [Fact, Trait("BL", "BL-26")]
    public void Worst_code_prefers_errors_over_orphan_warnings()
    {
        Assert.Null(CatalogCheckService.WorstCode(Report()));
        Assert.Equal(KbCode.CheckOrphanObject, CatalogCheckService.WorstCode(Report(KbCode.CheckOrphanObject)));
        Assert.Equal(KbCode.CheckPackMissing, CatalogCheckService.WorstCode(Report(KbCode.CheckOrphanObject, KbCode.CheckPackMissing, KbCode.AuthenticationFailed)));
    }
}
