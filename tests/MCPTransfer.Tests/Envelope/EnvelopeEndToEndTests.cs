using System.Security.Cryptography;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Envelope;
using MCPTransfer.Core.Ipfs;

namespace MCPTransfer.Tests.Envelope;

public class EnvelopeEndToEndTests
{
    private const int TestChunkSize = 256;

    private static async Task<(byte[] recovered, EnvelopeReadResult result, string manifestCid)>
        RoundTripAsync(
            byte[] plaintext,
            AgentIdentity? aliceOverride = null,
            AgentIdentity? bobOverride = null,
            int chunkSize = TestChunkSize,
            string? filename = "doc.bin",
            string? mimeType = "application/octet-stream",
            int writerParallelism = EnvelopeWriter.DefaultMaxParallelism,
            int readerParallelism = EnvelopeReader.DefaultMaxParallelism)
    {
        var alice = aliceOverride ?? AgentIdentity.Generate();
        var bob = bobOverride ?? AgentIdentity.Generate();
        var ipfs = new InMemoryIpfsClient();

        var writer = new EnvelopeWriter(ipfs, writerParallelism);
        var reader = new EnvelopeReader(ipfs, readerParallelism);

        using var input = new MemoryStream(plaintext);
        var write = await writer.SendAsync(
            input, alice, bob.ToPublic(),
            filename: filename, mimeType: mimeType,
            chunkSize: chunkSize);

        using var output = new MemoryStream();
        var read = await reader.ReceiveAsync(write.ManifestCid, bob, output);

        return (output.ToArray(), read, write.ManifestCid);
    }

