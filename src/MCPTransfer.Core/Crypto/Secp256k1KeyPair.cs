using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace MCPTransfer.Core.Crypto;

/// <summary>
/// secp256k1 keypair: the classical half of the agent identity. Derives an
/// Ethereum-compatible address and performs ECDH for the hybrid KEM.
/// </summary>
public sealed class Secp256k1KeyPair
{
    public const int PrivateKeyByteLength = 32;
    public const int PublicKeyCompressedByteLength = 33;
    public const int PublicKeyUncompressedByteLength = 65;
    public const int SharedSecretByteLength = 32;
    public const int SignatureByteLength = 64;
    public const int MessageHashByteLength = 32;

    private static readonly Lazy<ECDomainParameters> CurveLazy = new(() =>
    {
        X9ECParameters x9 = SecNamedCurves.GetByName("secp256k1");
        return new ECDomainParameters(x9.Curve, x9.G, x9.N, x9.H);
    });

    private static ECDomainParameters Curve => CurveLazy.Value;

    private readonly BigInteger _privateScalar;
    private readonly ECPoint _publicPoint;

    private byte[]? _privateKeyBytes;
    private byte[]? _publicKeyCompressed;
    private byte[]? _publicKeyUncompressed;
    private EthereumAddress? _address;

    private Secp256k1KeyPair(BigInteger privateScalar, ECPoint publicPoint)
    {
        _privateScalar = privateScalar;
        _publicPoint = publicPoint.Normalize();
    }

    public static Secp256k1KeyPair Generate()
    {
        var rng = new SecureRandom();
        var gen = new ECKeyPairGenerator("ECDH");
        gen.Init(new ECKeyGenerationParameters(Curve, rng));
        var kp = gen.GenerateKeyPair();
        var sk = ((ECPrivateKeyParameters)kp.Private).D;
        var pk = ((ECPublicKeyParameters)kp.Public).Q;
        return new Secp256k1KeyPair(sk, pk);
    }

