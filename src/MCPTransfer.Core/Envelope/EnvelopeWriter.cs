using System.Security.Cryptography;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Core.Envelope;

/// <summary>
/// Sender-side orchestrator: takes a plaintext stream and produces a
/// signed manifest plus a population of encrypted chunks in an
/// <see cref="IIpfsClient"/>.
/// </summary>
/// <remarks>
/// Chunk uploads run with bounded parallelism. The encryption pipeline is
/// inherently sequential (the input stream is consumed in order), but each
/// produced chunk is handed to a task pool of at most
/// <c>maxParallelism</c> in-flight uploads. Order is preserved by indexing
/// the resulting <see cref="ManifestChunkEntry"/> array on completion.
/// </remarks>
public sealed class EnvelopeWriter
{
    public const int DefaultMaxParallelism = 4;

    private readonly IIpfsClient _ipfs;
    private readonly int _maxParallelism;

    public EnvelopeWriter(IIpfsClient ipfs, int maxParallelism = DefaultMaxParallelism)
    {
        ArgumentNullException.ThrowIfNull(ipfs);
        if (maxParallelism < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maxParallelism), "Max parallelism must be at least 1.");

        _ipfs = ipfs;
        _maxParallelism = maxParallelism;
    }

    public async Task<EnvelopeWriteResult> SendAsync(
        Stream input,
        AgentIdentity sender,
        AgentPublicIdentity recipient,
        string? filename = null,
        string? mimeType = null,
        int chunkSize = ChunkedAead.DefaultChunkSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(recipient);

        var noncePrefix = RandomNumberGenerator.GetBytes(ChunkedAead.NoncePrefixByteLength);
        var hkdfContext = EnvelopeContext.BuildHkdfContext(
            sender.Address, recipient.Address, noncePrefix);

        using var encapsulation = HybridKem.Encapsulate(recipient, hkdfContext);

        var uploadTasks = new List<Task<ManifestChunkEntry>>();
        var semaphore = new SemaphoreSlim(_maxParallelism, _maxParallelism);
        long totalSize = 0;

        await foreach (var chunk in ChunkedAead.EncryptAsync(
            input,
            encapsulation.DerivedKey,
            noncePrefix,
            chunkSize,
            cancellationToken).ConfigureAwait(false))
        {
            // Gate the producer: wait until a free upload slot is available
            // so we never hold more than _maxParallelism encrypted chunks in
            // memory at any one time.
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            totalSize += chunk.Ciphertext.Length;
            var capturedChunk = chunk;
            uploadTasks.Add(UploadChunkAsync(capturedChunk, semaphore, cancellationToken));
        }

        // Task.WhenAll preserves array order: tasks were added in chunk-index
        // order, so the resulting entries are also in index order.
        var chunkEntries = await Task.WhenAll(uploadTasks).ConfigureAwait(false);

        var manifest = new Manifest(
            version: Manifest.CurrentVersion,
            suite: HybridKem.SuiteIdentifier,
            sender: sender.Address,
            recipient: recipient.Address,
            ephemeralSecp256k1PublicKey: encapsulation.EphemeralSecp256k1PublicKey,
            kemCiphertext: encapsulation.KemCiphertext,
            noncePrefix: noncePrefix,
            chunkSize: chunkSize,
            totalSize: totalSize,
            createdAtUnixSeconds: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            chunks: chunkEntries,
            filename: filename,
            mimeType: mimeType);

        var signed = SignedManifest.Create(manifest, sender);
        var manifestCid = await _ipfs.PinAsync(
            signed.ToCanonicalJsonBytes(),
            cancellationToken).ConfigureAwait(false);

        return new EnvelopeWriteResult(manifestCid, signed);
    }

    private async Task<ManifestChunkEntry> UploadChunkAsync(
        EncryptedChunk chunk,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        try
        {
            var cid = await _ipfs.PinAsync(chunk.Ciphertext, cancellationToken).ConfigureAwait(false);
            return new ManifestChunkEntry(
                index: chunk.Index,
                cid: cid,
                tag: chunk.Tag,
                ciphertextSize: chunk.Ciphertext.Length);
        }
        finally
        {
            semaphore.Release();
        }
    }
}

public sealed record EnvelopeWriteResult(string ManifestCid, SignedManifest SignedManifest);
