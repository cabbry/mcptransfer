using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace MCPTransfer.Core.Crypto;

/// <summary>
/// ML-KEM-768 keypair: the post-quantum half of the agent identity. The
/// public key is committed to via the on-chain <c>KeyRegistry</c>; the
/// private key stays local and is used to decapsulate incoming KEM
/// ciphertexts.
/// </summary>
/// <remarks>
/// <see cref="Dispose"/> zeroes the cached private-key encoding. This is
/// best-effort only: BouncyCastle's parameter objects hold their own
/// internal copies that cannot be zeroed, and the GC may have moved the
/// arrays before they are cleared (see docs/CRYPTO.md, Zeroization).
/// </remarks>
public sealed class MlKemKeyPair : IDisposable
{
    /// <summary>FIPS 203 full encoding of the private key (k=3 / ML-KEM-768).</summary>
    public const int PrivateKeyEncodedByteLength = 2400;

    private readonly MLKemPrivateKeyParameters _privateParameters;
    private byte[]? _privateEncoded;

    public MlKemPublicKey PublicKey { get; }

    private MlKemKeyPair(MLKemPrivateKeyParameters privateParameters, MLKemPublicKeyParameters publicParameters)
    {
        _privateParameters = privateParameters;
        PublicKey = new MlKemPublicKey(publicParameters);
    }

    public static MlKemKeyPair Generate()
    {
        var rng = new SecureRandom();
        var generator = new MLKemKeyPairGenerator();
        generator.Init(new MLKemKeyGenerationParameters(rng, MLKemParameters.ml_kem_768));
        var kp = generator.GenerateKeyPair();
        return new MlKemKeyPair(
            (MLKemPrivateKeyParameters)kp.Private,
            (MLKemPublicKeyParameters)kp.Public);
    }

    public static MlKemKeyPair FromEncodedPrivateKey(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != PrivateKeyEncodedByteLength)
            throw new ArgumentException(
                $"ML-KEM-768 private key encoding must be exactly {PrivateKeyEncodedByteLength} bytes (got {encoded.Length}).",
                nameof(encoded));

        var priv = MLKemPrivateKeyParameters.FromEncoding(MLKemParameters.ml_kem_768, encoded.ToArray());
        var pubEncoded = priv.GetPublicKeyEncoded();
        var pub = MLKemPublicKeyParameters.FromEncoding(MLKemParameters.ml_kem_768, pubEncoded);
        return new MlKemKeyPair(priv, pub);
    }

    public ReadOnlySpan<byte> PrivateKeyEncoded
        => _privateEncoded ??= _privateParameters.GetEncoded();

    /// <summary>Zero the cached private-key encoding (best-effort).</summary>
    public void Dispose()
    {
        if (_privateEncoded is not null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_privateEncoded);
            _privateEncoded = null;
        }
    }

    /// <summary>
    /// Recovers the 32-byte shared secret from a KEM ciphertext produced by
    /// someone who encapsulated against this keypair's public key.
    /// </summary>
    public byte[] Decapsulate(ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length != MlKemPublicKey.CiphertextByteLength)
            throw new ArgumentException(
                $"ML-KEM-768 ciphertext must be exactly {MlKemPublicKey.CiphertextByteLength} bytes (got {ciphertext.Length}).",
                nameof(ciphertext));

        var decapsulator = new MLKemDecapsulator(MLKemParameters.ml_kem_768);
        decapsulator.Init(_privateParameters);

        var sharedSecret = new byte[decapsulator.SecretLength];
        var ctArray = ciphertext.ToArray();
        decapsulator.Decapsulate(ctArray, 0, ctArray.Length, sharedSecret, 0, sharedSecret.Length);
        return sharedSecret;
    }
}
