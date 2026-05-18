using System.Security.Cryptography;
using System.Text;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Envelope;

namespace MCPTransfer.Tests.Envelope;

public class ManifestTests
{
    private static Manifest BuildSample(int chunkCount = 3, bool withOptionals = true)
    {
        var sender = AgentIdentity.Generate().Address;
        var recipient = AgentIdentity.Generate().Address;

        var ephPk = new byte[Secp256k1KeyPair.PublicKeyCompressedByteLength];
        var kemCt = new byte[MlKemPublicKey.CiphertextByteLength];
        var noncePrefix = new byte[ChunkedAead.NoncePrefixByteLength];
        RandomNumberGenerator.Fill(ephPk);
        RandomNumberGenerator.Fill(kemCt);
        RandomNumberGenerator.Fill(noncePrefix);

        var chunks = new ManifestChunkEntry[chunkCount];
        long total = 0;
        const int FullChunkSize = 16 * 1024 * 1024;
        for (var i = 0; i < chunkCount; i++)
        {
            var tag = new byte[ChunkedAead.TagByteLength];
            RandomNumberGenerator.Fill(tag);
            var size = (i == chunkCount - 1) ? 7 : FullChunkSize;
            chunks[i] = new ManifestChunkEntry(
                index: i,
                cid: $"bafkreih{i:D2}-test-cid-placeholder",
                tag: tag,
                ciphertextSize: size);
            total += size;
        }

        return new Manifest(
            version: Manifest.CurrentVersion,
            suite: HybridKem.SuiteIdentifier,
            sender: sender,
            recipient: recipient,
            ephemeralSecp256k1PublicKey: ephPk,
            kemCiphertext: kemCt,
            noncePrefix: noncePrefix,
            chunkSize: FullChunkSize,
            totalSize: total,
            createdAtUnixSeconds: 1_700_000_000,
            chunks: chunks,
            filename: withOptionals ? "test.pdf" : null,
            mimeType: withOptionals ? "application/pdf" : null);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = BuildSample();
        var bytes = original.ToCanonicalJsonBytes();
        var parsed = Manifest.FromJsonBytes(bytes);

        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.Suite, parsed.Suite);
        Assert.Equal(original.Sender, parsed.Sender);
        Assert.Equal(original.Recipient, parsed.Recipient);
        Assert.Equal(original.EphemeralSecp256k1PublicKey, parsed.EphemeralSecp256k1PublicKey);
        Assert.Equal(original.KemCiphertext, parsed.KemCiphertext);
        Assert.Equal(original.NoncePrefix, parsed.NoncePrefix);
        Assert.Equal(original.ChunkSize, parsed.ChunkSize);
        Assert.Equal(original.TotalSize, parsed.TotalSize);
        Assert.Equal(original.CreatedAtUnixSeconds, parsed.CreatedAtUnixSeconds);
        Assert.Equal(original.Filename, parsed.Filename);
        Assert.Equal(original.MimeType, parsed.MimeType);
        Assert.Equal(original.Chunks.Count, parsed.Chunks.Count);
        for (var i = 0; i < original.Chunks.Count; i++)
        {
            Assert.Equal(original.Chunks[i].Index, parsed.Chunks[i].Index);
            Assert.Equal(original.Chunks[i].Cid, parsed.Chunks[i].Cid);
            Assert.Equal(original.Chunks[i].Tag, parsed.Chunks[i].Tag);
            Assert.Equal(original.Chunks[i].CiphertextSize, parsed.Chunks[i].CiphertextSize);
        }
    }

    [Fact]
    public void ToCanonicalJsonBytes_IsDeterministic()
    {
        var m = BuildSample();
        var first = m.ToCanonicalJsonBytes();
        var second = m.ToCanonicalJsonBytes();
        Assert.Equal(first, second);
    }

    [Fact]
    public void TwoIdenticalManifests_HaveIdenticalCanonicalBytes()
    {
        var ephPk = new byte[Secp256k1KeyPair.PublicKeyCompressedByteLength];
        var kemCt = new byte[MlKemPublicKey.CiphertextByteLength];
        var noncePrefix = new byte[ChunkedAead.NoncePrefixByteLength];
        var tag = new byte[ChunkedAead.TagByteLength];
        RandomNumberGenerator.Fill(ephPk);
        RandomNumberGenerator.Fill(kemCt);
        RandomNumberGenerator.Fill(noncePrefix);
        RandomNumberGenerator.Fill(tag);

        var sender = AgentIdentity.Generate().Address;
        var recipient = AgentIdentity.Generate().Address;
        var chunks = new[] { new ManifestChunkEntry(0, "bafytest", tag, 42) };

        var m1 = new Manifest(
            1, HybridKem.SuiteIdentifier, sender, recipient,
            ephPk, kemCt, noncePrefix, 16 * 1024 * 1024, 42, 1700000000, chunks);
        var m2 = new Manifest(
            1, HybridKem.SuiteIdentifier, sender, recipient,
            ephPk, kemCt, noncePrefix, 16 * 1024 * 1024, 42, 1700000000, chunks);

        Assert.Equal(m1.ToCanonicalJsonBytes(), m2.ToCanonicalJsonBytes());
        Assert.Equal(m1.ComputeContentHash(), m2.ComputeContentHash());
    }

    [Fact]
    public void CanonicalJson_OrdersTopLevelPropertiesAlphabetically()
    {
        var json = Encoding.UTF8.GetString(BuildSample().ToCanonicalJsonBytes());
        string[] expectedOrder =
        {
            "\"chunk_size\"",
            "\"chunks\"",
            "\"created_at\"",
            "\"ephemeral_secp256k1\"",
            "\"filename\"",
            "\"kem_ciphertext\"",
            "\"mime_type\"",
            "\"nonce_prefix\"",
            "\"recipient\"",
            "\"sender\"",
            "\"suite\"",
            "\"total_size\"",
            "\"version\"",
        };

        var lastIndex = -1;
        foreach (var key in expectedOrder)
        {
            var idx = json.IndexOf(key, StringComparison.Ordinal);
            Assert.True(idx >= 0, $"Key {key} missing from canonical JSON.");
            Assert.True(idx > lastIndex,
                $"Key {key} appears out of order (at {idx}, after prior at {lastIndex}).");
            lastIndex = idx;
        }
    }

    [Fact]
    public void CanonicalJson_OrdersChunkPropertiesAlphabetically()
    {
        var json = Encoding.UTF8.GetString(BuildSample().ToCanonicalJsonBytes());

        var chunksStart = json.IndexOf("\"chunks\":[", StringComparison.Ordinal);
        Assert.True(chunksStart >= 0);
        var firstChunkSlice = json.Substring(chunksStart);

        var posCid = firstChunkSlice.IndexOf("\"cid\"", StringComparison.Ordinal);
        var posIndex = firstChunkSlice.IndexOf("\"index\"", StringComparison.Ordinal);
        var posSize = firstChunkSlice.IndexOf("\"size\"", StringComparison.Ordinal);
        var posTag = firstChunkSlice.IndexOf("\"tag\"", StringComparison.Ordinal);

        Assert.True(posCid < posIndex);
        Assert.True(posIndex < posSize);
        Assert.True(posSize < posTag);
    }

    [Fact]
    public void OptionalFields_OmittedWhenNull()
    {
        var json = Encoding.UTF8.GetString(BuildSample(withOptionals: false).ToCanonicalJsonBytes());
        Assert.DoesNotContain("filename", json);
        Assert.DoesNotContain("mime_type", json);
    }

    [Fact]
    public void OptionalFields_IncludedWhenPresent()
    {
        var json = Encoding.UTF8.GetString(BuildSample(withOptionals: true).ToCanonicalJsonBytes());
        Assert.Contains("\"filename\":\"test.pdf\"", json);
        Assert.Contains("\"mime_type\":\"application/pdf\"", json);
    }

    [Fact]
    public void ContentHash_IsStableAcrossReSerializations()
    {
        var m = BuildSample();
        var h1 = m.ComputeContentHash();
        var h2 = m.ComputeContentHash();
        Assert.Equal(Hashes.Keccak256ByteLength, h1.Length);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void ContentHash_DiffersWhenAnyFieldChanges()
    {
        var first = BuildSample();
        var second = BuildSample();
        Assert.NotEqual(first.ComputeContentHash(), second.ComputeContentHash());
    }

    [Fact]
    public void Parser_IgnoresUnknownProperties()
    {
        var canonical = Encoding.UTF8.GetString(BuildSample().ToCanonicalJsonBytes());
        var withExtra = canonical.Insert(canonical.Length - 1, ",\"extra_future_field\":\"ignored\"");
        var parsed = Manifest.FromJsonBytes(Encoding.UTF8.GetBytes(withExtra));
        Assert.Equal(Manifest.CurrentVersion, parsed.Version);
    }

    [Fact]
    public void Constructor_RejectsWrongEphemeralKeyLength()
    {
        Assert.Throws<ArgumentException>(() => BuildWithEphemeralKeyLength(32));
        Assert.Throws<ArgumentException>(() => BuildWithEphemeralKeyLength(34));
    }

    [Fact]
    public void Constructor_RejectsWrongKemCiphertextLength()
    {
        Assert.Throws<ArgumentException>(() => BuildWithKemCiphertextLength(1087));
        Assert.Throws<ArgumentException>(() => BuildWithKemCiphertextLength(1089));
    }

    [Fact]
    public void Constructor_RejectsWrongNoncePrefixLength()
    {
        Assert.Throws<ArgumentException>(() => BuildWithNoncePrefixLength(7));
        Assert.Throws<ArgumentException>(() => BuildWithNoncePrefixLength(12));
    }

    [Fact]
    public void Constructor_RejectsEmptyChunks()
    {
        Assert.Throws<ArgumentException>(() => BuildWithChunks(Array.Empty<ManifestChunkEntry>(), totalSize: 0));
    }

    [Fact]
    public void Constructor_RejectsNonContiguousChunkIndices()
    {
        var tag = new byte[ChunkedAead.TagByteLength];
        var chunks = new[]
        {
            new ManifestChunkEntry(0, "cid0", tag, 10),
            new ManifestChunkEntry(2, "cid2", tag, 10), // gap
        };
        Assert.Throws<ArgumentException>(() => BuildWithChunks(chunks, totalSize: 20));
    }

    [Fact]
    public void Constructor_RejectsMismatchedTotalSize()
    {
        var tag = new byte[ChunkedAead.TagByteLength];
        var chunks = new[] { new ManifestChunkEntry(0, "cid0", tag, 10) };
        Assert.Throws<ArgumentException>(() => BuildWithChunks(chunks, totalSize: 999));
    }

    // --- helpers that mutate one field at a time ---

    private static Manifest BuildWithEphemeralKeyLength(int length)
        => new(
            1, HybridKem.SuiteIdentifier,
            AgentIdentity.Generate().Address, AgentIdentity.Generate().Address,
            new byte[length],
            new byte[MlKemPublicKey.CiphertextByteLength],
            new byte[ChunkedAead.NoncePrefixByteLength],
            16, 0, 1, OneChunk(0));

    private static Manifest BuildWithKemCiphertextLength(int length)
        => new(
            1, HybridKem.SuiteIdentifier,
            AgentIdentity.Generate().Address, AgentIdentity.Generate().Address,
            new byte[Secp256k1KeyPair.PublicKeyCompressedByteLength],
            new byte[length],
            new byte[ChunkedAead.NoncePrefixByteLength],
            16, 0, 1, OneChunk(0));

    private static Manifest BuildWithNoncePrefixLength(int length)
        => new(
            1, HybridKem.SuiteIdentifier,
            AgentIdentity.Generate().Address, AgentIdentity.Generate().Address,
            new byte[Secp256k1KeyPair.PublicKeyCompressedByteLength],
            new byte[MlKemPublicKey.CiphertextByteLength],
            new byte[length],
            16, 0, 1, OneChunk(0));

    private static Manifest BuildWithChunks(IReadOnlyList<ManifestChunkEntry> chunks, long totalSize)
        => new(
            1, HybridKem.SuiteIdentifier,
            AgentIdentity.Generate().Address, AgentIdentity.Generate().Address,
            new byte[Secp256k1KeyPair.PublicKeyCompressedByteLength],
            new byte[MlKemPublicKey.CiphertextByteLength],
            new byte[ChunkedAead.NoncePrefixByteLength],
            16, totalSize, 1, chunks);

    private static ManifestChunkEntry[] OneChunk(int size)
    {
        var tag = new byte[ChunkedAead.TagByteLength];
        return new[] { new ManifestChunkEntry(0, "cid0", tag, size) };
    }
}
