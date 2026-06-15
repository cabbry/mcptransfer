using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCPTransfer.Core.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace MCPTransfer.Core.Storage;

/// <summary>
/// Argon2id cost parameters for the encrypted identity file (v3). The
/// defaults follow the OWASP baseline (19 MiB, t=2, p=1); the parameters
/// used at save time are stored in the file header (and bound into the
/// AES-GCM associated data) so loading always uses what was written.
/// </summary>
public sealed record IdentityEncryptionParams(int MemoryKib, int Iterations, int Parallelism)
{
    public static IdentityEncryptionParams Default { get; } = new(19_456, 2, 1);

    // Upper bounds matter for security, not just sanity: the cost parameters
    // are read from the (potentially corrupt or hostile) v3 file header, and
    // DeriveKey allocates `MemoryKib` BEFORE the AES-GCM tag is checked. Without
    // a ceiling, a crafted header (e.g. argon2_memory_kib near int.MaxValue ≈
    // 2 TiB) would OOM/hang the loader. 1 GiB is ~55× the default — far beyond
    // any legitimate config, well short of a process-killing allocation.
    /// <summary>Hard ceiling on Argon2 memory (1 GiB), enforced on load.</summary>
    public const int MaxMemoryKib = 1_048_576;
    /// <summary>Hard ceiling on Argon2 iterations.</summary>
    public const int MaxIterations = 64;
    /// <summary>Hard ceiling on Argon2 parallelism (lanes).</summary>
    public const int MaxParallelism = 16;

    internal void Validate()
    {
        if (MemoryKib is < 8 or > MaxMemoryKib)
            throw new ArgumentOutOfRangeException(nameof(MemoryKib), $"Argon2 memory must be in [8, {MaxMemoryKib}] KiB (got {MemoryKib}).");
        if (Iterations is < 1 or > MaxIterations)
            throw new ArgumentOutOfRangeException(nameof(Iterations), $"Argon2 iterations must be in [1, {MaxIterations}] (got {Iterations}).");
        if (Parallelism is < 1 or > MaxParallelism)
            throw new ArgumentOutOfRangeException(nameof(Parallelism), $"Argon2 parallelism must be in [1, {MaxParallelism}] (got {Parallelism}).");
    }
}

/// <summary>
/// On-disk format and helpers for persisting an <see cref="AgentIdentity"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two formats coexist: v2 stores the private keys in plaintext JSON (POC
/// convenience); v3 wraps that same payload in passphrase encryption —
/// Argon2id derives a 32-byte key that AES-256-GCM uses over the v2 JSON,
/// with the KDF header (salt + costs) bound as associated data so a tampered
/// header fails authentication. The passphrase reaches the CLI/MCP via the
/// <c>MCPTX_PASSPHRASE</c> environment variable.
/// </para>
/// </remarks>
public static class AgentIdentityFile
{
    /// <summary>
    /// v2 adds the ML-DSA-65 signing key. v1 files (secp256k1 + ML-KEM only)
    /// are no longer loadable — regenerate with <c>mcptx keygen --force</c>.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>v3 = the v2 payload encrypted at rest (Argon2id + AES-256-GCM).</summary>
    public const int EncryptedVersion = 3;

    /// <summary>Environment variable the CLI/MCP read the passphrase from.</summary>
    public const string PassphraseEnvVar = "MCPTX_PASSPHRASE";

    private const int SaltByteLength = 16;
    private const int NonceByteLength = 12;
    private const int TagByteLength = 16;
    private const int AesKeyByteLength = 32;

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
    public static Task SaveAsync(
        AgentIdentity identity,
        string path,
        CancellationToken cancellationToken = default)
        => SaveAsync(identity, path, passphrase: null, kdfParams: null, cancellationToken);

