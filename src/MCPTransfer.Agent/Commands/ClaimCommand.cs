using MCPTransfer.Core.Chain;

namespace MCPTransfer.Agent.Commands;

internal static class ClaimCommand
{
    public const string Usage =
        "  mcptx claim <handle> [--identity PATH] [--config PATH]\n"
      + "      Claim a handle (alice-ai, gpt5-instance-42, ...) for your address.\n"
      + "      First-come-first-served. Format: [a-z0-9-]{3,32}, no leading/trailing hyphen.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Common.Fail("missing required positional <handle>. Example: mcptx claim alice-ai");

        var handle = args[1];

        try { HandleValidation.Validate(handle); }
        catch (ArgumentException ex) { return Common.Fail(ex.Message); }

        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var chain = Common.BuildChainClient(config);

        Console.WriteLine($"Claiming handle '{handle}' for {identity.Address}");

        try
        {
            var txHash = await chain.AgentDirectory.ClaimAsync(handle, identity.Secp256k1, ct).ConfigureAwait(false);
            Console.WriteLine($"  tx hash: {txHash}");

            // Verify round-trip.
            var resolved = await chain.AgentDirectory.ResolveAsync(handle, ct).ConfigureAwait(false);
            if (resolved == identity.Address)
                Console.WriteLine($"  ✓ resolved '{handle}' -> {resolved}");
            else
                Console.WriteLine($"  ! resolved to {resolved?.ToString() ?? "(null)"} — verify chain state");

            return Common.ExitSuccess;
        }
        catch (Exception ex)
        {
            return Common.Fail($"claim failed: {ex.Message}");
        }
    }
}
