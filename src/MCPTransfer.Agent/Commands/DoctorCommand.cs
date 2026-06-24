using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Configuration;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Storage;

namespace MCPTransfer.Agent.Commands;

/// <summary>
/// <c>mcptx doctor</c> — diagnose the local setup end to end and print an
/// actionable checklist (each blocker comes with the command that fixes it).
/// Read-only: it never changes config, keys, or chain state.
/// </summary>
internal static class DoctorCommand
{
    public const string Usage =
        "  mcptx doctor [--identity PATH] [--config PATH]\n"
      + "      Check your setup (identity, config, RPC, gas, storage, key\n"
      + "      registration, Claude Desktop wiring) and print what to fix.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        Console.WriteLine("mcptx doctor — setup diagnostics");
        Console.WriteLine();

        var checks = new List<Check>();

        var (identity, idCheck) = await CheckIdentityAsync(args, ct).ConfigureAwait(false);
        checks.Add(idCheck);

        var (config, cfgCheck) = await CheckConfigAsync(args, ct).ConfigureAwait(false);
        checks.Add(cfgCheck);

        if (config is not null)
            checks.Add(DoctorChecks.Storage(config.Ipfs));

        if (identity is not null && config is not null)
            await AddChainChecksAsync(checks, identity, config, ct).ConfigureAwait(false);

        checks.Add(CheckClaudeWiring());

        foreach (var c in checks)
            PrintCheck(c);

