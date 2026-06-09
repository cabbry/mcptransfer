using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Query helpers over <see cref="IFileRegistryClient"/> that degrade
/// gracefully on public RPC providers, which commonly cap the
/// <c>eth_getLogs</c> block span far below our default scan windows
/// (Amoy's public endpoint rejects even 10 000 blocks).
/// </summary>
public static class FileRegistryQueries
{
    /// <summary>Default corroboration scan window (blocks).</summary>
    public const ulong DefaultLookback = 50_000;

    /// <summary>
    /// Shrinking retry windows. Public-RPC caps vary wildly (1000, 500, 100,
    /// even less — Amoy's public endpoint rejected 450), so on failure each
    /// strictly-narrower window is tried in turn before giving up.
    /// </summary>
    public static readonly ulong[] FallbackSpans = { 450, 45, 9 };

    /// <summary>
    /// <see cref="IFileRegistryClient.FindByCidAsync"/>, but when a scan
    /// fails (typically an RPC block-range cap) it retries over each
    /// strictly-narrower <see cref="FallbackSpans"/> window before giving
    /// up. A recent announcement is still found; an old one needs an
    /// explicit <c>--since</c>/<c>since_block</c> from the caller.
    /// </summary>
    public static async Task<FileSentEvent?> FindByCidWithFallbackAsync(
        this IFileRegistryClient fileRegistry,
        EthereumAddress me,
        string cid,
        ulong fromBlock,
        ulong latestBlock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileRegistry);
        var span = latestBlock - fromBlock;
        Exception lastError;
        try
        {
            return await fileRegistry.FindByCidAsync(me, cid, fromBlock, latestBlock, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lastError = ex;
        }

        foreach (var fallbackSpan in FallbackSpans)
        {
            if (fallbackSpan >= span) continue;
            try
            {
                var narrowFrom = latestBlock > fallbackSpan ? latestBlock - fallbackSpan : 0UL;
                return await fileRegistry.FindByCidAsync(me, cid, narrowFrom, latestBlock, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                span = fallbackSpan;
            }
        }
        throw lastError;
    }

    /// <summary>
    /// <see cref="IFileRegistryClient.GetInboxAsync"/> with the same
    /// shrinking-window retries. Returns the events plus the range actually
    /// scanned so callers can tell the user when the window shrank.
    /// </summary>
    public static async Task<(IReadOnlyList<FileSentEvent> Events, ulong FromBlock)> GetInboxWithFallbackAsync(
        this IFileRegistryClient fileRegistry,
        EthereumAddress me,
        ulong fromBlock,
        ulong latestBlock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileRegistry);
        var span = latestBlock - fromBlock;
        Exception lastError;
        try
        {
            var events = await fileRegistry.GetInboxAsync(me, fromBlock, latestBlock, cancellationToken)
                .ConfigureAwait(false);
            return (events, fromBlock);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lastError = ex;
        }

        foreach (var fallbackSpan in FallbackSpans)
        {
            if (fallbackSpan >= span) continue;
            try
            {
                var narrowFrom = latestBlock > fallbackSpan ? latestBlock - fallbackSpan : 0UL;
                var events = await fileRegistry.GetInboxAsync(me, narrowFrom, latestBlock, cancellationToken)
                    .ConfigureAwait(false);
                return (events, narrowFrom);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                span = fallbackSpan;
            }
        }
        throw lastError;
    }
}
