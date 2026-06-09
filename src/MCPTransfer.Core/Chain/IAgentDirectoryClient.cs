using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Wraps the on-chain <c>AgentDirectory</c> contract.
/// </summary>
public interface IAgentDirectoryClient
{
    /// <summary>
    /// Submit a <c>claim(handle)</c> transaction signed by
    /// <paramref name="self"/>. The handle must satisfy
    /// <c>[a-z0-9-]{3,32}</c> with no leading or trailing hyphen.
    /// </summary>
    Task<string> ClaimAsync(
        string handle,
        Secp256k1KeyPair self,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submit a <c>transfer(handle, newOwner)</c> transaction signed by
    /// <paramref name="self"/>, moving <paramref name="handle"/> (which
    /// <paramref name="self"/> must own) to <paramref name="newOwner"/>
    /// (which must not already own a handle). The previous owner is freed
    /// and may claim a different handle later.
    /// </summary>
    Task<string> TransferAsync(
        string handle,
        EthereumAddress newOwner,
        Secp256k1KeyPair self,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve <paramref name="handle"/> to the owning address, or
    /// <c>null</c> if the handle is unclaimed.
    /// </summary>
    Task<EthereumAddress?> ResolveAsync(
        string handle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverse-resolve <paramref name="address"/> to its registered handle,
    /// or <c>null</c> if the address has not claimed one.
    /// </summary>
    Task<string?> ReverseResolveAsync(
        EthereumAddress address,
        CancellationToken cancellationToken = default);
}
