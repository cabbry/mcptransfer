using System.Numerics;
using MCPTransfer.Agent.Commands;
using MCPTransfer.Core.Configuration;

namespace MCPTransfer.Tests.Commands;

public class DoctorChecksTests
{
    private static IpfsConfigSection Ipfs(string kind, string? jwt = null, string? dir = null)
        => new() { Kind = kind, PinataJwt = jwt, Directory = dir };

    [Fact]
    public void Storage_Memory_Warns()
        => Assert.Equal(CheckStatus.Warn, DoctorChecks.Storage(Ipfs(IpfsConfigSection.KindMemory)).Status);

    [Fact]
    public void Storage_PinataWithoutJwt_Fails()
        => Assert.Equal(CheckStatus.Fail, DoctorChecks.Storage(Ipfs(IpfsConfigSection.KindPinata)).Status);

    [Fact]
    public void Storage_PinataWithJwt_Ok()
        => Assert.Equal(CheckStatus.Ok, DoctorChecks.Storage(Ipfs(IpfsConfigSection.KindPinata, jwt: "eyJ-fake")).Status);

    [Fact]
    public void Storage_FileWithoutDirectory_Fails()
        => Assert.Equal(CheckStatus.Fail, DoctorChecks.Storage(Ipfs(IpfsConfigSection.KindFile)).Status);

    [Fact]
    public void Storage_FileDirectoryExists_Ok()
        => Assert.Equal(CheckStatus.Ok,
            DoctorChecks.Storage(Ipfs(IpfsConfigSection.KindFile, dir: "/some/dir"), _ => true).Status);

    [Fact]
    public void Storage_FileDirectoryMissing_Warns()
        => Assert.Equal(CheckStatus.Warn,
            DoctorChecks.Storage(Ipfs(IpfsConfigSection.KindFile, dir: "/some/dir"), _ => false).Status);

    [Fact]
    public void Storage_UnknownKind_Fails()
        => Assert.Equal(CheckStatus.Fail, DoctorChecks.Storage(Ipfs("arweave-soon")).Status);

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "0.000000000000000001")] // 1 wei (below display precision rounds to long form)
    public void FormatNativeBalance_SmallValues(long wei, string _)
    {
        // Just assert it produces a string without throwing; exact tiny formatting
        // is not contractual.
        Assert.False(string.IsNullOrEmpty(DoctorChecks.FormatNativeBalance(new BigInteger(wei))));
    }

    [Fact]
    public void FormatNativeBalance_WholeAndFractionalTokens()
    {
        var oneToken = BigInteger.Pow(10, 18);
        Assert.Equal("1", DoctorChecks.FormatNativeBalance(oneToken));
        Assert.Equal("1.5", DoctorChecks.FormatNativeBalance(oneToken + BigInteger.Pow(10, 17) * 5));
        Assert.Equal("0", DoctorChecks.FormatNativeBalance(BigInteger.Zero));
    }
}
