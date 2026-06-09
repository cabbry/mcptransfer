using System.Security.Cryptography;
using MCPTransfer.Core.Chain;

namespace MCPTransfer.Agent.Commands;

internal static class RegisterKeyCommand
{
    public const string Usage =
        "  mcptx register-key [--identity PATH] [--config PATH]\n"
      + "      Publish the local keys to KeyRegistry: secp256k1 in clear (ECDH) plus\n"
      + "      a keccak256 commitment to the ML-KEM-768 key, whose full bytes are\n"
      + "      pinned to the configured IPFS backend first (registry v2).";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var chain = Common.BuildChainClient(config);
        var ipfs = Common.TryBuildIpfsClient(config, out var ipfsErr);
        if (ipfs is null)
            return Common.Fail(ipfsErr ?? "could not build IPFS client.");

        var ecPubkey = identity.Secp256k1.PublicKeyCompressed.ToArray();
        var mlkemPubkey = identity.MlKem.PublicKey.Bytes.ToArray();
        var ecSha = Convert.ToHexString(SHA256.HashData(ecPubkey)).ToLowerInvariant()[..16];
        var mlkemSha = Convert.ToHexString(SHA256.HashData(mlkemPubkey)).ToLowerInvariant()[..16];

        Console.WriteLine($"Publishing keys for {identity.Address}");
        Console.WriteLine($"  secp256k1 sha256:{ecSha}    ({ecPubkey.Length} bytes, stored on-chain)");
        Console.WriteLine($"  mlkem     sha256:{mlkemSha}  ({mlkemPubkey.Length} bytes, pinned via {config.Ipfs.Kind})");
        Console.WriteLine($"  to chain {config.Chain.ChainId} via {config.Chain.RpcUrl}");

        try
        {
            var result = await KeyPublication.PublishAsync(chain.KeyRegistry, ipfs, identity, ct)
                .ConfigureAwait(false);
            Console.WriteLine($"  mlkem cid : {result.MlKemCid}");
            Console.WriteLine($"  mlkem hash: 0x{Convert.ToHexString(result.MlKemHash).ToLowerInvariant()}");
            Console.WriteLine($"  tx hash   : {result.TxHash}");

            // Round-trip: the on-chain entry must echo what we just published.
            var stored = await chain.KeyRegistry.GetAsync(identity.Address, ct).ConfigureAwait(false);
            var ecMatches = stored.Secp256k1Compressed.AsSpan().SequenceEqual(ecPubkey);
            var commitmentMatches = stored.MlKemHash.AsSpan().SequenceEqual(result.MlKemHash)
                && stored.MlKemCid == result.MlKemCid;

            if (ecMatches && commitmentMatches)
                Console.WriteLine("  ✓ round-trip verified (on-chain entry matches)");
            else
                Console.WriteLine("  ! round-trip mismatch — verify chain state");

            return Common.ExitSuccess;
        }
        catch (Exception ex)
        {
            return Common.Fail($"publish failed: {ex.Message}");
        }
        finally
        {
            if (ipfs is IDisposable d) d.Dispose();
        }
    }
}
