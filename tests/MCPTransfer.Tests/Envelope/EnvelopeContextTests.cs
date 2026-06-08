using System.Security.Cryptography;
using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Envelope;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Tests.Envelope;

/// <summary>
/// Tests for the HKDF context binding (v2-#1: ML-KEM pubkey is now part of
/// the derivation transcript) and the receive-side content-hash check
/// (v2-#2).
/// </summary>
public class EnvelopeHardeningTests
{
    private static async Task<(byte[] cid, byte[] contentHash, InMemoryIpfsClient ipfs)>
        SendAsync(AgentIdentity alice, AgentIdentity bob, byte[] payload)
    {
        var ipfs = new InMemoryIpfsClient();
        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(payload), alice, bob.ToPublic(),
            filename: "f.bin", chunkSize: 256);
        return (System.Text.Encoding.UTF8.GetBytes(write.ManifestCid), write.SignedManifest.ContentHash(), ipfs);
    }

    [Fact]
    public async Task RoundTrip_StillWorks_WithMlKemBoundContext()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var payload = RandomNumberGenerator.GetBytes(1000);

        var ipfs = new InMemoryIpfsClient();
        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(payload), alice, bob.ToPublic(), chunkSize: 256);

        using var output = new MemoryStream();
        await new EnvelopeReader(ipfs).ReceiveAsync(write.ManifestCid, bob, output);
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task Receive_WithCorrectExpectedHash_Succeeds()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var payload = RandomNumberGenerator.GetBytes(500);

        var ipfs = new InMemoryIpfsClient();
        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(payload), alice, bob.ToPublic(), chunkSize: 256);
        var expected = write.SignedManifest.ContentHash();

        using var output = new MemoryStream();
        var result = await new EnvelopeReader(ipfs).ReceiveAsync(
            write.ManifestCid, bob, output, expected);

        Assert.Equal(payload, output.ToArray());
        Assert.Equal(bob.Address, result.Manifest.Recipient);
    }

    [Fact]
    public async Task Receive_WithWrongExpectedHash_IsRefused()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var payload = RandomNumberGenerator.GetBytes(500);

        var ipfs = new InMemoryIpfsClient();
        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(payload), alice, bob.ToPublic(), chunkSize: 256);

        var wrong = RandomNumberGenerator.GetBytes(Hashes.Keccak256ByteLength);

        using var output = new MemoryStream();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EnvelopeReader(ipfs).ReceiveAsync(write.ManifestCid, bob, output, wrong));
        Assert.Contains("content hash", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Receive_WithWrongLengthExpectedHash_IsRefused()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var ipfs = new InMemoryIpfsClient();
        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(new byte[10]), alice, bob.ToPublic(), chunkSize: 256);

        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EnvelopeReader(ipfs).ReceiveAsync(write.ManifestCid, bob, output, new byte[16]));
    }

    [Fact]
    public void HkdfContext_RejectsWrongMlKemPubkeyLength()
    {
        var sender = AgentIdentity.Generate().Address;
        var recipient = AgentIdentity.Generate().Address;
        var noncePrefix = new byte[ChunkedAead.NoncePrefixByteLength];

        // BuildHkdfContext is internal — reachable via InternalsVisibleTo.
        Assert.Throws<ArgumentException>(() =>
            MCPTransfer.Core.Envelope.EnvelopeContext.BuildHkdfContext(
                sender, recipient, new byte[100], noncePrefix));
    }

    [Fact]
    public void HkdfContext_DiffersWhenMlKemPubkeyDiffers()
    {
        var sender = AgentIdentity.Generate().Address;
        var recipient = AgentIdentity.Generate().Address;
        var noncePrefix = RandomNumberGenerator.GetBytes(ChunkedAead.NoncePrefixByteLength);

        var pkA = AgentIdentity.Generate().MlKem.PublicKey.Bytes.ToArray();
        var pkB = AgentIdentity.Generate().MlKem.PublicKey.Bytes.ToArray();

        var ctxA = MCPTransfer.Core.Envelope.EnvelopeContext.BuildHkdfContext(sender, recipient, pkA, noncePrefix);
        var ctxB = MCPTransfer.Core.Envelope.EnvelopeContext.BuildHkdfContext(sender, recipient, pkB, noncePrefix);

        Assert.NotEqual(ctxA, ctxB);
        // Sanity: the ML-KEM pubkey bytes are actually embedded.
        Assert.Equal(
            MCPTransfer.Core.Crypto.EthereumAddress.ByteLength * 2
              + MlKemPublicKey.PublicKeyByteLength
              + ChunkedAead.NoncePrefixByteLength,
            ctxA.Length);
    }
}
