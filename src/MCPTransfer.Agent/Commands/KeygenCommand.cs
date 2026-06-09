using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Storage;

namespace MCPTransfer.Agent.Commands;

internal static class KeygenCommand
{
    public const string Usage =
        "  mcptx keygen [--out PATH] [--force]\n"
      + "      Generate a new hybrid (secp256k1 + ML-KEM-768 + ML-DSA-65) identity.\n"
      + "      Default path: ~/.mcptx/identity.json\n"
      + "      If MCPTX_PASSPHRASE is set, the file is encrypted at rest\n"
      + "      (Argon2id + AES-256-GCM); the same variable is read back on load.";

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

        var passphrase = Environment.GetEnvironmentVariable(AgentIdentityFile.PassphraseEnvVar);
        var identity = AgentIdentity.Generate();
        await AgentIdentityFile.SaveAsync(identity, path, passphrase, kdfParams: null, ct).ConfigureAwait(false);

        Console.WriteLine($"Identity written to {path}");
        Console.WriteLine(string.IsNullOrEmpty(passphrase)
            ? $"  at rest : PLAINTEXT (set {AgentIdentityFile.PassphraseEnvVar} before keygen to encrypt)"
            : "  at rest : encrypted (Argon2id + AES-256-GCM)");
        Console.WriteLine($"Address: {identity.Address}");
        return Common.ExitSuccess;
    }
}
