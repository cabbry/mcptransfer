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
public sealed class EnvelopeReader
{
    private readonly IIpfsClient _ipfs;

    public EnvelopeReader(IIpfsClient ipfs)
    {
        ArgumentNullException.ThrowIfNull(ipfs);
        _ipfs = ipfs;
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
            manifest.Sender, manifest.Recipient, manifest.NoncePrefix);

        var derivedKey = HybridKem.Decapsulate(
            recipient,
            manifest.EphemeralSecp256k1PublicKey,
            manifest.KemCiphertext,
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
        foreach (var entry in manifest.Chunks)
        {
            var ciphertext = await _ipfs.FetchAsync(entry.Cid, cancellationToken).ConfigureAwait(false);
            if (ciphertext.Length != entry.CiphertextSize)
            {
                throw new CryptographicException(
                    $"Chunk {entry.Index}: fetched ciphertext length {ciphertext.Length} "
                    + $"does not match manifest-declared size {entry.CiphertextSize}.");
            }
            yield return new EncryptedChunk(entry.Index, ciphertext, entry.Tag);
        }
    }
}

public sealed record EnvelopeReadResult(
    Manifest Manifest,
    byte[] SenderSecp256k1PublicKey,
    long PlaintextBytesWritten);
