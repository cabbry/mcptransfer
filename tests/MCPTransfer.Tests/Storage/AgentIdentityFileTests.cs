using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Storage;

namespace MCPTransfer.Tests.Storage;

public class AgentIdentityFileTests
{
    [Fact]
    public void Serialize_Then_Deserialize_PreservesIdentity()
    {
        var original = AgentIdentity.Generate();
        var bytes = AgentIdentityFile.Serialize(original);
        var restored = AgentIdentityFile.Deserialize(bytes);

        Assert.Equal(original.Address, restored.Address);
        Assert.True(original.Secp256k1.PrivateKey.SequenceEqual(restored.Secp256k1.PrivateKey));
        Assert.True(original.MlKem.PrivateKeyEncoded.SequenceEqual(restored.MlKem.PrivateKeyEncoded));
        Assert.True(original.MlKem.PublicKey.Bytes.SequenceEqual(restored.MlKem.PublicKey.Bytes));
    }

    [Fact]
    public async Task SaveAsync_Then_LoadAsync_RoundTripThroughDisk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcptx-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(tempDir, "identity.json");

        try
        {
            var original = AgentIdentity.Generate();
            await AgentIdentityFile.SaveAsync(original, path);
            Assert.True(File.Exists(path));

            var restored = await AgentIdentityFile.LoadAsync(path);
            Assert.Equal(original.Address, restored.Address);
            Assert.True(original.Secp256k1.PrivateKey.SequenceEqual(restored.Secp256k1.PrivateKey));
            Assert.True(original.MlKem.PrivateKeyEncoded.SequenceEqual(restored.MlKem.PrivateKeyEncoded));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Deserialize_RejectsUnsupportedVersion()
    {
        var json = """
            {
                "version": 999,
                "secp256k1_private_key": "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80",
                "mlkem_private_key": ""
            }
            """;
        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentIdentityFile.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultPath_PointsToUserProfile()
    {
        Assert.EndsWith(Path.Combine(".mcptx", "identity.json"), AgentIdentityFile.DefaultPath);
        Assert.True(Path.IsPathRooted(AgentIdentityFile.DefaultPath));
    }
}
