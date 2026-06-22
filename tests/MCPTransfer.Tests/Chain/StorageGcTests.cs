using System.Security.Cryptography;
using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Envelope;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Tests.Chain;

public class StorageGcTests
{
    // --- fixture: a real signed transfer pinned into an in-memory store ---

    /// <summary>Pin <paramref name="chunkCount"/> random chunks plus a signed
    /// manifest that references them; return the manifest CID and chunk CIDs.</summary>
    private static async Task<(string ManifestCid, List<string> ChunkCids, AgentIdentity Sender, AgentIdentity Recipient)>
        PinTransferAsync(InMemoryIpfsClient ipfs, int chunkCount, long createdAtUnix = 1_700_000_000)
    {
        var sender = AgentIdentity.Generate();
        var recipient = AgentIdentity.Generate();

        var chunkCids = new List<string>();
        var entries = new List<ManifestChunkEntry>();
        for (var i = 0; i < chunkCount; i++)
        {
            var cid = await ipfs.PinAsync(RandomNumberGenerator.GetBytes(256));
            chunkCids.Add(cid);
            var tag = new byte[ChunkedAead.TagByteLength];
            RandomNumberGenerator.Fill(tag);
            entries.Add(new ManifestChunkEntry(i, cid, tag, 256));
        }

        var ephPk = new byte[Secp256k1KeyPair.PublicKeyCompressedByteLength];
        var kemCt = new byte[MlKemPublicKey.CiphertextByteLength];
        var noncePrefix = new byte[ChunkedAead.NoncePrefixByteLength];
        RandomNumberGenerator.Fill(ephPk);
        RandomNumberGenerator.Fill(kemCt);
        RandomNumberGenerator.Fill(noncePrefix);

        var manifest = new Manifest(
            version: Manifest.CurrentVersion,
            suite: HybridKem.SuiteIdentifier,
            sender: sender.Address,
            recipient: recipient.Address,
            ephemeralSecp256k1PublicKey: ephPk,
            kemCiphertext: kemCt,
            noncePrefix: noncePrefix,
            chunkSize: 16 * 1024 * 1024,
            totalSize: 256 * chunkCount,
            createdAtUnixSeconds: createdAtUnix,
            chunks: entries);

        var signed = SignedManifest.Create(manifest, sender);
        var manifestCid = await ipfs.PinAsync(signed.ToCanonicalJsonBytes());
        return (manifestCid, chunkCids, sender, recipient);
    }

    [Fact]
    public async Task PlanByCids_ResolvesManifestToItsChunkCids()
    {
        var ipfs = new InMemoryIpfsClient();
        var (manifestCid, chunkCids, _, recipient) = await PinTransferAsync(ipfs, chunkCount: 3);

        var plan = await StorageGc.PlanByCidsAsync(ipfs, new[] { manifestCid });

        var target = Assert.Single(plan);
        Assert.True(target.ManifestResolved);
        Assert.Equal(manifestCid, target.ManifestCid);
        Assert.Equal(recipient.Address, target.Recipient);
        Assert.Equal(chunkCids, target.ChunkCids);
    }

    [Fact]
    public async Task Unpin_ReleasesEveryChunkAndTheManifest()
    {
        var ipfs = new InMemoryIpfsClient();
        var (manifestCid, chunkCids, _, _) = await PinTransferAsync(ipfs, chunkCount: 4);
        var plan = await StorageGc.PlanByCidsAsync(ipfs, new[] { manifestCid });

        var result = await StorageGc.UnpinAsync(ipfs, plan);

        Assert.Equal(1, result.TransfersProcessed);
        Assert.Equal(chunkCids.Count + 1, result.CidsUnpinned); // chunks + manifest
        Assert.Empty(result.Errors);
        Assert.Equal(0, result.CidsSkipped);
        Assert.Equal(0, ipfs.Count);
        Assert.False(ipfs.Contains(manifestCid));
        foreach (var c in chunkCids)
            Assert.False(ipfs.Contains(c));
    }

