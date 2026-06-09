using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Tests.Chain;

public class RecipientResolverTests
{
    private static ChainConfig Config() => new()
    {
        RpcUrl = "http://127.0.0.1:8545",
        ChainId = ChainConfig.AnvilChainId,
        FileRegistryAddress = EthereumAddress.FromHex("0x5FbDB2315678afecb367f032d93F642f64180aa3"),
        KeyRegistryAddress = EthereumAddress.FromHex("0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512"),
        AgentDirectoryAddress = EthereumAddress.FromHex("0x9fE46736679d2D9a65F0992F2272dE9f3c7fa6e0"),
    };

    private static EthereumChainClient Chain() => new(Config());

    [Fact]
    public async Task ResolveAsync_MalformedHexAddress_ThrowsInvalidOperationNotArgument()
    {
        // Honors the documented contract: failures are InvalidOperationException,
        // not a raw ArgumentException/FormatException leaking from FromHex.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RecipientResolver.ResolveAsync(Chain(), new InMemoryIpfsClient(), "0xZZ"));
        Assert.Contains("not a valid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_InvalidHandleShape_ThrowsInvalidOperation()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RecipientResolver.ResolveAsync(Chain(), new InMemoryIpfsClient(), "Bad Handle!"));
        Assert.Contains("not a valid handle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_RejectsNullsAndEmpty()
    {
        var ipfs = new InMemoryIpfsClient();
        await Assert.ThrowsAsync<ArgumentNullException>(() => RecipientResolver.ResolveAsync(null!, ipfs, "x"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RecipientResolver.ResolveAsync(Chain(), null!, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => RecipientResolver.ResolveAsync(Chain(), ipfs, ""));
    }

    // ──────────────────────────────────────────────────────────────────
    // Registry-v2 commitment verification (fake registry, in-memory IPFS)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_HappyPath_FetchesKeyAndVerifiesCommitment()
    {
        var recipient = AgentIdentity.Generate();
        var ipfs = new InMemoryIpfsClient();
        var mlkemKey = recipient.MlKem.PublicKey.Bytes.ToArray();
        var cid = await ipfs.PinAsync(mlkemKey);

        var chain = ChainWithFakeRegistry(new AgentPublicKeys(
            recipient.Secp256k1.PublicKeyCompressed.ToArray(),
            Hashes.Keccak256(mlkemKey),
            cid));

        var resolved = await RecipientResolver.ResolveAsync(
            chain, ipfs, recipient.Address.ToString());

        Assert.Equal(recipient.Address, resolved.Address);
        Assert.Equal(mlkemKey, resolved.PublicIdentity.MlKem.Bytes.ToArray());
    }

    [Fact]
    public async Task ResolveAsync_TamperedKeyInStore_RefusesOnCommitmentMismatch()
    {
        var recipient = AgentIdentity.Generate();
        var ipfs = new InMemoryIpfsClient();
        var mlkemKey = recipient.MlKem.PublicKey.Bytes.ToArray();

        // The store serves DIFFERENT bytes than what the hash commits to.
        var tampered = (byte[])mlkemKey.Clone();
        tampered[0] ^= 0xFF;
        var cid = await ipfs.PinAsync(tampered);

        var chain = ChainWithFakeRegistry(new AgentPublicKeys(
            recipient.Secp256k1.PublicKeyCompressed.ToArray(),
            Hashes.Keccak256(mlkemKey), // commitment over the REAL key
            cid));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RecipientResolver.ResolveAsync(chain, ipfs, recipient.Address.ToString()));
        Assert.Contains("commitment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_KeyMissingFromStore_FailsWithCleanMessage()
    {
        var recipient = AgentIdentity.Generate();
        var mlkemKey = recipient.MlKem.PublicKey.Bytes.ToArray();

        var chain = ChainWithFakeRegistry(new AgentPublicKeys(
            recipient.Secp256k1.PublicKeyCompressed.ToArray(),
            Hashes.Keccak256(mlkemKey),
            "cid-that-was-never-pinned"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RecipientResolver.ResolveAsync(chain, new InMemoryIpfsClient(), recipient.Address.ToString()));
        Assert.Contains("Could not fetch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static EthereumChainClient ChainWithFakeRegistry(AgentPublicKeys entry)
        => new(
            Config(),
            new ThrowingFileRegistry(),
            new FakeKeyRegistry(entry),
            new ThrowingAgentDirectory());

    private sealed class FakeKeyRegistry : IKeyRegistryClient
    {
        private readonly AgentPublicKeys _entry;
        public FakeKeyRegistry(AgentPublicKeys entry) => _entry = entry;

        public Task<AgentPublicKeys> GetAsync(EthereumAddress who, CancellationToken ct = default)
            => Task.FromResult(_entry);

        public Task<string> PublishAsync(ReadOnlyMemory<byte> s, ReadOnlyMemory<byte> h, string c, Secp256k1KeyPair self, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingFileRegistry : IFileRegistryClient
    {
        public Task<string> SendAsync(EthereumAddress r, string c, byte[] h, Secp256k1KeyPair s, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<FileSentEvent> WatchInboxAsync(EthereumAddress m, ulong f, TimeSpan p, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<FileSentEvent>> GetInboxAsync(EthereumAddress m, ulong f, ulong t, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ulong> GetLatestBlockNumberAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileSentEvent?> FindByCidAsync(EthereumAddress m, string c, ulong f, ulong t, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingAgentDirectory : IAgentDirectoryClient
    {
        public Task<string> ClaimAsync(string h, Secp256k1KeyPair s, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> TransferAsync(string h, EthereumAddress n, Secp256k1KeyPair s, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<EthereumAddress?> ResolveAsync(string h, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string?> ReverseResolveAsync(EthereumAddress a, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
