using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Agent.Commands;

internal static class InboxCommand
{
    public const string Usage =
        "  mcptx inbox [--since BLOCK] [--identity PATH] [--config PATH]\n"
      + "      List FileSent events addressed to this agent. Default range is\n"
      + "      the last 10 000 blocks (~6h on Amoy 2s blocks).";

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
            ulong? since = sinceFlag is not null ? ParseBlock(sinceFlag) : null;
            // Shared window logic: guards against a --since past the chain head
            // (no unsigned underflow) and the over-wide span the RPC would reject.
            (fromBlock, latest) = InboxWindow.Compute(latest, since);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Common.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return Common.Fail($"unable to read chain head: {ex.Message}");
        }

        Console.WriteLine($"Inbox for {identity.Address}");
        Console.WriteLine($"  scanning blocks {fromBlock} .. {latest} (latest)");
        Console.WriteLine();

        IReadOnlyList<MCPTransfer.Core.Chain.FileSentEvent> events;
        var hiddenCount = 0;
        try
        {
            // Falls back to a ~450-block window when the RPC caps eth_getLogs
            // (public Amoy endpoints reject even the default 10 000).
            var scan = await MCPTransfer.Core.Chain.FileRegistryQueries
                .GetInboxWithFallbackAsync(chain.FileRegistry, identity.Address, fromBlock, latest, ct)
                .ConfigureAwait(false);
            if (scan.FromBlock != fromBlock)
            {
                Console.Error.WriteLine(
                    $"  note: the RPC rejected the {latest - fromBlock}-block scan; "
                    + $"narrowed to blocks {scan.FromBlock}..{latest}. Use --since for older events.");
                fromBlock = scan.FromBlock;
            }
            events = scan.Events;

            // Drop events from senders on this agent's on-chain blocklist
            // (no-op when no Blocklist contract is configured).
            var filtered = await MCPTransfer.Core.Chain.InboxFilter
                .ApplyAsync(chain.Blocklist, identity.Address, events, ct).ConfigureAwait(false);
            events = filtered.Kept;
            hiddenCount = filtered.Hidden;
        }
        catch (Exception ex)
        {
            return Common.Fail($"event query failed: {ex.Message}");
        }

        if (events.Count == 0)
        {
            Console.WriteLine(hiddenCount > 0
                ? $"  (no events — {hiddenCount} from blocked sender(s) hidden)"
                : "  (no events)");
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
            Console.WriteLine($"      content hash: {HexFormat.ToHex0x(e.ContentHash)}");
        }
        Console.WriteLine();
        Console.WriteLine(hiddenCount > 0
            ? $"  {events.Count} event(s) ({hiddenCount} from blocked sender(s) hidden)."
            : $"  {events.Count} event(s).");
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
