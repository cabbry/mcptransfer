using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Chain;

public class RecipientResolverTests
{
    private static EthereumChainClient Chain() => new(new ChainConfig
    {
        RpcUrl = "http://127.0.0.1:8545",
        ChainId = ChainConfig.AnvilChainId,
        FileRegistryAddress = EthereumAddress.FromHex("0x5FbDB2315678afecb367f032d93F642f64180aa3"),
        KeyRegistryAddress = EthereumAddress.FromHex("0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512"),
        AgentDirectoryAddress = EthereumAddress.FromHex("0x9fE46736679d2D9a65F0992F2272dE9f3c7fa6e0"),
    });

    [Fact]
    public async Task ResolveAsync_MalformedHexAddress_ThrowsInvalidOperationNotArgument()
    {
        // Honors the documented contract: failures are InvalidOperationException,
        // not a raw ArgumentException/FormatException leaking from FromHex.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RecipientResolver.ResolveAsync(Chain(), "0xZZ"));
        Assert.Contains("not a valid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_InvalidHandleShape_ThrowsInvalidOperation()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RecipientResolver.ResolveAsync(Chain(), "Bad Handle!"));
        Assert.Contains("not a valid handle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_RejectsNullsAndEmpty()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => RecipientResolver.ResolveAsync(null!, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => RecipientResolver.ResolveAsync(Chain(), ""));
    }
}
