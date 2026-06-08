using MCPTransfer.Core.Crypto;

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
    /// <item>fetch both public keys from <c>KeyRegistry</c>;</item>
    /// <item>verify the secp256k1 key derives to the declared address.</item>
    /// </list>
    /// Throws <see cref="InvalidOperationException"/> with a caller-friendly
    /// message on any failure (unknown handle, unregistered keys, key/address
    /// mismatch).
    /// </summary>
    public static async Task<Resolved> ResolveAsync(
        EthereumChainClient chain,
        string recipient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentException.ThrowIfNullOrEmpty(recipient);

        EthereumAddress address;
        string? handle = null;

        if (recipient.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                address = EthereumAddress.FromHex(recipient);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                // Honor the documented contract: failures surface as
                // InvalidOperationException, not a raw parse exception.
                throw new InvalidOperationException(
                    $"'{recipient}' is not a valid 0x Ethereum address: {ex.Message}", ex);
            }
        }
        else
        {
            if (!HandleValidation.IsValid(recipient))
            {
                throw new InvalidOperationException(
                    $"'{recipient}' is not a valid handle and doesn't look like a 0x address.");
            }
            var resolved = await chain.AgentDirectory.ResolveAsync(recipient, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Handle '{recipient}' is not claimed on chain.");
            address = resolved;
            handle = recipient;
        }

        var keys = await chain.KeyRegistry.GetAsync(address, cancellationToken).ConfigureAwait(false);
        if (!keys.IsRegistered)
        {
            throw new InvalidOperationException(
                $"Recipient {address} has not registered both public keys. "
                + "They must run 'register-key' before they can receive.");
        }

        AgentPublicIdentity publicIdentity;
        try
        {
            publicIdentity = AgentPublicIdentity.FromBytes(keys.Secp256k1Compressed, keys.MlKem);
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
