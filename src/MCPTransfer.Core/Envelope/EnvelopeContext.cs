using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Envelope;

/// <summary>
/// Builds the binding string fed into HKDF's <c>info</c> parameter when
/// deriving the per-transfer AES key. Binding the addresses and nonce
/// prefix into the key derivation prevents cross-protocol replay: an
/// attacker who intercepts the ephemeral pubkey and KEM ciphertext
/// cannot reuse them in a different (sender, recipient) pairing.
/// </summary>
internal static class EnvelopeContext
{
    public static byte[] BuildHkdfContext(
        EthereumAddress sender,
        EthereumAddress recipient,
        ReadOnlySpan<byte> noncePrefix)
    {
        if (noncePrefix.Length != ChunkedAead.NoncePrefixByteLength)
            throw new ArgumentException(
                $"Nonce prefix must be {ChunkedAead.NoncePrefixByteLength} bytes "
                + $"(got {noncePrefix.Length}).",
                nameof(noncePrefix));

        var output = new byte[EthereumAddress.ByteLength * 2 + ChunkedAead.NoncePrefixByteLength];
        sender.Bytes.CopyTo(output);
        recipient.Bytes.CopyTo(output.AsSpan(EthereumAddress.ByteLength));
        noncePrefix.CopyTo(output.AsSpan(EthereumAddress.ByteLength * 2));
        return output;
    }
}
