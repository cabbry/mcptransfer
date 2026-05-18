using System.Text;

namespace MCPTransfer.Core.Crypto;

/// <summary>
/// A 20-byte Ethereum-style address, derivable from a secp256k1 public key.
/// Display form follows EIP-55 mixed-case checksum.
/// </summary>
public sealed class EthereumAddress : IEquatable<EthereumAddress>
{
    public const int ByteLength = 20;

    private readonly byte[] _bytes;
    private string? _checksumHex;

    public EthereumAddress(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
            throw new ArgumentException(
                $"Ethereum address must be exactly {ByteLength} bytes (got {bytes.Length}).",
                nameof(bytes));
        _bytes = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>EIP-55 mixed-case hex with <c>0x</c> prefix.</summary>
    public string ChecksumHex => _checksumHex ??= ComputeChecksumHex(_bytes);

    /// <summary>All-lowercase hex with <c>0x</c> prefix.</summary>
    public string LowerHex => "0x" + Convert.ToHexString(_bytes).ToLowerInvariant();

    public override string ToString() => ChecksumHex;

    public static EthereumAddress FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var trimmed = hex.AsSpan();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];
        if (trimmed.Length != ByteLength * 2)
            throw new ArgumentException(
                $"Ethereum address hex must be {ByteLength * 2} chars (got {trimmed.Length}).",
                nameof(hex));
        return new EthereumAddress(Convert.FromHexString(trimmed));
    }

    private static string ComputeChecksumHex(byte[] addressBytes)
    {
        var lower = Convert.ToHexString(addressBytes).ToLowerInvariant();
        var hashOfLower = Hashes.Keccak256(Encoding.ASCII.GetBytes(lower));
        var hashHex = Convert.ToHexString(hashOfLower).ToLowerInvariant();

        var sb = new StringBuilder(42);
        sb.Append("0x");
        for (var i = 0; i < lower.Length; i++)
        {
            var c = lower[i];
            // Per EIP-55: uppercase the address char if it's a letter AND the
            // corresponding hash nibble is >= 8.
            if (c is >= 'a' and <= 'f' && HexNibble(hashHex[i]) >= 8)
                sb.Append(char.ToUpperInvariant(c));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static int HexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => 10 + (c - 'a'),
        >= 'A' and <= 'F' => 10 + (c - 'A'),
        _ => throw new ArgumentException("Invalid hex char.", nameof(c))
    };

    public bool Equals(EthereumAddress? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is EthereumAddress a && Equals(a);

    public override int GetHashCode() => BitConverter.ToInt32(_bytes, 0);

    public static bool operator ==(EthereumAddress? left, EthereumAddress? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(EthereumAddress? left, EthereumAddress? right)
        => !(left == right);
}
