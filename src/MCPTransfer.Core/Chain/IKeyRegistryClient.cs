using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Wraps the on-chain <c>KeyRegistry</c> contract.
/// </summary>
public interface IKeyRegistryClient
{
    /// <summary>
    /// Submit a <c>publish(secp256k1Pubkey, mlkemPubkey)</c> transaction
    /// signed by <paramref name="self"/>. Both keys are stored on-chain so
    /// senders can perform the hybrid KEM (ECDH on secp256k1 + ML-KEM
    /// encapsulation) without an out-of-band channel.
    /// </summary>
    /// <param name="secp256k1Pubkey">Must be exactly 33 bytes (compressed form).</param>
    /// <param name="mlkemPubkey">Must be exactly 1184 bytes (ML-KEM-768).</param>
    Task<string> PublishAsync(
        ReadOnlyMemory<byte> secp256k1Pubkey,
        ReadOnlyMemory<byte> mlkemPubkey,
        Secp256k1KeyPair self,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read both public keys registered for <paramref name="who"/>. The
    /// returned <see cref="AgentPublicKeys.IsRegistered"/> is false iff
    /// the address has never published.
    /// </summary>
    Task<AgentPublicKeys> GetAsync(
        EthereumAddress who,
        CancellationToken cancellationToken = default);
}
