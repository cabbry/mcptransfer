using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// The full key-publication flow for registry v2: pin the ML-KEM public key
/// to the off-chain store, then publish (secp256k1 in clear, keccak256
/// commitment, CID) on-chain. Shared by the CLI <c>register-key</c> command
/// and the MCP <c>register_key</c> tool.
/// </summary>
public static class KeyPublication
{
    /// <summary>Outcome of a publication: the tx plus what was committed.</summary>
    public sealed record Result(string TxHash, string MlKemCid, byte[] MlKemHash);

    /// <summary>
    /// Pin <paramref name="identity"/>'s ML-KEM-768 public key to
    /// <paramref name="ipfs"/>, then publish the entry to the on-chain
    /// <c>KeyRegistry</c> signed by the identity's secp256k1 key.
    /// </summary>
    /// <remarks>
    /// The pin happens FIRST so a successful transaction always points at
    /// retrievable bytes. If the transaction fails after the pin, the orphan
    /// pin is harmless (public key material, content-addressed).
    /// </remarks>
    public static async Task<Result> PublishAsync(
        IKeyRegistryClient keyRegistry,
        IIpfsClient ipfs,
        AgentIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyRegistry);
        ArgumentNullException.ThrowIfNull(ipfs);
        ArgumentNullException.ThrowIfNull(identity);

        var mlkemKey = identity.MlKem.PublicKey.Bytes.ToArray();
        var cid = await ipfs.PinAsync(mlkemKey, cancellationToken).ConfigureAwait(false);
        var hash = Hashes.Keccak256(mlkemKey);

        var txHash = await keyRegistry.PublishAsync(
            identity.Secp256k1.PublicKeyCompressed.ToArray(),
            hash,
            cid,
            identity.Secp256k1,
            cancellationToken).ConfigureAwait(false);

        return new Result(txHash, cid, hash);
    }
}
