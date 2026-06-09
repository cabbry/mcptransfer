using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Wraps the on-chain <c>KeyRegistry</c> contract (v2: hash commitment).
/// </summary>
public interface IKeyRegistryClient
{
    /// <summary>
    /// Submit a <c>publish(secp256k1Pubkey, mlkemHash, mlkemCid)</c>
    /// transaction signed by <paramref name="self"/>. The secp256k1 key is
    /// stored in clear (the sender's ECDH needs it); the ML-KEM-768 key is
    /// committed to by its keccak256 hash plus a content-addressed pointer
    /// where the full key can be fetched. Use
    /// <see cref="KeyPublication.PublishAsync"/> for the full
    /// pin-then-publish flow.
    /// </summary>
    /// <param name="secp256k1Pubkey">Must be exactly 33 bytes (compressed form).</param>
    /// <param name="mlkemHash">keccak256 of the 1184-byte ML-KEM-768 public key (32 bytes, non-zero).</param>
    /// <param name="mlkemCid">Non-empty pointer (&lt;= 128 bytes) to the full key.</param>
    Task<string> PublishAsync(
        ReadOnlyMemory<byte> secp256k1Pubkey,
        ReadOnlyMemory<byte> mlkemHash,
        string mlkemCid,
        Secp256k1KeyPair self,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the key entry registered for <paramref name="who"/>. The returned
    /// <see cref="AgentPublicKeys.IsRegistered"/> is false iff the address has
    /// never published. The ML-KEM key itself must be fetched off-chain via
    /// <see cref="AgentPublicKeys.MlKemCid"/> and verified against
    /// <see cref="AgentPublicKeys.MlKemHash"/>.
    /// </summary>
    Task<AgentPublicKeys> GetAsync(
        EthereumAddress who,
        CancellationToken cancellationToken = default);
}
