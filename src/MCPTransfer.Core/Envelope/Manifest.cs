using System.Text.Encodings.Web;
using System.Text.Json;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Envelope;

/// <summary>
/// The signed payload of a transfer: enumerates the encrypted chunks, the
/// hybrid KEM public material the recipient needs to derive the AES key,
/// and the addressing / suite metadata.
/// </summary>
/// <remarks>
/// <para>
/// Canonical JSON form is deterministic: property names are emitted in
/// alphabetical order, optional fields are omitted when null, byte values
/// are base64-encoded, addresses are lower-case 0x-prefixed hex. The
/// content hash signed by the sender is <c>Keccak-256</c> of this canonical
/// byte sequence.
/// </para>
/// <para>
/// Byte-valued properties are exposed as <see cref="ReadOnlyMemory{T}"/>
/// so callers cannot mutate the stored bytes. Use <c>.Span</c> for
/// synchronous access and <c>.ToArray()</c> when a fresh array is needed.
/// </para>
/// </remarks>
public sealed class Manifest
{
    public const int CurrentVersion = 1;

    private readonly byte[] _ephemeralSecp256k1PublicKey;
    private readonly byte[] _kemCiphertext;
    private readonly byte[] _noncePrefix;

    public int Version { get; }
    public string Suite { get; }
    public EthereumAddress Sender { get; }
    public EthereumAddress Recipient { get; }
    public ReadOnlyMemory<byte> EphemeralSecp256k1PublicKey => _ephemeralSecp256k1PublicKey;
    public ReadOnlyMemory<byte> KemCiphertext => _kemCiphertext;
    public ReadOnlyMemory<byte> NoncePrefix => _noncePrefix;
    public int ChunkSize { get; }
    public long TotalSize { get; }
    public long CreatedAtUnixSeconds { get; }
    public string? Filename { get; }
    public string? MimeType { get; }
    public IReadOnlyList<ManifestChunkEntry> Chunks { get; }