    [Theory]
    [InlineData(0)]                       // empty
    [InlineData(1)]                       // single byte
    [InlineData(TestChunkSize - 1)]       // just under
    [InlineData(TestChunkSize)]           // exactly one chunk
    [InlineData(TestChunkSize + 1)]       // one chunk + 1 byte
    [InlineData(TestChunkSize * 5)]       // 5 full chunks
    [InlineData(TestChunkSize * 5 + 17)]  // 5 full + tail
    [InlineData(64 * 1024)]               // larger
    public async Task RoundTrip_RecoversIdenticalPlaintext(int size)
    {
        var plaintext = RandomNumberGenerator.GetBytes(size);
        var (recovered, _, _) = await RoundTripAsync(plaintext);
        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public async Task RoundTrip_PopulatesManifestFields()
    {
        var plaintext = RandomNumberGenerator.GetBytes(2 * TestChunkSize + 5);
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();

        var (_, read, manifestCid) = await RoundTripAsync(
            plaintext, alice, bob,
            filename: "report.pdf", mimeType: "application/pdf");

        Assert.Equal(alice.Address, read.Manifest.Sender);
        Assert.Equal(bob.Address, read.Manifest.Recipient);
        Assert.Equal(HybridKem.SuiteIdentifier, read.Manifest.Suite);
        Assert.Equal("report.pdf", read.Manifest.Filename);
        Assert.Equal("application/pdf", read.Manifest.MimeType);
        Assert.Equal(plaintext.Length, read.Manifest.TotalSize);
        Assert.Equal(3, read.Manifest.Chunks.Count); // 2 full + tail
        Assert.True(read.PlaintextBytesWritten == plaintext.Length);
        Assert.StartsWith("mem:", manifestCid);
    }

    [Theory]
    [InlineData(1, 1)]    // strictly sequential
    [InlineData(2, 2)]
    [InlineData(8, 8)]    // more workers than chunks (here: 4 chunks)
    [InlineData(1, 8)]    // asymmetric: serial encode, parallel fetch
    [InlineData(8, 1)]    // asymmetric: parallel upload, serial decode
    public async Task RoundTrip_PreservesPlaintextAcrossParallelismSettings(
        int writerParallelism, int readerParallelism)
    {
        // 4 full chunks + a tail, to actually exercise the parallel path.
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize * 4 + 11);
        var (recovered, _, _) = await RoundTripAsync(
            plaintext,
            writerParallelism: writerParallelism,
            readerParallelism: readerParallelism);
        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void EnvelopeWriter_RejectsNonPositiveMaxParallelism()
    {
        var ipfs = new InMemoryIpfsClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnvelopeWriter(ipfs, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnvelopeWriter(ipfs, -1));
    }

    [Fact]
    public void EnvelopeReader_RejectsNonPositiveMaxParallelism()
    {
        var ipfs = new InMemoryIpfsClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnvelopeReader(ipfs, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnvelopeReader(ipfs, -1));
    }

    [Fact]
    public async Task ReceiveToFileAsync_HappyPath_WritesFinalFileNoTemp()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var ipfs = new InMemoryIpfsClient();
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize * 2 + 7);

        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(plaintext), alice, bob.ToPublic(),
            chunkSize: TestChunkSize);

        var tempDir = Path.Combine(Path.GetTempPath(), "mcptx-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "out.bin");

        try
        {
            var result = await new EnvelopeReader(ipfs).ReceiveToFileAsync(
                write.ManifestCid, bob, outputPath);

            Assert.True(File.Exists(outputPath));
            Assert.False(File.Exists(outputPath + ".mcptx-tmp"));
            Assert.Equal(plaintext, await File.ReadAllBytesAsync(outputPath));
            Assert.Equal(plaintext.Length, result.PlaintextBytesWritten);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReceiveToFileAsync_OnTagMismatch_DoesNotCreateFinalFileAndCleansTemp()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var ipfs = new MutatingIpfsClient();

        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(RandomNumberGenerator.GetBytes(TestChunkSize * 3)),
            alice, bob.ToPublic(),
            chunkSize: TestChunkSize);

        // Corrupt the LAST chunk so we have a partial-write window:
        // decryption will get through chunks 0..N-2 OK, then fail on chunk N-1.
        var lastChunk = write.SignedManifest.Manifest.Chunks[^1];
        ipfs.Mutate(lastChunk.Cid, bytes => bytes[0] ^= 0xFF);

        var tempDir = Path.Combine(Path.GetTempPath(), "mcptx-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "out.bin");

        try
        {
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
                () => new EnvelopeReader(ipfs).ReceiveToFileAsync(
                    write.ManifestCid, bob, outputPath));

            Assert.False(File.Exists(outputPath), "final file must not exist on failure");
            Assert.False(File.Exists(outputPath + ".mcptx-tmp"), "temp file must be cleaned up");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReceiveToFileAsync_PreservesExistingFileOnFailure()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var ipfs = new MutatingIpfsClient();

        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(RandomNumberGenerator.GetBytes(TestChunkSize)),
            alice, bob.ToPublic(),
            chunkSize: TestChunkSize);

        // Tamper so the receive will fail.
        var firstChunk = write.SignedManifest.Manifest.Chunks[0];
        ipfs.Mutate(firstChunk.Cid, bytes => bytes[0] ^= 0xFF);

        var tempDir = Path.Combine(Path.GetTempPath(), "mcptx-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "out.bin");
        var preExistingContent = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(outputPath, preExistingContent);

        try
        {
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
                () => new EnvelopeReader(ipfs).ReceiveToFileAsync(
                    write.ManifestCid, bob, outputPath));

            // Pre-existing file must be untouched (atomic semantics).
            Assert.True(File.Exists(outputPath));
            Assert.Equal(preExistingContent, await File.ReadAllBytesAsync(outputPath));
            Assert.False(File.Exists(outputPath + ".mcptx-tmp"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnvelopeWriter_DrainsInFlightUploads_OnPermanentFailure()
    {
        // Use a controlled IPFS that fails one specific pin permanently.
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var ipfs = new FailingIpfsClient(failOnNthPin: 3);

        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize * 10);

        // Once the pin failure surfaces, SendAsync should drain (cancel + await
        // pending tasks) rather than leaving them running. We can't directly
        // observe cancellation, but we CAN observe that PinAsync stops being
        // called past the failure (no leaked tasks scheduled new pins).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EnvelopeWriter(ipfs, maxParallelism: 2).SendAsync(
                new MemoryStream(plaintext), alice, bob.ToPublic(),
                chunkSize: TestChunkSize));

        // Give any (incorrectly) leaked tasks a moment to do extra calls — none should.
        var pinCountAfterFailure = ipfs.PinCallCount;
        await Task.Delay(50);
        Assert.Equal(pinCountAfterFailure, ipfs.PinCallCount);
    }

    [Fact]
    public async Task EnvelopeWriter_RejectsSeekableInputAboveCap()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var ipfs = new InMemoryIpfsClient();

        // Override the per-transfer cap to a tiny value so we can exercise
        // the upfront seekable-stream check without actually allocating GiBs.
        var writer = new EnvelopeWriter(ipfs) { MaxPlaintextBytes = 64 };

        // Seekable stream of 200 bytes, cap is 64 bytes.
        using var input = new MemoryStream(new byte[200]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.SendAsync(input, alice, bob.ToPublic(), chunkSize: TestChunkSize));
    }

    [Fact]
    public async Task EnvelopeWriter_RejectsNonSeekableInputAboveCap()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var ipfs = new InMemoryIpfsClient();

        var writer = new EnvelopeWriter(ipfs) { MaxPlaintextBytes = 64 };

        // Wrap a stream so CanSeek == false: the upfront check is skipped
        // and the mid-stream safety net must catch the overflow.
        using var inner = new MemoryStream(new byte[200]);
        using var nonSeekable = new NonSeekableStreamWrapper(inner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.SendAsync(nonSeekable, alice, bob.ToPublic(), chunkSize: TestChunkSize));
    }

    [Fact]
    public async Task ChunkedAead_RejectsOversizeChunkSize()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in ChunkedAead.EncryptAsync(
                new MemoryStream(new byte[10]),
                key: new byte[ChunkedAead.KeyByteLength],
                noncePrefix: new byte[ChunkedAead.NoncePrefixByteLength],
                chunkSize: ChunkedAead.MaxChunkSize + 1))
            {
                // never reached
            }
        });
    }

    /// <summary>
    /// In-memory IPFS that fails permanently on the N-th pin call (1-indexed).
    /// Used to exercise the drain pattern in EnvelopeWriter.
    /// </summary>
    private sealed class FailingIpfsClient : IIpfsClient
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        private readonly int _failOnNthPin;
        private int _pinCount;
        public int PinCallCount => _pinCount;

        public FailingIpfsClient(int failOnNthPin) { _failOnNthPin = failOnNthPin; }

        public Task<string> PinAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _pinCount);
            if (n == _failOnNthPin)
                throw new InvalidOperationException($"permanent failure on pin #{n}");

            ct.ThrowIfCancellationRequested();
            var bytes = data.ToArray();
            var cid = InMemoryIpfsClient.ComputeCid(bytes);
            lock (_store) { _store[cid] = bytes; }
            return Task.FromResult(cid);
        }

        public Task<byte[]> FetchAsync(string cid, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_store)
            {
                if (_store.TryGetValue(cid, out var bytes))
                    return Task.FromResult((byte[])bytes.Clone());
            }
            throw new KeyNotFoundException(cid);
        }
    }

    private sealed class NonSeekableStreamWrapper : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStreamWrapper(Stream inner) { _inner = inner; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Reader_RejectsWrongRecipient()
    {
        var alice = AgentIdentity.Generate();
        var intended = AgentIdentity.Generate();
        var attacker = AgentIdentity.Generate();
        var ipfs = new InMemoryIpfsClient();

        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(new byte[100]), alice, intended.ToPublic(),
            chunkSize: TestChunkSize);

        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EnvelopeReader(ipfs).ReceiveAsync(write.ManifestCid, attacker, output));
    }

    [Fact]
    public async Task Reader_RejectsTamperedManifest()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var ipfs = new InMemoryIpfsClient();

        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(new byte[100]), alice, bob.ToPublic(),
            filename: "doc.pdf", chunkSize: TestChunkSize);

        // Re-pin a tampered manifest blob under a different CID.
        var originalBytes = await ipfs.FetchAsync(write.ManifestCid);
        var asString = System.Text.Encoding.UTF8.GetString(originalBytes);
        var tamperedString = asString.Replace("doc.pdf", "evil.pdf", StringComparison.Ordinal);
        var tamperedBytes = System.Text.Encoding.UTF8.GetBytes(tamperedString);
        var tamperedCid = await ipfs.PinAsync(tamperedBytes);

        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EnvelopeReader(ipfs).ReceiveAsync(tamperedCid, bob, output));
    }

    [Fact]
    public async Task Reader_RejectsTamperedChunkCiphertext()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var ipfs = new MutatingIpfsClient();

        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(RandomNumberGenerator.GetBytes(2 * TestChunkSize)),
            alice, bob.ToPublic(),
            chunkSize: TestChunkSize);

        // Find the CID of the first chunk and corrupt its bytes.
        var firstChunkCid = write.SignedManifest.Manifest.Chunks[0].Cid;
        ipfs.Mutate(firstChunkCid, bytes => bytes[0] ^= 0xFF);

        using var output = new MemoryStream();
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => new EnvelopeReader(ipfs).ReceiveAsync(write.ManifestCid, bob, output));
    }

    [Fact]
    public async Task Reader_RejectsChunkWithWrongLength()
    {
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var ipfs = new MutatingIpfsClient();

        var write = await new EnvelopeWriter(ipfs).SendAsync(
            new MemoryStream(RandomNumberGenerator.GetBytes(2 * TestChunkSize)),
            alice, bob.ToPublic(),
            chunkSize: TestChunkSize);

        // Truncate the first chunk's stored bytes.
        var firstChunkCid = write.SignedManifest.Manifest.Chunks[0].Cid;
        ipfs.Replace(firstChunkCid, bytes => bytes.Take(bytes.Length - 1).ToArray());

        using var output = new MemoryStream();
        await Assert.ThrowsAsync<CryptographicException>(
            () => new EnvelopeReader(ipfs).ReceiveAsync(write.ManifestCid, bob, output));
    }

    [Fact]
    public async Task Reader_RejectsUnsupportedSuite()
    {
        // We don't have multiple suites yet, so we forge a signed manifest with
        // an unknown suite and sign it with the sender identity.
        var alice = AgentIdentity.Generate();
        var bob = AgentIdentity.Generate();
        var ipfs = new InMemoryIpfsClient();

        var ephPk = new byte[Secp256k1KeyPair.PublicKeyCompressedByteLength];
        var kemCt = new byte[MlKemPublicKey.CiphertextByteLength];
        var noncePrefix = new byte[ChunkedAead.NoncePrefixByteLength];
        var tag = new byte[ChunkedAead.TagByteLength];
        RandomNumberGenerator.Fill(ephPk);
        RandomNumberGenerator.Fill(kemCt);
        RandomNumberGenerator.Fill(noncePrefix);
        RandomNumberGenerator.Fill(tag);

        var manifest = new Manifest(
            version: 1,
            suite: "Some-Other-Suite",
            sender: alice.Address,
            recipient: bob.Address,
            ephemeralSecp256k1PublicKey: ephPk,
            kemCiphertext: kemCt,
            noncePrefix: noncePrefix,
            chunkSize: TestChunkSize,
            totalSize: 0,
            createdAtUnixSeconds: 1_700_000_000,
            chunks: new[] { new ManifestChunkEntry(0, "mem:dummy", tag, 0) });

        var signed = SignedManifest.Create(manifest, alice);
        var manifestCid = await ipfs.PinAsync(signed.ToCanonicalJsonBytes());

        using var output = new MemoryStream();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EnvelopeReader(ipfs).ReceiveAsync(manifestCid, bob, output));
        Assert.Contains("suite", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- helper: a mutable IIpfsClient that lets tests poke stored chunks ---

    private sealed class MutatingIpfsClient : IIpfsClient
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

        public Task<string> PinAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            var bytes = data.ToArray();
            var cid = InMemoryIpfsClient.ComputeCid(bytes);
            _store[cid] = bytes;
            return Task.FromResult(cid);
        }

        public Task<byte[]> FetchAsync(string cid, CancellationToken cancellationToken = default)
        {
            if (_store.TryGetValue(cid, out var bytes))
                return Task.FromResult((byte[])bytes.Clone());
            throw new KeyNotFoundException(cid);
        }

        public void Mutate(string cid, Action<byte[]> mutate)
        {
            mutate(_store[cid]);
        }

        public void Replace(string cid, Func<byte[], byte[]> transform)
        {
            _store[cid] = transform(_store[cid]);
        }
    }
}
