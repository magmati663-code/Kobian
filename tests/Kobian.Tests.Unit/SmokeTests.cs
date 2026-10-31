using Xunit;

namespace Kobian.Tests.Unit;

public sealed class SmokeTests
{
    [Fact]
    public void Assembly_markers_are_named_after_projects()
    {
        Assert.Equal("Kobian.Domain", Kobian.Domain.AssemblyMarker.Name);
        Assert.Equal("Kobian.Format", Kobian.Format.AssemblyMarker.Name);
        Assert.Equal("Kobian.Crypto", Kobian.Crypto.AssemblyMarker.Name);
    }
}
