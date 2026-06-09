using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Chain;

public class InboxFilterTests
{
    private static readonly EthereumAddress Me =
        EthereumAddress.FromHex("0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266");
    private static readonly EthereumAddress Alice =
        EthereumAddress.FromHex("0x70997970C51812dc3A010C7d01b50e0d17dc79C8");
    private static readonly EthereumAddress Mallory =
        EthereumAddress.FromHex("0x3C44CdDdB6a900fa2b585dd299e03d12FA4293BC");

    private static FileSentEvent Ev(EthereumAddress from, ulong block) => new(
        From: from, To: Me, Cid: $"cid-{block}",
        ContentHash: new byte[32],
        Timestamp: DateTimeOffset.UnixEpoch.AddSeconds(block),
        TransactionHash: "0x" + new string('a', 64),
        BlockNumber: block, LogIndex: 0);

    [Fact]
    public async Task Apply_NullBlocklist_PassesAllThrough()
    {
        var events = new[] { Ev(Alice, 1), Ev(Mallory, 2) };
        var result = await InboxFilter.ApplyAsync(null, Me, events);
        Assert.Equal(2, result.Kept.Count);
        Assert.Equal(0, result.Hidden);
    }

    [Fact]
    public async Task Apply_HidesBlockedSendersOnly()
    {
        var events = new[] { Ev(Alice, 1), Ev(Mallory, 2), Ev(Mallory, 3), Ev(Alice, 4) };
        var fake = new FakeBlocklist(Mallory);

        var result = await InboxFilter.ApplyAsync(fake, Me, events);

        Assert.Equal(2, result.Kept.Count);
        Assert.All(result.Kept, e => Assert.Equal(Alice, e.From));
        Assert.Equal(2, result.Hidden);
        // One read per DISTINCT sender, not per event.
        Assert.Equal(2, fake.Queries);
    }

    [Fact]
    public async Task Apply_NoBlockedSenders_ReturnsSameList()
    {
        var events = new[] { Ev(Alice, 1) };
        var result = await InboxFilter.ApplyAsync(new FakeBlocklist(), Me, events);
        Assert.Single(result.Kept);
        Assert.Equal(0, result.Hidden);
    }

    [Fact]
    public async Task Apply_EmptyInbox_SkipsQueriesEntirely()
    {
        var fake = new FakeBlocklist(Mallory);
        var result = await InboxFilter.ApplyAsync(fake, Me, Array.Empty<FileSentEvent>());
        Assert.Empty(result.Kept);
        Assert.Equal(0, fake.Queries);
    }

    private sealed class FakeBlocklist : IBlocklistClient
    {
        private readonly HashSet<EthereumAddress> _blocked;
        public int Queries { get; private set; }

        public FakeBlocklist(params EthereumAddress[] blocked) => _blocked = new(blocked);

        public Task<bool> IsBlockedAsync(EthereumAddress recipient, EthereumAddress sender, CancellationToken ct = default)
        {
            Queries++;
            return Task.FromResult(_blocked.Contains(sender));
        }

        public Task<string> SetBlockedAsync(EthereumAddress sender, bool blocked, Secp256k1KeyPair self, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
