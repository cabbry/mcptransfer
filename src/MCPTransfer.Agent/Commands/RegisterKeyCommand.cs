using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

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

        Console.WriteLine($"Publishing keys for {identity.Address}");
        Console.WriteLine($"  secp256k1 {HexFormat.Fingerprint(identity.Secp256k1.PublicKeyCompressed)}    (33 bytes, stored on-chain)");
        Console.WriteLine($"  mlkem     {HexFormat.Fingerprint(identity.MlKem.PublicKey.Bytes)}  (1184 bytes, pinned via {config.Ipfs.Kind})");
        Console.WriteLine($"  to chain {config.Chain.ChainId} via {config.Chain.RpcUrl}");

        try
        {
            // KeyPublication pins the ML-KEM key then publishes the commitment;
            // the contract's require()s already guarantee the stored entry, so
            // no read-back round-trip is needed (the MCP register_key tool also
            // skips it — kept consistent).
            var result = await KeyPublication.PublishAsync(chain.KeyRegistry, ipfs, identity, ct)
                .ConfigureAwait(false);
            Console.WriteLine($"  mlkem cid : {result.MlKemCid}");
            Console.WriteLine($"  mlkem hash: {HexFormat.ToHex0x(result.MlKemHash)}");
            Console.WriteLine($"  tx hash   : {result.TxHash}");
            Console.WriteLine("  ✓ published");
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
