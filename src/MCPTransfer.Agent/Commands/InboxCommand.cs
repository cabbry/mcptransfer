namespace MCPTransfer.Agent.Commands;

internal static class InboxCommand
{
    public const string Usage =
        "  mcptx inbox [--since BLOCK] [--identity PATH] [--config PATH]\n"
      + "      List FileSent events addressed to this agent. Default range is\n"
      + "      the last 10 000 blocks (~6h on Amoy 2s blocks).";

    private const ulong DefaultLookbackBlocks = 10_000;

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var chain = Common.BuildChainClient(config);

        ulong fromBlock;
        ulong latest;
        try
        {
            latest = await chain.FileRegistry.GetLatestBlockNumberAsync(ct).ConfigureAwait(false);
            var sinceFlag = Common.GetFlagValue(args, "--since");
            fromBlock = sinceFlag is not null
                ? ParseBlock(sinceFlag)
                : (latest > DefaultLookbackBlocks ? latest - DefaultLookbackBlocks : 0UL);
        }
        catch (Exception ex)
        {
            return Common.Fail($"unable to read chain head: {ex.Message}");
        }

        Console.WriteLine($"Inbox for {identity.Address}");
        Console.WriteLine($"  scanning blocks {fromBlock} .. {latest} (latest)");
        Console.WriteLine();

        IReadOnlyList<MCPTransfer.Core.Chain.FileSentEvent> events;
        try
        {
            events = await chain.FileRegistry.GetInboxAsync(identity.Address, fromBlock, latest, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Common.Fail($"event query failed: {ex.Message}");
        }

        if (events.Count == 0)
        {
            Console.WriteLine("  (no events)");
            return Common.ExitSuccess;
        }

        // Per entry: index, block, timestamp, from, cid, and the on-chain
        // content hash (pass it to 'receive --expect-hash' to corroborate).
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var ts = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            Console.WriteLine($"  [{i}] block {e.BlockNumber}  {ts}");
            Console.WriteLine($"      from        : {e.From}");
            Console.WriteLine($"      cid         : {e.Cid}");
            Console.WriteLine($"      content hash: 0x{Convert.ToHexString(e.ContentHash).ToLowerInvariant()}");
        }
        Console.WriteLine();
        Console.WriteLine($"  {events.Count} event(s).");
        Console.WriteLine("  Decrypt one with:");
        Console.WriteLine("    mcptx receive <cid> --out PATH --expect-hash <content hash>");
        return Common.ExitSuccess;
    }

    private static ulong ParseBlock(string raw)
    {
        if (!ulong.TryParse(raw, out var v))
            throw new ArgumentException($"--since: invalid block number '{raw}' (expected a non-negative integer).");
        return v;
    }
}
