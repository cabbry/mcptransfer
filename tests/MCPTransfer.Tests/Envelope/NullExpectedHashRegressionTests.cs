using System.Security.Cryptography;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Envelope;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Tests.Envelope;

/// <summary>
/// Regression for a live-Amoy finding: with the expected-content-hash
/// parameter typed as <c>ReadOnlyMemory&lt;byte&gt;?</c>, passing a NULL
/// <c>byte[]</c> at a call site silently converted into an EMPTY memory
/// with <c>HasValue == true</c> — turning "no hash to check" into "check
/// against an empty hash", which refused every receive whenever on-chain
/// corroboration was unavailable. The parameter is now <c>byte[]?</c>;
/// this test pins the null path.
/// </summary>
public class NullExpectedHashRegressionTests
{
    [Fact]
    public async Task ReceiveToFileAsync_ExplicitNullByteArrayHash_DecryptsNormally()
    {
        var ipfs = new InMemoryIpfsClient();
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var plaintext = RandomNumberGenerator.GetBytes(64 * 1024);

        EnvelopeWriteResult write;
        using (var input = new MemoryStream(plaintext, writable: false))
        {
            write = await new EnvelopeWriter(ipfs).SendAsync(input, alice, bob.ToPublic());
        }

        var outPath = Path.Combine(
            Path.GetTempPath(), "mcptx-nullhash-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            // The exact shape of the buggy call site: a byte[]? that is null.
            byte[]? expectedHash = null;
            var result = await new EnvelopeReader(ipfs)
                .ReceiveToFileAsync(write.ManifestCid, bob, outPath, expectedHash);

            Assert.Equal(plaintext, await File.ReadAllBytesAsync(outPath));
            Assert.Equal(plaintext.Length, result.PlaintextBytesWritten);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
