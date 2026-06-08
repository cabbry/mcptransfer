using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Wraps the on-chain <c>KeyRegistry</c> contract.
/// </summary>
public interface IKeyRegistryClient
{
    /// <summary>
    /// Submit a <c>publish(mlkemPubkey)</c> transaction signed by
    /// <paramref name="self"/>. The address that publishes is determined by
    /// the signer; callers cannot publish for someone else.
    /// </summary>
    /// <param name="mlkemPubkey">Must be exactly 1184 bytes (ML-KEM-768).</param>
    Task<string> PublishAsync(
        ReadOnlyMemory<byte> mlkemPubkey,
        Secp256k1KeyPair self,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the ML-KEM-768 public key registered for <paramref name="who"/>.
    /// Returns an empty byte array if the address has never published.
    /// </summary>
    Task<byte[]> GetAsync(
        EthereumAddress who,
        CancellationToken cancellationToken = default);
}
