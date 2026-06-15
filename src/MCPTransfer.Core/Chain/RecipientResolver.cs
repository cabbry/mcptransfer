using System.Security.Cryptography;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Resolves a user-supplied recipient string (a handle like <c>alice-ai</c>
/// or a <c>0x</c> address) to a verified <see cref="AgentPublicIdentity"/>
/// ready for <see cref="HybridKem"/> encapsulation. Shared by the CLI
/// <c>send</c> command and the MCP <c>send_file</c> tool.
/// </summary>
public static class RecipientResolver
{
    /// <summary>
    /// Result of resolving + fetching + verifying a recipient.
    /// </summary>
    public sealed record Resolved(
        EthereumAddress Address,
        string? Handle,
        AgentPublicIdentity PublicIdentity);

    /// <summary>
    /// Resolve <paramref name="recipient"/> to a verified public identity:
    /// <list type="number">
    /// <item>handle → address via <c>AgentDirectory</c> (or parse a 0x address);</item>
    /// <item>read the key entry from <c>KeyRegistry</c> (secp256k1 in clear
    /// + ML-KEM hash commitment and CID);</item>
    /// <item>fetch the full ML-KEM key from <paramref name="ipfs"/> by CID
    /// and verify <c>keccak256(key)</c> matches the on-chain commitment —
    /// the distribution channel is untrusted;</item>
    /// <item>verify the secp256k1 key derives to the declared address.</item>
    /// </list>
    /// Throws <see cref="InvalidOperationException"/> with a caller-friendly
    /// message on any failure (unknown handle, unregistered keys, commitment
    /// mismatch, key/address mismatch).
    /// </summary>
    public static async Task<Resolved> ResolveAsync(
        EthereumChainClient chain,
        IIpfsClient ipfs,
        string recipient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(ipfs);
        ArgumentException.ThrowIfNullOrEmpty(recipient);

        // Parse a 0x address or resolve a handle (clean errors for both).
        var (address, handle) = await HandleResolution
            .ResolveRequiredAsync(chain.AgentDirectory, recipient, cancellationToken).ConfigureAwait(false);

        var keys = await chain.KeyRegistry.GetAsync(address, cancellationToken).ConfigureAwait(false);
        if (!keys.IsRegistered)
        {
            throw new InvalidOperationException(
                $"Recipient {address} has not registered its public keys. "
                + "They must run 'register-key' before they can receive.");
        }

        // Fetch the full ML-KEM key from the off-chain store and verify it
        // against the on-chain hash commitment. The CID/store is untrusted;
        // the keccak256 check is what ties the bytes to the registration.
        byte[] mlkemKey;
        try
        {
            mlkemKey = await ipfs.FetchAsync(keys.MlKemCid, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Could not fetch the recipient's ML-KEM key from the IPFS store "
                + $"(cid '{keys.MlKemCid}'): {ex.Message} "
                + "The recipient may have registered against a different IPFS backend.", ex);
        }

        if (!CryptographicOperations.FixedTimeEquals(Hashes.Keccak256(mlkemKey), keys.MlKemHash))
        {
            throw new InvalidOperationException(
                $"The ML-KEM key fetched for {address} does not match its on-chain keccak256 "
                + "commitment. The IPFS store served tampered or stale bytes; refusing to encrypt.");
        }

        AgentPublicIdentity publicIdentity;
        try
        {
            publicIdentity = AgentPublicIdentity.FromBytes(keys.Secp256k1Compressed, mlkemKey);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Recipient key material is malformed: {ex.Message}", ex);
        }

        // Defend against a misbehaving registry / MITM RPC: the on-chain
        // secp256k1 key must derive to the address we are sending to.
        if (publicIdentity.Address != address)
        {
            throw new InvalidOperationException(
                $"Recipient secp256k1 pubkey derives to {publicIdentity.Address}, which does not "
                + $"match the declared address {address}. Refusing to send.");
        }

        return new Resolved(address, handle, publicIdentity);
    }
}
