using MCPTransfer.Core.Envelope;

namespace MCPTransfer.Agent.Commands;

internal static class ReceiveCommand
{
    public const string Usage =
        "  mcptx receive <cid> --out PATH [--identity PATH] [--config PATH]\n"
      + "      Fetch the SignedManifest at <cid> from IPFS, verify its signature\n"
      + "      and tags, then atomically decrypt to <out>.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Common.Fail("missing required positional <cid>. Example: mcptx receive QmXyz... --out received.bin");

        var cid = args[1];

        var outPath = Common.GetFlagValue(args, "--out");
        if (string.IsNullOrEmpty(outPath))
            return Common.Fail("missing --out PATH");

        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var ipfs = Common.TryBuildIpfsClient(config, out var ipfsErr);
        if (ipfs is null)
            return Common.Fail(ipfsErr ?? "could not build IPFS client.");

        try
        {
            var reader = new EnvelopeReader(ipfs);

            Console.WriteLine($"Fetching {cid}");
            Console.WriteLine($"  output: {outPath}");

            var result = await reader.ReceiveToFileAsync(cid, identity, outPath, ct).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("✓ decrypted");
            Console.WriteLine($"  from         : {result.Manifest.Sender}");
            Console.WriteLine($"  filename     : {result.Manifest.Filename ?? "(unset)"}");
            Console.WriteLine($"  mime         : {result.Manifest.MimeType ?? "(unset)"}");
            Console.WriteLine($"  total size   : {result.PlaintextBytesWritten} bytes");
            Console.WriteLine($"  chunks       : {result.Manifest.Chunks.Count}");
            Console.WriteLine($"  created at   : {DateTimeOffset.FromUnixTimeSeconds(result.Manifest.CreatedAtUnixSeconds):u}");
            return Common.ExitSuccess;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("signature", StringComparison.OrdinalIgnoreCase))
        {
            return Common.Fail($"signature verification failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Common.Fail($"receive failed: {ex.GetType().Name} — {ex.Message}");
        }
        finally
        {
            if (ipfs is IDisposable d) d.Dispose();
        }
    }
}
