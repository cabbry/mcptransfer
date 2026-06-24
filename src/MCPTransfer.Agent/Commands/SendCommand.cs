using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;
using MCPTransfer.Core.Envelope;

namespace MCPTransfer.Agent.Commands;

internal static class SendCommand
{
    public const string Usage =
        "  mcptx send <file> (--to <handle|0xaddress> | --to-pubkey <card>) [--mime TYPE]\n"
      + "             [--identity PATH] [--config PATH]\n"
      + "      Encrypt <file> end-to-end for the recipient, pin chunks + manifest\n"
      + "      on IPFS, then emit a FileSent event on chain.\n"
      + "      --to        : resolve + verify the recipient via the on-chain KeyRegistry.\n"
      + "      --to-pubkey : use a contact card (from 'whoami --card') directly — lets you\n"
      + "                    send to someone who never registered on-chain. Path or inline JSON.";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Common.Fail("missing required positional <file>. Example: mcptx send report.pdf --to alice-ai");

        var filePath = args[1];
        if (!File.Exists(filePath))
            return Common.Fail($"file not found: {filePath}");

        var toArg = Common.GetFlagValue(args, "--to");
        var toPubkeyArg = Common.GetFlagValue(args, "--to-pubkey");
        if (string.IsNullOrEmpty(toArg) && string.IsNullOrEmpty(toPubkeyArg))
            return Common.Fail("missing recipient: pass --to <handle|0xaddress> or --to-pubkey <card>.");
        if (!string.IsNullOrEmpty(toArg) && !string.IsNullOrEmpty(toPubkeyArg))
            return Common.Fail("pass only one of --to or --to-pubkey, not both.");

        var mime = Common.GetFlagValue(args, "--mime") ?? "application/octet-stream";

        var identity = await Common.TryLoadIdentityAsync(args, ct).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, ct).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var chain = Common.BuildChainClient(config);
        var ipfs = Common.TryBuildIpfsClient(config, out var ipfsErr);
        if (ipfs is null)
            return Common.Fail(ipfsErr ?? "could not build IPFS client.");

        EthereumAddress recipient;
        AgentPublicIdentity recipientPublic;
        string label;
        try
        {
            if (!string.IsNullOrEmpty(toPubkeyArg))
            {
                // Out-of-band path: trust the card's keys (no on-chain commitment
                // to check), but still re-derive and verify the address↔key binding.
                recipientPublic = LoadFromCard(toPubkeyArg);
                recipient = recipientPublic.Address;
                label = $"{recipient} (from contact card)";
            }
            else
            {
                // Resolve + verify via KeyRegistry: ML-KEM key fetched from IPFS and
                // checked against the on-chain keccak256 commitment, secp256k1 key
                // checked against the address.
                var resolved = await RecipientResolver
                    .ResolveAsync(chain, ipfs, toArg!, ct).ConfigureAwait(false);
                recipient = resolved.Address;
                recipientPublic = resolved.PublicIdentity;
                label = resolved.Handle is null ? recipient.ToString() : $"{resolved.Handle} ({recipient})";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            if (ipfs is IDisposable disp) disp.Dispose();
            return Common.Fail(string.IsNullOrEmpty(toPubkeyArg) ? $"--to: {ex.Message}" : $"--to-pubkey: {ex.Message}");
        }

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

    /// <summary>
    /// Load a recipient from a contact card (a file path or inline JSON) and
    /// re-verify the address↔secp256k1 binding. The ML-KEM key is trusted as it
    /// arrived (no on-chain commitment to check), so the card's authenticity
    /// rests on the out-of-band channel it came through.
    /// </summary>
    private static AgentPublicIdentity LoadFromCard(string pathOrInline)
    {
        var json = File.Exists(pathOrInline) ? File.ReadAllText(pathOrInline) : pathOrInline;
        var card = ContactCard.FromJson(json);
        var publicIdentity = card.ToPublicIdentity();
        var declared = EthereumAddress.FromHex(card.Address);
        if (publicIdentity.Address != declared)
        {
            throw new InvalidOperationException(
                $"contact card address {card.Address} does not match its secp256k1 key "
                + $"(which derives to {publicIdentity.Address}). Refusing to send.");
        }
        return publicIdentity;
    }
}
