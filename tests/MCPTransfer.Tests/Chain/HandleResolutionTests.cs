using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Chain;

public class HandleResolutionTests
{
    private static readonly EthereumAddress Alice =
        EthereumAddress.FromHex("0x70997970C51812dc3A010C7d01b50e0d17dc79C8");

    // ── ParseAddress: every malformed-input exception -> InvalidOperationException ──

    [Fact]
    public void ParseAddress_ValidHex_Parses()
    {
        var a = HandleResolution.ParseAddress("0x70997970C51812dc3A010C7d01b50e0d17dc79C8");
        Assert.Equal(Alice.LowerHex, a.LowerHex);
    }

    [Fact]
    public void ParseAddress_CorrectLengthButNonHex_ThrowsInvalidOperationNotFormat()
    {
        // 42 chars (0x + 40) but 'Z'/'G' are not hex -> Convert.FromHexString would
        // throw FormatException; the helper must surface InvalidOperationException.
        var bad = "0xZZ7Ed3AccA5a467e9e704C703E8D87F634fB0Fc9"; // 40 hex-length, non-hex
        var ex = Assert.Throws<InvalidOperationException>(() => HandleResolution.ParseAddress(bad));
        Assert.Contains("not a valid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAddress_WrongLength_ThrowsInvalidOperation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HandleResolution.ParseAddress("0xabcd"));
        Assert.Contains("not a valid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAddress_NullOrEmpty_Throws()
    {
        Assert.Throws<ArgumentException>(() => HandleResolution.ParseAddress(""));
    }

    // ── ResolveRequiredAsync ──

    [Fact]
    public async Task ResolveRequired_Address_ReturnsItWithNullHandle()
    {
        var dir = new FakeDirectory();
        var (addr, handle) = await HandleResolution.ResolveRequiredAsync(dir, Alice.ToString());
        Assert.Equal(Alice.LowerHex, addr.LowerHex);
        Assert.Null(handle);
        Assert.Equal(0, dir.Resolves); // a raw address never hits the directory
    }

    [Fact]
    public async Task ResolveRequired_ClaimedHandle_Resolves()
    {
        var dir = new FakeDirectory { { "alice-ai", Alice } };
        var (addr, handle) = await HandleResolution.ResolveRequiredAsync(dir, "alice-ai");
        Assert.Equal(Alice.LowerHex, addr.LowerHex);
        Assert.Equal("alice-ai", handle);
    }

    [Fact]
    public async Task ResolveRequired_UnclaimedHandle_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HandleResolution.ResolveRequiredAsync(new FakeDirectory(), "ghost-ai"));
        Assert.Contains("not claimed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveRequired_InvalidHandleShape_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HandleResolution.ResolveRequiredAsync(new FakeDirectory(), "Bad Handle!"));
        Assert.Contains("not a valid handle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveRequired_MalformedAddress_ThrowsInvalidOperationNotFormat()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => HandleResolution.ResolveRequiredAsync(
                new FakeDirectory(), "0xZZ7Ed3AccA5a467e9e704C703E8D87F634fB0Fc9"));
    }

    private sealed class FakeDirectory : IAgentDirectoryClient, System.Collections.IEnumerable
    {
        private readonly Dictionary<string, EthereumAddress> _handles = new(StringComparer.Ordinal);
        public int Resolves { get; private set; }

        public void Add(string handle, EthereumAddress addr) => _handles[handle] = addr;
        public System.Collections.IEnumerator GetEnumerator() => _handles.GetEnumerator();

        public Task<EthereumAddress?> ResolveAsync(string handle, CancellationToken ct = default)
        {
            Resolves++;
            return Task.FromResult(_handles.TryGetValue(handle, out var a) ? a : null);
        }

        public Task<string> ClaimAsync(string h, Secp256k1KeyPair s, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> TransferAsync(string h, EthereumAddress n, Secp256k1KeyPair s, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string?> ReverseResolveAsync(EthereumAddress a, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
