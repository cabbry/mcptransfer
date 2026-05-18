using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Envelope;

/// <summary>
/// One row in <see cref="Manifest.Chunks"/>: locates an encrypted chunk on
/// IPFS, carries its 16-byte AES-GCM tag, and records the ciphertext size
/// so a recipient can prefetch and verify without trial-and-error.
/// </summary>
public sealed class ManifestChunkEntry
{
    public int Index { get; }
    public string Cid { get; }
    public byte[] Tag { get; }
    public int CiphertextSize { get; }

    public ManifestChunkEntry(int index, string cid, byte[] tag, int ciphertextSize)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), "Chunk index must be non-negative.");
        ArgumentException.ThrowIfNullOrEmpty(cid);
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length != ChunkedAead.TagByteLength)
            throw new ArgumentException(
                $"Tag must be exactly {ChunkedAead.TagByteLength} bytes (got {tag.Length}).",
                nameof(tag));
        if (ciphertextSize < 0)
            throw new ArgumentOutOfRangeException(
                nameof(ciphertextSize), "Ciphertext size must be non-negative.");

        Index = index;
        Cid = cid;
        Tag = (byte[])tag.Clone();
        CiphertextSize = ciphertextSize;
    }
}
