using System.Security.Cryptography;
using System.Text;

namespace MCPTransfer.Core.Crypto;

/// <summary>
/// Hybrid Key Encapsulation Mechanism combining classical ECDHE on secp256k1
/// with post-quantum ML-KEM-768. The two shared secrets are concatenated and
/// fed into HKDF-SHA256 to derive a single 32-byte symmetric key used by the
/// AEAD layer.
/// </summary>
/// <remarks>
/// <para>
/// Security property: the derived key remains confidential as long as
/// <b>at least one</b> of the two underlying primitives is sound. This is
/// the standard "harvest now, decrypt later" mitigation — see
/// <c>docs/CRYPTO.md</c> for the threat model.
/// </para>
/// <para>
/// The HKDF <c>info</c> parameter is built from a fixed suite identifier
/// plus optional caller-supplied context (sender address ‖ recipient
/// address ‖ nonce_prefix ‖ …). Bind the derived key to every relevant
/// piece of transcript material to defeat cross-protocol attacks.
/// </para>
/// </remarks>
public static class HybridKem
{
    public const string SuiteIdentifier = "Hybrid-secp256k1+MLKEM768-AES256GCM";
    public const int DerivedKeyByteLength = 32;

    private static readonly byte[] BaseInfoBytes = Encoding.UTF8.GetBytes("MCPTx-v1-" + SuiteIdentifier);

    /// <summary>
    /// Run the encapsulation side: generate an ephemeral secp256k1 keypair,
    /// derive both shared secrets, and produce the symmetric key plus the
    /// public material the recipient needs to reproduce it.
    /// </summary>
    public static HybridKemEncapsulation Encapsulate(
        AgentPublicIdentity recipient,
        ReadOnlySpan<byte> additionalContext = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        var ephemeral = Secp256k1KeyPair.Generate();
        var ssEcdh = ephemeral.Ecdh(recipient.Secp256k1PublicKeyCompressed.Span);

        var kem = recipient.MlKem.Encapsulate();

        var derivedKey = DeriveKey(ssEcdh, kem.SharedSecret, additionalContext);

        // The ECDH and ML-KEM shared secrets are no longer needed.
        CryptographicOperations.ZeroMemory(ssEcdh);
        CryptographicOperations.ZeroMemory(kem.SharedSecret);

        return new HybridKemEncapsulation(
            ephemeralSecp256k1PublicKey: ephemeral.PublicKeyCompressed.ToArray(),
            kemCiphertext: kem.Ciphertext,
            derivedKey: derivedKey);
    }

    /// <summary>
    /// Run the decapsulation side: recompute both shared secrets from the
    /// recipient's private material plus the public values from the sender,
    /// then derive the same 32-byte symmetric key.
    /// </summary>
    public static byte[] Decapsulate(
        AgentIdentity recipient,
        ReadOnlySpan<byte> ephemeralSecp256k1PublicKey,
        ReadOnlySpan<byte> kemCiphertext,
        ReadOnlySpan<byte> additionalContext = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        var ssEcdh = recipient.Secp256k1.Ecdh(ephemeralSecp256k1PublicKey);
        var ssMlKem = recipient.MlKem.Decapsulate(kemCiphertext);

        var derivedKey = DeriveKey(ssEcdh, ssMlKem, additionalContext);

        CryptographicOperations.ZeroMemory(ssEcdh);
        CryptographicOperations.ZeroMemory(ssMlKem);

        return derivedKey;
    }

    private static byte[] DeriveKey(
        ReadOnlySpan<byte> ssEcdh,
        ReadOnlySpan<byte> ssMlKem,
        ReadOnlySpan<byte> additionalContext)
    {
        Span<byte> ikm = stackalloc byte[ssEcdh.Length + ssMlKem.Length];
        ssEcdh.CopyTo(ikm);
        ssMlKem.CopyTo(ikm[ssEcdh.Length..]);

        Span<byte> info = stackalloc byte[BaseInfoBytes.Length + additionalContext.Length];
        BaseInfoBytes.CopyTo(info);
        additionalContext.CopyTo(info[BaseInfoBytes.Length..]);

        var derived = new byte[DerivedKeyByteLength];
        HKDF.DeriveKey(
            hashAlgorithmName: HashAlgorithmName.SHA256,
            ikm: ikm,
            output: derived,
            salt: ReadOnlySpan<byte>.Empty,
            info: info);

        CryptographicOperations.ZeroMemory(ikm);
        return derived;
    }
}

/// <summary>
/// Public material a sender produces during <see cref="HybridKem.Encapsulate"/>:
/// the ephemeral secp256k1 public key, the ML-KEM ciphertext, and the
/// locally-derived symmetric key. The first two travel in the manifest; the
/// last stays on the sender's machine and is used to encrypt chunks.
/// </summary>
/// <remarks>
/// Implements <see cref="IDisposable"/> so the derived key is zeroed from
/// managed memory on scope exit. Use <c>using var encap = HybridKem.Encapsulate(...);</c>.
/// </remarks>
public sealed class HybridKemEncapsulation : IDisposable
{
    private readonly byte[] _ephemeralSecp256k1PublicKey;
    private readonly byte[] _kemCiphertext;
    private readonly byte[] _derivedKey;
    private bool _disposed;

    public ReadOnlyMemory<byte> EphemeralSecp256k1PublicKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _ephemeralSecp256k1PublicKey;
        }
    }

    public ReadOnlyMemory<byte> KemCiphertext
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _kemCiphertext;
        }
    }

    public ReadOnlyMemory<byte> DerivedKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _derivedKey;
        }
    }

    internal HybridKemEncapsulation(
        byte[] ephemeralSecp256k1PublicKey,
        byte[] kemCiphertext,
        byte[] derivedKey)
    {
        _ephemeralSecp256k1PublicKey = ephemeralSecp256k1PublicKey;
        _kemCiphertext = kemCiphertext;
        _derivedKey = derivedKey;
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_derivedKey);
        _disposed = true;
    }
}
