using System.Reflection;
using MCPTransfer.Agent.Commands;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0)
        return PrintUsage();

    try
    {
        return args[0].ToLowerInvariant() switch
        {
            "keygen"        => await KeygenCommand.RunAsync(args),
            "whoami"        => await WhoamiCommand.RunAsync(args),
            "config"        => await ConfigCommand.RunAsync(args),
            "register-key"  => await RegisterKeyCommand.RunAsync(args),
            "claim"         => await ClaimCommand.RunAsync(args),
            "resolve"       => await ResolveCommand.RunAsync(args),
            "whois"         => await WhoisCommand.RunAsync(args),
            "send"          => await SendCommand.RunAsync(args),
            "inbox"         => await InboxCommand.RunAsync(args),
            "receive"       => await ReceiveCommand.RunAsync(args),
            "mcp-serve"     => await McpServeCommand.RunAsync(args),
            "version" or "--version" or "-v" => PrintVersion(),
            "help" or "--help" or "-h" => PrintUsage(),
            var other       => UnknownCommand(other),
        };
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Common.Fail(ex.Message);
    }
}

static int PrintUsage()
{
    Console.WriteLine("mcptx - MCPTransfer agent");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine(KeygenCommand.Usage);
    Console.WriteLine();
    Console.WriteLine(WhoamiCommand.Usage);
    Console.WriteLine();
    Console.WriteLine(ConfigCommand.Usage);
    Console.WriteLine();
    Console.WriteLine(RegisterKeyCommand.Usage);
    Console.WriteLine();
    Console.WriteLine(ClaimCommand.Usage);
    Console.WriteLine();
    Console.WriteLine(ResolveCommand.Usage);
    Console.WriteLine();
    Console.WriteLine(WhoisCommand.Usage);
    Console.WriteLine();
    Console.WriteLine(SendCommand.Usage);
    Console.WriteLine();
    Console.WriteLine(InboxCommand.Usage);
    Console.WriteLine();
    Console.WriteLine(ReceiveCommand.Usage);
    Console.WriteLine();
    Console.WriteLine(McpServeCommand.Usage);
    Console.WriteLine();
    Console.WriteLine("  mcptx version");
    Console.WriteLine("      Print version + crypto suite.");
    Console.WriteLine();
    Console.WriteLine("  mcptx help");
    Console.WriteLine("      Show this message.");
    return Common.ExitSuccess;
}

static int PrintVersion()
{
    var asm = System.Reflection.Assembly.GetExecutingAssembly();
    var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? asm.GetName().Version?.ToString()
        ?? "unknown";
    // Strip the build-metadata suffix (e.g. "+<git sha>") for a clean display.
    var plus = info.IndexOf('+');
    if (plus >= 0) info = info[..plus];

    Console.WriteLine($"mcptx {info}");
    Console.WriteLine($"  crypto suite : {MCPTransfer.Core.Crypto.HybridKem.SuiteIdentifier}");
    Console.WriteLine("  signatures   : ECDSA secp256k1 + ML-DSA-65 (hybrid)");
    Console.WriteLine($"  runtime      : .NET {Environment.Version}");
    return Common.ExitSuccess;
}

static int UnknownCommand(string verb)
{
    Console.Error.WriteLine($"Unknown command: '{verb}'.");
    Console.Error.WriteLine("Run 'mcptx help' for available commands.");
    return Common.ExitError;
}
