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

    /// <summary>Optional filesystem confinement for send_file / receive_file.</summary>
    public McpWorkspaceGuard Workspace { get; }

    /// <summary>
    /// Serializes state-changing chain operations (register_key / claim /
    /// send_file). All sign with the same EOA and rely on auto-nonce, so two
    /// concurrent tool calls would fetch the same pending nonce and one tx
    /// would be rejected. Acquire this around any transaction submission.
    /// </summary>
    public SemaphoreSlim SigningLock { get; } = new(1, 1);

    public McpAgentContext(
        AgentIdentity identity,
        MCPTransferConfig config,
        EthereumChainClient chain,
        IIpfsClient ipfs,
        McpWorkspaceGuard workspace)
    {
        Identity = identity;
        Config = config;
        Chain = chain;
        Ipfs = ipfs;
        Workspace = workspace;
    }

    public void Dispose()
    {
        // Chain currently owns no disposable handles directly; the IPFS
        // client may (PinataIpfsClient's HttpClient via RetryingIpfsClient).
        if (Ipfs is IDisposable d)
            d.Dispose();
        // Zero the agent's cached private-key material — the server is
        // long-lived, so this is where best-effort zeroization pays off.
        Identity.Dispose();
        SigningLock.Dispose();
    }
}
