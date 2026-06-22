using System.Security.Cryptography;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Tests.Ipfs;

public class FileIpfsClientTests : IDisposable
{
    private readonly string _dir;

    public FileIpfsClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mcptx-fileipfs-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Pin_IsContentAddressed_SameBytesYieldSameCid()
    {
        var client = new FileIpfsClient(_dir);
        var bytes = RandomNumberGenerator.GetBytes(1024);

        var a = await client.PinAsync(bytes);
        var b = await client.PinAsync(bytes);

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public async Task Fetch_ReturnsExactBytesPinned()
    {
        var client = new FileIpfsClient(_dir);
        var original = RandomNumberGenerator.GetBytes(4096);
        var cid = await client.PinAsync(original);

        var fetched = await client.FetchAsync(cid);
        Assert.Equal(original, fetched);
    }

    [Fact]
    public async Task Fetch_AcrossSeparateClientInstances_SharesStore()
    {
        // Simulates two processes pointing at the same directory.
        var alice = new FileIpfsClient(_dir);
        var bob = new FileIpfsClient(_dir);

        var original = RandomNumberGenerator.GetBytes(2048);
        var cid = await alice.PinAsync(original);

        var fetched = await bob.FetchAsync(cid);
        Assert.Equal(original, fetched);
    }

    [Fact]
    public async Task Fetch_ThrowsKeyNotFoundForUnknownCid()
    {
        var client = new FileIpfsClient(_dir);
        var unknown = new string('a', 64); // valid shape, never pinned
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.FetchAsync(unknown));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("not-hex")]
    [InlineData("ABCDEF")]               // uppercase, wrong length
    [InlineData("deadbeef")]             // hex but too short
    public async Task Fetch_RejectsMalformedOrTraversalCid(string cid)
    {
        var client = new FileIpfsClient(_dir);
        await Assert.ThrowsAsync<ArgumentException>(() => client.FetchAsync(cid));
    }

    [Fact]
    public async Task Unpin_DeletesBlob_AndIsIdempotent()
    {
        var client = new FileIpfsClient(_dir);
        var cid = await client.PinAsync(RandomNumberGenerator.GetBytes(512));
        Assert.True(File.Exists(Path.Combine(_dir, cid)));

        await client.UnpinAsync(cid);
        Assert.False(File.Exists(Path.Combine(_dir, cid)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.FetchAsync(cid));

        // Re-unpinning an absent CID is a no-op, not an error.
        await client.UnpinAsync(cid);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("not-hex")]
    public async Task Unpin_RejectsMalformedOrTraversalCid(string cid)
    {
        var client = new FileIpfsClient(_dir);
        await Assert.ThrowsAsync<ArgumentException>(() => client.UnpinAsync(cid));
    }

    [Fact]
    public async Task ConcurrentPins_OfSameContent_AreIdempotent()
    {
        var client = new FileIpfsClient(_dir);
        var bytes = RandomNumberGenerator.GetBytes(8192);

        var cids = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => client.PinAsync(bytes)));

        Assert.Single(cids.Distinct());
        // Exactly one blob file (plus no leftover .tmp).
        var files = Directory.GetFiles(_dir);
        Assert.Single(files);
        Assert.DoesNotContain(files, f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pin_LeavesNoTempFiles()
    {
        var client = new FileIpfsClient(_dir);
        await client.PinAsync(RandomNumberGenerator.GetBytes(100));
        var tempFiles = Directory.GetFiles(_dir, "*.tmp");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public void Constructor_RejectsEmptyDirectory()
    {
        Assert.ThrowsAny<ArgumentException>(() => new FileIpfsClient(""));
    }

    [Fact]
    public void ComputeCid_MatchesSha256Hex()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(expected, FileIpfsClient.ComputeCid(bytes));
    }
}
