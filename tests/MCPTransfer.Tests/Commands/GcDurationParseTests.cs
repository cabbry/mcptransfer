using MCPTransfer.Agent.Commands;

namespace MCPTransfer.Tests.Commands;

public class GcDurationParseTests
{
    [Theory]
    [InlineData("30d", 30 * 24 * 3600)]
    [InlineData("12h", 12 * 3600)]
    [InlineData("90m", 90 * 60)]
    [InlineData("3600s", 3600)]
    [InlineData("1s", 1)]
    public void ParsesValidDurations(string raw, long expectedSeconds)
    {
        Assert.True(GcCommand.TryParseDuration(raw, out var dur, out var error));
        Assert.Null(error);
        Assert.Equal(expectedSeconds, (long)dur.TotalSeconds);
    }

    [Theory]
    [InlineData("")]                 // empty
    [InlineData("d")]                // no number
    [InlineData("30")]               // no unit
    [InlineData("30x")]              // unknown unit
    [InlineData("-5d")]              // negative
    [InlineData("abcd")]             // garbage
    [InlineData("0d")]               // zero would select EVERY transfer, incl. in-flight
    [InlineData("0s")]               // zero (any unit)
    [InlineData("9999999999d")]      // overflows TimeSpan.FromDays
    [InlineData("99999999999999999s")] // overflows even in seconds
    [InlineData("40000d")]           // valid TimeSpan but exceeds the ~100y cap
    public void RejectsInvalidDurations(string raw)
    {
        Assert.False(GcCommand.TryParseDuration(raw, out _, out var error));
        Assert.NotNull(error);
    }
}
