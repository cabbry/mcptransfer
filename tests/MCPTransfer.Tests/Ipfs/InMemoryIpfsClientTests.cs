using System.Security.Cryptography;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Tests.Ipfs;

public class InMemoryIpfsClientTests
{
    [Fact]
    public async Task Pin_IsContentAddressed_SameBytesYieldSameCid()
    {
        var client = new InMemoryIpfsClient();
        var bytes = RandomNumberGenerator.GetBytes(1024);

        var cidA = await client.PinAsync(bytes);
        var cidB = await client.PinAsync(bytes);

        Assert.Equal(cidA, cidB);
        Assert.StartsWith("mem:", cidA);
        Assert.Equal(1, client.Count);
    }

    [Fact]
    public async Task Pin_DifferentBytesYieldDifferentCids()
    {
        var client = new InMemoryIpfsClient();
        var a = await client.PinAsync(RandomNumberGenerator.GetBytes(32));
        var b = await client.PinAsync(RandomNumberGenerator.GetBytes(32));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Fetch_ReturnsExactBytesThatWerePinned()
    {
        var client = new InMemoryIpfsClient();
        var original = RandomNumberGenerator.GetBytes(2048);
        var cid = await client.PinAsync(original);

        var roundTripped = await client.FetchAsync(cid);
        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public async Task Fetch_ReturnsDefensiveCopy()
    {
        var client = new InMemoryIpfsClient();
        var original = RandomNumberGenerator.GetBytes(64);
        var cid = await client.PinAsync(original);

        var copy = await client.FetchAsync(cid);
        copy[0] ^= 0xFF;

        var refetched = await client.FetchAsync(cid);
        Assert.NotEqual(copy[0], refetched[0]);
        Assert.Equal(original, refetched);
    }

    [Fact]
    public async Task Fetch_ThrowsOnUnknownCid()
    {
        var client = new InMemoryIpfsClient();
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => client.FetchAsync("mem:nonexistent"));
    }

    [Fact]
    public async Task ConcurrentPins_WithDistinctContent_AllSucceed()
    {
        var client = new InMemoryIpfsClient();
        var inputs = Enumerable.Range(0, 32)
            .Select(_ => RandomNumberGenerator.GetBytes(128))
            .ToArray();

        var cids = await Task.WhenAll(inputs.Select(i => client.PinAsync(i)));

        Assert.Equal(32, cids.Distinct().Count());
        Assert.Equal(32, client.Count);

        // Each can be fetched and matches its source.
        for (var i = 0; i < inputs.Length; i++)
        {
            var fetched = await client.FetchAsync(cids[i]);
            Assert.Equal(inputs[i], fetched);
        }
    }

    [Fact]
    public async Task ConcurrentPins_WithSameContent_AreIdempotent()
    {
        var client = new InMemoryIpfsClient();
        var bytes = RandomNumberGenerator.GetBytes(256);

        var cids = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => client.PinAsync(bytes)));

        Assert.Single(cids.Distinct());
        Assert.Equal(1, client.Count);
    }

    [Fact]
    public async Task Unpin_RemovesContent_AndIsIdempotent()
    {
        var client = new InMemoryIpfsClient();
        var cid = await client.PinAsync(RandomNumberGenerator.GetBytes(64));
        Assert.True(client.Contains(cid));

        await client.UnpinAsync(cid);
        Assert.False(client.Contains(cid));
        Assert.Equal(0, client.Count);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.FetchAsync(cid));

        // Re-unpinning an absent CID is a no-op, not an error.
        await client.UnpinAsync(cid);
    }

    [Fact]
    public void ComputeCid_StaticHelper_IsDeterministic()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var a = InMemoryIpfsClient.ComputeCid(bytes);
        var b = InMemoryIpfsClient.ComputeCid(bytes);
        Assert.Equal(a, b);
        Assert.StartsWith("mem:", a);
    }
}
