using Kobian.UI.Dashboard;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>6.3: model stanu ochrony jako czysta funkcja (tabela przypadków).</summary>
public sealed class ProtectionEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private static JobStatusInfo Ok(Func<JobStatusInfo, JobStatusInfo>? f = null)
    {
        var j = new JobStatusInfo("j", "Dokumenty", true, LastRunResult.Succeeded, Now.AddHours(-12), Now.AddHours(-12), Now.AddHours(12), Day,
            LastSuccessVerifyLevel: 2, DestinationName: "NAS", DestinationFreePercent: 60, DestinationDeleteProtected: true);
        return f is null ? j : f(j);
    }

    public static TheoryData<string, ProtectionState, ProtectionReason> Cases() => new()
    {
        { "ok", ProtectionState.Protected, ProtectionReason.Ok },
        { "disabled", ProtectionState.Paused, ProtectionReason.Disabled },
        { "failed", ProtectionState.AtRisk, ProtectionReason.LastRunFailed },
        { "overdue", ProtectionState.AtRisk, ProtectionReason.Overdue },
        { "within-tolerance", ProtectionState.Protected, ProtectionReason.Ok },
        { "damaged", ProtectionState.AtRisk, ProtectionReason.RepositoryDamaged },
        { "hostkey", ProtectionState.AtRisk, ProtectionReason.HostKeyMismatch },
        { "warning", ProtectionState.Warning, ProtectionReason.LastRunWarning },
        { "unverified", ProtectionState.Warning, ProtectionReason.NotVerified },
        { "lowspace", ProtectionState.Warning, ProtectionReason.LowSpace },
        { "unprotected-dest", ProtectionState.Warning, ProtectionReason.NotDeleteProtected },
        { "v3", ProtectionState.Warning, ProtectionReason.V3Overdue },
        { "new-scheduled", ProtectionState.Warning, ProtectionReason.NeverSucceeded },
        { "never-manual", ProtectionState.AtRisk, ProtectionReason.NeverSucceeded },
        { "damaged-beats-warning", ProtectionState.AtRisk, ProtectionReason.RepositoryDamaged },
    };

    [Theory, MemberData(nameof(Cases)), Trait("Plan", "6.3")]
    public void State_table(string name, ProtectionState state, ProtectionReason reason)
    {
        var job = name switch
        {
            "ok" => Ok(),
            "disabled" => Ok(j => j with { Enabled = false, LastResult = LastRunResult.Failed }),
            "failed" => Ok(j => j with { LastResult = LastRunResult.Failed }),
            "overdue" => Ok(j => j with { LastSuccessUtc = Now - Day - TimeSpan.FromHours(7) }),
            "within-tolerance" => Ok(j => j with { LastSuccessUtc = Now - Day - TimeSpan.FromHours(5) }),
            "damaged" => Ok(j => j with { RepositoryDamaged = true }),
            "hostkey" => Ok(j => j with { HostKeyMismatch = true }),
            "warning" => Ok(j => j with { LastResult = LastRunResult.Warning }),
            "unverified" => Ok(j => j with { LastSuccessVerifyLevel = 1 }),
            "lowspace" => Ok(j => j with { DestinationFreePercent = 14.9 }),
            "unprotected-dest" => Ok(j => j with { DestinationDeleteProtected = false }),
            "v3" => Ok(j => j with { V3Overdue = true }),
            "new-scheduled" => Ok(j => j with { LastResult = LastRunResult.None, LastSuccessUtc = null, LastRunUtc = null }),
            "never-manual" => Ok(j => j with { LastResult = LastRunResult.None, LastSuccessUtc = null, NextRunUtc = null }),
            "damaged-beats-warning" => Ok(j => j with { RepositoryDamaged = true, LastResult = LastRunResult.Warning, DestinationFreePercent = 3 }),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        Assert.Equal(new JobProtection(state, reason), ProtectionEvaluator.Evaluate(job, Now));
    }

    [Fact]
    public void Overall_is_worst_and_null_without_jobs()
    {
        Assert.Null(ProtectionEvaluator.Overall([]));
        Assert.Equal(ProtectionState.Warning, ProtectionEvaluator.Overall([new(ProtectionState.Protected, ProtectionReason.Ok), new(ProtectionState.Paused, ProtectionReason.Disabled), new(ProtectionState.Warning, ProtectionReason.LowSpace)]));
        Assert.Equal(ProtectionState.Paused, ProtectionEvaluator.Overall([new(ProtectionState.Protected, ProtectionReason.Ok), new(ProtectionState.Paused, ProtectionReason.Disabled)]));
    }

    [Fact]
    public void Tolerance_is_at_least_one_hour_and_a_quarter_of_the_cycle()
    {
        Assert.Equal(TimeSpan.FromHours(1), ProtectionEvaluator.Tolerance(TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.FromHours(6), ProtectionEvaluator.Tolerance(Day));
    }
}
