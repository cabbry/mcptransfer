using System.Text.Json;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Storage;

/// <summary>
/// On-disk format and helpers for persisting an <see cref="AgentIdentity"/>.
/// </summary>
/// <remarks>
/// <para>
/// The private keys are stored in plaintext JSON. This is a POC convenience
/// and a documented limitation: in a production system the file should be
/// encrypted at rest (passphrase-derived key, OS keyring, or TPM-backed).
/// </para>
/// </remarks>
public static class AgentIdentityFile
{
    public const int CurrentVersion = 1;

    /// <summary><c>~/.mcptx/identity.json</c> (cross-platform user profile).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".mcptx",
        "identity.json");

    public static async Task SaveAsync(
        AgentIdentity identity,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var bytes = Serialize(identity);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<AgentIdentity> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize(bytes);
    }

    public static byte[] Serialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "mlkem_private_key",
                Convert.ToBase64String(identity.MlKem.PrivateKeyEncoded));
            writer.WriteString(
                "secp256k1_private_key",
                "0x" + Convert.ToHexString(identity.Secp256k1.PrivateKey).ToLowerInvariant());
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static AgentIdentity Deserialize(ReadOnlySpan<byte> bytes)
    {
        using var doc = JsonDocument.Parse(bytes.ToArray());
        var root = doc.RootElement;

        var version = root.GetProperty("version").GetInt32();
        if (version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported identity file version: got {version}, expected {CurrentVersion}.");
        }

        var ecHex = root.GetProperty("secp256k1_private_key").GetString()
            ?? throw new InvalidOperationException("Missing 'secp256k1_private_key'.");
        var mlkemB64 = root.GetProperty("mlkem_private_key").GetString()
            ?? throw new InvalidOperationException("Missing 'mlkem_private_key'.");

        var ec = Secp256k1KeyPair.FromPrivateKeyHex(ecHex);
        var mlkem = MlKemKeyPair.FromEncodedPrivateKey(Convert.FromBase64String(mlkemB64));
        return AgentIdentity.FromKeys(ec, mlkem);
    }
}