        Console.WriteLine();
        var fails = checks.Count(c => c.Status == CheckStatus.Fail);
        var warns = checks.Count(c => c.Status == CheckStatus.Warn);
        Console.WriteLine(fails == 0
            ? (warns == 0 ? "All good — you're ready to send and receive." : $"{warns} warning(s), no blockers.")
            : $"{fails} blocker(s), {warns} warning(s). Fix the [FAIL] items above (try 'mcptx setup').");
        return fails == 0 ? Common.ExitSuccess : Common.ExitError;
    }

    private static async Task<(AgentIdentity?, Check)> CheckIdentityAsync(string[] args, CancellationToken ct)
    {
        var path = Common.GetFlagValue(args, "--identity") ?? AgentIdentityFile.DefaultPath;
        if (!File.Exists(path))
            return (null, new(CheckStatus.Fail, $"Identity: missing ({path})", null,
                "Run 'mcptx setup' (guided) or 'mcptx keygen'."));
        try
        {
            var passphrase = Environment.GetEnvironmentVariable(AgentIdentityFile.PassphraseEnvVar);
            var identity = await AgentIdentityFile.LoadAsync(path, passphrase, ct).ConfigureAwait(false);
            return (identity, new(CheckStatus.Ok, $"Identity: {identity.Address}"));
        }
        catch (InvalidOperationException ex)
        {
            return (null, new(CheckStatus.Fail, "Identity: present but unreadable", ex.Message,
                $"Wrong/missing {AgentIdentityFile.PassphraseEnvVar}? Or the file is corrupt."));
        }
    }

    private static async Task<(MCPTransferConfig?, Check)> CheckConfigAsync(string[] args, CancellationToken ct)
    {
        var path = Common.GetFlagValue(args, "--config") ?? MCPTransferConfigFile.DefaultPath;
        if (!File.Exists(path))
            return (null, new(CheckStatus.Fail, $"Config: missing ({path})", null,
                "Run 'mcptx setup' (guided) or 'mcptx config init'."));
        try
        {
            var config = await MCPTransferConfigFile.LoadAsync(path, applyEnvOverrides: true, ct).ConfigureAwait(false);
            return (config, new(CheckStatus.Ok, $"Config: {path}",
                $"chain {config.Chain.ChainId} @ {config.Chain.RpcUrl}"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return (null, new(CheckStatus.Fail, "Config: invalid", ex.Message, "Fix it or re-run 'mcptx setup'."));
        }
    }

    private static async Task AddChainChecksAsync(
        List<Check> checks, AgentIdentity identity, MCPTransferConfig config, CancellationToken ct)
    {
        EthereumChainClient chain;
        try
        {
            chain = Common.BuildChainClient(config);
        }
        catch (InvalidOperationException ex)
        {
            // ToCoreConfig throws when contract addresses are unset (amoy profile
            // shipped blank, never filled).
            checks.Add(new(CheckStatus.Fail, "Chain: contract addresses not set", ex.Message,
                "Deploy + fill the addresses, or use the 'anvil-local' profile (mcptx setup)."));
            return;
        }

        ulong head;
        try
        {
            head = await chain.FileRegistry.GetLatestBlockNumberAsync(ct).ConfigureAwait(false);
            checks.Add(new(CheckStatus.Ok, "Chain RPC reachable", $"head block {head}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add(new(CheckStatus.Fail, "Chain RPC unreachable", ex.Message,
                "Check chain.rpc_url / your network. Local profile: is `anvil` running?"));
            return; // the remaining checks all need a live RPC
        }

        try
        {
            var wei = await chain.GetNativeBalanceAsync(identity.Address, ct).ConfigureAwait(false);
            if (wei.IsZero)
            {
                var faucet = SetupCatalog.FindChainByChainId(config.Chain.ChainId)?.FaucetHint
                    ?? "Fund this address with the chain's native test token.";
                checks.Add(new(CheckStatus.Warn, "Gas balance: 0",
                    "You can RECEIVE files, but can't pay gas to register-key / claim / send.", faucet));
            }
            else
            {
                checks.Add(new(CheckStatus.Ok, $"Gas balance: {DoctorChecks.FormatNativeBalance(wei)} (native token)"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add(new(CheckStatus.Warn, "Gas balance: could not read", ex.Message));
        }

        try
        {
            var entry = await chain.KeyRegistry.GetAsync(identity.Address, ct).ConfigureAwait(false);
            checks.Add(entry.IsRegistered
                ? new(CheckStatus.Ok, "Key registered on-chain", "others can send to you via the registry")
                : new(CheckStatus.Warn, "Key NOT registered on-chain",
                    "others can't look you up to send to you",
                    "Run 'mcptx register-key' (gas), or share your public key out-of-band."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add(new(CheckStatus.Warn, "Key registration: could not read", ex.Message));
        }

        try
        {
            var handle = await chain.AgentDirectory.ReverseResolveAsync(identity.Address, ct).ConfigureAwait(false);
            checks.Add(new(CheckStatus.Info, handle is null ? "Handle: none (optional)" : $"Handle: {handle}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Handle lookup is purely informational — don't let it add noise.
        }
    }

    private static Check CheckClaudeWiring()
    {
        var path = ClaudeDesktopConfig.ConfigPath();
        try
        {
            if (File.Exists(path) && ClaudeDesktopConfig.IsConfigured(File.ReadAllText(path)))
                return new(CheckStatus.Ok, "Claude Desktop: mcptransfer server configured", path);
        }
        catch (IOException)
        {
            // Unreadable file — treat as not configured.
        }
        return new(CheckStatus.Info, "Claude Desktop: mcptransfer not detected", path,
            "Run 'mcptx setup' to print (or --write) the connector snippet.");
    }

    private static void PrintCheck(Check c)
    {
        var tag = c.Status switch
        {
            CheckStatus.Ok => "[ OK ]",
            CheckStatus.Warn => "[WARN]",
            CheckStatus.Fail => "[FAIL]",
            _ => "[INFO]",
        };
        Console.WriteLine($"  {tag}  {c.Label}");
        if (!string.IsNullOrEmpty(c.Detail))
            Console.WriteLine($"          {c.Detail}");
        if (!string.IsNullOrEmpty(c.Fix))
            Console.WriteLine($"          -> {c.Fix}");
    }
}
