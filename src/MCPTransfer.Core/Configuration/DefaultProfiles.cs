using MCPTransfer.Core.Chain;

namespace MCPTransfer.Core.Configuration;

/// <summary>
/// Canned <see cref="MCPTransferConfig"/> templates that <c>mcptx config init</c>
/// can write out as a starting point.
/// </summary>
public static class DefaultProfiles
{
    /// <summary>
    /// Local Anvil dev profile. Addresses are the deterministic ones produced
    /// by deploying <c>script/Deploy.s.sol</c> from Anvil's default
    /// nonce-0 / 1 / 2 deployer — matching what
    /// <c>anvil --silent &amp;&amp; forge script ... --broadcast</c> yields.
    /// </summary>
    public static MCPTransferConfig AnvilLocal(string? pinataJwt = null) => new()
    {
        Chain = new ChainConfigSection
        {
            RpcUrl = "http://127.0.0.1:8545",
            ChainId = ChainConfig.AnvilChainId,
            FileRegistryAddress = "0x5FbDB2315678afecb367f032d93F642f64180aa3",
            KeyRegistryAddress = "0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512",
            AgentDirectoryAddress = "0x9fE46736679d2D9a65F0992F2272dE9f3c7fa6e0",
        },
        Ipfs = new IpfsConfigSection
        {
            Kind = string.IsNullOrEmpty(pinataJwt) ? IpfsConfigSection.KindMemory : IpfsConfigSection.KindPinata,
            PinataJwt = pinataJwt,
            GatewayUrl = null,
        },
    };

    /// <summary>
    /// Polygon Amoy testnet profile. Contract addresses left as empty placeholders;
    /// the operator must fill them in after deploying to Amoy.
    /// </summary>
    public static MCPTransferConfig Amoy(string? pinataJwt = null) => new()
    {
        Chain = new ChainConfigSection
        {
            RpcUrl = "https://rpc-amoy.polygon.technology",
            ChainId = ChainConfig.AmoyChainId,
            FileRegistryAddress = string.Empty,
            KeyRegistryAddress = string.Empty,
            AgentDirectoryAddress = string.Empty,
        },
        Ipfs = new IpfsConfigSection
        {
            Kind = IpfsConfigSection.KindPinata,
            PinataJwt = pinataJwt,
            GatewayUrl = null,
        },
    };
}
