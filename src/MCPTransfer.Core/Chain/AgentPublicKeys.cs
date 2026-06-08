namespace MCPTransfer.Core.Chain;

/// <summary>
/// Pair of public keys returned by <see cref="IKeyRegistryClient.GetAsync"/>.
/// Both lengths are zero when the address has never published.
/// </summary>
public sealed record AgentPublicKeys(byte[] Secp256k1Compressed, byte[] MlKem)
{
    /// <summary>True iff both keys are present and at the expected length.</summary>
    public bool IsRegistered =>
        Secp256k1Compressed.Length == 33 && MlKem.Length == 1184;

    /// <summary>Empty value for an address that has never published.</summary>
    public static AgentPublicKeys Empty { get; } = new(Array.Empty<byte>(), Array.Empty<byte>());
}
