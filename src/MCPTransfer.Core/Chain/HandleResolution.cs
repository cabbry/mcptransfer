using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Core.Chain;

/// <summary>
/// Shared resolution of a user-supplied recipient string — a <c>0x</c> address
/// or a handle — to an <see cref="EthereumAddress"/>. Centralizes the parse so
/// the "0x but not valid hex" case is rejected the SAME way everywhere (a clean
/// <see cref="InvalidOperationException"/>), instead of leaking a
/// <see cref="FormatException"/> from one caller and an
/// <see cref="ArgumentException"/> from another.
/// </summary>
public static class HandleResolution
{
    /// <summary>
    /// Parse a <c>0x</c>-prefixed Ethereum address, mapping every
    /// malformed-input exception (<see cref="ArgumentException"/> for a wrong
    /// length, <see cref="FormatException"/> for non-hex characters) to a
    /// single clean <see cref="InvalidOperationException"/>.
    /// </summary>
    public static EthereumAddress ParseAddress(string address)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        try
        {
            return EthereumAddress.FromHex(address);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                $"'{address}' is not a valid 0x Ethereum address: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Resolve a recipient that MUST exist: a <c>0x</c> address (parsed) or a
    /// handle (validated, then resolved on chain). Returns the address and the
    /// handle (null when a raw address was given). Throws
    /// <see cref="InvalidOperationException"/> on a malformed address, an
    /// invalid handle shape, or an unclaimed handle.
    /// </summary>
    public static async Task<(EthereumAddress Address, string? Handle)> ResolveRequiredAsync(
        IAgentDirectoryClient directory,
        string recipient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrEmpty(recipient);

        if (recipient.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return (ParseAddress(recipient), null);

        if (!HandleValidation.IsValid(recipient))
        {
            throw new InvalidOperationException(
                $"'{recipient}' is not a valid handle and doesn't look like a 0x address.");
        }

        var resolved = await directory.ResolveAsync(recipient, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Handle '{recipient}' is not claimed on chain.");
        return (resolved, recipient);
    }
}
