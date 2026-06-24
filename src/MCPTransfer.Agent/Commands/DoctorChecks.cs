using System.Numerics;
using MCPTransfer.Core.Configuration;

namespace MCPTransfer.Agent.Commands;

internal enum CheckStatus { Ok, Warn, Fail, Info }

/// <summary>One diagnostic line: a status, a label, optional detail and fix.</summary>
internal sealed record Check(CheckStatus Status, string Label, string? Detail = null, string? Fix = null);

/// <summary>
/// Pure (I/O-free, network-free) diagnostic evaluations used by
/// <c>mcptx doctor</c>, factored out so they can be unit-tested without a chain
/// or filesystem. The network checks (RPC, balance, registration) live in the
/// command itself.
/// </summary>
internal static class DoctorChecks
{
    /// <summary>
    /// Evaluate the configured storage backend from config alone.
    /// <paramref name="directoryExists"/> is injectable for tests (defaults to
    /// <see cref="Directory.Exists"/>).
    /// </summary>
    public static Check Storage(IpfsConfigSection ipfs, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        switch (ipfs.Kind)
        {
            case IpfsConfigSection.KindMemory:
                return new(CheckStatus.Warn, "Storage: memory (in-process only)",
                    "Pins are lost when the process exits.",
                    "Fine for a local demo; pick 'file' or 'pinata' for real transfers (mcptx setup).");

            case IpfsConfigSection.KindPinata:
                return string.IsNullOrEmpty(ipfs.PinataJwt)
                    ? new(CheckStatus.Fail, "Storage: Pinata — no JWT",
                        "PinataJwt is empty and PINATA_JWT is unset.",
                        "Set PINATA_JWT or re-run 'mcptx setup'.")
                    : new(CheckStatus.Ok, "Storage: Pinata JWT configured", "(not live-verified)");

            case IpfsConfigSection.KindFile:
                if (string.IsNullOrEmpty(ipfs.Directory))
                    return new(CheckStatus.Fail, "Storage: file — no directory set", null,
                        "Set one via 'mcptx setup' or 'config init --ipfs-dir'.");
                return directoryExists(ipfs.Directory)
                    ? new(CheckStatus.Ok, $"Storage: file store at {ipfs.Directory}")
                    : new(CheckStatus.Warn, $"Storage: file store {ipfs.Directory}",
                        "directory does not exist yet", "It is created on first use.");

            default:
                return new(CheckStatus.Fail, $"Storage: unknown kind '{ipfs.Kind}'", null, "Re-run 'mcptx setup'.");
        }
    }

    /// <summary>Format a wei balance as short decimal native-token units
    /// (invariant culture, so it reads the same on a French-locale machine).</summary>
    public static string FormatNativeBalance(BigInteger wei)
    {
        // Testnet balances are tiny; (decimal) is plenty of range here.
        var units = (decimal)wei / 1_000_000_000_000_000_000m;
        return units.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }
}
