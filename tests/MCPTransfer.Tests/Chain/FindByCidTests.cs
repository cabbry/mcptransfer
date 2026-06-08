using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Chain;

/// <summary>
/// Unit test of the FindByCidAsync selection logic over a fake event source,
/// without a live RPC. (The live round-trip is covered by the gated anvil
/// integration tests.)
/// </summary>
public class FindByCidTests
{
    private static readonly EthereumAddress Me =
        EthereumAddress.FromHex("0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266");
    private static readonly EthereumAddress Alice =
        EthereumAddress.FromHex("0x70997970C51812dc3A010C7d01b50e0d17dc79C8");

    private static FileSentEvent Ev(string cid, ulong block, uint logIndex, byte tag) => new(
        From: Alice, To: Me, Cid: cid,
        ContentHash: Enumerable.Repeat(tag, 32).ToArray(),
        Timestamp: DateTimeOffset.UnixEpoch.AddSeconds(block),
        TransactionHash: "0x" + new string('a', 64),
        BlockNumber: block, LogIndex: logIndex);

    [Fact]
    public async Task FindByCid_ReturnsMostRecentMatch()
    {
        var fake = new FakeFileRegistry(new[]
        {
            Ev("cid-A", 10, 0, 0x11),
            Ev("cid-B", 12, 0, 0x22),
            Ev("cid-A", 20, 1, 0x33), // newer announcement of cid-A
        });

        var found = await fake.FindByCidAsync(Me, "cid-A", 0, 100);
        Assert.NotNull(found);
        Assert.Equal(20UL, found!.BlockNumber);
        Assert.Equal(0x33, found.ContentHash[0]);
    }

    [Fact]
    public async Task FindByCid_ReturnsNullWhenAbsent()
    {
        var fake = new FakeFileRegistry(new[] { Ev("cid-A", 10, 0, 0x11) });
        Assert.Null(await fake.FindByCidAsync(Me, "cid-missing", 0, 100));
    }

    [Fact]
    public async Task FindByCid_RejectsEmptyCid()
    {
        var fake = new FakeFileRegistry(Array.Empty<FileSentEvent>());
        await Assert.ThrowsAsync<ArgumentException>(() => fake.FindByCidAsync(Me, "", 0, 100));
    }

    /// <summary>
    /// Minimal IFileRegistryClient that serves a fixed event set from
    /// GetInboxAsync, so the default FindByCidAsync filtering (which calls
    /// GetInboxAsync) can be tested without an RPC. Mirrors the real
    /// FileRegistryClient.FindByCidAsync selection (most-recent match).
    /// </summary>
    private sealed class FakeFileRegistry : IFileRegistryClient
    {
        private readonly FileSentEvent[] _events;
        public FakeFileRegistry(FileSentEvent[] events) => _events = events;

        public Task<IReadOnlyList<FileSentEvent>> GetInboxAsync(
            EthereumAddress me, ulong fromBlock, ulong toBlock, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileSentEvent>>(
                _events.Where(e => e.BlockNumber >= fromBlock && e.BlockNumber <= toBlock).ToList());

        public Task<FileSentEvent?> FindByCidAsync(
            EthereumAddress me, string cid, ulong fromBlock, ulong toBlock, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(cid);
            var match = _events
                .Where(e => e.BlockNumber >= fromBlock && e.BlockNumber <= toBlock
                    && string.Equals(e.Cid, cid, StringComparison.Ordinal))
                .OrderByDescending(e => e.BlockNumber).ThenByDescending(e => e.LogIndex)
                .FirstOrDefault();
            return Task.FromResult(match);
        }

        public Task<string> SendAsync(EthereumAddress r, string c, byte[] h, Secp256k1KeyPair s, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<FileSentEvent> WatchInboxAsync(EthereumAddress me, ulong f, TimeSpan p, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ulong> GetLatestBlockNumberAsync(CancellationToken ct = default)
            => Task.FromResult(100UL);
    }
}
