using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Agent.Commands;

internal static class WhoamiCommand
{
    public const string Usage =
        "  mcptx whoami [--in PATH] [--full]\n"
      + "      Print the Ethereum address and public keys of the local identity.\n"
      + "      The ML-KEM public key is truncated by default; pass --full to dump.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var identityPathOverride = Common.GetFlagValue(args, "--in");
        var argsForLoader = identityPathOverride is null
            ? args
            : args.Select(a => a == "--in" ? "--identity" : a).ToArray();
        var identity = await Common.TryLoadIdentityAsync(argsForLoader, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;

        var full = Common.HasFlag(args, "--full");

        var mlkemBytes = identity.MlKem.PublicKey.Bytes;
        var mlkemB64 = Convert.ToBase64String(mlkemBytes);
        var mlkemFp = HexFormat.Fingerprint(mlkemBytes);

        var mldsaBytes = identity.MlDsa.PublicKeyEncoded;
        var mldsaFp = HexFormat.Fingerprint(mldsaBytes);

        Console.WriteLine($"Address                : {identity.Address}");
        Console.WriteLine($"secp256k1 public key   : {HexFormat.ToHex0x(identity.Secp256k1.PublicKeyCompressed)}");
        if (full)
        {
            Console.WriteLine($"ML-KEM-768 public key  : {mlkemB64}");
            Console.WriteLine($"  ({mlkemBytes.Length} bytes)");
        }
        else
        {
            Console.WriteLine($"ML-KEM-768 public key  : {mlkemB64[..20]}...{mlkemB64[^8..]}");
            Console.WriteLine($"  ({mlkemBytes.Length} bytes, {mlkemFp}, use --full to print)");
        }
        Console.WriteLine($"ML-DSA-65 signing key  : {mldsaBytes.Length} bytes, {mldsaFp}");

        return Common.ExitSuccess;
    }
}
