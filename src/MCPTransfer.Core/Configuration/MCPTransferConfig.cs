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
    /// Project to the <see cref="ChainConfig"/> type used by the
    /// <c>MCPTransfer.Core.Chain</c> services.
    /// </summary>
    public ChainConfig ToCoreConfig() => new()
    {
        RpcUrl = RpcUrl,
        ChainId = ChainId,
        FileRegistryAddress = EthereumAddress.FromHex(FileRegistryAddress),
        KeyRegistryAddress = EthereumAddress.FromHex(KeyRegistryAddress),
        AgentDirectoryAddress = EthereumAddress.FromHex(AgentDirectoryAddress),
    };
}

/// <summary>IPFS-backend slice of <see cref="MCPTransferConfig"/>.</summary>
public sealed record IpfsConfigSection
{
    public const string KindPinata = "pinata";
    public const string KindMemory = "memory";

    /// <summary><c>"pinata"</c> or <c>"memory"</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Pinata JWT bearer token. May be left empty in the file (set via
    /// <c>PINATA_JWT</c> env var at runtime). Required when <see cref="Kind"/>
    /// is <c>"pinata"</c>.
    /// </summary>
    public string? PinataJwt { get; init; }

    /// <summary>Optional override for the IPFS gateway URL.</summary>
    public string? GatewayUrl { get; init; }
}
