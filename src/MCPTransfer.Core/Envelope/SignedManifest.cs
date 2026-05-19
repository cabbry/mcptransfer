using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Envelope;

/// <summary>
/// The IPFS-stored, on-the-wire representation of a transfer: a
/// <see cref="Manifest"/> plus the sender's compressed secp256k1 public
/// key plus the ECDSA signature over the canonical manifest bytes.
/// </summary>
/// <remarks>
/// The wrapper preserves the <em>exact</em> bytes of the manifest as
/// transmitted. Signature verification rehashes those bytes rather than
/// re-canonicalizing the parsed manifest, so any encoding drift fails
/// verification (the conservative choice).
/// </remarks>
public sealed class SignedManifest
{
    private readonly byte[] _senderPublicKey;
    private readonly byte[] _signature;
    private readonly byte[] _signedBytes;

    public Manifest Manifest { get; }
    public ReadOnlyMemory<byte> SenderSecp256k1PublicKey => _senderPublicKey;
    public ReadOnlyMemory<byte> Signature => _signature;

    private SignedManifest(
        Manifest manifest,
        byte[] senderPublicKey,
        byte[] signature,
        byte[] signedBytes)
    {
        Manifest = manifest;
        _senderPublicKey = senderPublicKey;
        _signature = signature;
        _signedBytes = signedBytes;
    }

    /// <summary>
    /// Produce a signed manifest from the sender's full identity. The
    /// sender's address (as declared in the manifest) must match the
    /// identity's address.
    /// </summary>
    public static SignedManifest Create(Manifest manifest, AgentIdentity sender)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(sender);
        if (manifest.Sender != sender.Address)
        {
            throw new InvalidOperationException(
                $"Sender identity address {sender.Address} does not match "
                + $"manifest sender {manifest.Sender}.");
        }

        var bytes = manifest.ToCanonicalJsonBytes();
        var hash = Hashes.Keccak256(bytes);
        var signature = sender.Secp256k1.SignEcdsa(hash);
        var senderPublicKey = sender.Secp256k1.PublicKeyCompressed.ToArray();

        return new SignedManifest(manifest, senderPublicKey, signature, bytes);
    }

    /// <summary>
    /// Returns true iff the signature verifies against the embedded sender
    /// public key, AND that public key derives to the address declared in
    /// the manifest.
    /// </summary>
    public bool VerifySignature()
    {
        var derivedAddress = Secp256k1KeyPair.AddressFromPublicKey(_senderPublicKey);
        if (derivedAddress != Manifest.Sender)
            return false;

        var hash = Hashes.Keccak256(_signedBytes);
        return Secp256k1KeyPair.VerifyEcdsa(_senderPublicKey, hash, _signature);
    }

    /// <summary>Keccak-256 of the manifest bytes that were signed.</summary>
    public byte[] ContentHash() => Hashes.Keccak256(_signedBytes);

    /// <summary>
    /// Canonical JSON form for IPFS storage: an outer object with the
    /// manifest embedded verbatim plus the sender public key and signature.
    /// </summary>
    public byte[] ToCanonicalJsonBytes()
    {
        using var stream = new MemoryStream();
        var options = new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        using (var writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("manifest");
            writer.WriteRawValue(_signedBytes, skipInputValidation: false);
            writer.WriteBase64String("sender_secp256k1", _senderPublicKey);
            writer.WriteBase64String("signature", _signature);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Parse a signed manifest from JSON bytes. The verbatim bytes of the
    /// nested <c>manifest</c> element are preserved internally for signature
    /// verification — they are <em>not</em> re-canonicalized.
    /// </summary>
    public static SignedManifest FromJsonBytes(ReadOnlySpan<byte> jsonBytes)
    {
        using var doc = JsonDocument.Parse(jsonBytes.ToArray());
        var root = doc.RootElement;

        var manifestElement = root.GetProperty("manifest");
        var manifestRawText = manifestElement.GetRawText();
        var manifestRawBytes = Encoding.UTF8.GetBytes(manifestRawText);
        var manifest = Manifest.FromJsonBytes(manifestRawBytes);

        var senderPk = root.GetProperty("sender_secp256k1").GetBytesFromBase64();
        if (senderPk.Length != Secp256k1KeyPair.PublicKeyCompressedByteLength)
        {
            throw new InvalidOperationException(
                $"sender_secp256k1 must be {Secp256k1KeyPair.PublicKeyCompressedByteLength} bytes "
                + $"(got {senderPk.Length}).");
        }

        var signature = root.GetProperty("signature").GetBytesFromBase64();
        if (signature.Length != Secp256k1KeyPair.SignatureByteLength)
        {
            throw new InvalidOperationException(
                $"signature must be {Secp256k1KeyPair.SignatureByteLength} bytes "
                + $"(got {signature.Length}).");
        }

        return new SignedManifest(manifest, senderPk, signature, manifestRawBytes);
    }
}
