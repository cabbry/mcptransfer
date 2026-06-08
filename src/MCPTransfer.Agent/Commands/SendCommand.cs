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

        // Resolve recipient address (handle → address, or hex).
        EthereumAddress recipient;
        string? recipientHandleLabel = null;
        if (toArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            try { recipient = EthereumAddress.FromHex(toArg); }
            catch (ArgumentException ex) { return Common.Fail($"--to: {ex.Message}"); }
        }
        else
        {
            if (!HandleValidation.IsValid(toArg))
                return Common.Fail($"--to: '{toArg}' is not a valid handle and doesn't look like an address.");
            var resolved = await chain.AgentDirectory.ResolveAsync(toArg, ct).ConfigureAwait(false);
            if (resolved is null)
                return Common.Fail($"handle '{toArg}' is not claimed on chain.");
            recipient = resolved;
            recipientHandleLabel = toArg;
        }

        // Fetch recipient's keys (both must be present).
        var recipientKeys = await chain.KeyRegistry.GetAsync(recipient, ct).ConfigureAwait(false);
        if (!recipientKeys.IsRegistered)
        {
            return Common.Fail(
                $"recipient {recipient} has not registered both public keys. "
              + "They must run 'mcptx register-key' before they can receive.");
        }

        AgentPublicIdentity recipientPublic;
        try
        {
            recipientPublic = AgentPublicIdentity.FromBytes(
                recipientKeys.Secp256k1Compressed,
                recipientKeys.MlKem);
        }
        catch (Exception ex)
        {
            return Common.Fail($"recipient key material malformed: {ex.Message}");
        }

        // Verify the on-chain secp256k1 actually derives to the recipient address
        // (defends against a misbehaving registry or man-in-the-middle proxy).
        if (recipientPublic.Address != recipient)
        {
            return Common.Fail(
                $"recipient secp256k1 pubkey derives to {recipientPublic.Address}, "
              + $"which does not match the declared address {recipient}. Refusing to send.");
        }

        var label = recipientHandleLabel is null
            ? recipient.ToString()
            : $"{recipientHandleLabel} ({recipient})";
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
            Console.WriteLine($"  content hash: 0x{Convert.ToHexString(contentHash).ToLowerInvariant()}");
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
