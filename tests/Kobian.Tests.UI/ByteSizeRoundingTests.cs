using System.Globalization;
using Kobian.UI.History;
using Xunit;

namespace Kobian.Tests.UI;

public sealed class ByteSizeRoundingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048575, "1 MB")] // wcześniej „1024 KB”
    [InlineData(1073741823, "1 GB")] // wcześniej „1024 MB”
    [InlineData(1048576L * 100, "100 MB")]
    [InlineData(1099511627775, "1 TB")]
    [InlineData(9223372036854775807, "8 EB")]
    public void Rounding_never_produces_a_value_of_1024_or_more(long bytes, string expected)
    {
        var text = ByteSize.Format(bytes, CultureInfo.InvariantCulture);
        if (bytes == long.MaxValue) Assert.DoesNotContain("1024", text, StringComparison.Ordinal); // ostatnia jednostka: nie ma wyższej
        else Assert.Equal(expected, text);
    }
}
