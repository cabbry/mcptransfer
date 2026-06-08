using MCPTransfer.Core.Chain;

namespace MCPTransfer.Agent.Commands;

internal static class ResolveCommand
{
    public const string Usage =
        "  mcptx resolve <handle> [--config PATH]\n"
      + "      Look up the Ethereum address claiming <handle>.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Common.Fail("missing required positional <handle>. Example: mcptx resolve alice-ai");

        var handle = args[1];
        if (!HandleValidation.IsValid(handle))
            return Common.Fail($"'{handle}' is not a valid handle.");

        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;
        var chain = Common.BuildChainClient(config);

        var resolved = await chain.AgentDirectory.ResolveAsync(handle, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            Console.WriteLine($"'{handle}' is not claimed.");
            return Common.ExitSuccess;
        }
        Console.WriteLine(resolved.ToString());
        return Common.ExitSuccess;
    }
}
