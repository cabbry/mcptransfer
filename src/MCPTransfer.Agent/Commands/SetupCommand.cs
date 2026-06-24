using MCPTransfer.Core.Configuration;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Storage;

namespace MCPTransfer.Agent.Commands;

/// <summary>
/// <c>mcptx setup</c> — one guided flow that turns a blank machine into a
/// working agent: generate an identity, pick a chain + storage backend, write
/// the config, and print (or merge-write) the Claude Desktop connector snippet.
/// Interactive on a TTY; fully flag-driven otherwise (<c>--local</c> needs zero
/// external accounts). Chains and storage backends come from
/// <see cref="SetupCatalog"/>, so new ones light up here automatically.
/// </summary>
internal static class SetupCommand
{
    public const string Usage =
        "  mcptx setup [--local] [--chain NAME] [--storage KIND] [--pinata-jwt JWT]\n"
      + "              [--ipfs-dir DIR] [--workspace DIR] [--write-claude-config]\n"
      + "              [--yes] [--force] [--identity PATH] [--config PATH]\n"
      + "      Guided onboarding: identity + config + Claude Desktop wiring in one go.\n"
      + "      --local : anvil + file store, no accounts/gas/Pinata (fastest first run).\n"
      + "      Interactive on a terminal; --yes / --local run non-interactively.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var local = Common.HasFlag(args, "--local");
        var assumeYes = Common.HasFlag(args, "--yes");
        var force = Common.HasFlag(args, "--force");
        var interactive = !local && !assumeYes && !Console.IsInputRedirected;

        Console.WriteLine("mcptx setup — let's get you ready to send & receive.");
        Console.WriteLine();

