using MCPTransfer.Core.Configuration;

namespace MCPTransfer.Agent.Commands;

internal static class ConfigCommand
{
    public const string Usage =
        "  mcptx config init [--profile anvil-local|amoy] [--pinata-jwt JWT] [--out PATH] [--force]\n"
      + "      Bootstrap ~/.mcptx/config.json with a pre-filled profile.\n"
      + "      anvil-local : 127.0.0.1:8545 + deterministic deploy addresses (default).\n"
      + "      amoy        : Polygon Amoy RPC + empty addresses (fill after deploy).\n"
      + "\n"
      + "  mcptx config show\n"
      + "      Print the effective config (file values overlaid with env vars).";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Missing subcommand. Try: mcptx config init  or  mcptx config show");
            return Common.ExitError;
        }

        return args[1].ToLowerInvariant() switch
        {
            "init" => await InitAsync(args, ct).ConfigureAwait(false),
            "show" => await ShowAsync(args, ct).ConfigureAwait(false),
            var other => Common.Fail($"unknown 'config' subcommand '{other}'."),
        };
    }

    private static async Task<int> InitAsync(string[] args, CancellationToken ct)
    {
        var profileName = (Common.GetFlagValue(args, "--profile") ?? "anvil-local").ToLowerInvariant();
        var jwt = Common.GetFlagValue(args, "--pinata-jwt");
        var outPath = Common.GetFlagValue(args, "--out") ?? MCPTransferConfigFile.DefaultPath;
        var force = Common.HasFlag(args, "--force");

        if (File.Exists(outPath) && !force)
        {
            Console.Error.WriteLine($"Config already exists at {outPath}.");
            Console.Error.WriteLine("Pass --force to overwrite.");
            return Common.ExitError;
        }

        MCPTransferConfig profile = profileName switch
        {
            "anvil-local" or "anvil" or "local" => DefaultProfiles.AnvilLocal(jwt),
            "amoy" or "polygon-amoy"            => DefaultProfiles.Amoy(jwt),
            _ => throw new ArgumentException(
                $"unknown profile '{profileName}'. Use 'anvil-local' or 'amoy'."),
        };

        await MCPTransferConfigFile.SaveAsync(profile, outPath, ct).ConfigureAwait(false);

        Console.WriteLine($"Config written to {outPath}");
        Console.WriteLine($"  Profile         : {profileName}");
        Console.WriteLine($"  RPC URL         : {profile.Chain.RpcUrl}");
        Console.WriteLine($"  Chain ID        : {profile.Chain.ChainId}");
        Console.WriteLine($"  FileRegistry    : {DisplayAddress(profile.Chain.FileRegistryAddress)}");
        Console.WriteLine($"  KeyRegistry     : {DisplayAddress(profile.Chain.KeyRegistryAddress)}");
        Console.WriteLine($"  AgentDirectory  : {DisplayAddress(profile.Chain.AgentDirectoryAddress)}");
        Console.WriteLine($"  IPFS kind       : {profile.Ipfs.Kind}");
        if (profile.Ipfs.Kind == IpfsConfigSection.KindPinata)
        {
            Console.WriteLine(profile.Ipfs.PinataJwt is null
                ? "  PINATA_JWT      : (not set; provide via env var at runtime)"
                : "  PINATA_JWT      : (stored in file — protect 0600)");
        }
        return Common.ExitSuccess;
    }

    private static async Task<int> ShowAsync(string[] args, CancellationToken ct)
    {
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        Console.WriteLine($"Chain RPC       : {config.Chain.RpcUrl}");
        Console.WriteLine($"Chain ID        : {config.Chain.ChainId}");
        Console.WriteLine($"FileRegistry    : {DisplayAddress(config.Chain.FileRegistryAddress)}");
        Console.WriteLine($"KeyRegistry     : {DisplayAddress(config.Chain.KeyRegistryAddress)}");
        Console.WriteLine($"AgentDirectory  : {DisplayAddress(config.Chain.AgentDirectoryAddress)}");
        Console.WriteLine($"IPFS kind       : {config.Ipfs.Kind}");
        Console.WriteLine($"IPFS gateway    : {config.Ipfs.GatewayUrl ?? "(default)"}");
        Console.WriteLine($"PINATA_JWT set  : {(string.IsNullOrEmpty(config.Ipfs.PinataJwt) ? "no" : "yes")}");
        return Common.ExitSuccess;
    }

    private static string DisplayAddress(string addr)
        => string.IsNullOrEmpty(addr) ? "(empty — fill after deploy)" : addr;
}
