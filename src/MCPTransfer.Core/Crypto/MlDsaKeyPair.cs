using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace MCPTransfer.Core.Crypto;

/// <summary>
/// ML-DSA-65 keypair (FIPS 204, ex-CRYSTALS-Dilithium): the post-quantum
/// signature half of the agent identity. Used to co-sign the transfer
/// manifest alongside classical ECDSA-secp256k1, so a manifest stays
/// authenticatable even against a future adversary who can break ECDSA.
/// </summary>
/// <remarks>
/// Cat-3 security, matched to <see cref="MlKemKeyPair"/> (ML-KEM-768).
/// The public key travels inside the signed manifest (it is not published
/// on chain), so verifiers learn it from the wrapper; the classical ECDSA
/// signature — whose key derives to the sender's address — vouches for it.
/// </remarks>
public sealed class MlDsaKeyPair
{
    /// <summary>FIPS 204 ML-DSA-65 public key length, in bytes.</summary>
    public const int PublicKeyByteLength = 1952;

    /// <summary>FIPS 204 ML-DSA-65 signature length, in bytes.</summary>
    public const int SignatureByteLength = 3309;

    private static readonly MLDsaParameters Params = MLDsaParameters.ml_dsa_65;

    private readonly MLDsaPrivateKeyParameters _privateParameters;
    private readonly MLDsaPublicKeyParameters _publicParameters;
    private byte[]? _privateEncoded;
    private byte[]? _publicEncoded;

    private MlDsaKeyPair(MLDsaPrivateKeyParameters priv, MLDsaPublicKeyParameters pub)
    {
        _privateParameters = priv;
        _publicParameters = pub;
    }

    public static MlDsaKeyPair Generate()
    {
        var rng = new SecureRandom();
        var generator = new MLDsaKeyPairGenerator();
        generator.Init(new MLDsaKeyGenerationParameters(rng, Params));
        var kp = generator.GenerateKeyPair();
        return new MlDsaKeyPair(
            (MLDsaPrivateKeyParameters)kp.Private,
            (MLDsaPublicKeyParameters)kp.Public);
    }

    public static MlDsaKeyPair FromEncodedPrivateKey(ReadOnlySpan<byte> encoded)
    {
        var priv = MLDsaPrivateKeyParameters.FromEncoding(Params, encoded.ToArray());
        var pubEncoded = priv.GetPublicKeyEncoded();
        var pub = MLDsaPublicKeyParameters.FromEncoding(Params, pubEncoded);
        return new MlDsaKeyPair(priv, pub);
    }

    public ReadOnlySpan<byte> PrivateKeyEncoded
        => _privateEncoded ??= _privateParameters.GetEncoded();

    public ReadOnlySpan<byte> PublicKeyEncoded
        => _publicEncoded ??= _publicParameters.GetEncoded();

    /// <summary>Sign <paramref name="message"/> and return the 3309-byte ML-DSA-65 signature.</summary>
    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var signer = new MLDsaSigner(Params, deterministic: false);
        signer.Init(forSigning: true, _privateParameters);
        var msg = message.ToArray();
        signer.BlockUpdate(msg, 0, msg.Length);
        return signer.GenerateSignature();
    }

    /// <summary>
    /// Verify an ML-DSA-65 signature against a message and a raw 1952-byte
    /// public key. Returns <c>false</c> on any structural problem; never throws
    /// on malformed-but-wrong-length inputs.
    /// </summary>
    public static bool Verify(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeyByteLength) return false;
        if (signature.Length != SignatureByteLength) return false;

        // Guard the WHOLE path: an adversarial (correct-length) pubkey or
        // signature can make BouncyCastle throw during FromEncoding, Init, or
        // VerifySignature. Verify is documented to never throw on bad input.
        try
        {
            var pub = MLDsaPublicKeyParameters.FromEncoding(Params, publicKey.ToArray());
            var verifier = new MLDsaSigner(Params, deterministic: false);
            verifier.Init(forSigning: false, pub);
            var msg = message.ToArray();
            verifier.BlockUpdate(msg, 0, msg.Length);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch (Exception)
        {
            return false;
        }
    }
}
