using Kobian.Agent.Jobs;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

public sealed class MemoryTrimTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(MemoryTrim.LargeRunBytes - 1, false)]
    [InlineData(MemoryTrim.LargeRunBytes, true)]
    [InlineData(10L << 30, true)]
    public void Only_large_runs_trigger_trimming(long bytes, bool expected) => Assert.Equal(expected, MemoryTrim.ShouldTrim(bytes));

    [Fact]
    public void Trimming_is_rate_limited_and_shrinks_the_heap()
    {
        var junk = new byte[64 << 20]; // LOH
        junk[0] = 1;
        junk = null;
        var before = GC.GetTotalMemory(false);
        var t0 = TimeSpan.FromHours(1).Ticks; // dowolny punkt czasu większy niż okno
        Assert.True(MemoryTrim.TrimNow(t0));
        Assert.False(MemoryTrim.TrimNow(t0 + TimeSpan.FromSeconds(1).Ticks)); // za wcześnie
        Assert.True(MemoryTrim.TrimNow(t0 + TimeSpan.FromSeconds(6).Ticks));
        Assert.True(GC.GetTotalMemory(false) < before);
    }
}