    /// <summary>
    /// Persist <paramref name="identity"/>; when <paramref name="passphrase"/>
    /// is non-empty the file is written encrypted (v3), otherwise as
    /// plaintext v2.
    /// </summary>
    public static async Task SaveAsync(
        AgentIdentity identity,
        string path,
        string? passphrase,
        IdentityEncryptionParams? kdfParams = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var bytes = string.IsNullOrEmpty(passphrase)
            ? Serialize(identity)
            : SerializeEncrypted(identity, passphrase, kdfParams ?? IdentityEncryptionParams.Default);
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

    public static Task<AgentIdentity> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
        => LoadAsync(path, passphrase: null, cancellationToken);

    /// <summary>
    /// Load an identity file; <paramref name="passphrase"/> is required for
    /// v3 (encrypted) files and ignored for v2 (plaintext) ones.
    /// </summary>
    public static async Task<AgentIdentity> LoadAsync(
        string path,
        string? passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return Deserialize(bytes, passphrase);
        }
        finally
        {
            // For v2 these bytes contain the private keys; for v3 only the
            // envelope. Zero them unconditionally — cheap either way.
            CryptographicOperations.ZeroMemory(bytes);
        }
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

    public static AgentIdentity Deserialize(ReadOnlySpan<byte> bytes, string? passphrase = null)
    {
        // Parse the file JSON ONCE; the version field decides the branch.
        using var doc = ParseJson(bytes);
        var root = doc.RootElement;
        var version = ReadVersion(root);

        if (version == EncryptedVersion)
        {
            if (string.IsNullOrEmpty(passphrase))
            {
                throw new InvalidOperationException(
                    "This identity file is encrypted (v3) but no passphrase was provided. "
                    + $"Set the {PassphraseEnvVar} environment variable and retry.");
            }
            var plaintext = DecryptEnvelope(root, passphrase);
            try
            {
                using var inner = ParseJson(plaintext);
                return DeserializePlaintext(inner.RootElement, ReadVersion(inner.RootElement));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        return DeserializePlaintext(root, version);
    }

    private static AgentIdentity DeserializePlaintext(JsonElement root, int version)
    {
        if (version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported identity file version: got {version}, expected {CurrentVersion}. "
                + (version < CurrentVersion
                    ? "This identity predates the ML-DSA signing key; regenerate with 'mcptx keygen --force'."
                    : "This file was written by a newer mcptx build."));
        }

        var ecHex = RequireString(root, "secp256k1_private_key");

        // The FromEncoded* constructors copy out of these buffers, so the
        // decode intermediates can be zeroed immediately. (The JSON strings
        // themselves are immanageable: .NET strings cannot be zeroed.)
        var mlkemBytes = RequireBase64(root, "mlkem_private_key");
        var mldsaBytes = RequireBase64(root, "mldsa_private_key");
        try
        {
            // Wrap every malformed-key exception as a clean InvalidOperationException
            // so a corrupt identity file yields the actionable "could not load"
            // message rather than a raw FormatException/ArgumentException crash.
            Secp256k1KeyPair ec;
            try { ec = Secp256k1KeyPair.FromPrivateKeyHex(ecHex); }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                throw new InvalidOperationException($"Identity 'secp256k1_private_key' is invalid: {ex.Message}", ex);
            }

            MlKemKeyPair mlkem;
            MlDsaKeyPair mldsa;
            try
            {
                mlkem = MlKemKeyPair.FromEncodedPrivateKey(mlkemBytes);
                mldsa = MlDsaKeyPair.FromEncodedPrivateKey(mldsaBytes);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Identity post-quantum key material is invalid: {ex.Message}", ex);
            }

            return AgentIdentity.FromKeys(ec, mlkem, mldsa);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mlkemBytes);
            CryptographicOperations.ZeroMemory(mldsaBytes);
        }
    }

    // ── v3 encrypted envelope ────────────────────────────────────────────

    /// <summary>
    /// Encrypt the v2 payload under a passphrase: Argon2id(passphrase, salt)
    /// → AES-256-GCM(key, nonce, payload, aad = KDF header). The plaintext
    /// buffer is zeroed before returning.
    /// </summary>
    public static byte[] SerializeEncrypted(
        AgentIdentity identity,
        string passphrase,
        IdentityEncryptionParams kdfParams)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(kdfParams);
        kdfParams.Validate();

        var plaintext = Serialize(identity);
        try
        {
            var salt = RandomNumberGenerator.GetBytes(SaltByteLength);
            var nonce = RandomNumberGenerator.GetBytes(NonceByteLength);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagByteLength];

