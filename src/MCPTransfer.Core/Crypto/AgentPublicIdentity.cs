namespace MCPTransfer.Core.Crypto;

/// <summary>
/// The public-facing view of an agent: enough material for any sender to
/// run an end-to-end hybrid KEM against this recipient. The Ethereum
/// address is derived directly from the secp256k1 public key — there is
/// no separate trust step for it.
/// </summary>
public sealed class AgentPublicIdentity
{
    private readonly byte[] _secp256k1PublicKeyCompressed;

    public EthereumAddress Address { get; }

    /// <summary>33-byte compressed secp256k1 public key, exposed as
    /// <see cref="ReadOnlyMemory{T}"/> so callers cannot mutate the
    /// stored bytes.</summary>
    public ReadOnlyMemory<byte> Secp256k1PublicKeyCompressed => _secp256k1PublicKeyCompressed;

    public MlKemPublicKey MlKem { get; }

    public AgentPublicIdentity(ReadOnlyMemory<byte> secp256k1PublicKeyCompressed, MlKemPublicKey mlKem)
    {
        ArgumentNullException.ThrowIfNull(mlKem);

        if (secp256k1PublicKeyCompressed.Length != Secp256k1KeyPair.PublicKeyCompressedByteLength)
        {
            throw new ArgumentException(
                $"Compressed secp256k1 public key must be exactly "
                + $"{Secp256k1KeyPair.PublicKeyCompressedByteLength} bytes "
                + $"(got {secp256k1PublicKeyCompressed.Length}).",
                nameof(secp256k1PublicKeyCompressed));
        }

        _secp256k1PublicKeyCompressed = secp256k1PublicKeyCompressed.ToArray();
        MlKem = mlKem;
        Address = Secp256k1KeyPair.AddressFromPublicKey(_secp256k1PublicKeyCompressed);
    }

    /// <summary>
    /// Construct from raw byte material — typically used when reconstructing
    /// a peer's identity from on-chain data (event log + KeyRegistry lookup).
    /// </summary>
    public static AgentPublicIdentity FromBytes(
        ReadOnlySpan<byte> secp256k1PublicKeyCompressed,
        ReadOnlySpan<byte> mlKemPublicKey)
    {
        return new AgentPublicIdentity(
            secp256k1PublicKeyCompressed.ToArray(),
            new MlKemPublicKey(mlKemPublicKey));
    }
}
