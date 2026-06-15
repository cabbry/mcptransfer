using System.Security.Cryptography;
using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Agent.Commands;

internal static class WhoisCommand
{
    public const string Usage =
        "  mcptx whois <handle|0xaddress> [--config PATH]\n"
      + "      Aggregate lookup: address ↔ handle, secp256k1 + ML-KEM presence + fingerprints.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Common.Fail("missing required positional <handle|0xaddress>. Example: mcptx whois alice-ai");

        var target = args[1];

        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;
        var chain = Common.BuildChainClient(config);

        EthereumAddress? address;
        string? handle;
        if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            // ParseAddress maps both ArgumentException (wrong length) and
            // FormatException (non-hex chars) to a clean InvalidOperationException.
            try { address = HandleResolution.ParseAddress(target); }
            catch (InvalidOperationException ex) { return Common.Fail(ex.Message); }
            handle = await chain.AgentDirectory.ReverseResolveAsync(address, ct).ConfigureAwait(false);
        }
        else
        {
            if (!HandleValidation.IsValid(target))
                return Common.Fail($"'{target}' is not a valid handle and doesn't look like an address.");
            address = await chain.AgentDirectory.ResolveAsync(target, ct).ConfigureAwait(false);
            handle = target;
            if (address is null)
            {
                Console.WriteLine($"Handle '{target}' is not claimed.");
                return Common.ExitSuccess;
            }
        }

        var keys = await chain.KeyRegistry.GetAsync(address, ct).ConfigureAwait(false);

        var ecFp = keys.Secp256k1Compressed.Length > 0
            ? Convert.ToHexString(SHA256.HashData(keys.Secp256k1Compressed)).ToLowerInvariant()[..16]
            : "(none — not registered)";
        var mlkemHash = keys.MlKemHash.Length > 0
            ? "0x" + Convert.ToHexString(keys.MlKemHash).ToLowerInvariant()
            : "(none — not registered)";

        Console.WriteLine($"Address              : {address}");
        Console.WriteLine($"Handle               : {handle ?? "(none)"}");
        Console.WriteLine($"Registered           : {(keys.IsRegistered ? "yes" : "no")}");
        Console.WriteLine($"secp256k1 pubkey     : {(keys.Secp256k1Compressed.Length > 0 ? $"{keys.Secp256k1Compressed.Length} bytes (on-chain)" : "(none)")}");
        Console.WriteLine($"  sha256 fp          : {ecFp}");
        Console.WriteLine($"ML-KEM-768 commitment: {mlkemHash}");
        Console.WriteLine($"  key cid            : {(string.IsNullOrEmpty(keys.MlKemCid) ? "(none)" : keys.MlKemCid)}");
        return Common.ExitSuccess;
    }
}
