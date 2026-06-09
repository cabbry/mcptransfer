using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Envelope;

namespace MCPTransfer.Agent.Commands;

internal static class ReceiveCommand
{
    /// <summary>How many recent blocks to scan when auto-corroborating a CID.</summary>
    private const ulong DefaultCorroborationLookback = 50_000;

    public const string Usage =
        "  mcptx receive <cid> --out PATH [--expect-hash 0x...] [--no-verify-onchain] [--since BLOCK]\n"
      + "      Fetch the SignedManifest at <cid>, verify its signature + tags, then\n"
      + "      atomically decrypt to <out>.\n"
      + "      By default the CID is corroborated against the on-chain FileSent event\n"
      + "      addressed to you: its content hash must match and the sender is shown\n"
      + "      (reverse-resolved to a handle). --expect-hash pins the hash manually;\n"
      + "      --no-verify-onchain skips the chain lookup (signature-only).";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Common.Fail("missing required positional <cid>. Example: mcptx receive QmXyz... --out received.bin");

        var cid = args[1];

        var outPath = Common.GetFlagValue(args, "--out");
        if (string.IsNullOrEmpty(outPath))
            return Common.Fail("missing --out PATH");

        var noVerifyOnchain = Common.HasFlag(args, "--no-verify-onchain");

        var expectHashHex = Common.GetFlagValue(args, "--expect-hash");
        byte[]? expectedHash = null;
        if (!string.IsNullOrEmpty(expectHashHex))
        {
            var span = expectHashHex.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
            try { expectedHash = Convert.FromHexString(span); }
            catch (FormatException) { return Common.Fail("--expect-hash must be a hex string (optionally 0x-prefixed)."); }
            if (expectedHash.Length != Hashes.Keccak256ByteLength)
                return Common.Fail(
                    $"--expect-hash must be {Hashes.Keccak256ByteLength} bytes "
                    + $"(a Keccak-256 hash); got {expectedHash.Length}.");
        }

        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var ipfs = Common.TryBuildIpfsClient(config, out var ipfsErr);
        if (ipfs is null)
            return Common.Fail(ipfsErr ?? "could not build IPFS client.");

        // Auto-corroborate against the on-chain FileSent event unless the user
        // pinned a hash manually or opted out.
        string? corroboratedSenderHandle = null;
        var corroborated = false;
        if (expectedHash is null && !noVerifyOnchain)
        {
            try
            {
                var chain = Common.BuildChainClient(config);
                var latest = await chain.FileRegistry.GetLatestBlockNumberAsync(ct).ConfigureAwait(false);
                var sinceArg = Common.GetFlagValue(args, "--since");
                var fromBlock = ulong.TryParse(sinceArg, out var s)
                    ? s
                    : (latest > DefaultCorroborationLookback ? latest - DefaultCorroborationLookback : 0UL);

                // Falls back to a ~450-block scan when the RPC caps eth_getLogs.
                var ev = await chain.FileRegistry
                    .FindByCidWithFallbackAsync(identity.Address, cid, fromBlock, latest, ct).ConfigureAwait(false);
                if (ev is not null)
                {
                    expectedHash = ev.ContentHash;
                    corroborated = true;
                    corroboratedSenderHandle = await chain.AgentDirectory
                        .ReverseResolveAsync(ev.From, ct).ConfigureAwait(false);
                }
                else
                {
                    Console.Error.WriteLine(
                        $"  note: no on-chain FileSent event for this CID addressed to you in blocks "
                        + $"{fromBlock}..{latest}; proceeding on the manifest signature alone. "
                        + "Use --since to widen the scan, or --no-verify-onchain to silence this.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"  note: on-chain corroboration unavailable ({ex.GetType().Name}: {ex.Message}); "
                    + "proceeding on the manifest signature alone.");
            }
        }

        try
        {
            var reader = new EnvelopeReader(ipfs);

            Console.WriteLine($"Fetching {cid}");
            Console.WriteLine($"  output: {outPath}");

            var result = await reader.ReceiveToFileAsync(cid, identity, outPath, expectedHash, ct).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("✓ decrypted");
            if (corroborated)
                Console.WriteLine("  ✓ corroborated against on-chain FileSent (content hash matched)");
            else if (expectedHash is not null)
                Console.WriteLine("  ✓ matched the provided --expect-hash");
            var senderLine = corroboratedSenderHandle is not null
                ? $"{result.Manifest.Sender} ({corroboratedSenderHandle})"
                : result.Manifest.Sender.ToString();
            Console.WriteLine($"  from         : {senderLine}");
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
