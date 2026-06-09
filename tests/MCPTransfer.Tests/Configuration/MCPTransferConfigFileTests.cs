using MCPTransfer.Core.Configuration;

namespace MCPTransfer.Tests.Configuration;

public class MCPTransferConfigFileTests
{
    private static MCPTransferConfig Sample() => DefaultProfiles.AnvilLocal();

    [Fact]
    public void Serialize_Deserialize_RoundTrips()
    {
        var original = Sample();
        var bytes = MCPTransferConfigFile.Serialize(original);
        var restored = MCPTransferConfigFile.Deserialize(bytes);

        Assert.Equal(original.Version, restored.Version);
        Assert.Equal(original.Chain.RpcUrl, restored.Chain.RpcUrl);
        Assert.Equal(original.Chain.ChainId, restored.Chain.ChainId);
        Assert.Equal(original.Chain.FileRegistryAddress, restored.Chain.FileRegistryAddress);
        Assert.Equal(original.Chain.KeyRegistryAddress, restored.Chain.KeyRegistryAddress);
        Assert.Equal(original.Chain.AgentDirectoryAddress, restored.Chain.AgentDirectoryAddress);
        Assert.Equal(original.Ipfs.Kind, restored.Ipfs.Kind);
    }

    [Fact]
    public void Serialize_UsesSnakeCaseJson()
    {
        var bytes = MCPTransferConfigFile.Serialize(Sample());
        var asText = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"rpc_url\":", asText);
        Assert.Contains("\"chain_id\":", asText);
        Assert.Contains("\"file_registry_address\":", asText);
    }

    [Fact]
    public void Deserialize_RejectsUnsupportedVersion()
    {
        var json = """
            {
              "version": 999,
              "chain": {
                "rpc_url": "http://x", "chain_id": 1,
                "file_registry_address": "0xAA", "key_registry_address": "0xBB",
                "agent_directory_address": "0xCC"
              },
              "ipfs": { "kind": "memory" }
            }
            """;
        Assert.Throws<InvalidOperationException>(
            () => MCPTransferConfigFile.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task SaveAsync_LoadAsync_RoundTripThroughDisk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcptx-cfg-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(tempDir, "config.json");
        try
        {
            await MCPTransferConfigFile.SaveAsync(Sample(), path);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));

            var loaded = await MCPTransferConfigFile.LoadAsync(path, applyEnvOverrides: false);
            Assert.Equal(Sample().Chain.RpcUrl, loaded.Chain.RpcUrl);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ApplyEnvOverrides_OverridesIndividualFields()
    {
        var baseline = Sample();

        Environment.SetEnvironmentVariable("MCPTX_RPC_URL", "https://overridden.example/rpc");
        Environment.SetEnvironmentVariable("MCPTX_CHAIN_ID", "12345");
        try
        {
            var overridden = MCPTransferConfigFile.ApplyEnvOverrides(baseline);
            Assert.Equal("https://overridden.example/rpc", overridden.Chain.RpcUrl);
            Assert.Equal(12345, overridden.Chain.ChainId);
            // Other fields unchanged.
            Assert.Equal(baseline.Chain.FileRegistryAddress, overridden.Chain.FileRegistryAddress);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCPTX_RPC_URL", null);
            Environment.SetEnvironmentVariable("MCPTX_CHAIN_ID", null);
        }
    }

    [Fact]
    public void ApplyEnvOverrides_PinataJwtFromEnv()
    {
        var baseline = Sample() with
        {
            Ipfs = Sample().Ipfs with { Kind = "pinata", PinataJwt = null },
        };

        Environment.SetEnvironmentVariable("PINATA_JWT", "envjwt.abc");
        try
        {
            var overridden = MCPTransferConfigFile.ApplyEnvOverrides(baseline);
            Assert.Equal("envjwt.abc", overridden.Ipfs.PinataJwt);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PINATA_JWT", null);
        }
    }

    [Fact]
    public void DefaultProfiles_AnvilLocal_HasDeterministicAddresses()
    {
        var p = DefaultProfiles.AnvilLocal();
        Assert.Equal(31337, p.Chain.ChainId);
        Assert.Equal("0x5FbDB2315678afecb367f032d93F642f64180aa3", p.Chain.FileRegistryAddress);
        // ipfs defaults to memory when no JWT supplied.
        Assert.Equal("memory", p.Ipfs.Kind);
    }

    [Fact]
    public void DefaultProfiles_AnvilLocal_WithJwt_SwitchesToPinata()
    {
        var p = DefaultProfiles.AnvilLocal(pinataJwt: "eyJ.test.jwt");
        Assert.Equal("pinata", p.Ipfs.Kind);
        Assert.Equal("eyJ.test.jwt", p.Ipfs.PinataJwt);
    }

    [Fact]
    public void DefaultProfiles_Amoy_ShipsCanonicalDeployment()
    {
        var p = DefaultProfiles.Amoy();
        Assert.Equal(80002, p.Chain.ChainId);
        // The POC's canonical Amoy deployment (2026-06-10); all four present
        // and parseable.
        Assert.Equal("0x04d02596F41b620857603240d822309847A07261", p.Chain.FileRegistryAddress);
        var core = p.Chain.ToCoreConfig();
        Assert.NotNull(core.BlocklistAddress);
        Assert.Equal("pinata", p.Ipfs.Kind);
    }

    [Fact]
    public void DefaultProfiles_AnvilLocal_WithIpfsDir_SelectsFileStore()
    {
        var p = DefaultProfiles.AnvilLocal(ipfsDir: "/tmp/shared");
        Assert.Equal("file", p.Ipfs.Kind);
        Assert.Equal("/tmp/shared", p.Ipfs.Directory);
    }

    [Fact]
    public void DefaultProfiles_AnvilLocal_IpfsDir_WinsOverJwt()
    {
        var p = DefaultProfiles.AnvilLocal(pinataJwt: "eyJ.x", ipfsDir: "/tmp/shared");
        Assert.Equal("file", p.Ipfs.Kind);
    }

    [Fact]
    public void ApplyEnvOverrides_IpfsDirFromEnv()
    {
        var baseline = Sample();
        Environment.SetEnvironmentVariable("MCPTX_IPFS_DIR", "/env/dir");
        try
        {
            var o = MCPTransferConfigFile.ApplyEnvOverrides(baseline);
            Assert.Equal("/env/dir", o.Ipfs.Directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCPTX_IPFS_DIR", null);
        }
    }

    [Fact]
    public void ApplyEnvOverrides_ThrowsOnUnparseableChainId()
    {
        var baseline = Sample();
        Environment.SetEnvironmentVariable("MCPTX_CHAIN_ID", "0x13882");
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => MCPTransferConfigFile.ApplyEnvOverrides(baseline));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCPTX_CHAIN_ID", null);
        }
    }

    [Fact]
    public void Deserialize_WrapsMalformedJsonAsInvalidOperation()
    {
        // Present but missing the required `chain` section.
        var json = "{ \"version\": 1, \"ipfs\": { \"kind\": \"memory\" } }";
        var ex = Assert.Throws<InvalidOperationException>(
            () => MCPTransferConfigFile.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
        Assert.Contains("malformed or missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_WrapsInvalidSyntaxAsInvalidOperation()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MCPTransferConfigFile.Deserialize(System.Text.Encoding.UTF8.GetBytes("not json at all")));
        Assert.Contains("malformed or missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToCoreConfig_ThrowsFriendlyOnEmptyAddresses()
    {
        var cfg = DefaultProfiles.Amoy();
        var emptied = cfg with { Chain = cfg.Chain with { FileRegistryAddress = string.Empty } };
        var ex = Assert.Throws<InvalidOperationException>(() => emptied.Chain.ToCoreConfig());
        Assert.Contains("not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FileRegistryAddress", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCoreConfig_ThrowsFriendlyOnMalformedAddress()
    {
        var cfg = DefaultProfiles.AnvilLocal();
        var bad = cfg with { Chain = cfg.Chain with { KeyRegistryAddress = "0xZZ" } };

        var ex = Assert.Throws<InvalidOperationException>(() => bad.Chain.ToCoreConfig());
        Assert.Contains("KeyRegistryAddress", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a valid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChainConfigSection_ToCoreConfig_ProjectsAddresses()
    {
        var section = new ChainConfigSection
        {
            RpcUrl = "http://x",
            ChainId = 31337,
            FileRegistryAddress = "0x5FbDB2315678afecb367f032d93F642f64180aa3",
            KeyRegistryAddress = "0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512",
            AgentDirectoryAddress = "0x9fE46736679d2D9a65F0992F2272dE9f3c7fa6e0",
        };
        var core = section.ToCoreConfig();
        Assert.Equal("http://x", core.RpcUrl);
        Assert.Equal(31337, core.ChainId);
        Assert.Equal("0x5FbDB2315678afecb367f032d93F642f64180aa3", core.FileRegistryAddress.ChecksumHex);
    }
}
