using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Configuration;

/// <summary>
/// On-disk and in-memory configuration for the <c>mcptx</c> CLI:
/// which chain to talk to, which IPFS backend to use.
/// </summary>
/// <remarks>
/// Loaded from <c>~/.mcptx/config.json</c> by default; environment
/// variables override individual fields at runtime (so a CI pipeline
/// can inject secrets without writing them to disk).
/// </remarks>
public sealed record MCPTransferConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public required ChainConfigSection Chain { get; init; }
    public required IpfsConfigSection Ipfs { get; init; }
}

/// <summary>Chain-layer slice of <see cref="MCPTransferConfig"/>.</summary>
public sealed record ChainConfigSection
{
    public required string RpcUrl { get; init; }
    public required long ChainId { get; init; }
    public required string FileRegistryAddress { get; init; }
    public required string KeyRegistryAddress { get; init; }
    public required string AgentDirectoryAddress { get; init; }

    /// <summary>
    /// Address of the deployed <c>Blocklist</c> contract. OPTIONAL (may be
    /// empty/absent in configs written before v2): when unset, inbox
    /// filtering is skipped and block/unblock are unavailable.
    /// </summary>
    public string? BlocklistAddress { get; init; }

    /// <summary>
    /// Project to the <see cref="ChainConfig"/> type used by the
    /// <c>MCPTransfer.Core.Chain</c> services. Throws a descriptive
    /// <see cref="InvalidOperationException"/> if any contract address is
    /// still empty (the <c>amoy</c> profile ships them blank to be filled
    /// after deployment).
    /// </summary>
    public ChainConfig ToCoreConfig()
    {
        RequireAddress(FileRegistryAddress, nameof(FileRegistryAddress));
        RequireAddress(KeyRegistryAddress, nameof(KeyRegistryAddress));
        RequireAddress(AgentDirectoryAddress, nameof(AgentDirectoryAddress));
        // Blocklist is optional — but if a value IS present it must parse.
        if (!string.IsNullOrEmpty(BlocklistAddress))
            RequireAddress(BlocklistAddress, nameof(BlocklistAddress));

        return new ChainConfig
        {
            RpcUrl = RpcUrl,
            ChainId = ChainId,
            FileRegistryAddress = EthereumAddress.FromHex(FileRegistryAddress),
            KeyRegistryAddress = EthereumAddress.FromHex(KeyRegistryAddress),
            AgentDirectoryAddress = EthereumAddress.FromHex(AgentDirectoryAddress),
            BlocklistAddress = string.IsNullOrEmpty(BlocklistAddress)
                ? null
                : EthereumAddress.FromHex(BlocklistAddress),
        };
    }

    private static void RequireAddress(string value, string field)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"Chain address '{field}' is not configured. If you bootstrapped with the "
                + "'amoy' profile, deploy the contracts and fill the three address fields in "
                + "your config (~/.mcptx/config.json) or via the MCPTX_* environment variables.");
        }
        // Validate the FORMAT here too, so a malformed-but-non-empty value
        // (typo like '0xZZ') yields a clear field-named error instead of a
        // raw FromHex ArgumentException/FormatException later.
        try
        {
            _ = EthereumAddress.FromHex(value);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                $"Chain address '{field}' = '{value}' is not a valid 0x Ethereum address: {ex.Message}", ex);
        }
    }
}

/// <summary>IPFS-backend slice of <see cref="MCPTransferConfig"/>.</summary>
public sealed record IpfsConfigSection
{
    public const string KindPinata = "pinata";
    public const string KindMemory = "memory";
    public const string KindFile = "file";

    /// <summary><c>"pinata"</c>, <c>"file"</c>, or <c>"memory"</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Pinata JWT bearer token. May be left empty in the file (set via
    /// <c>PINATA_JWT</c> env var at runtime). Required when <see cref="Kind"/>
    /// is <c>"pinata"</c>.
    /// </summary>
    public string? PinataJwt { get; init; }

    /// <summary>Optional override for the IPFS gateway URL.</summary>
    public string? GatewayUrl { get; init; }

    /// <summary>
    /// Shared directory backing the local file store. Required when
    /// <see cref="Kind"/> is <c>"file"</c>. Two agents pointing at the same
    /// directory can exchange files without a network IPFS provider.
    /// </summary>
    public string? Directory { get; init; }
}
