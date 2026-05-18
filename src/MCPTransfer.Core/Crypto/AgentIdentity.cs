namespace MCPTransfer.Core.Crypto;

/// <summary>
/// The full local identity of an agent: a secp256k1 keypair (giving the
/// Ethereum address and used for both signing and the classical half of
/// the hybrid KEM) and an ML-KEM-768 keypair (the PQC half).
/// </summary>
public sealed class AgentIdentity
{
    public Secp256k1KeyPair Secp256k1 { get; }
    public MlKemKeyPair MlKem { get; }
    public EthereumAddress Address => Secp256k1.Address;

    private AgentIdentity(Secp256k1KeyPair secp256k1, MlKemKeyPair mlKem)
    {
        Secp256k1 = secp256k1;
        MlKem = mlKem;
    }

    public static AgentIdentity Generate()
        => new(Secp256k1KeyPair.Generate(), MlKemKeyPair.Generate());

    public static AgentIdentity FromKeys(Secp256k1KeyPair secp256k1, MlKemKeyPair mlKem)
    {
        ArgumentNullException.ThrowIfNull(secp256k1);
        ArgumentNullException.ThrowIfNull(mlKem);
        return new AgentIdentity(secp256k1, mlKem);
    }

    /// <summary>
    /// Project this identity to its public face: address and both public keys.
    /// What other agents (or the on-chain <c>KeyRegistry</c>) need to see.
    /// </summary>
    public AgentPublicIdentity ToPublic()
        => new(Secp256k1.PublicKeyCompressed.ToArray(), MlKem.PublicKey);
}