    [Fact]
    public async Task Unpin_SkipsProtectedCid_SoARegisteredKeyBlobIsNeverCollected()
    {
        var ipfs = new InMemoryIpfsClient();
        var (manifestCid, chunkCids, _, _) = await PinTransferAsync(ipfs, chunkCount: 2);

        // Simulate the agent's registered ML-KEM key blob colliding with a CID
        // in the plan: it must be preserved, not unpinned.
        var protectedCid = chunkCids[0];
        var plan = await StorageGc.PlanByCidsAsync(ipfs, new[] { manifestCid });

        var result = await StorageGc.UnpinAsync(
            ipfs, plan, new HashSet<string>(StringComparer.Ordinal) { protectedCid });

        Assert.Equal(1, result.CidsSkipped);
        Assert.True(ipfs.Contains(protectedCid));        // protected → kept
        Assert.False(ipfs.Contains(chunkCids[1]));        // others → released
        Assert.False(ipfs.Contains(manifestCid));
    }

    [Fact]
    public async Task Unpin_IsBestEffort_RecordsFailuresAndContinues()
    {
        var inner = new InMemoryIpfsClient();
        var (manifestCid, chunkCids, _, _) = await PinTransferAsync(inner, chunkCount: 3);
        var plan = await StorageGc.PlanByCidsAsync(inner, new[] { manifestCid });

        // One chunk's unpin throws; the rest must still be released.
        var flaky = new FailingUnpinClient(inner, failOnCid: chunkCids[1]);
        var result = await StorageGc.UnpinAsync(flaky, plan);

        Assert.Single(result.Errors);
        Assert.Contains(chunkCids[1], result.Errors[0]);
        Assert.Equal(chunkCids.Count + 1 - 1, result.CidsUnpinned); // all but the failing one
        Assert.True(inner.Contains(chunkCids[1]));  // the failed one survives
        Assert.False(inner.Contains(manifestCid));  // a later CID still got released
    }

    [Fact]
    public async Task PlanByAge_OnlyTargetsTransfersOlderThanCutoff()
    {
        var ipfs = new InMemoryIpfsClient();
        var me = AgentIdentity.Generate().Address;

        var old = await PinTransferAsync(ipfs, chunkCount: 2);
        var recent = await PinTransferAsync(ipfs, chunkCount: 2);

        var oldTs = DateTimeOffset.FromUnixTimeSeconds(1_600_000_000); // older
        var recentTs = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000); // newer
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var recipient = AgentIdentity.Generate().Address;

        var registry = new ScriptedSentRegistry(new[]
        {
            Sent(me, old.ManifestCid, oldTs, 10, to: recipient),
            Sent(me, recent.ManifestCid, recentTs, 20),
        });

        var plan = await StorageGc.PlanByAgeAsync(registry, ipfs, me, cutoff, fromBlock: 0, toBlock: 100);

