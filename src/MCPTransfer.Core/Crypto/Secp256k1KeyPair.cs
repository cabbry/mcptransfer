using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
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
