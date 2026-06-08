using System.Security.Cryptography;
using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Envelope;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Tests.Integration;

/// <summary>
/// Live round-trip tests against a local anvil + freshly-deployed contracts.
/// Gated behind <see cref="AnvilFixture.GateEnvVar"/> (see the fixture); each
/// test returns early when the fixture is disabled.
/// </summary>
/// <remarks>
/// Tests in the "Anvil" collection run sequentially against ONE shared chain,
/// so each uses its own dev account(s) (an AgentDirectory handle claim is
/// permanent per address). The full-envelope test exercises the whole pipeline
/// end to end, including the I2 on-chain content-hash corroboration tie.
/// </remarks>
[Collection(AnvilCollection.Name)]
public sealed class AnvilIntegrationTests
{
    private readonly AnvilFixture _anvil;

    public AnvilIntegrationTests(AnvilFixture anvil) => _anvil = anvil;

    private EthereumChainClient Chain => new(_anvil.Config);

    [Fact]
    public async Task KeyRegistry_PublishThenGet_RoundTripsBothKeys()
    {
        if (!_anvil.Enabled) return;
        var chain = Chain;
        var agent = AnvilFixture.Identity(1);

        await chain.KeyRegistry.PublishAsync(
            agent.Secp256k1.PublicKeyCompressed.ToArray(),
            agent.MlKem.PublicKey.Bytes.ToArray(),
            agent.Secp256k1);

        var got = await chain.KeyRegistry.GetAsync(agent.Address);

        Assert.True(got.IsRegistered);
        Assert.Equal(agent.Secp256k1.PublicKeyCompressed.ToArray(), got.Secp256k1Compressed);
        Assert.Equal(agent.MlKem.PublicKey.Bytes.ToArray(), got.MlKem);
    }

    [Fact]
    public async Task AgentDirectory_Claim_ResolvesBothDirections()
    {
        if (!_anvil.Enabled) return;
        var chain = Chain;
        var agent = AnvilFixture.Identity(2);
        const string handle = "agentdir-a";

        await chain.AgentDirectory.ClaimAsync(handle, agent.Secp256k1);

        var resolved = await chain.AgentDirectory.ResolveAsync(handle);
        var reverse = await chain.AgentDirectory.ReverseResolveAsync(agent.Address);

        Assert.NotNull(resolved);
        Assert.Equal(agent.Address.LowerHex, resolved!.LowerHex);
        Assert.Equal(handle, reverse);
        Assert.Null(await chain.AgentDirectory.ResolveAsync("never-claimed-handle"));
    }

    [Fact]
    public async Task FileRegistry_Send_AppearsInInboxAndFindByCid()
    {
        if (!_anvil.Enabled) return;
        var chain = Chain;
        var sender = AnvilFixture.Identity(3);
        var recipient = AnvilFixture.Identity(4);
        const string cid = "QmIntegrationTestManifestCidExample0001";
        var contentHash = SHA256.HashData(new byte[] { 1, 2, 3, 4 }); // any 32-byte value

        await chain.FileRegistry.SendAsync(recipient.Address, cid, contentHash, sender.Secp256k1);

        var latest = await chain.FileRegistry.GetLatestBlockNumberAsync();
        var inbox = await chain.FileRegistry.GetInboxAsync(recipient.Address, 0, latest);
        Assert.Contains(inbox, e =>
            e.Cid == cid && e.From.LowerHex == sender.Address.LowerHex);

        var found = await chain.FileRegistry.FindByCidAsync(recipient.Address, cid, 0, latest);
        Assert.NotNull(found);
        Assert.Equal(sender.Address.LowerHex, found!.From.LowerHex);
        Assert.Equal(contentHash, found.ContentHash);
    }

