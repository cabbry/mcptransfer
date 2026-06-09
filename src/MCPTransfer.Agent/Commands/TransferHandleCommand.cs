using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Agent.Commands;

internal static class TransferHandleCommand
{
    public const string Usage =
        "  mcptx transfer-handle <handle> --to <0xaddress> [--identity PATH] [--config PATH]\n"
      + "      Transfer YOUR handle to a new owner address (e.g. after migrating to a\n"
      + "      fresh keypair). The new owner must not already have a handle; you are\n"
      + "      freed and may claim a different one later. Signs a transaction.\n"
      + "      WARNING: after the transfer the new owner fully controls the handle.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Common.Fail("missing required positional <handle>. Example: mcptx transfer-handle alice-ai --to 0xNewOwner");

        var handle = args[1];
        if (!HandleValidation.IsValid(handle))
            return Common.Fail($"'{handle}' is not a valid handle ([a-z0-9-]{{3,32}}, no leading/trailing hyphen).");

        var toArg = Common.GetFlagValue(args, "--to");
        if (string.IsNullOrEmpty(toArg))
            return Common.Fail("missing --to <0xaddress> (the new owner).");

        EthereumAddress newOwner;
        try { newOwner = EthereumAddress.FromHex(toArg); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return Common.Fail($"--to: '{toArg}' is not a valid 0x address: {ex.Message}");
        }

        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var chain = Common.BuildChainClient(config);

        // Pre-flight: confirm the handle is actually ours, for a clear error
        // instead of a contract revert.
        var currentOwner = await chain.AgentDirectory.ResolveAsync(handle, ct).ConfigureAwait(false);
        if (currentOwner is null)
            return Common.Fail($"handle '{handle}' is not claimed on chain.");
        if (currentOwner != identity.Address)
            return Common.Fail($"handle '{handle}' is owned by {currentOwner}, not by this identity ({identity.Address}).");

        Console.WriteLine($"Transferring handle '{handle}'");
        Console.WriteLine($"  from: {identity.Address} (you)");
        Console.WriteLine($"  to  : {newOwner}");

        try
        {
            var txHash = await chain.AgentDirectory
                .TransferAsync(handle, newOwner, identity.Secp256k1, ct).ConfigureAwait(false);
            Console.WriteLine($"  tx hash: {txHash}");
            Console.WriteLine($"  ✓ transferred — {newOwner} now owns '{handle}'; you may claim a new handle.");
            return Common.ExitSuccess;
        }
        catch (Exception ex)
        {
            return Common.Fail($"transfer failed: {ex.Message}");
        }
    }
}
