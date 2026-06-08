using System.Security.Cryptography;
using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Agent.Commands;

internal static class WhoisCommand
{
    public const string Usage =
        "  mcptx whois <handle|0xaddress> [--config PATH]\n"
      + "      Aggregate lookup: address ↔ handle, ML-KEM presence + fingerprint.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Common.Fail("missing required positional <handle|0xaddress>. Example: mcptx whois alice-ai");

        var target = args[1];

        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;
        var chain = Common.BuildChainClient(config);

        // Resolve target -> address.
        EthereumAddress? address;
        string? handle;
        if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            try { address = EthereumAddress.FromHex(target); }
            catch (ArgumentException ex) { return Common.Fail(ex.Message); }
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

        var mlkem = await chain.KeyRegistry.GetAsync(address, ct).ConfigureAwait(false);
        var fp = mlkem.Length > 0
            ? Convert.ToHexString(SHA256.HashData(mlkem)).ToLowerInvariant()[..16]
            : "(none — not registered)";

        Console.WriteLine($"Address          : {address}");
        Console.WriteLine($"Handle           : {handle ?? "(none)"}");
        Console.WriteLine($"ML-KEM pubkey    : {(mlkem.Length > 0 ? $"{mlkem.Length} bytes" : "(none — not registered)")}");
        Console.WriteLine($"  sha256 fp      : {fp}");
        return Common.ExitSuccess;
    }
}
