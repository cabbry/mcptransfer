using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Core.Envelope;

/// <summary>
/// Recipient-side orchestrator: fetches the signed manifest and all
/// referenced chunks from an <see cref="IIpfsClient"/>, verifies the
/// signature, derives the AES key, and streams the recovered plaintext.
/// </summary>
/// <remarks>
/// Chunk fetches run with bounded parallelism. All fetch tasks are kicked
/// off up front and gated by a semaphore so at most <c>maxParallelism</c>
/// downloads are in flight. The async iterator awaits them in chunk-index
/// order, so <see cref="ChunkedAead.DecryptAsync"/> still sees a strictly
/// ordered sequence.
/// </remarks>
public sealed class EnvelopeReader
{
    public const int DefaultMaxParallelism = 4;

    private readonly IIpfsClient _ipfs;
    private readonly int _maxParallelism;

    public EnvelopeReader(IIpfsClient ipfs, int maxParallelism = DefaultMaxParallelism)
    {
        ArgumentNullException.ThrowIfNull(ipfs);
        if (maxParallelism < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maxParallelism), "Max parallelism must be at least 1.");

        _ipfs = ipfs;
        _maxParallelism = maxParallelism;
    }

    public async Task<EnvelopeReadResult> ReceiveAsync(
        string manifestCid,
        AgentIdentity recipient,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestCid);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(output);

        var signedBytes = await _ipfs.FetchAsync(manifestCid, cancellationToken).ConfigureAwait(false);
        var signed = SignedManifest.FromJsonBytes(signedBytes);

        if (!signed.VerifySignature())
            throw new InvalidOperationException("Manifest signature does not verify.");

        var manifest = signed.Manifest;

        if (manifest.Recipient != recipient.Address)
        {
            throw new InvalidOperationException(
                $"Manifest recipient {manifest.Recipient} does not match this agent's "
                + $"address {recipient.Address}.");
        }

        if (manifest.Suite != HybridKem.SuiteIdentifier)
        {
            throw new InvalidOperationException(
                $"Unsupported cryptographic suite '{manifest.Suite}' "
                + $"(this build expects '{HybridKem.SuiteIdentifier}').");
        }

        var hkdfContext = EnvelopeContext.BuildHkdfContext(
            manifest.Sender, manifest.Recipient, manifest.NoncePrefix.Span);

        var derivedKey = HybridKem.Decapsulate(
            recipient,
            manifest.EphemeralSecp256k1PublicKey.Span,
            manifest.KemCiphertext.Span,
            hkdfContext);

        try
        {
            await ChunkedAead.DecryptAsync(
                FetchChunksAsync(manifest, cancellationToken),
                output,
                derivedKey,
                manifest.NoncePrefix,
                cancellationToken).ConfigureAwait(false);

            return new EnvelopeReadResult(
                Manifest: manifest,
                SenderSecp256k1PublicKey: signed.SenderSecp256k1PublicKey,
                PlaintextBytesWritten: manifest.TotalSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    private async IAsyncEnumerable<EncryptedChunk> FetchChunksAsync(
        Manifest manifest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semaphore = new SemaphoreSlim(_maxParallelism, _maxParallelism);
        var fetchTasks = new Task<EncryptedChunk>[manifest.Chunks.Count];

        // Kick off all fetches up front. They each await the semaphore for
        // the actual network work; pending tasks are cheap (no ciphertext
        // held until the download starts).
        for (var i = 0; i < manifest.Chunks.Count; i++)
        {
            fetchTasks[i] = FetchOneChunkAsync(manifest.Chunks[i], semaphore, cancellationToken);
        }

        // Yield in chunk-index order. Each await may already be complete
        // by the time we get to it (prefetch effect).
        for (var i = 0; i < fetchTasks.Length; i++)
        {
            yield return await fetchTasks[i].ConfigureAwait(false);
        }
    }

    private async Task<EncryptedChunk> FetchOneChunkAsync(
        ManifestChunkEntry entry,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ciphertext = await _ipfs.FetchAsync(entry.Cid, cancellationToken).ConfigureAwait(false);
            if (ciphertext.Length != entry.CiphertextSize)
            {
                throw new CryptographicException(
                    $"Chunk {entry.Index}: fetched ciphertext length {ciphertext.Length} "
                    + $"does not match manifest-declared size {entry.CiphertextSize}.");
            }
            return new EncryptedChunk(entry.Index, ciphertext, entry.Tag);
        }
        finally
        {
            semaphore.Release();
        }
    }
}

public sealed record EnvelopeReadResult(
    Manifest Manifest,
    ReadOnlyMemory<byte> SenderSecp256k1PublicKey,
    long PlaintextBytesWritten);
