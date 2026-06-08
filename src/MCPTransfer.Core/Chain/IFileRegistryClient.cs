using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Wraps the on-chain <c>FileRegistry</c> contract.
/// </summary>
public interface IFileRegistryClient
{
    /// <summary>
    /// Submit a <c>send(to, cid, contentHash)</c> transaction signed by
    /// <paramref name="sender"/>. Returns the transaction hash; the
    /// transaction is broadcast but the caller must use
    /// <see cref="GetReceiptAsync"/> to confirm inclusion.
    /// </summary>
    Task<string> SendAsync(
        EthereumAddress recipient,
        string cid,
        byte[] contentHash,
        Secp256k1KeyPair sender,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream all <c>FileSent</c> events addressed to <paramref name="me"/>
    /// from <paramref name="fromBlock"/> (inclusive) onwards. Polls the RPC
    /// in batches; yields one event at a time in block order.
    /// </summary>
    IAsyncEnumerable<FileSentEvent> WatchInboxAsync(
        EthereumAddress me,
        ulong fromBlock,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return all <c>FileSent</c> events addressed to <paramref name="me"/>
    /// within the closed block range [<paramref name="fromBlock"/>,
    /// <paramref name="toBlock"/>]. Single RPC round-trip (or batched
    /// internally by the implementation if the range is wide).
    /// </summary>
    Task<IReadOnlyList<FileSentEvent>> GetInboxAsync(
        EthereumAddress me,
        ulong fromBlock,
        ulong toBlock,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Current block height as reported by the RPC endpoint. Used to compute
    /// "from the last N blocks" sliding windows for <see cref="GetInboxAsync"/>.
    /// </summary>
    Task<ulong> GetLatestBlockNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Find the <c>FileSent</c> event addressed to <paramref name="me"/> that
    /// announced <paramref name="cid"/>, within the closed block range. Returns
    /// the most recent match, or <c>null</c> if no such announcement exists in
    /// the range. Lets a recipient corroborate a received manifest against the
    /// on-chain record (its <c>contentHash</c> and non-spoofable sender).
    /// </summary>
    Task<FileSentEvent?> FindByCidAsync(
        EthereumAddress me,
        string cid,
        ulong fromBlock,
        ulong toBlock,
        CancellationToken cancellationToken = default);
}
