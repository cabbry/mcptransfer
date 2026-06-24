using MCPTransfer.Agent.Commands;
using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Configuration;

namespace MCPTransfer.Tests.Commands;

public class SetupCatalogTests
{
    [Fact]
    public void FindChain_KnownAndUnknown()
    {
        Assert.NotNull(SetupCatalog.FindChain("amoy"));
        Assert.NotNull(SetupCatalog.FindChain("ANVIL-LOCAL")); // case-insensitive
        Assert.Null(SetupCatalog.FindChain("ethereum-mainnet"));
    }

    [Fact]
    public void FindChainByChainId_MapsBack()
    {
        Assert.Equal("amoy", SetupCatalog.FindChainByChainId(ChainConfig.AmoyChainId)?.Name);
        Assert.Equal("anvil-local", SetupCatalog.FindChainByChainId(ChainConfig.AnvilChainId)?.Name);
        Assert.Null(SetupCatalog.FindChainByChainId(999_999));
    }

    [Fact]
    public void BuildStorage_EachBackend()
    {
        var file = SetupCatalog.BuildStorage(IpfsConfigSection.KindFile, null, "/data");
        Assert.Equal(IpfsConfigSection.KindFile, file.Kind);
        Assert.Equal("/data", file.Directory);

        var pinata = SetupCatalog.BuildStorage(IpfsConfigSection.KindPinata, "eyJ-fake", null);
        Assert.Equal(IpfsConfigSection.KindPinata, pinata.Kind);
        Assert.Equal("eyJ-fake", pinata.PinataJwt);

        var mem = SetupCatalog.BuildStorage(IpfsConfigSection.KindMemory, null, null);
        Assert.Equal(IpfsConfigSection.KindMemory, mem.Kind);
    }

    [Fact]
    public void BuildStorage_UnknownKind_Throws()
        => Assert.Throws<ArgumentException>(() => SetupCatalog.BuildStorage("arweave-soon", null, null));

    [Fact]
    public void Compose_JoinsChainAndStorage()
    {
        var chain = SetupCatalog.FindChain("anvil-local")!;
        var storage = SetupCatalog.BuildStorage(IpfsConfigSection.KindFile, null, "/data");

        var config = SetupCatalog.Compose(chain, storage);

        Assert.Equal(ChainConfig.AnvilChainId, config.Chain.ChainId);
        Assert.Equal(IpfsConfigSection.KindFile, config.Ipfs.Kind);
        Assert.Equal("/data", config.Ipfs.Directory);
    }
}
