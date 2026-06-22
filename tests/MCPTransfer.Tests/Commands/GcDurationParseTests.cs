using MCPTransfer.Agent.Commands;

namespace MCPTransfer.Tests.Commands;

public class GcDurationParseTests
{
    [Theory]
    [InlineData("30d", 30 * 24 * 3600)]
    [InlineData("12h", 12 * 3600)]
    [InlineData("90m", 90 * 60)]
    [InlineData("3600s", 3600)]
    [InlineData("0d", 0)]
    public void ParsesValidDurations(string raw, long expectedSeconds)
    {
        Assert.True(GcCommand.TryParseDuration(raw, out var dur, out var error));
        Assert.Null(error);
        Assert.Equal(expectedSeconds, (long)dur.TotalSeconds);
    }

    [Theory]
    [InlineData("")]        // empty
    [InlineData("d")]       // no number
    [InlineData("30")]      // no unit
    [InlineData("30x")]     // unknown unit
    [InlineData("-5d")]     // negative
    [InlineData("abcd")]    // garbage
    public void RejectsInvalidDurations(string raw)
    {
        Assert.False(GcCommand.TryParseDuration(raw, out _, out var error));
        Assert.NotNull(error);
    }
}
