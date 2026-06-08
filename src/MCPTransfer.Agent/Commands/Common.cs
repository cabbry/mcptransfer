using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Configuration;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Ipfs;
using MCPTransfer.Core.Storage;

namespace MCPTransfer.Agent.Commands;

/// <summary>
/// Shared helpers used by every <c>mcptx</c> subcommand: argument parsing,
/// config/identity loading, dependency wiring.
/// </summary>
internal static class Common
{
    public const int ExitSuccess = 0;
    public const int ExitError = 1;

    /// <summary>
    /// Extract the value of a <c>--flag VALUE</c> arg. Returns null if the
    /// flag is absent. Throws <see cref="ArgumentException"/> if the flag
    /// is present without a value, or if the next token looks like another
    /// flag (defends against e.g. <c>mcptx send --to --force</c>).
    /// </summary>
    public static string? GetFlagValue(string[] args, string flag)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (!string.Equals(args[i], flag, StringComparison.Ordinal))
                continue;

            if (i + 1 >= args.Length)
                throw new ArgumentException($"Missing value after {flag}.");

            var next = args[i + 1];
            if (next.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Expected a value after {flag}, got '{next}' (which looks like another flag).");
            }
            return next;
        }
        return null;
    }

    /// <summary>Returns true iff <paramref name="flag"/> appears in <paramref name="args"/>.</summary>
    public static bool HasFlag(string[] args, string flag)
    {
        for (var i = 1; i < args.Length; i++)
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Load the config file (honouring <c>--config PATH</c>) and apply env
    /// overrides. Returns null and prints a helpful error if the file is
    /// missing.
    /// </summary>
    public static async Task<MCPTransferConfig?> TryLoadConfigAsync(string[] args, CancellationToken ct = default)
    {
        var path = GetFlagValue(args, "--config") ?? MCPTransferConfigFile.DefaultPath;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No config file at {path}.");
            Console.Error.WriteLine("Run 'mcptx config init' to bootstrap one.");
            return null;
        }
        try
        {
            return await MCPTransferConfigFile.LoadAsync(path, applyEnvOverrides: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // Malformed/incomplete config file, unsupported version, or a bad
            // MCPTX_* env override — present cleanly instead of a stack trace.
            Console.Error.WriteLine($"Config at {path} is invalid: {ex.Message}");
            Console.Error.WriteLine("Fix it by hand or re-run 'mcptx config init --force'.");
            return null;
        }
    }

    /// <summary>
    /// Load the local agent identity (honouring <c>--identity PATH</c>).
    /// Returns null and prints a helpful error if the file is missing.
    /// </summary>
    public static async Task<AgentIdentity?> TryLoadIdentityAsync(string[] args, CancellationToken ct = default)
    {
        var path = GetFlagValue(args, "--identity") ?? AgentIdentityFile.DefaultPath;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No identity file at {path}.");
            Console.Error.WriteLine("Run 'mcptx keygen' first.");
            return null;
        }
        return await AgentIdentityFile.LoadAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build an <see cref="IIpfsClient"/> from the IPFS section of the config.
    /// For Pinata mode the client is wrapped in <see cref="RetryingIpfsClient"/>
    /// with the default policy.
    /// </summary>
    public static IIpfsClient? TryBuildIpfsClient(MCPTransferConfig config, out string? error)
    {
        error = null;
        switch (config.Ipfs.Kind)
        {
            case IpfsConfigSection.KindPinata:
                if (string.IsNullOrEmpty(config.Ipfs.PinataJwt))
                {
                    error = "IPFS kind is 'pinata' but no JWT configured. Set PINATA_JWT env var "
                          + "or pass --pinata-jwt to 'mcptx config init'.";
                    return null;
                }
                var pinata = new PinataIpfsClient(
                    config.Ipfs.PinataJwt,
                    gatewayUrl: config.Ipfs.GatewayUrl);
                return new RetryingIpfsClient(pinata, RetryPolicy.Default);

            case IpfsConfigSection.KindMemory:
                Console.Error.WriteLine(
                    "Warning: IPFS kind is 'memory' — pins are in-process only and lost when "
                    + "this command exits. Useful for unit tests, not for CLI usage.");
                return new InMemoryIpfsClient();

            default:
                error = $"Unknown IPFS kind '{config.Ipfs.Kind}' (expected 'pinata' or 'memory').";
                return null;
        }
    }

    /// <summary>Build the on-chain client facade from the chain section of the config.</summary>
    public static EthereumChainClient BuildChainClient(MCPTransferConfig config)
        => new(config.Chain.ToCoreConfig());

    /// <summary>
    /// Print an error in a uniform format and return <see cref="ExitError"/>.
    /// </summary>
    public static int Fail(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return ExitError;
    }
}
