using MCPTransfer.Core.Chain;

namespace MCPTransfer.Tests.Chain;

public class InboxWindowTests
{
    [Fact]
    public void Compute_DefaultLookback_WhenNoSince()
    {
        var (from, latest) = InboxWindow.Compute(100_000, sinceBlock: null);
        Assert.Equal(100_000UL, latest);
        Assert.Equal(100_000UL - InboxWindow.DefaultLookback, from);
    }

    [Fact]
    public void Compute_ClampsToZero_OnYoungChain()
    {
        var (from, _) = InboxWindow.Compute(500, sinceBlock: null);
        Assert.Equal(0UL, from);
    }

    [Fact]
    public void Compute_HonorsSinceWithinRange()
    {
        var (from, _) = InboxWindow.Compute(100_000, sinceBlock: 99_990);
        Assert.Equal(99_990UL, from);
    }

    [Fact]
    public void Compute_SincePastHead_ThrowsCleanly_NoUnderflow()
    {
        // Regression: previously the CLI computed `latest - fromBlock` unsigned
        // and underflowed to a ~1.8e19 garbage span instead of erroring.
        var ex = Assert.Throws<InvalidOperationException>(
            () => InboxWindow.Compute(5_000, sinceBlock: 999_999_999));
        Assert.Contains("past the chain head", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_SpanWiderThanMax_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => InboxWindow.Compute(1_000_000, sinceBlock: 0));
        Assert.Contains("exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_ExactlyMaxSpan_Allowed()
    {
        var (from, latest) = InboxWindow.Compute(InboxWindow.MaxSpan, sinceBlock: 0);
        Assert.Equal(0UL, from);
        Assert.Equal(InboxWindow.MaxSpan, latest);
    }
}
