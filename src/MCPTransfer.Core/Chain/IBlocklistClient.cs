using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Wraps the on-chain <c>Blocklist</c> contract: per-recipient advisory
/// sender blocklist, enforced client-side at inbox-read time.
/// </summary>
public interface IBlocklistClient
{
    /// <summary>
    /// Submit a <c>setBlocked(sender, blocked)</c> transaction signed by
    /// <paramref name="self"/>: records whether <paramref name="sender"/> is
    /// blocked for <paramref name="self"/>'s inbox. Idempotent and reversible.
    /// </summary>
    Task<string> SetBlockedAsync(
        EthereumAddress sender,
        bool blocked,
        Secp256k1KeyPair self,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True iff <paramref name="recipient"/> has blocked
    /// <paramref name="sender"/>. Read-only (eth_call).
    /// </summary>
    Task<bool> IsBlockedAsync(
        EthereumAddress recipient,
        EthereumAddress sender,
        CancellationToken cancellationToken = default);
}
