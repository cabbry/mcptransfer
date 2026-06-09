namespace MCPTransfer.Core.Chain;

/// <summary>
/// Key entry returned by <see cref="IKeyRegistryClient.GetAsync"/> (registry
/// v2): the secp256k1 key in clear plus the ML-KEM-768 COMMITMENT — the
/// keccak256 hash of the full key and the content-addressed pointer (CID)
/// where it can be fetched. The full key itself lives off-chain; callers must
/// verify <c>keccak256(fetchedKey) == MlKemHash</c> before using it.
/// </summary>
public sealed record AgentPublicKeys(byte[] Secp256k1Compressed, byte[] MlKemHash, string MlKemCid)
{
    /// <summary>True iff the entry is present and well-formed.</summary>
    public bool IsRegistered =>
        Secp256k1Compressed.Length == 33
        && MlKemHash.Length == 32
        && MlKemHash.Any(b => b != 0)
        && !string.IsNullOrEmpty(MlKemCid);

    /// <summary>Empty value for an address that has never published.</summary>
    public static AgentPublicKeys Empty { get; } =
        new(Array.Empty<byte>(), Array.Empty<byte>(), string.Empty);
}
