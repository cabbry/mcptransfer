using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Storage;

namespace MCPTransfer.Agent.Commands;

internal static class KeygenCommand
{
    public const string Usage =
        "  mcptx keygen [--out PATH] [--force]\n"
      + "      Generate a new hybrid (secp256k1 + ML-KEM-768) identity.\n"
      + "      Default path: ~/.mcptx/identity.json";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var path = Common.GetFlagValue(args, "--out") ?? AgentIdentityFile.DefaultPath;
        var force = Common.HasFlag(args, "--force");

        if (File.Exists(path) && !force)
        {
            Console.Error.WriteLine($"Identity already exists at {path}.");
            Console.Error.WriteLine("Pass --force to overwrite (irreversible: the previous keys will be lost).");
            return Common.ExitError;
        }

        var identity = AgentIdentity.Generate();
        await AgentIdentityFile.SaveAsync(identity, path, ct).ConfigureAwait(false);

        Console.WriteLine($"Identity written to {path}");
        Console.WriteLine($"Address: {identity.Address}");
        return Common.ExitSuccess;
    }
}
