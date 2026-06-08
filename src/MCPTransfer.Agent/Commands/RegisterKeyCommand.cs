using System.Security.Cryptography;

namespace MCPTransfer.Agent.Commands;

internal static class RegisterKeyCommand
{
    public const string Usage =
        "  mcptx register-key [--identity PATH] [--config PATH]\n"
      + "      Publish the local ML-KEM-768 public key to KeyRegistry on chain.\n"
      + "      Other agents will then be able to encapsulate against you.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var chain = Common.BuildChainClient(config);

        var pkBytes = identity.MlKem.PublicKey.Bytes;
        var pkSha = Convert.ToHexString(SHA256.HashData(pkBytes)).ToLowerInvariant()[..16];

        Console.WriteLine($"Publishing ML-KEM-768 public key for {identity.Address}");
        Console.WriteLine($"  pubkey sha256:{pkSha} ({pkBytes.Length} bytes)");
        Console.WriteLine($"  to chain id {config.Chain.ChainId} via {config.Chain.RpcUrl}");

        try
        {
            var txHash = await chain.KeyRegistry.PublishAsync(
                pkBytes.ToArray(),
                identity.Secp256k1,
                ct).ConfigureAwait(false);
            Console.WriteLine($"  tx hash: {txHash}");

            // Verify round-trip: read it back.
            var stored = await chain.KeyRegistry.GetAsync(identity.Address, ct).ConfigureAwait(false);
            var storedSha = stored.Length > 0
                ? Convert.ToHexString(SHA256.HashData(stored)).ToLowerInvariant()[..16]
                : "(none)";
            Console.WriteLine($"  on-chain sha256:{storedSha} ({stored.Length} bytes)");
            if (stored.Length == pkBytes.Length && storedSha == pkSha)
                Console.WriteLine("  ✓ round-trip verified");
            else
                Console.WriteLine("  ! round-trip mismatch — verify chain state");

            return Common.ExitSuccess;
        }
        catch (Exception ex)
        {
            return Common.Fail($"publish failed: {ex.Message}");
        }
    }
}