    [Fact]
    public async Task FullEnvelope_E2E_ByteIdentical_DualSigned_AndCorroborated()
    {
        if (!_anvil.Enabled) return;
        var chain = Chain;
        var alice = AnvilFixture.Identity(5); // sender
        var bob = AnvilFixture.Identity(6);   // recipient

        // Recipient must be discoverable: publish keys + claim a handle.
        await chain.KeyRegistry.PublishAsync(
            bob.Secp256k1.PublicKeyCompressed.ToArray(), bob.MlKem.PublicKey.Bytes.ToArray(), bob.Secp256k1);
        await chain.AgentDirectory.ClaimAsync("bob-e2e", bob.Secp256k1);
        // Sender claims a handle so the recipient can reverse-resolve it.
        await chain.AgentDirectory.ClaimAsync("alice-e2e", alice.Secp256k1);

        var ipfsDir = Path.Combine(Path.GetTempPath(), "mcptx-anvil-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var aliceIpfs = new FileIpfsClient(ipfsDir);
            var bobIpfs = new FileIpfsClient(ipfsDir); // same store, separate client = two "processes"

            // Alice resolves Bob from the chain (AgentDirectory + KeyRegistry +
            // key/address consistency check), then encrypts a multi-chunk file.
            var recipient = await RecipientResolver.ResolveAsync(chain, "bob-e2e");

            var plaintext = RandomNumberGenerator.GetBytes(100 * 1024);
            EnvelopeWriteResult write;
            using (var input = new MemoryStream(plaintext, writable: false))
            {
                var writer = new EnvelopeWriter(aliceIpfs);
                write = await writer.SendAsync(
                    input, alice, recipient.PublicIdentity,
                    filename: "hello.bin", mimeType: "application/octet-stream",
                    chunkSize: 32 * 1024); // 100 KiB / 32 KiB => 4 chunks
            }

            Assert.True(write.SignedManifest.Manifest.Chunks.Count > 1, "expected a multi-chunk transfer");

            // Hybrid signature is well-formed and verifies.
            Assert.True(write.SignedManifest.VerifySignature());
            Assert.Equal(Secp256k1KeyPair.SignatureByteLength, write.SignedManifest.Signature.Length);
            Assert.Equal(MlDsaKeyPair.SignatureByteLength, write.SignedManifest.MlDsaSignature.Length);
            Assert.Equal(MlDsaKeyPair.PublicKeyByteLength, write.SignedManifest.SenderMlDsaPublicKey.Length);

            // Alice announces the transfer on chain.
            var contentHash = write.SignedManifest.ContentHash();
            await chain.FileRegistry.SendAsync(bob.Address, write.ManifestCid, contentHash, alice.Secp256k1);

            // Bob finds the announcement and corroborates the sender.
            var latest = await chain.FileRegistry.GetLatestBlockNumberAsync();
            var ev = await chain.FileRegistry.FindByCidAsync(bob.Address, write.ManifestCid, 0, latest);
            Assert.NotNull(ev);
            Assert.Equal(contentHash, ev!.ContentHash);
            Assert.Equal(alice.Address.LowerHex, ev.From.LowerHex);

            var senderHandle = await chain.AgentDirectory.ReverseResolveAsync(ev.From);
            Assert.Equal("alice-e2e", senderHandle);

            // Bob receives, pinning the on-chain content hash (the I2 tie).
            var outPath = Path.Combine(ipfsDir, "received.bin");
            var reader = new EnvelopeReader(bobIpfs);
            var result = await reader.ReceiveToFileAsync(
                write.ManifestCid, bob, outPath, ev.ContentHash);

            var decrypted = await File.ReadAllBytesAsync(outPath);
            Assert.Equal(plaintext, decrypted); // byte-identical round trip
            Assert.Equal(alice.Address.LowerHex, result.Manifest.Sender.LowerHex);
            Assert.Equal("hello.bin", result.Manifest.Filename);
            Assert.Equal(plaintext.Length, result.PlaintextBytesWritten);
        }
        finally
        {
            if (Directory.Exists(ipfsDir))
                Directory.Delete(ipfsDir, recursive: true);
        }
    }
}
