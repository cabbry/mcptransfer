namespace MCPTransfer.Core.Ipfs;

/// <summary>
/// Abstraction over an IPFS-like content-addressed store. The transfer
/// layer is otherwise agnostic to how chunks and manifests are stored.
/// </summary>
public interface IIpfsClient
{
    /// <summary>
    /// Pin the given byte buffer and return its CID. Two calls with the
    /// same content must return the same CID (content-addressed).
    /// </summary>
    Task<string> PinAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch the bytes previously pinned under <paramref name="cid"/>.
    /// Throws if the CID is unknown to this client.
    /// </summary>
    Task<byte[]> FetchAsync(
        string cid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove the pin for <paramref name="cid"/> so the content can be
    /// garbage-collected by the backend. Used by the transfer lifecycle to
    /// keep storage ephemeral (a transfer is a mailbox, not an archive).
    /// MUST be idempotent: unpinning a CID that is absent / already unpinned
    /// completes without error (so a re-run of <c>gc</c> is safe).
    /// </summary>
    Task UnpinAsync(
        string cid,
        CancellationToken cancellationToken = default);
}
