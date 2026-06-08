using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MCPTransfer.Core.Configuration;

/// <summary>
/// On-disk format and helpers for <see cref="MCPTransferConfig"/>.
/// Atomic write (<c>.tmp</c> + rename) and POSIX <c>0600</c> permissions
/// for symmetry with <see cref="MCPTransfer.Core.Storage.AgentIdentityFile"/>.
/// </summary>
public static class MCPTransferConfigFile
{
    /// <summary><c>~/.mcptx/config.json</c> (cross-platform user profile).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".mcptx",
        "config.json");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Load the config file at <paramref name="path"/>, then layer
    /// environment-variable overrides on top.
    /// </summary>
    public static async Task<MCPTransferConfig> LoadAsync(
        string path,
        bool applyEnvOverrides = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var config = Deserialize(bytes);
        return applyEnvOverrides ? ApplyEnvOverrides(config) : config;
    }

    /// <summary>
    /// Persist <paramref name="config"/> atomically: writes to
    /// <paramref name="path"/><c>.tmp</c> then renames into place.
    /// On POSIX the final file is mode <c>0600</c>.
    /// </summary>
    public static async Task SaveAsync(
        MCPTransferConfig config,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var bytes = Serialize(config);
        var tempPath = path + ".tmp";

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            TryRestrictUnixPermissions(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* best effort */ }
            throw;
        }
    }

    public static byte[] Serialize(MCPTransferConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return JsonSerializer.SerializeToUtf8Bytes(config, WriteOptions);
    }

    public static MCPTransferConfig Deserialize(ReadOnlySpan<byte> bytes)
    {
        MCPTransferConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<MCPTransferConfig>(bytes, ReadOptions);
        }
        catch (JsonException ex)
        {
            // A present-but-incomplete file (e.g. missing the required `chain`
            // or `ipfs` section) surfaces here as a JsonException; re-wrap it
            // as a domain error the CLI can present cleanly.
            throw new InvalidOperationException(
                $"Config JSON is malformed or missing required fields: {ex.Message}", ex);
        }
        if (config is null)
            throw new InvalidOperationException("Empty or null config payload.");
        if (config.Version != MCPTransferConfig.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported config version: got {config.Version}, expected {MCPTransferConfig.CurrentVersion}.");
        }
        return config;
    }

    /// <summary>
    /// Return a copy of <paramref name="config"/> with environment variables
    /// applied as overrides for individual fields. Variables that are unset
    /// or empty leave the field untouched.
    /// </summary>
    /// <remarks>
    /// Recognized variables:
    /// <list type="bullet">
    /// <item><c>MCPTX_RPC_URL</c></item>
    /// <item><c>MCPTX_CHAIN_ID</c></item>
    /// <item><c>MCPTX_FILE_REGISTRY</c></item>
    /// <item><c>MCPTX_KEY_REGISTRY</c></item>
    /// <item><c>MCPTX_AGENT_DIRECTORY</c></item>
    /// <item><c>MCPTX_IPFS_KIND</c></item>
    /// <item><c>MCPTX_GATEWAY_URL</c></item>
    /// <item><c>PINATA_JWT</c></item>
    /// </list>
    /// </remarks>
    public static MCPTransferConfig ApplyEnvOverrides(MCPTransferConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var chain = config.Chain with
        {
            RpcUrl = EnvOrDefault("MCPTX_RPC_URL", config.Chain.RpcUrl),
            ChainId = EnvLongOrDefault("MCPTX_CHAIN_ID", config.Chain.ChainId),
            FileRegistryAddress = EnvOrDefault("MCPTX_FILE_REGISTRY", config.Chain.FileRegistryAddress),
            KeyRegistryAddress = EnvOrDefault("MCPTX_KEY_REGISTRY", config.Chain.KeyRegistryAddress),
            AgentDirectoryAddress = EnvOrDefault("MCPTX_AGENT_DIRECTORY", config.Chain.AgentDirectoryAddress),
        };

        var ipfs = config.Ipfs with
        {
            Kind = EnvOrDefault("MCPTX_IPFS_KIND", config.Ipfs.Kind),
            PinataJwt = EnvOrDefault("PINATA_JWT", config.Ipfs.PinataJwt ?? string.Empty) is { Length: > 0 } v
                ? v
                : config.Ipfs.PinataJwt,
            GatewayUrl = EnvOrDefault("MCPTX_GATEWAY_URL", config.Ipfs.GatewayUrl ?? string.Empty) is { Length: > 0 } g
                ? g
                : config.Ipfs.GatewayUrl,
            Directory = EnvOrDefault("MCPTX_IPFS_DIR", config.Ipfs.Directory ?? string.Empty) is { Length: > 0 } d
                ? d
                : config.Ipfs.Directory,
        };

        return config with { Chain = chain, Ipfs = ipfs };
    }

    private static string EnvOrDefault(string name, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? fallback : v;
    }

    private static long EnvLongOrDefault(string name, long fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v))
            return fallback;

        // Parse machine-oriented integers invariantly; do NOT silently fall
        // back when the variable is SET but unparseable — a botched chain-id
        // override (e.g. "0x13882" or a stray space) must fail loud, because
        // it is security-relevant (EIP-155 replay protection).
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        throw new InvalidOperationException(
            $"Environment variable {name}='{v}' is not a valid integer. "
            + "Use a plain decimal value (e.g. 80002 for Polygon Amoy).");
    }

    private static void TryRestrictUnixPermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort: some filesystems don't support POSIX modes.
        }
    }
}
