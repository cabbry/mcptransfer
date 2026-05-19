using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace MCPTransfer.Core.Crypto;

/// <summary>
/// AES-256-GCM in a chunked, streaming mode tailored to per-chunk IPFS
/// storage. Every chunk is encrypted under a deterministic 12-byte nonce
/// formed as <c>nonce_prefix (8 random bytes) || chunk_index (4 bytes BE)</c>,
/// which makes nonce reuse structurally impossible within a single transfer.
/// </summary>
/// <remarks>
/// <para>
/// Capacity: 2³¹ chunks × <see cref="DefaultChunkSize"/> ≈ 32 TiB per
/// transfer before the chunk index overflows. The first uniformly random
/// 8 bytes of the nonce keep distinct transfers using the same key
/// statistically independent.
/// </para>
/// <para>
/// An empty input still produces one empty chunk so that recipients always
/// have at least one tag to verify and the manifest always has at least
/// one entry.
/// </para>
/// </remarks>
public static class ChunkedAead
{
    public const int DefaultChunkSize = 16 * 1024 * 1024;
    public const int KeyByteLength = 32;
    public const int NoncePrefixByteLength = 8;
    public const int FullNonceByteLength = 12;
    public const int TagByteLength = 16;
    public const int ChunkIndexByteLength = FullNonceByteLength - NoncePrefixByteLength;

    /// <summary>
    /// Encrypts <paramref name="input"/> chunk by chunk and yields each
    /// encrypted chunk in order. The input stream is consumed sequentially;
    /// the iterator does not buffer all chunks in memory.
    /// </summary>
    public static async IAsyncEnumerable<EncryptedChunk> EncryptAsync(
        Stream input,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> noncePrefix,
        int chunkSize = DefaultChunkSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateKeyAndNonce(key, noncePrefix);
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be positive.");

        using var aes = new AesGcm(key.Span, TagByteLength);
        var buffer = new byte[chunkSize];
        var nonce = new byte[FullNonceByteLength];
        noncePrefix.Span.CopyTo(nonce.AsSpan(0, NoncePrefixByteLength));

        var chunkIndex = 0;
        var emittedAny = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesRead = await ReadFullAsync(input, buffer, cancellationToken).ConfigureAwait(false);
            var isLastChunk = bytesRead < chunkSize;

            if (bytesRead == 0 && emittedAny)
                break;

            BinaryPrimitives.WriteInt32BigEndian(
                nonce.AsSpan(NoncePrefixByteLength, ChunkIndexByteLength),
                chunkIndex);

            var ciphertext = new byte[bytesRead];
            var tag = new byte[TagByteLength];
            aes.Encrypt(nonce, buffer.AsSpan(0, bytesRead), ciphertext, tag);

            // Internal factory: we own the freshly allocated arrays, no defensive copy.
            yield return EncryptedChunk.CreateOwning(chunkIndex, ciphertext, tag);

            chunkIndex++;
            emittedAny = true;
            if (isLastChunk)
                break;
        }
    }

    /// <summary>
    /// Decrypts a stream of chunks (assumed in order, starting at index 0)
    /// and writes the recovered plaintext to <paramref name="output"/>.
    /// Throws <see cref="CryptographicException"/> on any tag mismatch or
    /// out-of-order chunk index.
    /// </summary>
    public static async Task DecryptAsync(
        IAsyncEnumerable<EncryptedChunk> chunks,
        Stream output,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> noncePrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(output);
        ValidateKeyAndNonce(key, noncePrefix);

        using var aes = new AesGcm(key.Span, TagByteLength);
        var nonce = new byte[FullNonceByteLength];
        noncePrefix.Span.CopyTo(nonce.AsSpan(0, NoncePrefixByteLength));

        var expectedIndex = 0;
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.Index != expectedIndex)
                throw new CryptographicException(
                    $"Chunk out of order: expected index {expectedIndex}, got {chunk.Index}.");
            if (chunk.Tag.Length != TagByteLength)
                throw new CryptographicException(
                    $"Invalid tag length on chunk {chunk.Index}.");

            BinaryPrimitives.WriteInt32BigEndian(
                nonce.AsSpan(NoncePrefixByteLength, ChunkIndexByteLength),
                chunk.Index);

            var plain = new byte[chunk.Ciphertext.Length];
            aes.Decrypt(nonce, chunk.Ciphertext.Span, chunk.Tag.Span, plain);
            await output.WriteAsync(plain, cancellationToken).ConfigureAwait(false);

            expectedIndex++;
        }

        if (expectedIndex == 0)
            throw new CryptographicException("No chunks were provided.");
    }

    private static void ValidateKeyAndNonce(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> noncePrefix)
    {
        if (key.Length != KeyByteLength)
            throw new ArgumentException(
                $"Key must be exactly {KeyByteLength} bytes (got {key.Length}).",
                nameof(key));
        if (noncePrefix.Length != NoncePrefixByteLength)
            throw new ArgumentException(
                $"Nonce prefix must be exactly {NoncePrefixByteLength} bytes (got {noncePrefix.Length}).",
                nameof(noncePrefix));
    }

    private static async Task<int> ReadFullAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(total, buffer.Length - total),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }
}

/// <summary>
/// A single chunk produced by <see cref="ChunkedAead.EncryptAsync"/>: the
/// 0-based index, the ciphertext, and the 16-byte AES-GCM authentication tag.
/// The nonce is not carried — it is reconstructed deterministically from
/// the transfer's <c>nonce_prefix</c> and the index.
/// </summary>
/// <remarks>
/// Both <see cref="Ciphertext"/> and <see cref="Tag"/> are exposed as
/// <see cref="ReadOnlyMemory{T}"/> so callers cannot mutate the stored bytes.
/// </remarks>
public sealed class EncryptedChunk
{
    private readonly byte[] _ciphertext;
    private readonly byte[] _tag;

    public int Index { get; }
    public ReadOnlyMemory<byte> Ciphertext => _ciphertext;
    public ReadOnlyMemory<byte> Tag => _tag;

    public EncryptedChunk(int index, ReadOnlyMemory<byte> ciphertext, ReadOnlyMemory<byte> tag)
        : this(index, ciphertext.ToArray(), tag.ToArray(), validate: true)
    {
    }

    private EncryptedChunk(int index, byte[] ciphertext, byte[] tag, bool validate)
    {
        if (validate)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index), "Chunk index must be non-negative.");
            ArgumentNullException.ThrowIfNull(ciphertext);
            ArgumentNullException.ThrowIfNull(tag);
            if (tag.Length != ChunkedAead.TagByteLength)
                throw new ArgumentException(
                    $"Tag must be exactly {ChunkedAead.TagByteLength} bytes (got {tag.Length}).",
                    nameof(tag));
        }

        Index = index;
        _ciphertext = ciphertext;
        _tag = tag;
    }

    /// <summary>
    /// Internal factory used by the encryption pipeline to wrap freshly
    /// allocated byte arrays without a defensive copy. The producer is
    /// responsible for not retaining its own references to the arrays.
    /// </summary>
    internal static EncryptedChunk CreateOwning(int index, byte[] ciphertext, byte[] tag)
        => new(index, ciphertext, tag, validate: true);
}
