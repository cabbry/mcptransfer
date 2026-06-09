using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Agent.Commands;

/// <summary>
/// <c>mcptx block</c> / <c>mcptx unblock</c> — toggle a sender on this
/// agent's on-chain blocklist. Both verbs share this implementation; the
/// desired state is passed by the dispatcher.
/// </summary>
internal static class BlockCommand
{
    public const string Usage =
        "  mcptx block <handle|0xaddress> [--identity PATH] [--config PATH]\n"
      + "  mcptx unblock <handle|0xaddress> [--identity PATH] [--config PATH]\n"
      + "      Add/remove a sender on your on-chain blocklist. Blocked senders'\n"
      + "      FileSent events are hidden from 'mcptx inbox' (advisory: enforcement\n"
      + "      is client-side at read time). Signs a transaction and spends gas.";

    public static async Task<int> RunAsync(string[] args, bool blocked, CancellationToken ct = default)
    {
        var verb = blocked ? "block" : "unblock";
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Common.Fail($"missing required positional <handle|0xaddress>. Example: mcptx {verb} spammy-agent");

        var target = args[1];

        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var chain = Common.BuildChainClient(config);
        if (chain.Blocklist is null)
        {
            return Common.Fail(
                "no Blocklist contract configured. Set 'blocklist_address' in your config "
                + "(~/.mcptx/config.json) or the MCPTX_BLOCKLIST env var. "
                + "Re-running 'mcptx config init --force' with the anvil-local profile fills it in.");
        }

        // Resolve handle → address if needed.
        EthereumAddress sender;
        string? senderHandle = null;
        if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            try { sender = EthereumAddress.FromHex(target); }
            catch (ArgumentException ex) { return Common.Fail(ex.Message); }
        }
        else
        {
            if (!HandleValidation.IsValid(target))
                return Common.Fail($"'{target}' is not a valid handle and doesn't look like an address.");
            var resolved = await chain.AgentDirectory.ResolveAsync(target, ct).ConfigureAwait(false);
            if (resolved is null)
                return Common.Fail($"handle '{target}' is not claimed on chain.");
            sender = resolved;
            senderHandle = target;
        }

        var label = senderHandle is null ? sender.ToString() : $"{senderHandle} ({sender})";
        Console.WriteLine($"{(blocked ? "Blocking" : "Unblocking")} {label} for {identity.Address}");

        try
        {
            var txHash = await chain.Blocklist
                .SetBlockedAsync(sender, blocked, identity.Secp256k1, ct).ConfigureAwait(false);
            Console.WriteLine($"  tx hash: {txHash}");
            Console.WriteLine(blocked
                ? "  ✓ blocked — their events are now hidden from 'mcptx inbox'"
                : "  ✓ unblocked — their events appear in 'mcptx inbox' again");
            return Common.ExitSuccess;
        }
        catch (Exception ex)
        {
            return Common.Fail($"{verb} failed: {ex.Message}");
        }
    }
}
