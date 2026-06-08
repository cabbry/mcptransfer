using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Chain;

public class EthereumChainClientTests
{
    private static ChainConfig Sample() => new()
    {
        RpcUrl = "http://127.0.0.1:8545",
        ChainId = ChainConfig.AnvilChainId,
        FileRegistryAddress = EthereumAddress.FromHex("0x5FbDB2315678afecb367f032d93F642f64180aa3"),
        KeyRegistryAddress = EthereumAddress.FromHex("0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512"),
        AgentDirectoryAddress = EthereumAddress.FromHex("0x9fE46736679d2D9a65F0992F2272dE9f3c7fa6e0"),
    };

    [Fact]
    public void EthereumChainClient_Construction_ExposesAllThreeSubClients()
    {
        var client = new EthereumChainClient(Sample());

        Assert.NotNull(client.FileRegistry);
        Assert.NotNull(client.KeyRegistry);
        Assert.NotNull(client.AgentDirectory);
        Assert.Same(client.FileRegistry, client.FileRegistry); // stable reference
    }

    [Fact]
    public void EthereumChainClient_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EthereumChainClient(null!));
    }

    [Fact]
    public void EthereumChainClient_KnownChainIds_AreCorrect()
    {
        Assert.Equal(80002, ChainConfig.AmoyChainId);
        Assert.Equal(31337, ChainConfig.AnvilChainId);
    }

    [Fact]
    public void FileRegistryClient_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FileRegistryClient(null!));
    }

    [Fact]
    public void KeyRegistryClient_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new KeyRegistryClient(null!));
    }

    [Fact]
    public void AgentDirectoryClient_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AgentDirectoryClient(null!));
    }

    [Fact]
    public void KeyRegistryClient_MlKemPubkeyLengthConstant_MatchesContract()
    {
        // Mirror of KeyRegistry.ML_KEM_768_PUBKEY_LENGTH on the Solidity side.
        Assert.Equal(1184, KeyRegistryClient.MlKem768PubkeyLength);
        Assert.Equal(MlKemPublicKey.PublicKeyByteLength, KeyRegistryClient.MlKem768PubkeyLength);
    }
}
