using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Envelope;

namespace MCPTransfer.Tests.Envelope;

public class SignedManifestTests
{
    private static (AgentIdentity sender, Manifest manifest) BuildSampleForSender()
    {
        var sender = AgentIdentity.Generate();
        var recipient = AgentIdentity.Generate();

        var ephPk = new byte[Secp256k1KeyPair.PublicKeyCompressedByteLength];
        var kemCt = new byte[MlKemPublicKey.CiphertextByteLength];
        var noncePrefix = new byte[ChunkedAead.NoncePrefixByteLength];
        var tag = new byte[ChunkedAead.TagByteLength];
        RandomNumberGenerator.Fill(ephPk);
        RandomNumberGenerator.Fill(kemCt);
        RandomNumberGenerator.Fill(noncePrefix);
        RandomNumberGenerator.Fill(tag);

        var manifest = new Manifest(
            version: Manifest.CurrentVersion,
            suite: HybridKem.SuiteIdentifier,
            sender: sender.Address,
            recipient: recipient.Address,
            ephemeralSecp256k1PublicKey: ephPk,
            kemCiphertext: kemCt,
            noncePrefix: noncePrefix,
            chunkSize: 16 * 1024 * 1024,
            totalSize: 42,
            createdAtUnixSeconds: 1_700_000_000,
            chunks: new[] { new ManifestChunkEntry(0, "bafytest", tag, 42) },
            filename: "doc.pdf",
            mimeType: "application/pdf");

        return (sender, manifest);
    }

    [Fact]
    public void Create_ProducesValidSignedManifest()
    {
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);

