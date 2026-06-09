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

    /// <summary>
    /// Decrypt a transfer into the provided <paramref name="output"/> stream.
    /// </summary>
    /// <remarks>
    /// On a tag-mismatch or any other mid-stream failure, the <paramref name="output"/>
    /// stream may already contain a partial prefix of the plaintext (everything written
    /// before the failing chunk). Callers that need an all-or-nothing guarantee should
    /// use <see cref="ReceiveToFileAsync"/>, which writes to a side-by-side temporary
    /// file and renames into place only after the full decrypt succeeds.
    /// </remarks>
    public async Task<EnvelopeReadResult> ReceiveAsync(
        string manifestCid,
        AgentIdentity recipient,
        Stream output,
        byte[]? expectedContentHash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestCid);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(output);

        var signedBytes = await _ipfs.FetchAsync(manifestCid, cancellationToken).ConfigureAwait(false);
        var signed = SignedManifest.FromJsonBytes(signedBytes);

        if (!signed.VerifySignature())
            throw new InvalidOperationException("Manifest signature does not verify.");

        // If the caller supplied the content hash announced on chain (from the
        // FileSent event), require the fetched manifest to match it byte-for-byte
        // BEFORE doing any work. This ties the delivered bytes to the on-chain
        // record and defeats a substituted (but otherwise validly-signed)
        // manifest served at the same CID by a non-content-addressed backend.
        // The parameter is deliberately byte[]? (NOT ReadOnlyMemory<byte>?):
        // the implicit byte[]→ReadOnlyMemory conversion turns a null array
        // into an EMPTY memory with HasValue=true, which silently converted
        // "no hash to check" into "check against an empty hash" at call sites.
        if (expectedContentHash is not null)
        {
            if (expectedContentHash.Length != Hashes.Keccak256ByteLength
                || !CryptographicOperations.FixedTimeEquals(signed.ContentHash(), expectedContentHash))
            {
                throw new InvalidOperationException(
                    "Manifest content hash does not match the expected (on-chain) content hash. "
                    + "The CID may have been substituted; refusing to decrypt.");
            }
        }

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
            manifest.Sender, manifest.Recipient, recipient.MlKem.PublicKey.Bytes, manifest.NoncePrefix.Span);

        var derivedKey = HybridKem.Decapsulate(
            recipient,
            manifest.EphemeralSecp256k1PublicKey.Span,
            manifest.KemCiphertext.Span,
            hkdfContext);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        // Kick off all chunk fetches up front; bounded parallelism via the semaphore
        // inside FetchOneChunkAsync. The task array lives here (not inside the iterator)
        // so the catch block below can drain them on failure.
        var semaphore = new SemaphoreSlim(_maxParallelism, _maxParallelism);
        var fetchTasks = new Task<EncryptedChunk>[manifest.Chunks.Count];
        for (var i = 0; i < manifest.Chunks.Count; i++)
            fetchTasks[i] = FetchOneChunkAsync(manifest.Chunks[i], semaphore, ct);

        try
        {
            await ChunkedAead.DecryptAsync(
                YieldChunksInOrder(fetchTasks, ct),
                output,
                derivedKey,
                manifest.NoncePrefix,
                ct).ConfigureAwait(false);

            return new EnvelopeReadResult(
                Manifest: manifest,
                SenderSecp256k1PublicKey: signed.SenderSecp256k1PublicKey,
                PlaintextBytesWritten: manifest.TotalSize);
        }
        catch
        {
            // Drain: cancel pending fetches and wait for them to settle so we do
            // not leak running tasks (or wasted IPFS calls) past the failure.
            linkedCts.Cancel();
            try
            {
                await Task.WhenAll(fetchTasks).ConfigureAwait(false);
            }
            catch
            {
                // Swallow — the original exception is what the caller cares about.
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    /// <summary>
    /// Decrypt a transfer into a file at <paramref name="outputPath"/> with
    /// atomic semantics: nothing is written under that path until the full
    /// decrypt + verify has succeeded.
    /// </summary>
    /// <remarks>
    /// The implementation writes to <c>&lt;outputPath&gt;.mcptx-tmp</c> first and
    /// only renames into place after <see cref="ReceiveAsync"/> returns
    /// successfully. On any failure the temp file is best-effort deleted, so
    /// the destination path stays untouched (or, if it pre-existed, unchanged).
    /// </remarks>
    public async Task<EnvelopeReadResult> ReceiveToFileAsync(
        string manifestCid,
        AgentIdentity recipient,
        string outputPath,
        byte[]? expectedContentHash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var tempPath = outputPath + ".mcptx-tmp";

        try
        {
            EnvelopeReadResult result;
            await using (var tempStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                result = await ReceiveAsync(
                    manifestCid, recipient, tempStream, expectedContentHash, cancellationToken).ConfigureAwait(false);
            }

            // Stream is now closed and flushed; rename into place atomically (Move with
            // overwrite=true is atomic on NTFS/ext4 within the same volume).
            File.Move(tempPath, outputPath, overwrite: true);
            return result;
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup; ignore secondary errors.
            }
            throw;
        }
    }

    private static async IAsyncEnumerable<EncryptedChunk> YieldChunksInOrder(
        Task<EncryptedChunk>[] fetchTasks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < fetchTasks.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
