using Kobian.Agent.Jobs;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

public sealed class MemoryTrimTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(MemoryTrim.HeapThresholdBytes - 1, false)]
    [InlineData(MemoryTrim.HeapThresholdBytes, true)]
    [InlineData(1L << 30, true)]
    public void Only_a_large_managed_heap_triggers_trimming(long heap, bool expected) => Assert.Equal(expected, MemoryTrim.ShouldTrim(heap));

    [Fact]
    public void Trimming_is_rate_limited_and_shrinks_the_heap()
    {
        var junk = new byte[64 << 20]; // LOH
        junk[0] = 1;
        junk = null;
        var before = GC.GetTotalMemory(false);
        var t0 = (Environment.TickCount64 * TimeSpan.TicksPerMillisecond) + TimeSpan.FromHours(10).Ticks; // za każdym znacznikiem ustawionym przez inne testy tego procesu (AfterRun)
        Assert.True(MemoryTrim.TrimNow(t0));
        Assert.False(MemoryTrim.TrimNow(t0 + TimeSpan.FromMilliseconds(100).Ticks)); // za wcześnie
        Assert.True(MemoryTrim.TrimNow(t0 + TimeSpan.FromSeconds(2).Ticks));
        Assert.True(GC.GetTotalMemory(false) < before);
    }
}
