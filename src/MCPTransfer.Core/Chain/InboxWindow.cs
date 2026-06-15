namespace MCPTransfer.Core.Chain;

/// <summary>
/// Computes the block window for an inbox scan, with the range guards shared by
/// the CLI <c>inbox</c> command and the MCP <c>inbox</c> tool (previously only
/// the MCP tool enforced them, so the CLI underflowed on an out-of-range
/// <c>--since</c>).
/// </summary>
public static class InboxWindow
{
    /// <summary>Default look-back when no explicit start block is given.</summary>
    public const ulong DefaultLookback = 10_000;

    /// <summary>
    /// Widest span we ask for up front; most public RPCs reject wider
    /// <c>eth_getLogs</c> ranges (the shrinking-window fallback narrows from
    /// here when even this is too wide).
    /// </summary>
    public const ulong MaxSpan = 50_000;

    /// <summary>
    /// Resolve the [from, latest] scan window. <paramref name="sinceBlock"/>
    /// overrides the default look-back. Throws
    /// <see cref="InvalidOperationException"/> if the start is past the chain
    /// head or the span exceeds <see cref="MaxSpan"/> — never underflows.
    /// </summary>
    public static (ulong From, ulong Latest) Compute(ulong latest, ulong? sinceBlock)
    {
        var from = sinceBlock ?? (latest > DefaultLookback ? latest - DefaultLookback : 0UL);

        if (from > latest)
        {
            throw new InvalidOperationException(
                $"since_block ({from}) is past the chain head ({latest}).");
        }
        if (latest - from > MaxSpan)
        {
            throw new InvalidOperationException(
                $"Requested block range {from}..{latest} ({latest - from} blocks) exceeds the "
                + $"{MaxSpan}-block limit most RPC providers enforce on eth_getLogs. Raise since_block "
                + "(e.g. to a recent block) or page through history in windows.");
        }
        return (from, latest);
    }
}