    public Manifest(
        int version,
        string suite,
        EthereumAddress sender,
        EthereumAddress recipient,
        ReadOnlyMemory<byte> ephemeralSecp256k1PublicKey,
        ReadOnlyMemory<byte> kemCiphertext,
        ReadOnlyMemory<byte> noncePrefix,
        int chunkSize,
        long totalSize,
        long createdAtUnixSeconds,
        IReadOnlyList<ManifestChunkEntry> chunks,
        string? filename = null,
        string? mimeType = null)
    {
        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version), "Version must be positive.");
        ArgumentException.ThrowIfNullOrEmpty(suite);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(chunks);

        if (ephemeralSecp256k1PublicKey.Length != Secp256k1KeyPair.PublicKeyCompressedByteLength)
            throw new ArgumentException(
                $"Ephemeral secp256k1 public key must be {Secp256k1KeyPair.PublicKeyCompressedByteLength} bytes "
                + $"(got {ephemeralSecp256k1PublicKey.Length}).",
                nameof(ephemeralSecp256k1PublicKey));
        if (kemCiphertext.Length != MlKemPublicKey.CiphertextByteLength)
            throw new ArgumentException(
                $"KEM ciphertext must be {MlKemPublicKey.CiphertextByteLength} bytes "
                + $"(got {kemCiphertext.Length}).",
                nameof(kemCiphertext));
        if (noncePrefix.Length != ChunkedAead.NoncePrefixByteLength)
            throw new ArgumentException(
                $"Nonce prefix must be {ChunkedAead.NoncePrefixByteLength} bytes "
                + $"(got {noncePrefix.Length}).",
                nameof(noncePrefix));
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be positive.");
        if (totalSize < 0)
            throw new ArgumentOutOfRangeException(nameof(totalSize), "Total size must be non-negative.");
        if (createdAtUnixSeconds <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(createdAtUnixSeconds), "Timestamp must be a positive Unix-seconds value.");
        if (chunks.Count == 0)
            throw new ArgumentException("Manifest must contain at least one chunk.", nameof(chunks));

        long sumOfCiphertextSizes = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Index != i)
            {
                throw new ArgumentException(
                    $"Chunks must be sorted with contiguous indices 0..N-1; "
                    + $"got index {chunks[i].Index} at position {i}.",
                    nameof(chunks));
            }
            sumOfCiphertextSizes += chunks[i].CiphertextSize;
        }
        if (sumOfCiphertextSizes != totalSize)
        {
            throw new ArgumentException(
                $"Sum of chunk ciphertext sizes ({sumOfCiphertextSizes}) does not match "
                + $"total_size ({totalSize}).",
                nameof(totalSize));
        }

        Version = version;
        Suite = suite;
        Sender = sender;
        Recipient = recipient;
        _ephemeralSecp256k1PublicKey = ephemeralSecp256k1PublicKey.ToArray();
        _kemCiphertext = kemCiphertext.ToArray();
        _noncePrefix = noncePrefix.ToArray();
        ChunkSize = chunkSize;
        TotalSize = totalSize;
        CreatedAtUnixSeconds = createdAtUnixSeconds;
        Filename = filename;
        MimeType = mimeType;
        // Always snapshot: if the caller passes a ManifestChunkEntry[], they can
        // still mutate it afterwards and we'd silently inherit the change.
        Chunks = chunks.ToArray();
    }

    /// <summary>
    /// Serialize this manifest to its canonical byte representation:
    /// minified JSON, properties in alphabetical order, base64 for bytes,
    /// lower-case 0x hex for addresses, omitted optional fields.
    /// </summary>
    public byte[] ToCanonicalJsonBytes()
    {
        using var stream = new MemoryStream();
        var options = new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            SkipValidation = false,
        };

        using (var writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartObject();

            writer.WriteNumber("chunk_size", ChunkSize);

            writer.WriteStartArray("chunks");
            foreach (var chunk in Chunks)
            {
                writer.WriteStartObject();
                writer.WriteString("cid", chunk.Cid);
                writer.WriteNumber("index", chunk.Index);
                writer.WriteNumber("size", chunk.CiphertextSize);
                writer.WriteBase64String("tag", chunk.Tag.Span);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteNumber("created_at", CreatedAtUnixSeconds);
            writer.WriteBase64String("ephemeral_secp256k1", _ephemeralSecp256k1PublicKey);

            if (Filename is not null)
                writer.WriteString("filename", Filename);

            writer.WriteBase64String("kem_ciphertext", _kemCiphertext);

            if (MimeType is not null)
                writer.WriteString("mime_type", MimeType);

            writer.WriteBase64String("nonce_prefix", _noncePrefix);
            writer.WriteString("recipient", Recipient.LowerHex);
            writer.WriteString("sender", Sender.LowerHex);
            writer.WriteString("suite", Suite);
            writer.WriteNumber("total_size", TotalSize);
            writer.WriteNumber("version", Version);

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Keccak-256 over <see cref="ToCanonicalJsonBytes"/>. This is what the sender signs.</summary>
    public byte[] ComputeContentHash() => Hashes.Keccak256(ToCanonicalJsonBytes());

    /// <summary>
    /// Parse a manifest from JSON bytes. Unknown properties are ignored
    /// (forward compatibility); missing required properties throw.
    /// </summary>
    public static Manifest FromJsonBytes(ReadOnlySpan<byte> jsonBytes)
    {
        using var doc = JsonDocument.Parse(jsonBytes.ToArray());
        var root = doc.RootElement;

        var version = root.GetProperty("version").GetInt32();
        var suite = root.GetProperty("suite").GetString()
            ?? throw new InvalidOperationException("Missing 'suite' property.");
        var sender = EthereumAddress.FromHex(root.GetProperty("sender").GetString()
            ?? throw new InvalidOperationException("Missing 'sender' property."));
        var recipient = EthereumAddress.FromHex(root.GetProperty("recipient").GetString()
            ?? throw new InvalidOperationException("Missing 'recipient' property."));
        var ephPk = root.GetProperty("ephemeral_secp256k1").GetBytesFromBase64();
        var kemCt = root.GetProperty("kem_ciphertext").GetBytesFromBase64();
        var noncePrefix = root.GetProperty("nonce_prefix").GetBytesFromBase64();
        var chunkSize = root.GetProperty("chunk_size").GetInt32();
        var totalSize = root.GetProperty("total_size").GetInt64();
        var createdAt = root.GetProperty("created_at").GetInt64();

        string? filename = null;
        if (root.TryGetProperty("filename", out var fnEl) && fnEl.ValueKind == JsonValueKind.String)
            filename = fnEl.GetString();

        string? mimeType = null;
        if (root.TryGetProperty("mime_type", out var mtEl) && mtEl.ValueKind == JsonValueKind.String)
            mimeType = mtEl.GetString();

        var chunksEl = root.GetProperty("chunks");
        var chunks = new ManifestChunkEntry[chunksEl.GetArrayLength()];
        var i = 0;
        foreach (var c in chunksEl.EnumerateArray())
        {
            chunks[i++] = new ManifestChunkEntry(
                index: c.GetProperty("index").GetInt32(),
                cid: c.GetProperty("cid").GetString()
                    ?? throw new InvalidOperationException($"Missing 'cid' on chunk {i - 1}."),
                tag: c.GetProperty("tag").GetBytesFromBase64(),
                ciphertextSize: c.GetProperty("size").GetInt32());
        }

        return new Manifest(
            version, suite, sender, recipient,
            ephPk, kemCt, noncePrefix,
            chunkSize, totalSize, createdAt,
            chunks, filename, mimeType);
    }
}
