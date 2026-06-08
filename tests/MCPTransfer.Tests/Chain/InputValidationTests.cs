using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Chain;

/// <summary>
/// Validation that the chain clients reject malformed inputs at the C# layer,
/// before any RPC round-trip. These tests do not need a live RPC endpoint.
/// </summary>
public class InputValidationTests
{
    private static ChainConfig Sample() => new()
    {
        RpcUrl = "http://127.0.0.1:8545",
        ChainId = ChainConfig.AnvilChainId,
        FileRegistryAddress = EthereumAddress.FromHex("0x5FbDB2315678afecb367f032d93F642f64180aa3"),
        KeyRegistryAddress = EthereumAddress.FromHex("0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512"),
        AgentDirectoryAddress = EthereumAddress.FromHex("0x9fE46736679d2D9a65F0992F2272dE9f3c7fa6e0"),
    };

    private static readonly EthereumAddress AnyAddress = EthereumAddress.FromHex(
        "0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266");

    // ──────────────────────────────────────────────────────────────────
    // FileRegistry.SendAsync
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FileRegistry_SendAsync_RejectsEmptyCid()
    {
        var client = new FileRegistryClient(Sample());
        var signer = Secp256k1KeyPair.Generate();
        var hash = new byte[Hashes.Keccak256ByteLength];

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SendAsync(AnyAddress, "", hash, signer));
    }

    [Fact]
    public async Task FileRegistry_SendAsync_RejectsWrongContentHashLength()
    {
        var client = new FileRegistryClient(Sample());
        var signer = Secp256k1KeyPair.Generate();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SendAsync(AnyAddress, "cid", new byte[31], signer));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SendAsync(AnyAddress, "cid", new byte[33], signer));
    }

    [Fact]
    public async Task FileRegistry_SendAsync_RejectsNullSigner()
    {
        var client = new FileRegistryClient(Sample());
        var hash = new byte[Hashes.Keccak256ByteLength];

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.SendAsync(AnyAddress, "cid", hash, null!));
    }

    [Fact]
    public async Task FileRegistry_GetInboxAsync_RejectsInvertedRange()
    {
        var client = new FileRegistryClient(Sample());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.GetInboxAsync(AnyAddress, fromBlock: 100, toBlock: 50));
    }

    // ──────────────────────────────────────────────────────────────────
    // KeyRegistry.PublishAsync
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task KeyRegistry_PublishAsync_RejectsWrongMlKemLength()
    {
        var client = new KeyRegistryClient(Sample());
        var signer = Secp256k1KeyPair.Generate();
        var ec = new byte[KeyRegistryClient.Secp256k1CompressedLength];

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.PublishAsync(ec, new byte[1183], signer));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.PublishAsync(ec, new byte[1185], signer));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.PublishAsync(ec, ReadOnlyMemory<byte>.Empty, signer));
    }

    [Fact]
    public async Task KeyRegistry_PublishAsync_RejectsWrongSecp256k1Length()
    {
        var client = new KeyRegistryClient(Sample());
        var signer = Secp256k1KeyPair.Generate();
        var ml = new byte[KeyRegistryClient.MlKem768PubkeyLength];

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.PublishAsync(new byte[32], ml, signer));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.PublishAsync(new byte[34], ml, signer));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.PublishAsync(ReadOnlyMemory<byte>.Empty, ml, signer));
    }

    [Fact]
    public async Task KeyRegistry_PublishAsync_RejectsNullSigner()
    {
        var client = new KeyRegistryClient(Sample());
        var ec = new byte[KeyRegistryClient.Secp256k1CompressedLength];
        var ml = new byte[KeyRegistryClient.MlKem768PubkeyLength];

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.PublishAsync(ec, ml, null!));
    }

    // ──────────────────────────────────────────────────────────────────
    // AgentDirectory.ClaimAsync
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentDirectory_ClaimAsync_RejectsEmptyHandle()
    {
        var client = new AgentDirectoryClient(Sample());
        var signer = Secp256k1KeyPair.Generate();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.ClaimAsync("", signer));
    }

    [Fact]
    public async Task AgentDirectory_ClaimAsync_RejectsNullSigner()
    {
        var client = new AgentDirectoryClient(Sample());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.ClaimAsync("alice-ai", null!));
    }

    [Fact]
    public async Task AgentDirectory_ResolveAsync_RejectsEmptyHandle()
    {
        var client = new AgentDirectoryClient(Sample());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.ResolveAsync(""));
    }
}
