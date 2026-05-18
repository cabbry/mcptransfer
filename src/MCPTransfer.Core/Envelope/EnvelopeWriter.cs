using System.Security.Cryptography;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Core.Envelope;

/// <summary>
/// Sender-side orchestrator: takes a plaintext stream and produces a
/// signed manifest plus a population of encrypted chunks in an
/// <see cref="IIpfsClient"/>.
/// </summary>
public sealed class EnvelopeWriter
{
    private readonly IIpfsClient _ipfs;

    public EnvelopeWriter(IIpfsClient ipfs)
    {
        ArgumentNullException.ThrowIfNull(ipfs);
        _ipfs = ipfs;
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

        var encapsulation = HybridKem.Encapsulate(recipient, hkdfContext);

        try
        {
            var chunkEntries = new List<ManifestChunkEntry>();
            long totalSize = 0;

            await foreach (var chunk in ChunkedAead.EncryptAsync(
                input,
                encapsulation.DerivedKey,
                noncePrefix,
                chunkSize,
                cancellationToken).ConfigureAwait(false))
            {
                var cid = await _ipfs.PinAsync(chunk.Ciphertext, cancellationToken).ConfigureAwait(false);
                chunkEntries.Add(new ManifestChunkEntry(
                    index: chunk.Index,
                    cid: cid,
                    tag: chunk.Tag,
                    ciphertextSize: chunk.Ciphertext.Length));
                totalSize += chunk.Ciphertext.Length;
            }

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
        finally
        {
            CryptographicOperations.ZeroMemory(encapsulation.DerivedKey);
        }
    }
}

public sealed record EnvelopeWriteResult(string ManifestCid, SignedManifest SignedManifest);
