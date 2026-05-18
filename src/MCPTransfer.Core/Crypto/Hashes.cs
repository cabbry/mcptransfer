using Org.BouncyCastle.Crypto.Digests;

namespace MCPTransfer.Core.Crypto;

/// <summary>
/// Centralized hash primitives used across the protocol. Keccak-256 is the
/// canonical hash for any value that touches the EVM (addresses, content
/// hashes, message-to-sign), to match the on-chain <c>KECCAK256</c> opcode.
/// </summary>
public static class Hashes
{
    public const int Keccak256ByteLength = 32;

    public static byte[] Keccak256(ReadOnlySpan<byte> data)
    {
        var output = new byte[Keccak256ByteLength];
        Keccak256(data, output);
        return output;
    }

    public static void Keccak256(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (destination.Length < Keccak256ByteLength)
            throw new ArgumentException(
                $"Destination must be at least {Keccak256ByteLength} bytes.",
                nameof(destination));

        var digest = new KeccakDigest(256);
        var buffer = data.ToArray();
        digest.BlockUpdate(buffer, 0, buffer.Length);
        var tmp = new byte[Keccak256ByteLength];
        digest.DoFinal(tmp, 0);
        tmp.CopyTo(destination);
    }
}
