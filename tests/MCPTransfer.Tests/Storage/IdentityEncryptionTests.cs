using System.Text;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Storage;

namespace MCPTransfer.Tests.Storage;

/// <summary>
/// v3 encrypted identity file: Argon2id + AES-256-GCM with the KDF header
/// bound as associated data. Tests use deliberately cheap Argon2 costs —
/// the format stores whatever costs were used, so this exercises the same
/// code path as the production defaults.
/// </summary>
public class IdentityEncryptionTests
{
    private static readonly IdentityEncryptionParams CheapKdf = new(MemoryKib: 64, Iterations: 1, Parallelism: 1);
    private const string Passphrase = "correct horse battery staple";

    [Fact]
    public void EncryptedRoundTrip_PreservesIdentity()
    {
        var original = AgentIdentity.Generate();
        var bytes = AgentIdentityFile.SerializeEncrypted(original, Passphrase, CheapKdf);
        var restored = AgentIdentityFile.Deserialize(bytes, Passphrase);

        Assert.Equal(original.Address, restored.Address);
        Assert.True(original.Secp256k1.PrivateKey.SequenceEqual(restored.Secp256k1.PrivateKey));
        Assert.True(original.MlKem.PrivateKeyEncoded.SequenceEqual(restored.MlKem.PrivateKeyEncoded));
        Assert.True(original.MlDsa.PrivateKeyEncoded.SequenceEqual(restored.MlDsa.PrivateKeyEncoded));
    }

    [Fact]
    public void EncryptedFile_ContainsNoPlaintextKeyMaterial()
    {
        var identity = AgentIdentity.Generate();
        var bytes = AgentIdentityFile.SerializeEncrypted(identity, Passphrase, CheapKdf);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"version\": 3", json);
        Assert.Contains("argon2id", json);
        Assert.DoesNotContain("secp256k1_private_key", json);
        Assert.DoesNotContain("mlkem_private_key", json);
        Assert.DoesNotContain("mldsa_private_key", json);
        // The raw secp256k1 key hex must not appear anywhere in the envelope.
        var skHex = Convert.ToHexString(identity.Secp256k1.PrivateKey).ToLowerInvariant();
        Assert.DoesNotContain(skHex, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongPassphrase_FailsWithCleanMessage()
    {
        var bytes = AgentIdentityFile.SerializeEncrypted(AgentIdentity.Generate(), Passphrase, CheapKdf);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentIdentityFile.Deserialize(bytes, "wrong passphrase"));
        Assert.Contains("passphrase", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingPassphrase_TellsUserAboutEnvVar()
    {
        var bytes = AgentIdentityFile.SerializeEncrypted(AgentIdentity.Generate(), Passphrase, CheapKdf);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentIdentityFile.Deserialize(bytes, passphrase: null));
        Assert.Contains(AgentIdentityFile.PassphraseEnvVar, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TamperedKdfHeader_FailsAuthentication()
    {
        var bytes = AgentIdentityFile.SerializeEncrypted(AgentIdentity.Generate(), Passphrase, CheapKdf);

        // Weaken the stored iteration count: the header is AAD-bound, so the
        // tag must no longer verify even with the right passphrase.
        var json = Encoding.UTF8.GetString(bytes)
            .Replace("\"argon2_iterations\": 1", "\"argon2_iterations\": 2", StringComparison.Ordinal);
        var tampered = Encoding.UTF8.GetBytes(json);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentIdentityFile.Deserialize(tampered, Passphrase));
        Assert.Contains("tampered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TamperedCiphertext_FailsAuthentication()
    {
        var identity = AgentIdentity.Generate();
        var bytes = AgentIdentityFile.SerializeEncrypted(identity, Passphrase, CheapKdf);

        // Flip one base64 char of the ciphertext field.
        var json = Encoding.UTF8.GetString(bytes);
        var marker = "\"ciphertext\": \"";
        var start = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var c = json[start];
        json = json.Remove(start, 1).Insert(start, c == 'A' ? "B" : "A");

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentIdentityFile.Deserialize(Encoding.UTF8.GetBytes(json), Passphrase));
        Assert.Contains("passphrase", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlaintextV2_StillLoads_WithOrWithoutPassphrase()
    {
        var identity = AgentIdentity.Generate();
        var v2 = AgentIdentityFile.Serialize(identity);

        // A passphrase in the environment must not break v2 files.
        Assert.Equal(identity.Address, AgentIdentityFile.Deserialize(v2, "ignored").Address);
        Assert.Equal(identity.Address, AgentIdentityFile.Deserialize(v2, null).Address);
    }

    [Fact]
    public async Task SaveAsync_WithPassphrase_WritesV3AndLoadsBack()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcptx-enc-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(tempDir, "identity.json");
        try
        {
            var original = AgentIdentity.Generate();
            await AgentIdentityFile.SaveAsync(original, path, Passphrase, CheapKdf);

            var onDisk = await File.ReadAllTextAsync(path);
            Assert.Contains("\"version\": 3", onDisk);

            var restored = await AgentIdentityFile.LoadAsync(path, Passphrase);
            Assert.Equal(original.Address, restored.Address);

            // And the no-passphrase load fails loudly rather than silently.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => AgentIdentityFile.LoadAsync(path));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DefaultKdfParams_AreOwaspBaseline()
    {
        var p = IdentityEncryptionParams.Default;
        Assert.Equal(19_456, p.MemoryKib);
        Assert.Equal(2, p.Iterations);
        Assert.Equal(1, p.Parallelism);
    }
}