    public static Secp256k1KeyPair FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != PrivateKeyByteLength)
            throw new ArgumentException(
                $"Private key must be exactly {PrivateKeyByteLength} bytes (got {privateKey.Length}).",
                nameof(privateKey));

        var scalar = new BigInteger(1, privateKey.ToArray());
        if (scalar.SignValue <= 0 || scalar.CompareTo(Curve.N) >= 0)
            throw new ArgumentException(
                "Private key scalar is out of range [1, n-1].",
                nameof(privateKey));

        var pk = Curve.G.Multiply(scalar).Normalize();
        return new Secp256k1KeyPair(scalar, pk);
    }

    public static Secp256k1KeyPair FromPrivateKeyHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var span = hex.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            span = span[2..];
        return FromPrivateKey(Convert.FromHexString(span));
    }

    public ReadOnlySpan<byte> PrivateKey
        => _privateKeyBytes ??= BigIntegers.AsUnsignedByteArray(PrivateKeyByteLength, _privateScalar);

    public ReadOnlySpan<byte> PublicKeyCompressed
        => _publicKeyCompressed ??= _publicPoint.GetEncoded(compressed: true);

    public ReadOnlySpan<byte> PublicKeyUncompressed
        => _publicKeyUncompressed ??= _publicPoint.GetEncoded(compressed: false);

    public EthereumAddress Address => _address ??= DeriveAddress();

    private EthereumAddress DeriveAddress()
    {
        // Drop the leading 0x04 prefix byte of the uncompressed encoding; keccak the
        // 64-byte (X || Y) coordinates and take the last 20 bytes.
        ReadOnlySpan<byte> uncompressed = PublicKeyUncompressed;
        var hash = Hashes.Keccak256(uncompressed[1..]);
        return new EthereumAddress(hash.AsSpan(12, EthereumAddress.ByteLength));
    }

    /// <summary>
    /// Computes the 32-byte ECDH shared secret between this private key and the peer's
    /// public key, encoded either compressed (33 bytes) or uncompressed (65 bytes).
    /// </summary>
    public byte[] Ecdh(ReadOnlySpan<byte> peerPublicKey)
    {
        if (peerPublicKey.Length is not PublicKeyCompressedByteLength
            and not PublicKeyUncompressedByteLength)
        {
            throw new ArgumentException(
                "Peer public key must be 33 (compressed) or 65 (uncompressed) bytes.",
                nameof(peerPublicKey));
        }

        var peerPoint = Curve.Curve.DecodePoint(peerPublicKey.ToArray());
        var peerPub = new ECPublicKeyParameters(peerPoint, Curve);
        var myPriv = new ECPrivateKeyParameters(_privateScalar, Curve);

        var agreement = new ECDHBasicAgreement();
        agreement.Init(myPriv);
        var z = agreement.CalculateAgreement(peerPub);
        return BigIntegers.AsUnsignedByteArray(SharedSecretByteLength, z);
    }

    /// <summary>
    /// Sign a 32-byte Keccak-256 hash with deterministic ECDSA (RFC 6979)
    /// using Keccak-256 as the inner hash to match Ethereum's behaviour.
    /// Returns 64 bytes (<c>r ‖ s</c>) with <c>s</c> normalized to the lower
    /// half of the curve order to avoid signature malleability.
    /// </summary>
    public byte[] SignEcdsa(ReadOnlySpan<byte> messageHash)
    {
        if (messageHash.Length != MessageHashByteLength)
            throw new ArgumentException(
                $"Message hash must be exactly {MessageHashByteLength} bytes (got {messageHash.Length}).",
                nameof(messageHash));

        var signer = new ECDsaSigner(new HMacDsaKCalculator(new KeccakDigest(256)));
        signer.Init(true, new ECPrivateKeyParameters(_privateScalar, Curve));
        var rs = signer.GenerateSignature(messageHash.ToArray());
        var r = rs[0];
        var s = rs[1];

        var halfOrder = Curve.N.ShiftRight(1);
        if (s.CompareTo(halfOrder) > 0)
            s = Curve.N.Subtract(s);

        var result = new byte[SignatureByteLength];
        var rBytes = BigIntegers.AsUnsignedByteArray(32, r);
        var sBytes = BigIntegers.AsUnsignedByteArray(32, s);
        Buffer.BlockCopy(rBytes, 0, result, 0, 32);
        Buffer.BlockCopy(sBytes, 0, result, 32, 32);
        return result;
    }

    /// <summary>
    /// Verify a 64-byte ECDSA signature against a 32-byte message hash and a
    /// peer's compressed or uncompressed public key. Returns <c>false</c> on
    /// any structural problem; throws only on truly malformed inputs.
    /// </summary>
    public static bool VerifyEcdsa(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> messageHash,
        ReadOnlySpan<byte> signature)
    {
        if (messageHash.Length != MessageHashByteLength) return false;
        if (signature.Length != SignatureByteLength) return false;
        if (publicKey.Length is not PublicKeyCompressedByteLength
            and not PublicKeyUncompressedByteLength)
        {
            return false;
        }

        ECPoint peerPoint;
        try
        {
            peerPoint = Curve.Curve.DecodePoint(publicKey.ToArray());
        }
        catch
        {
            return false;
        }

        var peerPub = new ECPublicKeyParameters(peerPoint, Curve);
        var r = new BigInteger(1, signature[..32].ToArray());
        var s = new BigInteger(1, signature.Slice(32, 32).ToArray());

        // Reject high-s signatures: only canonical low-s form is accepted.
        var halfOrder = Curve.N.ShiftRight(1);
        if (s.CompareTo(halfOrder) > 0) return false;
        if (r.SignValue <= 0 || s.SignValue <= 0) return false;
        if (r.CompareTo(Curve.N) >= 0 || s.CompareTo(Curve.N) >= 0) return false;

        var verifier = new ECDsaSigner(new HMacDsaKCalculator(new KeccakDigest(256)));
        verifier.Init(false, peerPub);
        return verifier.VerifySignature(messageHash.ToArray(), r, s);
    }

    /// <summary>
    /// Derives an Ethereum-style address directly from a peer's secp256k1 public key,
    /// without instantiating a keypair.
    /// </summary>
    public static EthereumAddress AddressFromPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length is not PublicKeyCompressedByteLength
            and not PublicKeyUncompressedByteLength)
        {
            throw new ArgumentException(
                "Public key must be 33 (compressed) or 65 (uncompressed) bytes.",
                nameof(publicKey));
        }

        var point = Curve.Curve.DecodePoint(publicKey.ToArray()).Normalize();
        var uncompressed = point.GetEncoded(compressed: false);
        var hash = Hashes.Keccak256(uncompressed.AsSpan(1));
        return new EthereumAddress(hash.AsSpan(12, EthereumAddress.ByteLength));
    }
}
