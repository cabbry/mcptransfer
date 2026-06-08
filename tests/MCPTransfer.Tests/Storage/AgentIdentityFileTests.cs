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

    [Fact]
    public void Deserialize_MissingField_GivesFriendlyError_NotKeyNotFound()
    {
        // version is current (2) but the mldsa field is absent: must yield a
        // descriptive InvalidOperationException, not a raw KeyNotFoundException.
        var json = """
            {
                "version": 2,
                "secp256k1_private_key": "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80",
                "mlkem_private_key": "AA=="
            }
            """;
        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentIdentityFile.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
        Assert.Contains("mldsa_private_key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTempFileOnSuccess()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcptx-id-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(tempDir, "identity.json");

        try
        {
            await AgentIdentityFile.SaveAsync(AgentIdentity.Generate(), path);

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"),
                "atomic write must clean up the .tmp file after the rename");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_PreservesPreviousFileOnFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcptx-id-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(tempDir, "identity.json");

        try
        {
            var first = AgentIdentity.Generate();
            await AgentIdentityFile.SaveAsync(first, path);

            // Force the next save to fail by making the destination an open
            // directory rather than a file. File.Move will throw and the
            // catch in SaveAsync should clean up .tmp without touching the
            // original identity file... actually that's not quite the right
            // shape; instead, simulate cancellation mid-write.
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // pre-cancelled

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => AgentIdentityFile.SaveAsync(AgentIdentity.Generate(), path, cts.Token));

            // Original file is still present and readable.
            var reloaded = await AgentIdentityFile.LoadAsync(path);
            Assert.Equal(first.Address, reloaded.Address);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_OnUnix_FileIsUserReadWriteOnly()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            return; // POSIX mode check is meaningless on Windows.
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "mcptx-id-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(tempDir, "identity.json");

        try
        {
            await AgentIdentityFile.SaveAsync(AgentIdentity.Generate(), path);

            var mode = File.GetUnixFileMode(path);
            // 0600 = UserRead | UserWrite, nothing else.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
