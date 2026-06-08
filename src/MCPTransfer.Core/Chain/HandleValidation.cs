namespace MCPTransfer.Core.Chain;

/// <summary>
/// Client-side mirror of the handle validation rules enforced by
/// <c>AgentDirectory.sol</c>. Use it to fail fast before paying gas.
/// </summary>
public static class HandleValidation
{
    /// <summary>Minimum handle length (inclusive), in bytes.</summary>
    public const int MinLength = 3;
    /// <summary>Maximum handle length (inclusive), in bytes.</summary>
    public const int MaxLength = 32;

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if <paramref name="handle"/> does
    /// not satisfy <c>[a-z0-9-]{3,32}</c> with no leading or trailing hyphen.
    /// </summary>
    public static void Validate(string handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.Length < MinLength || handle.Length > MaxLength)
        {
            throw new ArgumentException(
                $"Handle length must be {MinLength}..{MaxLength} bytes (got {handle.Length}).",
                nameof(handle));
        }
        if (handle[0] == '-' || handle[^1] == '-')
        {
            throw new ArgumentException(
                "Handle cannot start or end with '-'.",
                nameof(handle));
        }

        foreach (var c in handle)
        {
            var ok = c is >= '0' and <= '9'
                  or >= 'a' and <= 'z'
                  or '-';
            if (!ok)
            {
                throw new ArgumentException(
                    $"Handle must match [a-z0-9-] (offending character: '{c}').",
                    nameof(handle));
            }
        }
    }

    /// <summary>True iff <see cref="Validate"/> would accept the handle.</summary>
    public static bool IsValid(string? handle)
    {
        if (handle is null) return false;
        try { Validate(handle); return true; }
        catch (ArgumentException) { return false; }
    }
}
