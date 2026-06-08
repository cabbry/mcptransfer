using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Envelope;

/// <summary>
/// Builds the binding string fed into HKDF's <c>info</c> parameter when
/// deriving the per-transfer AES key. Binding the addresses, the recipient's
/// ML-KEM public key, and the nonce prefix into the key derivation:
/// <list type="bullet">
/// <item>prevents cross-protocol replay (an attacker who intercepts the
/// ephemeral pubkey and KEM ciphertext cannot reuse them in a different
/// (sender, recipient) pairing);</item>
/// <item>binds the recipient's ML-KEM public key to the derived key, so a
/// substituted ML-KEM key (e.g. served by a lying RPC) deterministically
/// changes the derived key. This makes the previously accidental safety
/// (the hybrid construction happened to fail closed) a design guarantee.</item>
/// </list>
/// </summary>
internal static class EnvelopeContext
{
    public static byte[] BuildHkdfContext(
        EthereumAddress sender,
        EthereumAddress recipient,
        ReadOnlySpan<byte> recipientMlKemPubkey,
        ReadOnlySpan<byte> noncePrefix)
    {
        if (recipientMlKemPubkey.Length != MlKemPublicKey.PublicKeyByteLength)
            throw new ArgumentException(
                $"Recipient ML-KEM public key must be {MlKemPublicKey.PublicKeyByteLength} bytes "
                + $"(got {recipientMlKemPubkey.Length}).",
                nameof(recipientMlKemPubkey));
        if (noncePrefix.Length != ChunkedAead.NoncePrefixByteLength)
            throw new ArgumentException(
                $"Nonce prefix must be {ChunkedAead.NoncePrefixByteLength} bytes "
                + $"(got {noncePrefix.Length}).",
                nameof(noncePrefix));

        var output = new byte[
            EthereumAddress.ByteLength * 2
            + MlKemPublicKey.PublicKeyByteLength
            + ChunkedAead.NoncePrefixByteLength];

        var offset = 0;
        sender.Bytes.CopyTo(output.AsSpan(offset));
        offset += EthereumAddress.ByteLength;
        recipient.Bytes.CopyTo(output.AsSpan(offset));
        offset += EthereumAddress.ByteLength;
        recipientMlKemPubkey.CopyTo(output.AsSpan(offset));
        offset += MlKemPublicKey.PublicKeyByteLength;
        noncePrefix.CopyTo(output.AsSpan(offset));
        return output;
    }
}
