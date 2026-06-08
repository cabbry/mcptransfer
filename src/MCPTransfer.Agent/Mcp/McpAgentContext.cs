using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Configuration;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Agent.Mcp;

/// <summary>
/// Process-wide singletons shared by every MCP tool: the local agent
/// identity, the loaded config, the on-chain client facade, and the IPFS
/// client. Registered once in the DI container at server startup so the
/// chain client's read-only Web3 instances and the IPFS HttpClient are
/// created once for the server's lifetime (not per tool call) and disposed
/// on host shutdown.
/// </summary>
public sealed class McpAgentContext : IDisposable
{
    public AgentIdentity Identity { get; }
    public MCPTransferConfig Config { get; }
    public EthereumChainClient Chain { get; }
    public IIpfsClient Ipfs { get; }

    public McpAgentContext(
        AgentIdentity identity,
        MCPTransferConfig config,
        EthereumChainClient chain,
        IIpfsClient ipfs)
    {
        Identity = identity;
        Config = config;
        Chain = chain;
        Ipfs = ipfs;
    }

    public void Dispose()
    {
        // Chain currently owns no disposable handles directly; the IPFS
        // client may (PinataIpfsClient's HttpClient via RetryingIpfsClient).
        if (Ipfs is IDisposable d)
            d.Dispose();
    }
}
