using MCPTransfer.Core.Envelope;

namespace MCPTransfer.Agent.Commands;

internal static class ReceiveCommand
{
    public const string Usage =
        "  mcptx receive <cid> --out PATH [--expect-hash 0x...] [--identity PATH] [--config PATH]\n"
      + "      Fetch the SignedManifest at <cid> from IPFS, verify its signature\n"
      + "      and tags, then atomically decrypt to <out>.\n"
      + "      --expect-hash : the content hash from the FileSent event ('mcptx inbox'\n"
      + "                      prints it). When given, the fetched manifest must match it\n"
      + "                      or the receive is refused (ties bytes to the on-chain record).";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Common.Fail("missing required positional <cid>. Example: mcptx receive QmXyz... --out received.bin");

        var cid = args[1];

        var outPath = Common.GetFlagValue(args, "--out");
        if (string.IsNullOrEmpty(outPath))
            return Common.Fail("missing --out PATH");

        var expectHashHex = Common.GetFlagValue(args, "--expect-hash");
        byte[]? expectedHash = null;
        if (!string.IsNullOrEmpty(expectHashHex))
        {
            var span = expectHashHex.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
            try { expectedHash = Convert.FromHexString(span); }
            catch (FormatException) { return Common.Fail("--expect-hash must be a hex string (optionally 0x-prefixed)."); }
        }

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
            if (expectedHash is null)
            {
                Console.Error.WriteLine(
                    "  note: no --expect-hash given; authenticity rests on the manifest signature "
                    + "alone, NOT corroborated against the on-chain FileSent content hash. "
                    + "Run 'mcptx inbox' to get the expected hash.");
            }

            var result = await reader.ReceiveToFileAsync(cid, identity, outPath, expectedHash, ct).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("✓ decrypted");
            if (expectedHash is not null)
                Console.WriteLine("  ✓ matched on-chain content hash");
            Console.WriteLine($"  from         : {result.Manifest.Sender}");
            Console.WriteLine($"  filename     : {result.Manifest.Filename ?? "(unset)"}");
            Console.WriteLine($"  mime         : {result.Manifest.MimeType ?? "(unset)"}");
            Console.WriteLine($"  total size   : {result.PlaintextBytesWritten} bytes");
            Console.WriteLine($"  chunks       : {result.Manifest.Chunks.Count}");
            Console.WriteLine($"  created at   : {DateTimeOffset.FromUnixTimeSeconds(result.Manifest.CreatedAtUnixSeconds):u}");
            return Common.ExitSuccess;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("content hash", StringComparison.OrdinalIgnoreCase))
        {
            return Common.Fail($"content-hash check failed: {ex.Message}");
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
