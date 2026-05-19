using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;

namespace MCPTransfer.Core.Crypto;

/// <summary>
/// A peer's ML-KEM-768 public key. The encapsulation side of the KEM:
/// given this key, anyone can produce a (ciphertext, shared_secret) pair
/// that only the holder of the matching private key can recover.
/// </summary>
public sealed class MlKemPublicKey
{
    public const int PublicKeyByteLength = 1184;
    public const int CiphertextByteLength = 1088;
    public const int SharedSecretByteLength = 32;

    internal MLKemPublicKeyParameters Parameters { get; }
    private readonly byte[] _encoded;

    internal MlKemPublicKey(MLKemPublicKeyParameters parameters)
    {
        Parameters = parameters;
        _encoded = parameters.GetEncoded();
    }

    public MlKemPublicKey(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != PublicKeyByteLength)
            throw new ArgumentException(
                $"ML-KEM-768 public key must be exactly {PublicKeyByteLength} bytes (got {encoded.Length}).",
                nameof(encoded));

        Parameters = MLKemPublicKeyParameters.FromEncoding(MLKemParameters.ml_kem_768, encoded.ToArray());
        _encoded = encoded.ToArray();
    }

    public ReadOnlySpan<byte> Bytes => _encoded;

    /// <summary>
    /// Runs the KEM encapsulation step: produces a ciphertext to send to the
    /// holder of the matching private key, and a 32-byte shared secret that
    /// both sides will arrive at independently.
    /// </summary>
    public KemEncapsulation Encapsulate()
    {
        var encapsulator = new MLKemEncapsulator(MLKemParameters.ml_kem_768);
        encapsulator.Init(Parameters);

        var ciphertext = new byte[encapsulator.EncapsulationLength];
        var sharedSecret = new byte[encapsulator.SecretLength];
        encapsulator.Encapsulate(ciphertext, 0, ciphertext.Length, sharedSecret, 0, sharedSecret.Length);

        return new KemEncapsulation(ciphertext, sharedSecret);
    }
}

/// <summary>
/// Output of <see cref="MlKemPublicKey.Encapsulate"/>: the ciphertext to
/// transmit and the shared secret derived locally. Both are exposed as
/// <see cref="ReadOnlyMemory{T}"/> so callers cannot mutate the stored
/// bytes; the shared secret in particular must be treated as key
/// material (zero on disposal).
/// </summary>
public readonly record struct KemEncapsulation(
    ReadOnlyMemory<byte> Ciphertext,
    ReadOnlyMemory<byte> SharedSecret);
