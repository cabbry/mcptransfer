using System.Numerics;
using MCPTransfer.Core.Chain.Abi;
using MCPTransfer.Core.Crypto;

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

    /// <summary>
    /// Blocklist client, or <c>null</c> when
    /// <see cref="ChainConfig.BlocklistAddress"/> is unset (pre-v2 config).
    /// Callers degrade gracefully: inbox filtering is skipped and
    /// block/unblock operations report the missing configuration.
    /// </summary>
    public IBlocklistClient? Blocklist { get; }

    public EthereumChainClient(ChainConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
        FileRegistry = new FileRegistryClient(config);
        KeyRegistry = new KeyRegistryClient(config);
        AgentDirectory = new AgentDirectoryClient(config);
        Blocklist = config.BlocklistAddress is null ? null : new BlocklistClient(config);
    }

    /// <summary>
    /// Internal-test constructor that lets a test rig substitute the
    /// clients (e.g., with a mock implementation that does not require an
    /// RPC endpoint).
    /// </summary>
    internal EthereumChainClient(
        ChainConfig config,
        IFileRegistryClient fileRegistry,
        IKeyRegistryClient keyRegistry,
        IAgentDirectoryClient agentDirectory,
        IBlocklistClient? blocklist = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(fileRegistry);
        ArgumentNullException.ThrowIfNull(keyRegistry);
        ArgumentNullException.ThrowIfNull(agentDirectory);
        Config = config;
        FileRegistry = fileRegistry;
        KeyRegistry = keyRegistry;
        AgentDirectory = agentDirectory;
        Blocklist = blocklist;
    }

    /// <summary>
    /// Native-token balance (in wei) of <paramref name="who"/>. Used by
    /// diagnostics to tell whether an agent can pay gas for state-changing
    /// operations (register-key, claim, send). EVM-specific — a multi-chain
    /// facade would surface this behind a per-chain abstraction.
    /// </summary>
    public async Task<BigInteger> GetNativeBalanceAsync(
        EthereumAddress who, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(who);
        var web3 = Web3Factory.CreateReadOnly(Config);
        var balance = await web3.Eth.GetBalance.SendRequestAsync(who.LowerHex).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return balance.Value;
    }
}
