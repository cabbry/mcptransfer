using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Chain;

/// <summary>
/// The narrow-window fallback that keeps inbox/corroboration working on
/// public RPCs that cap the eth_getLogs block span (live-Amoy finding).
/// </summary>
public class FileRegistryQueriesTests
{
    private static readonly EthereumAddress Me =
        EthereumAddress.FromHex("0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266");
    private static readonly EthereumAddress Alice =
        EthereumAddress.FromHex("0x70997970C51812dc3A010C7d01b50e0d17dc79C8");

    private static FileSentEvent Ev(string cid, ulong block) => new(
        From: Alice, To: Me, Cid: cid,
        ContentHash: Enumerable.Repeat((byte)0xAB, 32).ToArray(),
        Timestamp: DateTimeOffset.UnixEpoch.AddSeconds(block),
        TransactionHash: "0x" + new string('a', 64),
        BlockNumber: block, LogIndex: 0);

    [Fact]
    public async Task FindByCid_WideRangeRejected_RetriesNarrowAndFinds()
    {
        // Event sits 100 blocks back — inside the 450-block fallback window.
        var fake = new RangeCappedRegistry(maxSpan: 500, new[] { Ev("cid-A", 9_900) }) { Latest = 10_000 };

        var found = await fake.FindByCidWithFallbackAsync(Me, "cid-A", fromBlock: 0, latestBlock: 10_000);

        Assert.NotNull(found);
        Assert.Equal(9_900UL, found!.BlockNumber);
        Assert.Equal(2, fake.Calls); // wide attempt + narrow retry
    }

    [Fact]
    public async Task FindByCid_RequestNarrowerThanSmallestFallback_DoesNotRetry()
    {
        var fake = new RangeCappedRegistry(maxSpan: 500, Array.Empty<FileSentEvent>()) { Latest = 10_000 };

        // Span (8) is below the smallest fallback window (9): a failure must
        // surface after the single attempt, not loop.
        fake.AlwaysThrow = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fake.FindByCidWithFallbackAsync(Me, "cid-A", fromBlock: 9_992, latestBlock: 10_000));
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task FindByCid_TightCap_WalksDownToTheWindowThatFits()
    {
        // Cap of 50 blocks: 450 fails too, only the 45-block window passes.
        var fake = new RangeCappedRegistry(maxSpan: 50, new[] { Ev("cid-A", 9_980) }) { Latest = 10_000 };

        var found = await fake.FindByCidWithFallbackAsync(Me, "cid-A", fromBlock: 0, latestBlock: 10_000);

        Assert.NotNull(found);
        Assert.Equal(3, fake.Calls); // wide + 450 (refused) + 45 (ok)
    }

    [Fact]
    public async Task GetInbox_WideRangeRejected_RetriesNarrow_AndReportsShrunkWindow()
    {
        var fake = new RangeCappedRegistry(maxSpan: 500, new[] { Ev("cid-A", 9_950) }) { Latest = 10_000 };

        var (events, fromBlock) = await fake.GetInboxWithFallbackAsync(Me, fromBlock: 0, latestBlock: 10_000);

        Assert.Single(events);
        Assert.Equal(10_000UL - FileRegistryQueries.FallbackSpans[0], fromBlock);
    }

    /// <summary>Fake registry that mimics a public RPC's getLogs span cap.</summary>
    private sealed class RangeCappedRegistry : IFileRegistryClient
    {
        private readonly ulong _maxSpan;
        private readonly FileSentEvent[] _events;
        public ulong Latest { get; set; }
        public int Calls { get; private set; }
        public bool AlwaysThrow { get; set; }

        public RangeCappedRegistry(ulong maxSpan, FileSentEvent[] events)
        {
            _maxSpan = maxSpan;
            _events = events;
        }

        public Task<IReadOnlyList<FileSentEvent>> GetInboxAsync(
            EthereumAddress me, ulong fromBlock, ulong toBlock, CancellationToken ct = default)
        {
            Calls++;
            if (AlwaysThrow || toBlock - fromBlock > _maxSpan)
                throw new InvalidOperationException("block range exceeds configured limit: eth_getLogs");
            return Task.FromResult<IReadOnlyList<FileSentEvent>>(
                _events.Where(e => e.BlockNumber >= fromBlock && e.BlockNumber <= toBlock).ToList());
        }

        public async Task<FileSentEvent?> FindByCidAsync(
            EthereumAddress me, string cid, ulong fromBlock, ulong toBlock, CancellationToken ct = default)
        {
            var events = await GetInboxAsync(me, fromBlock, toBlock, ct);
            return events.Where(e => e.Cid == cid)
                .OrderByDescending(e => e.BlockNumber).FirstOrDefault();
        }

        public Task<ulong> GetLatestBlockNumberAsync(CancellationToken ct = default)
            => Task.FromResult(Latest);
        public Task<string> SendAsync(EthereumAddress r, string c, byte[] h, Secp256k1KeyPair s, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<FileSentEvent> WatchInboxAsync(EthereumAddress m, ulong f, TimeSpan p, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