        var target = Assert.Single(plan);
        Assert.Equal(old.ManifestCid, target.ManifestCid);
        Assert.Equal(old.ChunkCids, target.ChunkCids);
        Assert.Equal(recipient, target.Recipient); // by-age recipient comes from the FileSent `to`
    }

    [Fact]
    public async Task PlanByCids_TamperedManifest_IsNotTrusted()
    {
        var ipfs = new InMemoryIpfsClient();
        var (manifestCid, _, _, _) = await PinTransferAsync(ipfs, chunkCount: 2);

        // Pin a byte-flipped copy of the manifest under its own CID. Whether the
        // tamper breaks JSON parsing or just the signature, gc must NOT trust its
        // chunk list (defends against a gateway returning altered bytes).
        var good = await ipfs.FetchAsync(manifestCid);
        var tampered = (byte[])good.Clone();
        tampered[good.Length / 2] ^= 0xFF;
        var tamperedCid = await ipfs.PinAsync(tampered);

        var plan = await StorageGc.PlanByCidsAsync(ipfs, new[] { tamperedCid });

        var target = Assert.Single(plan);
        Assert.False(target.ManifestResolved);
        Assert.Empty(target.ChunkCids);
        Assert.Equal(tamperedCid, target.ManifestCid); // its own pin is still releasable
    }

    [Fact]
    public async Task PlanByAge_UnresolvableManifest_StillPlansTheManifestPin()
    {
        var ipfs = new InMemoryIpfsClient();
        var me = AgentIdentity.Generate().Address;

        // An event pointing at a CID that was never pinned (or already gone).
        var ghostCid = "mem:" + new string('a', 64);
        var registry = new ScriptedSentRegistry(new[]
        {
            Sent(me, ghostCid, DateTimeOffset.FromUnixTimeSeconds(1_600_000_000), 5),
        });

        var plan = await StorageGc.PlanByAgeAsync(
            registry, ipfs, me, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), 0, 100);

        var target = Assert.Single(plan);
        Assert.False(target.ManifestResolved);
        Assert.Empty(target.ChunkCids);
        Assert.Equal(ghostCid, target.ManifestCid);

        // Unpinning still attempts the manifest pin (idempotent no-op here).
        var result = await StorageGc.UnpinAsync(ipfs, plan);
        Assert.Equal(1, result.CidsUnpinned);
    }

    [Fact]
    public async Task UnpinAsync_RejectsNullArguments()
    {
        var ipfs = new InMemoryIpfsClient();
        await Assert.ThrowsAsync<ArgumentNullException>(() => StorageGc.UnpinAsync(null!, Array.Empty<StorageGc.Target>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => StorageGc.UnpinAsync(ipfs, null!));
    }

    // --- helpers ---

    private static FileSentEvent Sent(
        EthereumAddress from, string cid, DateTimeOffset ts, ulong block, EthereumAddress? to = null)
        => new(From: from, To: to ?? AgentIdentity.Generate().Address, Cid: cid,
               ContentHash: new byte[32], Timestamp: ts, TransactionHash: "0x", BlockNumber: block, LogIndex: 0);

    /// <summary>IFileRegistryClient that serves a fixed sent-event set from
    /// GetSentAsync; everything else is unsupported (not exercised here).</summary>
    private sealed class ScriptedSentRegistry : IFileRegistryClient
    {
        private readonly FileSentEvent[] _events;
        public ScriptedSentRegistry(FileSentEvent[] events) => _events = events;

        public Task<IReadOnlyList<FileSentEvent>> GetSentAsync(
            EthereumAddress me, ulong fromBlock, ulong toBlock, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FileSentEvent>>(
                _events.Where(e => e.BlockNumber >= fromBlock && e.BlockNumber <= toBlock).ToList());

        public Task<IReadOnlyList<FileSentEvent>> GetInboxAsync(EthereumAddress m, ulong f, ulong t, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> SendAsync(EthereumAddress r, string c, byte[] h, Secp256k1KeyPair s, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<FileSentEvent> WatchInboxAsync(EthereumAddress m, ulong f, TimeSpan p, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ulong> GetLatestBlockNumberAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileSentEvent?> FindByCidAsync(EthereumAddress m, string c, ulong f, ulong t, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Decorator whose UnpinAsync throws for one designated CID, to
    /// exercise the best-effort accounting.</summary>
    private sealed class FailingUnpinClient : IIpfsClient
    {
        private readonly IIpfsClient _inner;
        private readonly string _failOnCid;
        public FailingUnpinClient(IIpfsClient inner, string failOnCid) { _inner = inner; _failOnCid = failOnCid; }

        public Task<string> PinAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => _inner.PinAsync(data, ct);
        public Task<byte[]> FetchAsync(string cid, CancellationToken ct = default) => _inner.FetchAsync(cid, ct);
        public Task UnpinAsync(string cid, CancellationToken ct = default)
            => string.Equals(cid, _failOnCid, StringComparison.Ordinal)
                ? throw new HttpRequestException($"simulated unpin failure for {cid}")
                : _inner.UnpinAsync(cid, ct);
    }
}
