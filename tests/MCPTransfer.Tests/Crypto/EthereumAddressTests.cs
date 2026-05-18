using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Crypto;

public class EthereumAddressTests
{
    // Vectors from EIP-55 spec (https://eips.ethereum.org/EIPS/eip-55):
    [Theory]
    [InlineData("0x52908400098527886E0F7030069857D2E4169EE7")] // all upper
    [InlineData("0x8617E340B3D01FA5F11F306F4090FD50E238070D")] // all upper
    [InlineData("0xde709f2102306220921060314715629080e2fb77")] // all lower
    [InlineData("0x27b1fdb04752bbc536007a920d24acb045561c26")] // all lower
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")] // mixed
    [InlineData("0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")] // mixed
    [InlineData("0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB")] // mixed
    [InlineData("0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb")] // mixed
    public void ChecksumHex_MatchesEip55(string expected)
    {
        var address = EthereumAddress.FromHex(expected.ToLowerInvariant());
        Assert.Equal(expected, address.ChecksumHex);
    }

    [Fact]
    public void FromHex_AcceptsBothWithAndWithoutPrefix()
    {
        var a = EthereumAddress.FromHex("0x52908400098527886E0F7030069857D2E4169EE7");
        var b = EthereumAddress.FromHex("52908400098527886E0F7030069857D2E4169EE7");
        Assert.Equal(a, b);
    }

    [Fact]
    public void FromHex_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => EthereumAddress.FromHex("0xdeadbeef"));
    }

    [Fact]
    public void Constructor_RejectsWrongByteLength()
    {
        Assert.Throws<ArgumentException>(() => new EthereumAddress(new byte[19]));
        Assert.Throws<ArgumentException>(() => new EthereumAddress(new byte[21]));
    }

    [Fact]
    public void LowerHex_StripsCase()
    {
        var address = EthereumAddress.FromHex("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed");
        Assert.Equal("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", address.LowerHex);
    }

    [Fact]
    public void Equality_BasedOnBytes()
    {
        var a = EthereumAddress.FromHex("0x52908400098527886E0F7030069857D2E4169EE7");
        var b = EthereumAddress.FromHex("0x52908400098527886e0f7030069857d2e4169ee7");
        var c = EthereumAddress.FromHex("0x27b1fdb04752bbc536007a920d24acb045561c26");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.NotEqual(a, c);
        Assert.True(a != c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsChecksumForm()
    {
        var address = EthereumAddress.FromHex("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed");
        Assert.Equal("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", address.ToString());
    }
}
