using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Client-side enforcement of the on-chain <c>Blocklist</c>: drops inbox
/// events whose sender the recipient has blocked. Nothing on-chain stops a
/// blocked sender from emitting events — honest clients simply do not
/// surface them. Shared by the CLI <c>inbox</c> command and the MCP
/// <c>inbox</c> tool.
/// </summary>
public static class InboxFilter
{
    /// <summary>Filter outcome: the kept events plus how many were hidden.</summary>
    public sealed record Result(IReadOnlyList<FileSentEvent> Kept, int Hidden);

    /// <summary>
    /// Return <paramref name="events"/> minus those from senders that
    /// <paramref name="me"/> has blocked. One <c>isBlocked</c> read per
    /// DISTINCT sender (not per event). When <paramref name="blocklist"/> is
    /// <c>null</c> (unconfigured), all events pass through unfiltered.
    /// </summary>
    public static async Task<Result> ApplyAsync(
        IBlocklistClient? blocklist,
        EthereumAddress me,
        IReadOnlyList<FileSentEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(events);

        if (blocklist is null || events.Count == 0)
            return new Result(events, 0);

        var blocked = new HashSet<EthereumAddress>();
        foreach (var sender in events.Select(e => e.From).Distinct())
        {
            if (await blocklist.IsBlockedAsync(me, sender, cancellationToken).ConfigureAwait(false))
                blocked.Add(sender);
        }

        if (blocked.Count == 0)
            return new Result(events, 0);

        var kept = events.Where(e => !blocked.Contains(e.From)).ToList();
        return new Result(kept, events.Count - kept.Count);
    }
}
