using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Envelope;

/// <summary>
/// The IPFS-stored, on-the-wire representation of a transfer: a
/// <see cref="Manifest"/> plus the sender's public keys and a HYBRID
/// signature — classical ECDSA-secp256k1 AND post-quantum ML-DSA-65.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper preserves the <em>exact</em> bytes of the manifest as
/// transmitted; verification rehashes those bytes rather than
/// re-canonicalizing the parsed manifest, so any encoding drift fails.
/// </para>
/// <para>
/// Binding: the ECDSA signature (whose key derives to the sender's
/// Ethereum address — the identity anchor) is computed over
/// <c>Keccak256(manifestBytes ‖ mldsaPublicKey)</c>, so the
/// address-holder vouches for the ML-DSA public key. The ML-DSA
/// signature is computed over the manifest bytes and provides
/// post-quantum content authenticity. Both must verify.
/// </para>
/// <para>
/// Inherent ceiling (documented in <c>docs/CRYPTO.md</c>): because the
/// identity is an Ethereum address (ECDSA), the <em>binding</em> of any
/// PQC key to the identity is classically secured. ML-DSA protects the
/// authenticity of the manifest content post-quantum given a trusted
/// binding; it does not make the key-to-identity link post-quantum.
/// </para>
/// </remarks>
public sealed class SignedManifest
{
    private readonly byte[] _senderSecp256k1;
    private readonly byte[] _senderMlDsa;
    private readonly byte[] _ecdsaSignature;
    private readonly byte[] _mldsaSignature;
    private readonly byte[] _signedBytes;

    public Manifest Manifest { get; }
    public ReadOnlyMemory<byte> SenderSecp256k1PublicKey => _senderSecp256k1;
    public ReadOnlyMemory<byte> SenderMlDsaPublicKey => _senderMlDsa;
    public ReadOnlyMemory<byte> Signature => _ecdsaSignature;
    public ReadOnlyMemory<byte> MlDsaSignature => _mldsaSignature;

    private SignedManifest(
        Manifest manifest,
        byte[] senderSecp256k1,
        byte[] senderMlDsa,
        byte[] ecdsaSignature,
        byte[] mldsaSignature,
        byte[] signedBytes)
    {
        Manifest = manifest;
        _senderSecp256k1 = senderSecp256k1;
        _senderMlDsa = senderMlDsa;
        _ecdsaSignature = ecdsaSignature;
        _mldsaSignature = mldsaSignature;
        _signedBytes = signedBytes;
    }

    /// <summary>
    /// Produce a hybrid-signed manifest from the sender's full identity. The
    /// sender's address (declared in the manifest) must match the identity.
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
        var senderSecp256k1 = sender.Secp256k1.PublicKeyCompressed.ToArray();
        var senderMlDsa = sender.MlDsa.PublicKeyEncoded.ToArray();

        // ECDSA (address-anchored) signs over manifest ‖ mldsa pubkey, binding
        // the ML-DSA key to the sender's address.
        var ecdsaSignature = sender.Secp256k1.SignEcdsa(EcdsaDigest(bytes, senderMlDsa));
        // ML-DSA signs the manifest bytes (post-quantum content authenticity).
        var mldsaSignature = sender.MlDsa.Sign(bytes);

        return new SignedManifest(manifest, senderSecp256k1, senderMlDsa, ecdsaSignature, mldsaSignature, bytes);
    }

    /// <summary>
    /// Returns true iff ALL hold: the secp256k1 key derives to the manifest's
    /// sender address; the ECDSA signature verifies over
    /// <c>Keccak256(manifest ‖ mldsaPubkey)</c>; and the ML-DSA signature
    /// verifies over the manifest bytes.
    /// </summary>
    public bool VerifySignature()
    {
        var derivedAddress = Secp256k1KeyPair.AddressFromPublicKey(_senderSecp256k1);
        if (derivedAddress != Manifest.Sender)
            return false;

        if (!Secp256k1KeyPair.VerifyEcdsa(_senderSecp256k1, EcdsaDigest(_signedBytes, _senderMlDsa), _ecdsaSignature))
            return false;

        return MlDsaKeyPair.Verify(_senderMlDsa, _signedBytes, _mldsaSignature);
    }

    /// <summary>Keccak-256 of the manifest bytes that were signed (the on-chain content hash).</summary>
    public byte[] ContentHash() => Hashes.Keccak256(_signedBytes);

    /// <summary>
    /// The 32-byte digest the ECDSA signature covers: Keccak256 of the
    /// manifest bytes concatenated with the sender's ML-DSA public key.
    /// </summary>
    private static byte[] EcdsaDigest(byte[] manifestBytes, byte[] mldsaPubkey)
    {
        var combined = new byte[manifestBytes.Length + mldsaPubkey.Length];
        Buffer.BlockCopy(manifestBytes, 0, combined, 0, manifestBytes.Length);
        Buffer.BlockCopy(mldsaPubkey, 0, combined, manifestBytes.Length, mldsaPubkey.Length);
        return Hashes.Keccak256(combined);
    }

    /// <summary>
    /// Canonical JSON form for IPFS storage: the manifest embedded verbatim
    /// plus both sender public keys and both signatures.
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
            writer.WriteBase64String("mldsa_signature", _mldsaSignature);
            writer.WriteBase64String("sender_mldsa", _senderMlDsa);
            writer.WriteBase64String("sender_secp256k1", _senderSecp256k1);
            writer.WriteBase64String("signature", _ecdsaSignature);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Parse a hybrid-signed manifest. The verbatim bytes of the nested
    /// <c>manifest</c> element are preserved for verification (not re-canonicalized).
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

        var ecdsaSig = root.GetProperty("signature").GetBytesFromBase64();
        if (ecdsaSig.Length != Secp256k1KeyPair.SignatureByteLength)
        {
            throw new InvalidOperationException(
                $"signature must be {Secp256k1KeyPair.SignatureByteLength} bytes "
                + $"(got {ecdsaSig.Length}).");
        }

        var senderMlDsa = root.GetProperty("sender_mldsa").GetBytesFromBase64();
        if (senderMlDsa.Length != MlDsaKeyPair.PublicKeyByteLength)
        {
            throw new InvalidOperationException(
                $"sender_mldsa must be {MlDsaKeyPair.PublicKeyByteLength} bytes "
                + $"(got {senderMlDsa.Length}).");
        }

        var mldsaSig = root.GetProperty("mldsa_signature").GetBytesFromBase64();
        if (mldsaSig.Length != MlDsaKeyPair.SignatureByteLength)
        {
            throw new InvalidOperationException(
                $"mldsa_signature must be {MlDsaKeyPair.SignatureByteLength} bytes "
                + $"(got {mldsaSig.Length}).");
        }

        return new SignedManifest(manifest, senderPk, senderMlDsa, ecdsaSig, mldsaSig, manifestRawBytes);
    }
}
