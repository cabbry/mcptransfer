using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Storage;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0)
        return PrintUsage();

    return args[0].ToLowerInvariant() switch
    {
        "keygen" => await KeygenAsync(args),
        "whoami" => await WhoamiAsync(args),
        "help" or "--help" or "-h" => PrintUsage(),
        var other => UnknownCommand(other),
    };
}

static async Task<int> KeygenAsync(string[] args)
{
    var path = GetFlagValue(args, "--out") ?? AgentIdentityFile.DefaultPath;
    var force = HasFlag(args, "--force");

    if (File.Exists(path) && !force)
    {
        Console.Error.WriteLine($"Identity already exists at {path}.");
        Console.Error.WriteLine("Pass --force to overwrite (irreversible: the previous keys will be lost).");
        return 1;
    }

    var identity = AgentIdentity.Generate();
    await AgentIdentityFile.SaveAsync(identity, path);

    Console.WriteLine($"Identity written to {path}");
    Console.WriteLine($"Address: {identity.Address}");
    return 0;
}

static async Task<int> WhoamiAsync(string[] args)
{
    var path = GetFlagValue(args, "--in") ?? AgentIdentityFile.DefaultPath;

    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"No identity file at {path}.");
        Console.Error.WriteLine("Run 'mcptx keygen' first.");
        return 1;
    }

    var identity = await AgentIdentityFile.LoadAsync(path);
    Console.WriteLine($"Address               : {identity.Address}");
    Console.WriteLine($"secp256k1 public key  : 0x{Convert.ToHexString(identity.Secp256k1.PublicKeyCompressed).ToLowerInvariant()}");
    Console.WriteLine($"ML-KEM-768 public key : {Convert.ToBase64String(identity.MlKem.PublicKey.Bytes)}");
    Console.WriteLine($"  (length: {identity.MlKem.PublicKey.Bytes.Length} bytes)");
    return 0;
}

static int PrintUsage()
{
    Console.WriteLine("mcptx - MCPTransfer agent");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  mcptx keygen [--out PATH] [--force]");
    Console.WriteLine("      Generate a new hybrid (secp256k1 + ML-KEM-768) identity.");
    Console.WriteLine("      Default path: ~/.mcptx/identity.json");
    Console.WriteLine();
    Console.WriteLine("  mcptx whoami [--in PATH]");
    Console.WriteLine("      Print the Ethereum address and public keys of the local identity.");
    Console.WriteLine();
    Console.WriteLine("  mcptx help");
    Console.WriteLine("      Show this message.");
    return 0;
}

static int UnknownCommand(string verb)
{
    Console.Error.WriteLine($"Unknown command: '{verb}'.");
    Console.Error.WriteLine("Run 'mcptx help' for available commands.");
    return 1;
}

static string? GetFlagValue(string[] args, string flag)
{
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], flag, StringComparison.Ordinal))
            return args[i + 1];
    }
    return null;
}

static bool HasFlag(string[] args, string flag)
{
    for (var i = 1; i < args.Length; i++)
    {
        if (string.Equals(args[i], flag, StringComparison.Ordinal))
            return true;
    }
    return false;
}
