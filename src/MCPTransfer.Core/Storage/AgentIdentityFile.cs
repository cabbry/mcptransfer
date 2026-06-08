using System.Runtime.InteropServices;
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
    /// <summary>
    /// v2 adds the ML-DSA-65 signing key. v1 files (secp256k1 + ML-KEM only)
    /// are no longer loadable — regenerate with <c>mcptx keygen --force</c>.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary><c>~/.mcptx/identity.json</c> (cross-platform user profile).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".mcptx",
        "identity.json");

    /// <summary>
    /// Persist <paramref name="identity"/> atomically: the bytes are written
    /// to a sibling <c>.tmp</c> file, the permissions are tightened on POSIX
    /// systems, and only then the file is renamed into place. A crash or
    /// cancellation mid-write leaves the previous identity file (if any)
    /// untouched.
    /// </summary>
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
        var tempPath = path + ".tmp";

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            // Restrict permissions on the temp file BEFORE the rename so the
            // final file is never readable by other users, even momentarily.
            TryRestrictUnixPermissions(tempPath);
            File.Move(tempPath, path, overwrite: true);
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

    /// <summary>
    /// On POSIX (Linux, macOS), tighten the file mode to <c>0600</c> so
    /// other local users cannot read the plaintext private keys. No-op on
    /// Windows (file inherits ACLs from the user profile directory, which
    /// is already user-only by default).
    /// </summary>
    private static void TryRestrictUnixPermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort: some filesystems (e.g. FAT, certain network mounts)
            // do not support POSIX modes; silently continue.
        }
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
                "mldsa_private_key",
                Convert.ToBase64String(identity.MlDsa.PrivateKeyEncoded));
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

        if (!root.TryGetProperty("version", out var versionEl) || versionEl.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException("Identity file is missing a numeric 'version'.");
        var version = versionEl.GetInt32();
        if (version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported identity file version: got {version}, expected {CurrentVersion}. "
                + (version < CurrentVersion
                    ? "This identity predates the ML-DSA signing key; regenerate with 'mcptx keygen --force'."
                    : "This file was written by a newer mcptx build."));
        }

        var ecHex = RequireString(root, "secp256k1_private_key");
        var mlkemB64 = RequireString(root, "mlkem_private_key");
        var mldsaB64 = RequireString(root, "mldsa_private_key");

        var ec = Secp256k1KeyPair.FromPrivateKeyHex(ecHex);
        var mlkem = MlKemKeyPair.FromEncodedPrivateKey(Convert.FromBase64String(mlkemB64));
        var mldsa = MlDsaKeyPair.FromEncodedPrivateKey(Convert.FromBase64String(mldsaB64));
        return AgentIdentity.FromKeys(ec, mlkem, mldsa);
    }

    /// <summary>
    /// Read a required string property, throwing a descriptive
    /// <see cref="InvalidOperationException"/> if it is absent or not a string
    /// (rather than letting <c>GetProperty</c> raise a bare KeyNotFoundException).
    /// </summary>
    private static string RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Identity file is missing string property '{name}'.");
        return el.GetString()!;
    }
}