            var key = DeriveKey(passphrase, salt, kdfParams);
            try
            {
                using var aes = new AesGcm(key, TagByteLength);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, BuildAad(salt, kdfParams));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("argon2_iterations", kdfParams.Iterations);
                writer.WriteNumber("argon2_memory_kib", kdfParams.MemoryKib);
                writer.WriteNumber("argon2_parallelism", kdfParams.Parallelism);
                writer.WriteBase64String("ciphertext", ciphertext);
                writer.WriteString("kdf", "argon2id");
                writer.WriteBase64String("nonce", nonce);
                writer.WriteBase64String("salt", salt);
                writer.WriteBase64String("tag", tag);
                writer.WriteNumber("version", EncryptedVersion);
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DecryptEnvelope(JsonElement root, string passphrase)
    {
        var kdf = RequireString(root, "kdf");
        if (kdf != "argon2id")
            throw new InvalidOperationException($"Unsupported identity KDF '{kdf}' (expected 'argon2id').");

        var kdfParams = new IdentityEncryptionParams(
            RequireInt(root, "argon2_memory_kib"),
            RequireInt(root, "argon2_iterations"),
            RequireInt(root, "argon2_parallelism"));
        // Enforce the cost ceilings BEFORE DeriveKey allocates; surface a bad
        // header as a clean InvalidOperationException (not ArgumentOutOfRange,
        // which the load boundary doesn't catch).
        try
        {
            kdfParams.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidOperationException($"Identity file has invalid Argon2 parameters: {ex.Message}", ex);
        }

        var salt = RequireBase64(root, "salt", SaltByteLength);
        var nonce = RequireBase64(root, "nonce", NonceByteLength);
        var tag = RequireBase64(root, "tag", TagByteLength);
        var ciphertext = RequireBase64(root, "ciphertext"); // variable length; FormatException wrapped

        var key = DeriveKey(passphrase, salt, kdfParams);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagByteLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, BuildAad(salt, kdfParams));
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            // Covers both a wrong passphrase and any tampering with the
            // ciphertext or the (AAD-bound) KDF header — indistinguishable
            // by design.
            throw new InvalidOperationException(
                "Could not decrypt the identity file: wrong passphrase, or the file was tampered with.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Argon2id (RFC 9106, version 1.3) → 32-byte AES key.</summary>
    private static byte[] DeriveKey(string passphrase, byte[] salt, IdentityEncryptionParams kdfParams)
    {
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithSalt(salt)
            .WithMemoryAsKB(kdfParams.MemoryKib)
            .WithIterations(kdfParams.Iterations)
            .WithParallelism(kdfParams.Parallelism)
            .Build();

        var generator = new Argon2BytesGenerator();
        generator.Init(parameters);

        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        var key = new byte[AesKeyByteLength];
        try
        {
            generator.GenerateBytes(passphraseBytes, key, 0, key.Length);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }

    /// <summary>
    /// Associated data binding the KDF header into the GCM authentication:
    /// changing salt or any cost parameter invalidates the tag.
    /// </summary>
    private static byte[] BuildAad(byte[] salt, IdentityEncryptionParams kdfParams)
        => Encoding.UTF8.GetBytes(
            $"mcptx-identity-v{EncryptedVersion}|argon2id|{Convert.ToBase64String(salt)}"
            + $"|{kdfParams.MemoryKib}|{kdfParams.Iterations}|{kdfParams.Parallelism}");

    private static JsonDocument ParseJson(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Identity file is not valid JSON: {ex.Message}", ex);
        }
    }

    private static int ReadVersion(JsonElement root)
    {
        // TryGetInt32 (not GetInt32) so an out-of-int32 'version' yields a clean
        // error instead of an uncaught FormatException.
        if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt32(out var version))
        {
            throw new InvalidOperationException("Identity file is missing a valid numeric 'version'.");
        }
        return version;
    }

    private static int RequireInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException($"Identity file is missing numeric property '{name}'.");
        // TryGetInt32 so a number outside int32 range (e.g. a crafted Argon2
        // cost) is a clean error, not an uncaught FormatException.
        if (!el.TryGetInt32(out var value))
            throw new InvalidOperationException($"Identity file property '{name}' is out of the supported integer range.");
        return value;
    }

    /// <summary>Read a required base64 string property, wrapping a malformed value.</summary>
    private static byte[] RequireBase64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Identity file is missing string property '{name}'.");
        try
        {
            return el.GetBytesFromBase64();
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Identity file property '{name}' is not valid base64.", ex);
        }
    }

    private static byte[] RequireBase64(JsonElement root, string name, int expectedLength)
    {
        var value = RequireBase64(root, name);
        if (value.Length != expectedLength)
            throw new InvalidOperationException(
                $"Identity file property '{name}' must be {expectedLength} bytes (got {value.Length}).");
        return value;
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
