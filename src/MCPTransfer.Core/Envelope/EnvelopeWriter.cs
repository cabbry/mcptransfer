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

    /// <summary>
    /// NIST SP 800-38D recommends rotating an AES-GCM key after roughly
    /// 64 GiB of plaintext. Each envelope uses one HKDF-derived AES key,
    /// so we reject inputs that would exceed this budget on a single key.
    /// Override via <see cref="MaxPlaintextBytes"/> (e.g. tests).
    /// </summary>
    public const long DefaultMaxPlaintextBytes = 64L * 1024 * 1024 * 1024;

    private readonly IIpfsClient _ipfs;
    private readonly int _maxParallelism;

    /// <summary>
    /// Hard upper bound on the plaintext size of a single transfer (default
    /// <see cref="DefaultMaxPlaintextBytes"/> = 64 GiB). Set via an
    /// object initializer for tests or specialised deployments.
    /// </summary>
    public long MaxPlaintextBytes { get; init; } = DefaultMaxPlaintextBytes;

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

        // Best-effort upfront rejection for seekable streams so the caller
        // does not pay for any encryption before we know the file is too big.
        if (input.CanSeek)
        {
            var remaining = input.Length - input.Position;
            if (remaining > MaxPlaintextBytes)
            {
                throw new InvalidOperationException(
                    $"Input size {remaining} bytes exceeds the {MaxPlaintextBytes}-byte "
                    + "per-transfer AES-GCM safety cap. Split the file across multiple transfers.");
            }
        }

        var noncePrefix = RandomNumberGenerator.GetBytes(ChunkedAead.NoncePrefixByteLength);
        var hkdfContext = EnvelopeContext.BuildHkdfContext(
            sender.Address, recipient.Address, noncePrefix);

        using var encapsulation = HybridKem.Encapsulate(recipient, hkdfContext);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        var uploadTasks = new List<Task<ManifestChunkEntry>>();
        var semaphore = new SemaphoreSlim(_maxParallelism, _maxParallelism);
        long totalSize = 0;

        try
        {
            await foreach (var chunk in ChunkedAead.EncryptAsync(
                input,
                encapsulation.DerivedKey,
                noncePrefix,
                chunkSize,
                ct).ConfigureAwait(false))
            {
                // Safety net for non-seekable streams (or seekable streams that
                // lied about Length): catch oversize inputs once we have actually
                // consumed past the cap.
                if (totalSize + chunk.Ciphertext.Length > MaxPlaintextBytes)
                {
                    throw new InvalidOperationException(
                        $"Input exceeded the {MaxPlaintextBytes}-byte per-transfer AES-GCM "
                        + "safety cap mid-stream. Split the file across multiple transfers.");
                }

                // Gate the producer: wait until a free upload slot is available
                // so we never hold more than _maxParallelism encrypted chunks in
                // memory at any one time.
                await semaphore.WaitAsync(ct).ConfigureAwait(false);

                totalSize += chunk.Ciphertext.Length;
                var capturedChunk = chunk;
                uploadTasks.Add(UploadChunkAsync(capturedChunk, semaphore, ct));
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
                ct).ConfigureAwait(false);

            return new EnvelopeWriteResult(manifestCid, signed);
        }
        catch
        {
            // Drain: cancel any in-flight uploads and wait for them to settle
            // so they do not keep running (and burning Pinata quota / leaking
            // tasks) after the original exception bubbles up.
            linkedCts.Cancel();
            try
            {
                await Task.WhenAll(uploadTasks).ConfigureAwait(false);
            }
            catch
            {
                // Swallow follow-on exceptions — the first failure is what the
                // caller cares about; we just need the tasks to terminate.
            }
            throw;
        }
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
