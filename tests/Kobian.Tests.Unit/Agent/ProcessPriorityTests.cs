using System.Diagnostics;
using Kobian.Agent.Hosting;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

public sealed class ProcessPriorityTests
{
    [Theory]
    [InlineData(null, ProcessPriorityClass.BelowNormal)]
    [InlineData("", null)]
    [InlineData("belownormal", ProcessPriorityClass.BelowNormal)]
    [InlineData(" LOW ", ProcessPriorityClass.BelowNormal)]
    [InlineData("normal", ProcessPriorityClass.Normal)]
    [InlineData("idle", ProcessPriorityClass.Idle)]
    [InlineData("realtime", null)] // nigdy nie podnosimy priorytetu ponad normalny
    [InlineData("high", null)]
    public void Only_normal_or_lower_priorities_are_accepted(string? value, ProcessPriorityClass? expected) => Assert.Equal(expected, ProcessPriority.Parse(value));

    [Fact]
    public void Unknown_value_leaves_the_process_untouched()
    {
        using var p = Process.GetCurrentProcess();
        var before = p.PriorityClass;
        Assert.Null(ProcessPriority.Apply("realtime"));
        Assert.Equal(before, p.PriorityClass);
    }
}
