namespace MCPTransfer.Core.Chain;

/// <summary>
/// Facade that bundles the three contract-specific clients against a single
/// <see cref="ChainConfig"/>. Constructed once per process; the underlying
/// clients each hold their own read-only <c>Web3</c> instance.
/// </summary>
public sealed class EthereumChainClient
{
    public ChainConfig Config { get; }
    public IFileRegistryClient FileRegistry { get; }
    public IKeyRegistryClient KeyRegistry { get; }
    public IAgentDirectoryClient AgentDirectory { get; }

    public EthereumChainClient(ChainConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
        FileRegistry = new FileRegistryClient(config);
        KeyRegistry = new KeyRegistryClient(config);
        AgentDirectory = new AgentDirectoryClient(config);
    }

    /// <summary>
    /// Internal-test constructor that lets a test rig substitute the three
    /// clients (e.g., with a mock implementation that does not require an
    /// RPC endpoint).
    /// </summary>
    internal EthereumChainClient(
        ChainConfig config,
        IFileRegistryClient fileRegistry,
        IKeyRegistryClient keyRegistry,
        IAgentDirectoryClient agentDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(fileRegistry);
        ArgumentNullException.ThrowIfNull(keyRegistry);
        ArgumentNullException.ThrowIfNull(agentDirectory);
        Config = config;
        FileRegistry = fileRegistry;
        KeyRegistry = keyRegistry;
        AgentDirectory = agentDirectory;
    }
}
