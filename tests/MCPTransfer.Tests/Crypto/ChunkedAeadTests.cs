using System.Security.Cryptography;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Crypto;

public class ChunkedAeadTests
{
    private const int TestChunkSize = 64;

    private static (byte[] key, byte[] noncePrefix) NewKeyMaterial()
    {
        var key = RandomNumberGenerator.GetBytes(ChunkedAead.KeyByteLength);
        var prefix = RandomNumberGenerator.GetBytes(ChunkedAead.NoncePrefixByteLength);
        return (key, prefix);
    }

    private static async Task<EncryptedChunk[]> EncryptToArrayAsync(
        byte[] plaintext, byte[] key, byte[] noncePrefix, int chunkSize)
    {
        using var input = new MemoryStream(plaintext);
        var chunks = new List<EncryptedChunk>();
        await foreach (var c in ChunkedAead.EncryptAsync(input, key, noncePrefix, chunkSize))
            chunks.Add(c);
        return chunks.ToArray();
    }

    private static async Task<byte[]> DecryptToBytesAsync(
        EncryptedChunk[] chunks, byte[] key, byte[] noncePrefix)
    {
        using var output = new MemoryStream();
        await ChunkedAead.DecryptAsync(ToAsync(chunks), output, key, noncePrefix);
        return output.ToArray();
    }

    private static async IAsyncEnumerable<EncryptedChunk> ToAsync(IEnumerable<EncryptedChunk> source)
    {
        foreach (var c in source)
        {
            yield return c;
            await Task.Yield();
        }
    }

    [Theory]
    [InlineData(0)]                    // empty -> 1 zero-byte chunk
    [InlineData(1)]                    // smaller than chunk
    [InlineData(TestChunkSize - 1)]    // just under
    [InlineData(TestChunkSize)]        // exactly one chunk
    [InlineData(TestChunkSize + 1)]    // one full + 1 byte
    [InlineData(TestChunkSize * 3)]    // exact multiple
    [InlineData(TestChunkSize * 3 + 7)]// multiple + tail
    [InlineData(1024)]
    public async Task RoundTrip_PreservesPlaintext(int size)
    {
        var plaintext = RandomNumberGenerator.GetBytes(size);
        var (key, prefix) = NewKeyMaterial();

        var chunks = await EncryptToArrayAsync(plaintext, key, prefix, TestChunkSize);
        var recovered = await DecryptToBytesAsync(chunks, key, prefix);

        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public async Task EmptyInput_ProducesExactlyOneEmptyChunk()
    {
        var (key, prefix) = NewKeyMaterial();
        var chunks = await EncryptToArrayAsync(Array.Empty<byte>(), key, prefix, TestChunkSize);

        Assert.Single(chunks);
        Assert.Equal(0, chunks[0].Index);
        Assert.Equal(0, chunks[0].Ciphertext.Length);
        Assert.Equal(ChunkedAead.TagByteLength, chunks[0].Tag.Length);
    }

    [Fact]
    public async Task ChunkCount_MatchesCeilingOfSizeOverChunkSize()
    {
        var (key, prefix) = NewKeyMaterial();
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize * 3 + 5);

        var chunks = await EncryptToArrayAsync(plaintext, key, prefix, TestChunkSize);
        Assert.Equal(4, chunks.Length);

        Assert.Equal(TestChunkSize, chunks[0].Ciphertext.Length);
        Assert.Equal(TestChunkSize, chunks[1].Ciphertext.Length);
        Assert.Equal(TestChunkSize, chunks[2].Ciphertext.Length);
        Assert.Equal(5, chunks[3].Ciphertext.Length);

        Assert.Equal(new[] { 0, 1, 2, 3 }, chunks.Select(c => c.Index));
    }

    [Fact]
    public async Task TamperedCiphertext_FailsToDecrypt()
    {
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize * 2);
        var (key, prefix) = NewKeyMaterial();
        var chunks = await EncryptToArrayAsync(plaintext, key, prefix, TestChunkSize);

        // Flip a bit in chunk 1's ciphertext by rebuilding the chunk.
        var tamperedCiphertext = chunks[1].Ciphertext.ToArray();
        tamperedCiphertext[0] ^= 0x01;
        chunks[1] = new EncryptedChunk(chunks[1].Index, tamperedCiphertext, chunks[1].Tag);

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => DecryptToBytesAsync(chunks, key, prefix));
    }

    [Fact]
    public async Task TamperedTag_FailsToDecrypt()
    {
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize);
        var (key, prefix) = NewKeyMaterial();
        var chunks = await EncryptToArrayAsync(plaintext, key, prefix, TestChunkSize);

        var tamperedTag = chunks[0].Tag.ToArray();
        tamperedTag[0] ^= 0xFF;
        chunks[0] = new EncryptedChunk(chunks[0].Index, chunks[0].Ciphertext, tamperedTag);

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => DecryptToBytesAsync(chunks, key, prefix));
    }

    [Fact]
    public async Task WrongKey_FailsToDecrypt()
    {
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize);
        var (key, prefix) = NewKeyMaterial();
        var chunks = await EncryptToArrayAsync(plaintext, key, prefix, TestChunkSize);

        var wrongKey = RandomNumberGenerator.GetBytes(ChunkedAead.KeyByteLength);
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => DecryptToBytesAsync(chunks, wrongKey, prefix));
    }

    [Fact]
    public async Task WrongNoncePrefix_FailsToDecrypt()
    {
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize);
        var (key, prefix) = NewKeyMaterial();
        var chunks = await EncryptToArrayAsync(plaintext, key, prefix, TestChunkSize);

        var wrongPrefix = RandomNumberGenerator.GetBytes(ChunkedAead.NoncePrefixByteLength);
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => DecryptToBytesAsync(chunks, key, wrongPrefix));
    }

    [Fact]
    public async Task ChunkReordering_IsDetected()
    {
        var plaintext = RandomNumberGenerator.GetBytes(TestChunkSize * 2);
        var (key, prefix) = NewKeyMaterial();
        var chunks = await EncryptToArrayAsync(plaintext, key, prefix, TestChunkSize);

        // Swap order.
        var reordered = new[] { chunks[1], chunks[0] };
        await Assert.ThrowsAsync<CryptographicException>(
            () => DecryptToBytesAsync(reordered, key, prefix));
    }

    [Fact]
    public async Task EncryptAsync_RejectsWrongKeyLength()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in ChunkedAead.EncryptAsync(
                new MemoryStream(new byte[10]),
                key: new byte[31],
                noncePrefix: new byte[ChunkedAead.NoncePrefixByteLength]))
            {
                // never reached
            }
        });
    }

    [Fact]
    public async Task EncryptAsync_RejectsWrongNoncePrefixLength()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in ChunkedAead.EncryptAsync(
                new MemoryStream(new byte[10]),
                key: new byte[ChunkedAead.KeyByteLength],
                noncePrefix: new byte[7]))
            {
                // never reached
            }
        });
    }

    [Fact]
    public void EncryptedChunk_RejectsNegativeIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EncryptedChunk(-1, Array.Empty<byte>(), new byte[ChunkedAead.TagByteLength]));
    }

    [Fact]
    public void EncryptedChunk_RejectsWrongTagLength()
    {
        Assert.Throws<ArgumentException>(() =>
            new EncryptedChunk(0, Array.Empty<byte>(), new byte[15]));
    }
}
