using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Static configuration for an EVM-chain deployment of the three MCPTransfer
/// contracts. Default to Polygon Amoy testnet; tests use Anvil locally.
/// </summary>
public sealed record ChainConfig
{
    /// <summary>Polygon Amoy testnet chain id.</summary>
    public const long AmoyChainId = 80002;

    /// <summary>Foundry default local devnet chain id.</summary>
    public const long AnvilChainId = 31337;

    /// <summary>HTTP(S) JSON-RPC endpoint URL.</summary>
    public required string RpcUrl { get; init; }

    /// <summary>EVM chain id (used for EIP-155 replay protection on signed tx).</summary>
    public required long ChainId { get; init; }

    /// <summary>Address of the deployed <c>FileRegistry</c> contract.</summary>
    public required EthereumAddress FileRegistryAddress { get; init; }

    /// <summary>Address of the deployed <c>KeyRegistry</c> contract.</summary>
    public required EthereumAddress KeyRegistryAddress { get; init; }

    /// <summary>Address of the deployed <c>AgentDirectory</c> contract.</summary>
    public required EthereumAddress AgentDirectoryAddress { get; init; }

    /// <summary>
    /// Address of the deployed <c>Blocklist</c> contract, or <c>null</c> if
    /// no blocklist is configured (inbox filtering is then skipped and the
    /// block/unblock commands are unavailable). Optional so configs written
    /// before v2 keep loading.
    /// </summary>
    public EthereumAddress? BlocklistAddress { get; init; }
}
