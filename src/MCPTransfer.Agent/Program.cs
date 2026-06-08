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
            "help" or "--help" or "-h" => PrintUsage(),
            var other       => UnknownCommand(other),
        };
    }
    catch (ArgumentException ex)
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
    Console.WriteLine("  mcptx help");
    Console.WriteLine("      Show this message.");
    return Common.ExitSuccess;
}

static int UnknownCommand(string verb)
{
    Console.Error.WriteLine($"Unknown command: '{verb}'.");
    Console.Error.WriteLine("Run 'mcptx help' for available commands.");
    return Common.ExitError;
}
