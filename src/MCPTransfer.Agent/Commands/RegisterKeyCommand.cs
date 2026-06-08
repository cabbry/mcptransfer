using System.Security.Cryptography;

namespace MCPTransfer.Agent.Commands;

internal static class RegisterKeyCommand
{
    public const string Usage =
        "  mcptx register-key [--identity PATH] [--config PATH]\n"
      + "      Publish the local secp256k1 + ML-KEM-768 public keys to KeyRegistry.\n"
      + "      Both are required: secp256k1 for ECDH, ML-KEM for PQC encapsulation.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var chain = Common.BuildChainClient(config);

        var ecPubkey = identity.Secp256k1.PublicKeyCompressed;
        var mlkemPubkey = identity.MlKem.PublicKey.Bytes;
        var ecSha = Convert.ToHexString(SHA256.HashData(ecPubkey)).ToLowerInvariant()[..16];
        var mlkemSha = Convert.ToHexString(SHA256.HashData(mlkemPubkey)).ToLowerInvariant()[..16];

        Console.WriteLine($"Publishing keys for {identity.Address}");
        Console.WriteLine($"  secp256k1 sha256:{ecSha}    ({ecPubkey.Length} bytes)");
        Console.WriteLine($"  mlkem     sha256:{mlkemSha}  ({mlkemPubkey.Length} bytes)");
        Console.WriteLine($"  to chain {config.Chain.ChainId} via {config.Chain.RpcUrl}");

        try
        {
            var txHash = await chain.KeyRegistry.PublishAsync(
                ecPubkey.ToArray(),
                mlkemPubkey.ToArray(),
                identity.Secp256k1,
                ct).ConfigureAwait(false);
            Console.WriteLine($"  tx hash: {txHash}");

            var stored = await chain.KeyRegistry.GetAsync(identity.Address, ct).ConfigureAwait(false);
            var storedEcSha = stored.Secp256k1Compressed.Length > 0
                ? Convert.ToHexString(SHA256.HashData(stored.Secp256k1Compressed)).ToLowerInvariant()[..16]
                : "(none)";
            var storedMlkemSha = stored.MlKem.Length > 0
                ? Convert.ToHexString(SHA256.HashData(stored.MlKem)).ToLowerInvariant()[..16]
                : "(none)";

            Console.WriteLine($"  on-chain secp256k1 sha256:{storedEcSha}   ({stored.Secp256k1Compressed.Length} bytes)");
            Console.WriteLine($"  on-chain mlkem     sha256:{storedMlkemSha}  ({stored.MlKem.Length} bytes)");

            if (storedEcSha == ecSha && storedMlkemSha == mlkemSha)
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
