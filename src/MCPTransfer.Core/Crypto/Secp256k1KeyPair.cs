using Nethereum.Signer;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Agreement;
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
/// Ethereum-compatible address, performs ECDH for the hybrid KEM, and
/// signs / verifies 65-byte Ethereum-style signatures (<c>r ‖ s ‖ v</c>).
/// </summary>
/// <remarks>
/// Signing delegates to <c>Nethereum.Signer.EthECKey</c> so we get
/// RFC 6979 with SHA-256 (the Ethereum-ecosystem standard), low-s
/// normalization, and the recovery byte for free. Pubkey recovery from
/// a signature (<see cref="Recover"/>) is also delegated. Key generation
/// and ECDH stay on BouncyCastle.
/// </remarks>
public sealed class Secp256k1KeyPair
{
    public const int PrivateKeyByteLength = 32;
    public const int PublicKeyCompressedByteLength = 33;
    public const int PublicKeyUncompressedByteLength = 65;
    public const int SharedSecretByteLength = 32;
    public const int SignatureByteLength = 65;
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

    private EthereumAddress DeriveAddress() => AddressFromPublicKey(PublicKeyUncompressed);

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
    /// Sign a 32-byte hash with deterministic ECDSA-secp256k1 (RFC 6979, SHA-256).
    /// Returns 65 bytes (<c>r ‖ s ‖ v</c>) with <c>s</c> normalized to the lower half
    /// of the curve order and <c>v</c> set to the 27/28 recovery byte that <see cref="Recover"/>
    /// and Solidity's <c>ecrecover</c> consume.
    /// </summary>
    public byte[] SignEcdsa(ReadOnlySpan<byte> messageHash)
    {
        if (messageHash.Length != MessageHashByteLength)
            throw new ArgumentException(
                $"Message hash must be exactly {MessageHashByteLength} bytes (got {messageHash.Length}).",
                nameof(messageHash));

        // Nethereum performs the canonical low-s normalization and computes V.
        var ethKey = new EthECKey(PrivateKey.ToArray(), isPrivate: true);
        var sig = ethKey.SignAndCalculateV(messageHash.ToArray());

        // R / S come from BigInteger.ToByteArray with no leading-zero padding;
        // we explicitly left-pad into our fixed 32-byte slots.
        var result = new byte[SignatureByteLength];
        PadLeftInto(sig.R, result.AsSpan(0, 32));
        PadLeftInto(sig.S, result.AsSpan(32, 32));
        result[64] = sig.V[0];
        return result;
    }

    /// <summary>
    /// Verify a 65-byte signature against a 32-byte hash and the peer's public key
    /// (compressed or uncompressed). Rejects high-s (malleable) signatures.
    /// Returns <c>false</c> on any structural problem; never throws on bad input.
    /// </summary>
    /// <remarks>
    /// The recovery byte <c>v</c> is not consulted here — verification uses the
    /// peer public key directly. <c>v</c> is used only by <see cref="Recover"/>.
    /// </remarks>
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

        var r = new BigInteger(1, signature[..32].ToArray());
        var s = new BigInteger(1, signature.Slice(32, 32).ToArray());

        var halfOrder = Curve.N.ShiftRight(1);
        if (s.CompareTo(halfOrder) > 0) return false;
        if (r.SignValue <= 0 || s.SignValue <= 0) return false;
        if (r.CompareTo(Curve.N) >= 0 || s.CompareTo(Curve.N) >= 0) return false;

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
        var verifier = new ECDsaSigner();
        verifier.Init(false, peerPub);
        return verifier.VerifySignature(messageHash.ToArray(), r, s);
    }

    /// <summary>
    /// Recover the signer's 33-byte compressed public key from a 65-byte signature
    /// and the 32-byte message hash. Mirrors Solidity's <c>ecrecover</c>.
    /// </summary>
    public static byte[] Recover(ReadOnlySpan<byte> messageHash, ReadOnlySpan<byte> signature)
    {
        if (messageHash.Length != MessageHashByteLength)
            throw new ArgumentException(
                $"Message hash must be exactly {MessageHashByteLength} bytes (got {messageHash.Length}).",
                nameof(messageHash));
        if (signature.Length != SignatureByteLength)
            throw new ArgumentException(
                $"Signature must be exactly {SignatureByteLength} bytes (got {signature.Length}).",
                nameof(signature));

        var r = signature[..32].ToArray();
        var s = signature.Slice(32, 32).ToArray();
        var v = new[] { signature[64] };

        var ethSig = EthECDSASignatureFactory.FromComponents(r, s, v);
        var recovered = EthECKey.RecoverFromSignature(ethSig, messageHash.ToArray());
        return recovered.GetPubKey(true);
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

    private static void PadLeftInto(byte[] source, Span<byte> destination)
    {
        if (source.Length > destination.Length)
        {
            throw new InvalidOperationException(
                $"Cannot left-pad: source ({source.Length}) longer than destination ({destination.Length}).");
        }
        var offset = destination.Length - source.Length;
        destination[..offset].Clear();
        source.CopyTo(destination[offset..]);
    }
}