        Assert.Equal(Secp256k1KeyPair.PublicKeyCompressedByteLength,
            signed.SenderSecp256k1PublicKey.Length);
        Assert.Equal(Secp256k1KeyPair.SignatureByteLength,
            signed.Signature.Length);
        Assert.True(signed.VerifySignature());
    }

    [Fact]
    public void Create_RejectsMismatchedSenderIdentity()
    {
        var (_, manifest) = BuildSampleForSender();
        var otherSender = AgentIdentity.Generate();

        Assert.Throws<InvalidOperationException>(
            () => SignedManifest.Create(manifest, otherSender));
    }

    [Fact]
    public void RoundTrip_ThroughCanonicalJson_VerifiesSuccessfully()
    {
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);

        var bytes = signed.ToCanonicalJsonBytes();
        var parsed = SignedManifest.FromJsonBytes(bytes);

        Assert.True(parsed.VerifySignature());
        Assert.Equal(signed.SenderSecp256k1PublicKey, parsed.SenderSecp256k1PublicKey);
        Assert.Equal(signed.Signature, parsed.Signature);
        Assert.Equal(signed.ContentHash(), parsed.ContentHash());
        Assert.Equal(signed.Manifest.Sender, parsed.Manifest.Sender);
        Assert.Equal(signed.Manifest.Recipient, parsed.Manifest.Recipient);
    }

    [Fact]
    public void VerifySignature_FailsAfterManifestBitFlip()
    {
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);

        var bytes = signed.ToCanonicalJsonBytes();

        // Locate "doc.pdf" in the canonical JSON and corrupt one character.
        var asString = Encoding.UTF8.GetString(bytes);
        var idx = asString.IndexOf("doc.pdf", StringComparison.Ordinal);
        Assert.True(idx > 0);
        bytes[idx] = (byte)'D'; // 'd' -> 'D'

        var tampered = SignedManifest.FromJsonBytes(bytes);
        Assert.False(tampered.VerifySignature());
    }

    [Fact]
    public void VerifySignature_FailsAfterSignatureBitFlip()
    {
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);

        // Round-trip through JSON with the signature field swapped for a
        // random (but well-formed) one. The immutable surface prevents
        // direct mutation of signed.Signature.
        var json = Encoding.UTF8.GetString(signed.ToCanonicalJsonBytes());
        var randomSig = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(
                Secp256k1KeyPair.SignatureByteLength));
        var corrupted = System.Text.RegularExpressions.Regex.Replace(
            json,
            "\"signature\":\"[^\"]*\"",
            "\"signature\":\"" + randomSig + "\"");

        var tampered = SignedManifest.FromJsonBytes(Encoding.UTF8.GetBytes(corrupted));
        Assert.False(tampered.VerifySignature());
    }

    [Fact]
    public void VerifySignature_FailsIfPubKeyDoesNotMatchAddress()
    {
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);

        // Substitute the sender_secp256k1 field with another agent's pubkey
        // via JSON round-trip. The original signature is still cryptographic-
        // ally valid against the original sender, but VerifySignature also
        // checks that the embedded pubkey derives to the address declared
        // inside the manifest — and the substituted pubkey does not.
        var attacker = Secp256k1KeyPair.Generate();
        var attackerPkB64 = Convert.ToBase64String(attacker.PublicKeyCompressed);

        var json = Encoding.UTF8.GetString(signed.ToCanonicalJsonBytes());
        var corrupted = System.Text.RegularExpressions.Regex.Replace(
            json,
            "\"sender_secp256k1\":\"[^\"]*\"",
            "\"sender_secp256k1\":\"" + attackerPkB64 + "\"");

        var tampered = SignedManifest.FromJsonBytes(Encoding.UTF8.GetBytes(corrupted));
        Assert.False(tampered.VerifySignature());
    }

    [Fact]
    public void CanonicalJson_TopLevelOrderIsAlphabetical()
    {
        var (sender, manifest) = BuildSampleForSender();
        var json = Encoding.UTF8.GetString(SignedManifest.Create(manifest, sender).ToCanonicalJsonBytes());

        var posManifest = json.IndexOf("\"manifest\"", StringComparison.Ordinal);
        var posSender = json.IndexOf("\"sender_secp256k1\"", StringComparison.Ordinal);
        var posSignature = json.IndexOf("\"signature\"", StringComparison.Ordinal);

        Assert.True(posManifest >= 0 && posSender > posManifest && posSignature > posSender);
    }

    [Fact]
    public void FromJsonBytes_PreservesExactManifestBytes()
    {
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);

        // Re-serialize with extra whitespace; the embedded manifest bytes
        // must stay verbatim for verification to remain valid.
        var bytes = signed.ToCanonicalJsonBytes();
        var indented = ReformatPreservingManifest(bytes);

        var parsed = SignedManifest.FromJsonBytes(indented);
        Assert.True(parsed.VerifySignature());
    }

    private static byte[] ReformatPreservingManifest(byte[] canonical)
    {
        // Add a leading space outside the manifest field — manifest stays untouched.
        using var doc = JsonDocument.Parse(canonical);
        var root = doc.RootElement;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            // Property order intentionally preserved, but indented to introduce whitespace.
            writer.WritePropertyName("manifest");
            writer.WriteRawValue(root.GetProperty("manifest").GetRawText(), skipInputValidation: false);
            writer.WriteString("mldsa_signature", root.GetProperty("mldsa_signature").GetString());
            writer.WriteString("sender_mldsa", root.GetProperty("sender_mldsa").GetString());
            writer.WriteString("sender_secp256k1", root.GetProperty("sender_secp256k1").GetString());
            writer.WriteString("signature", root.GetProperty("signature").GetString());
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    [Fact]
    public void FromJsonBytes_RejectsWrongPublicKeyLength()
    {
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);
        var bytes = signed.ToCanonicalJsonBytes();
        var json = Encoding.UTF8.GetString(bytes);

        // Replace the sender_secp256k1 base64 value with a too-short value.
        var corrupted = System.Text.RegularExpressions.Regex.Replace(
            json,
            "\"sender_secp256k1\":\"[^\"]*\"",
            "\"sender_secp256k1\":\"" + Convert.ToBase64String(new byte[32]) + "\"");

        Assert.Throws<InvalidOperationException>(
            () => SignedManifest.FromJsonBytes(Encoding.UTF8.GetBytes(corrupted)));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Hybrid (ECDSA + ML-DSA) signature — v2
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_ProducesBothSignatures_WithExpectedSizes()
    {
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);

        Assert.Equal(Secp256k1KeyPair.SignatureByteLength, signed.Signature.Length);
        Assert.Equal(MlDsaKeyPair.SignatureByteLength, signed.MlDsaSignature.Length);
        Assert.Equal(MlDsaKeyPair.PublicKeyByteLength, signed.SenderMlDsaPublicKey.Length);
        Assert.True(signed.VerifySignature());
    }

    [Fact]
    public void DualSignature_RoundTripsThroughJson()
    {
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);

        var parsed = SignedManifest.FromJsonBytes(signed.ToCanonicalJsonBytes());
        Assert.True(parsed.VerifySignature());
        Assert.Equal(signed.MlDsaSignature.ToArray(), parsed.MlDsaSignature.ToArray());
        Assert.Equal(signed.SenderMlDsaPublicKey.ToArray(), parsed.SenderMlDsaPublicKey.ToArray());
    }

    [Fact]
    public void VerifySignature_FailsAfterMlDsaSignatureBitFlip()
    {
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);

        var json = Encoding.UTF8.GetString(signed.ToCanonicalJsonBytes());
        var randomMlDsaSig = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(MlDsaKeyPair.SignatureByteLength));
        var corrupted = System.Text.RegularExpressions.Regex.Replace(
            json, "\"mldsa_signature\":\"[^\"]*\"", "\"mldsa_signature\":\"" + randomMlDsaSig + "\"");

        var tampered = SignedManifest.FromJsonBytes(Encoding.UTF8.GetBytes(corrupted));
        Assert.False(tampered.VerifySignature());
    }

    [Fact]
    public void VerifySignature_FailsIfMlDsaPubkeySwapped()
    {
        // Swap the sender_mldsa pubkey for a different (well-formed) one. The
        // ECDSA signature is computed over manifest || mldsaPubkey, so changing
        // the ML-DSA pubkey breaks the ECDSA binding even before the ML-DSA
        // signature is checked.
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);

        var attacker = MlDsaKeyPair.Generate();
        var attackerPkB64 = Convert.ToBase64String(attacker.PublicKeyEncoded);

        var json = Encoding.UTF8.GetString(signed.ToCanonicalJsonBytes());
        var corrupted = System.Text.RegularExpressions.Regex.Replace(
            json, "\"sender_mldsa\":\"[^\"]*\"", "\"sender_mldsa\":\"" + attackerPkB64 + "\"");

        var tampered = SignedManifest.FromJsonBytes(Encoding.UTF8.GetBytes(corrupted));
        Assert.False(tampered.VerifySignature());
    }

    [Fact]
    public void FromJsonBytes_RejectsWrongMlDsaPubkeyLength()
    {
        var (sender, manifest) = BuildSampleForSender();
        var signed = SignedManifest.Create(manifest, sender);
        var json = Encoding.UTF8.GetString(signed.ToCanonicalJsonBytes());

        var corrupted = System.Text.RegularExpressions.Regex.Replace(
            json, "\"sender_mldsa\":\"[^\"]*\"", "\"sender_mldsa\":\"" + Convert.ToBase64String(new byte[100]) + "\"");

        Assert.Throws<InvalidOperationException>(
            () => SignedManifest.FromJsonBytes(Encoding.UTF8.GetBytes(corrupted)));
    }
}
