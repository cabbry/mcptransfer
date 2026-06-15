using System.Security.Cryptography;

namespace MCPTransfer.Core.Crypto;

/// <summary>
/// Lowercase-hex and short-fingerprint formatting, shared across the CLI and
/// MCP surfaces so the <c>0x</c>-prefix and fingerprint conventions (and the
/// 16-char truncation) live in exactly one place instead of being repeated by
/// hand at a dozen call sites.
/// </summary>
public static class HexFormat
{
    /// <summary>Lowercase hex of <paramref name="data"/> with a <c>0x</c> prefix.</summary>
    public static string ToHex0x(ReadOnlySpan<byte> data)
        => "0x" + Convert.ToHexString(data).ToLowerInvariant();

    /// <summary>
    /// Short display fingerprint: <c>sha256:</c> + the first 16 lowercase hex
    /// chars of <c>SHA-256(data)</c>. For human comparison, not security.
    /// </summary>
    public static string Fingerprint(ReadOnlySpan<byte> data)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()[..16];
}
