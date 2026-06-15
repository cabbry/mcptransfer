using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Envelope;

namespace MCPTransfer.Agent.Commands;

internal static class SendCommand
{
    public const string Usage =
        "  mcptx send <file> --to <handle|0xaddress> [--mime TYPE]\n"
      + "             [--identity PATH] [--config PATH]\n"
      + "      Encrypt <file> end-to-end for the recipient, pin chunks + manifest\n"
      + "      on IPFS, then emit a FileSent event on chain.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Common.Fail("missing required positional <file>. Example: mcptx send report.pdf --to alice-ai");

        var filePath = args[1];
        if (!File.Exists(filePath))
            return Common.Fail($"file not found: {filePath}");

        var toArg = Common.GetFlagValue(args, "--to");
        if (string.IsNullOrEmpty(toArg))
            return Common.Fail("missing --to <handle|0xaddress>");

        var mime = Common.GetFlagValue(args, "--mime") ?? "application/octet-stream";

        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var chain = Common.BuildChainClient(config);
        var ipfs = Common.TryBuildIpfsClient(config, out var ipfsErr);
        if (ipfs is null)
            return Common.Fail(ipfsErr ?? "could not build IPFS client.");

        // Resolve + verify the recipient: handle → address, KeyRegistry entry,
        // ML-KEM key fetched from IPFS and checked against the on-chain
        // keccak256 commitment, secp256k1 key checked against the address.
        RecipientResolver.Resolved resolvedRecipient;
        try
        {
            resolvedRecipient = await RecipientResolver
                .ResolveAsync(chain, ipfs, toArg, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            if (ipfs is IDisposable disp) disp.Dispose();
            return Common.Fail($"--to: {ex.Message}");
        }

        var recipient = resolvedRecipient.Address;
        var recipientPublic = resolvedRecipient.PublicIdentity;
        var label = resolvedRecipient.Handle is null
            ? recipient.ToString()
            : $"{resolvedRecipient.Handle} ({recipient})";
        Console.WriteLine($"Sending '{filePath}' to {label}");
        Console.WriteLine($"  mime: {mime}");
        Console.WriteLine($"  encrypting + uploading via {config.Ipfs.Kind} ...");

        EnvelopeWriteResult write;
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var writer = new EnvelopeWriter(ipfs);
            write = await writer.SendAsync(
                fs,
                identity,
                recipientPublic,
                filename: Path.GetFileName(filePath),
                mimeType: mime,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Common.Fail($"upload failed: {ex.Message}");
        }
        finally
        {
            if (ipfs is IDisposable d) d.Dispose();
        }

        Console.WriteLine($"  manifest CID: {write.ManifestCid}");
        Console.WriteLine($"  chunks: {write.SignedManifest.Manifest.Chunks.Count}");
        Console.WriteLine($"  total size: {write.SignedManifest.Manifest.TotalSize} bytes");
        Console.WriteLine($"  emitting FileSent event on chain ...");

        try
        {
            var contentHash = write.SignedManifest.ContentHash();
            var txHash = await chain.FileRegistry.SendAsync(
                recipient,
                write.ManifestCid,
                contentHash,
                identity.Secp256k1,
                ct).ConfigureAwait(false);
            Console.WriteLine($"  tx hash: {txHash}");
            Console.WriteLine($"  content hash: {HexFormat.ToHex0x(contentHash)}");
            Console.WriteLine("✓ sent");
            return Common.ExitSuccess;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Upload succeeded (CID {write.ManifestCid}) but chain event failed: {ex.Message}");
            Console.Error.WriteLine("Recipient can still fetch the CID via 'mcptx receive' if you communicate it out-of-band.");
            return Common.ExitError;
        }
    }
}