        // 1. Identity (never silently overwritten — --force required to replace).
        var idPath = Common.GetFlagValue(args, "--identity") ?? AgentIdentityFile.DefaultPath;
        var passphrase = Environment.GetEnvironmentVariable(AgentIdentityFile.PassphraseEnvVar);
        AgentIdentity identity;
        if (File.Exists(idPath) && !force)
        {
            try
            {
                identity = await AgentIdentityFile.LoadAsync(idPath, passphrase, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Common.Fail($"existing identity at {idPath} could not be loaded: {ex.Message}");
            }
            Console.WriteLine($"Using existing identity: {identity.Address}");
        }
        else
        {
            identity = AgentIdentity.Generate();
            await AgentIdentityFile.SaveAsync(identity, idPath, passphrase, kdfParams: null, ct).ConfigureAwait(false);
            Console.WriteLine($"New identity: {identity.Address}");
            Console.WriteLine(string.IsNullOrEmpty(passphrase)
                ? $"  at rest : PLAINTEXT (set {AgentIdentityFile.PassphraseEnvVar} before setup to encrypt)"
                : "  at rest : encrypted (Argon2id + AES-256-GCM)");
        }
        Console.WriteLine();

        // 2. Chain.
        var chain = ResolveChain(args, local, interactive);
        if (chain is null) return Common.ExitError;

        // 3. Storage backend.
        var (storageOpt, storageSection, storageErr) = ResolveStorage(args, local, interactive);
        if (storageOpt is null) return Common.Fail(storageErr!);

        // 4. Compose + write config.
        var cfgPath = Common.GetFlagValue(args, "--config") ?? MCPTransferConfigFile.DefaultPath;
        if (File.Exists(cfgPath) && !force)
        {
            if (!interactive)
                return Common.Fail($"config already exists at {cfgPath}; pass --force to overwrite.");
            if (!PromptYesNo($"Config already exists at {cfgPath}. Overwrite?", def: false))
                return Common.Fail("aborted (config left unchanged).");
        }
        var config = SetupCatalog.Compose(chain, storageSection!);
        await MCPTransferConfigFile.SaveAsync(config, cfgPath, ct).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine($"Config written to {cfgPath}");
        Console.WriteLine($"  chain   : {chain.Name} (id {config.Chain.ChainId}, {config.Chain.RpcUrl})");
        Console.WriteLine($"  storage : {storageOpt.Kind}{StorageDetail(storageSection!)}");

        // 5. Workspace (the folder send_file/receive_file are confined to);
        //    and pre-create the file store so the first transfer just works.
        var workspace = Common.GetFlagValue(args, "--workspace") ?? DefaultWorkspace();
        Directory.CreateDirectory(workspace);
        if (string.Equals(storageOpt.Kind, IpfsConfigSection.KindFile, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(storageSection!.Directory))
        {
            Directory.CreateDirectory(storageSection.Directory);
        }

        // 6. Claude Desktop wiring — always print, optionally write.
        await WireClaudeDesktopAsync(args, idPath, cfgPath, workspace, interactive, !string.IsNullOrEmpty(passphrase));

        // 7. Next steps.
        PrintNextSteps(chain);
        return Common.ExitSuccess;
    }

    private static SetupCatalog.ChainOption? ResolveChain(string[] args, bool local, bool interactive)
    {
        var flag = Common.GetFlagValue(args, "--chain") ?? (local ? "anvil-local" : null);
        if (flag is not null)
        {
            var found = SetupCatalog.FindChain(flag);
            if (found is null)
            {
                Common.Fail($"unknown --chain '{flag}'. Known: {string.Join(", ", SetupCatalog.Chains.Select(c => c.Name))}.");
                return null;
            }
            return found;
        }
        if (!interactive)
            return SetupCatalog.Chains[0]; // anvil-local default
        return ChooseFromList("Which chain?", SetupCatalog.Chains, c => c.Name, c => c.Summary, SetupCatalog.Chains[0]);
    }

    private static (SetupCatalog.StorageOption? Option, IpfsConfigSection? Section, string? Error) ResolveStorage(
        string[] args, bool local, bool interactive)
    {
        var flag = Common.GetFlagValue(args, "--storage") ?? (local ? IpfsConfigSection.KindFile : null);
        SetupCatalog.StorageOption? chosen;
        if (flag is not null)
        {
            chosen = SetupCatalog.FindStorage(flag);
            if (chosen is null)
                return (null, null, $"unknown --storage '{flag}'. Known: {string.Join(", ", SetupCatalog.Storages.Select(s => s.Kind))}.");
        }
        else if (!interactive)
        {
            chosen = SetupCatalog.Storages[0]; // file default
        }
        else
        {
            chosen = ChooseFromList("Where should encrypted files be stored?", SetupCatalog.Storages,
                s => s.Kind, s => s.Summary, SetupCatalog.Storages[0]);
        }

        string? jwt = null, dir = null;
        if (chosen.NeedsDirectory)
        {
            dir = Common.GetFlagValue(args, "--ipfs-dir")
                ?? (interactive ? PromptText("  Shared folder path", DefaultIpfsDir()) : DefaultIpfsDir());
        }
        if (chosen.NeedsCredential)
        {
            jwt = Common.GetFlagValue(args, "--pinata-jwt")
                ?? Environment.GetEnvironmentVariable("PINATA_JWT")
                ?? (interactive ? NullIfEmpty(PromptText("  Pinata JWT (blank to set later via PINATA_JWT)", null)) : null);
            if (string.IsNullOrEmpty(jwt))
                Console.WriteLine("  note: no Pinata JWT yet — set PINATA_JWT in the environment before sending.");
        }
        return (chosen, SetupCatalog.BuildStorage(chosen.Kind, jwt, dir), null);
    }

    private static string StorageDetail(IpfsConfigSection s) => s.Kind switch
    {
        IpfsConfigSection.KindFile => string.IsNullOrEmpty(s.Directory) ? string.Empty : $" ({s.Directory})",
        IpfsConfigSection.KindPinata => string.IsNullOrEmpty(s.PinataJwt) ? " (JWT via env)" : " (JWT set)",
        _ => string.Empty,
    };

    private static async Task WireClaudeDesktopAsync(
        string[] args, string idPath, string cfgPath, string workspace, bool interactive, bool encrypted)
    {
        var (command, invokeArgs) = ClaudeDesktopConfig.ServerInvocation(idPath, cfgPath);
        var env = new Dictionary<string, string> { ["MCPTX_MCP_ROOT"] = workspace };
        var snippet = ClaudeDesktopConfig.Snippet(command, invokeArgs, env);
        var targetPath = ClaudeDesktopConfig.ConfigPath();

        Console.WriteLine();
        Console.WriteLine("── Claude Desktop wiring ───────────────────────────────────");
        Console.WriteLine($"Config file: {targetPath}");
        Console.WriteLine("Add this under \"mcpServers\" (or use the .mcpb bundle's form):");
        Console.WriteLine();
        Console.WriteLine(snippet);
        if (encrypted)
            Console.WriteLine("(identity is encrypted — also add \"MCPTX_PASSPHRASE\" to the env block.)");
        Console.WriteLine();

        var write = Common.HasFlag(args, "--write-claude-config")
            || (interactive && PromptYesNo($"Write it into {targetPath} now?", def: false));
        if (!write)
        {
            Console.WriteLine("Not written — paste the snippet yourself, then restart Claude Desktop.");
            return;
        }
        try
        {
            ClaudeDesktopConfig.MergeWrite(targetPath, command, invokeArgs, env);
            Console.WriteLine($"Wrote the mcptransfer server into {targetPath}. Restart Claude Desktop to pick it up.");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write {targetPath}: {ex.Message}");
            Console.Error.WriteLine("Paste the snippet above manually instead.");
        }
        await Task.CompletedTask;
    }

    private static void PrintNextSteps(SetupCatalog.ChainOption chain)
    {
        Console.WriteLine();
        Console.WriteLine("── Next steps ──────────────────────────────────────────────");
        Console.WriteLine("  mcptx doctor          # verify everything is wired up");
        if (chain.NeedsGas)
            Console.WriteLine($"  fund your address     # {chain.FaucetHint}");
        Console.WriteLine("  mcptx register-key    # publish your key so others can send to you (gas)");
        Console.WriteLine("                        # (or share your pubkey out-of-band — no gas needed to RECEIVE)");
        Console.WriteLine("  mcptx claim <handle>  # optional friendly name (gas)");
    }

    // ── small interactive helpers ────────────────────────────────────────────

    private static T ChooseFromList<T>(
        string title, IReadOnlyList<T> options, Func<T, string> name, Func<T, string> summary, T def) where T : class
    {
        Console.WriteLine(title);
        for (var i = 0; i < options.Count; i++)
            Console.WriteLine($"  {i + 1}) {name(options[i])} — {summary(options[i])}");
        while (true)
        {
            Console.Write($"  choice [1-{options.Count}, default {name(def)}]: ");
            var line = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line)) return def;
            if (int.TryParse(line, out var idx) && idx >= 1 && idx <= options.Count) return options[idx - 1];
            var byName = options.FirstOrDefault(o => string.Equals(name(o), line, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
            Console.WriteLine("  (invalid choice — enter a number or name)");
        }
    }

    private static string PromptText(string label, string? def)
    {
        Console.Write(def is null ? $"{label}: " : $"{label} [{def}]: ");
        var line = Console.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? (def ?? string.Empty) : line.Trim();
    }

    private static bool PromptYesNo(string label, bool def)
    {
        Console.Write($"{label} ({(def ? "Y/n" : "y/N")}): ");
        var line = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(line)) return def;
        return line is "y" or "yes" or "o" or "oui";
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static string DefaultWorkspace() => Path.Combine(McptxHome(), "workspace");
    private static string DefaultIpfsDir() => Path.Combine(McptxHome(), "ipfs");
    private static string McptxHome()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcptx");
}
