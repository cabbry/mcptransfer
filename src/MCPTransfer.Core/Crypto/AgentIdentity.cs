namespace MCPTransfer.Core.Crypto;

/// <summary>
/// The full local identity of an agent:
/// <list type="bullet">
/// <item>a secp256k1 keypair — Ethereum address, classical ECDH (hybrid KEM),
/// and classical ECDSA manifest signature;</item>
/// <item>an ML-KEM-768 keypair — the PQC half of the hybrid KEM;</item>
/// <item>an ML-DSA-65 keypair — the PQC half of the hybrid manifest signature.</item>
/// </list>
/// <see cref="Dispose"/> zeroes the keypairs' cached private-key material
/// (best-effort; see docs/CRYPTO.md, Zeroization). Long-lived holders (the
/// MCP server context) dispose on shutdown; short-lived CLI processes rely
/// on process exit.
/// </summary>
public sealed class AgentIdentity : IDisposable
{
    public Secp256k1KeyPair Secp256k1 { get; }
    public MlKemKeyPair MlKem { get; }
    public MlDsaKeyPair MlDsa { get; }
    public EthereumAddress Address => Secp256k1.Address;

    private AgentIdentity(Secp256k1KeyPair secp256k1, MlKemKeyPair mlKem, MlDsaKeyPair mlDsa)
    {
        Secp256k1 = secp256k1;
        MlKem = mlKem;
        MlDsa = mlDsa;
    }

    public static AgentIdentity Generate()
        => new(Secp256k1KeyPair.Generate(), MlKemKeyPair.Generate(), MlDsaKeyPair.Generate());

    public static AgentIdentity FromKeys(Secp256k1KeyPair secp256k1, MlKemKeyPair mlKem, MlDsaKeyPair mlDsa)
    {
        ArgumentNullException.ThrowIfNull(secp256k1);
        ArgumentNullException.ThrowIfNull(mlKem);
        ArgumentNullException.ThrowIfNull(mlDsa);
        return new AgentIdentity(secp256k1, mlKem, mlDsa);
    }

    /// <summary>
    /// Project this identity to its public face: address + the KEM public keys
    /// a sender needs to encapsulate to this agent. The ML-DSA signing public
    /// key is NOT part of this view — it travels inside each signed manifest.
    /// </summary>
    public AgentPublicIdentity ToPublic()
        => new(Secp256k1.PublicKeyCompressed.ToArray(), MlKem.PublicKey);

    /// <summary>Zero all cached private-key material (best-effort).</summary>
    public void Dispose()
    {
        Secp256k1.Dispose();
        MlKem.Dispose();
        MlDsa.Dispose();
    }
}
