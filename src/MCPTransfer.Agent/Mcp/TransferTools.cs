using System.ComponentModel;
using System.Text.Json;
using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Envelope;
using ModelContextProtocol.Server;

namespace MCPTransfer.Agent.Mcp;

/// <summary>
/// State-changing MCP tools that sign transactions / spend gas, plus the
/// file transfer operations. The server holds the agent's private key and
/// signs on the host's request — this is the trust model (documented in
/// docs/MCP.md).
/// </summary>
[McpServerToolType]
public static class TransferTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [McpServerTool(Name = "register_key")]
    [Description("Publish this agent's secp256k1 + ML-KEM-768 public keys to the on-chain KeyRegistry. Signs a transaction and spends gas. Required before this agent can receive files.")]
    public static async Task<string> RegisterKey(McpAgentContext ctx, CancellationToken cancellationToken)
    {
        var ec = ctx.Identity.Secp256k1.PublicKeyCompressed.ToArray();
        var ml = ctx.Identity.MlKem.PublicKey.Bytes.ToArray();
        var txHash = await ctx.Chain.KeyRegistry
            .PublishAsync(ec, ml, ctx.Identity.Secp256k1, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            address = ctx.Identity.Address.ToString(),
            tx_hash = txHash,
            secp256k1_fingerprint = DirectoryTools.Fingerprint(ec),
            mlkem_fingerprint = DirectoryTools.Fingerprint(ml),
        }, Json);
    }

    [McpServerTool(Name = "claim")]
    [Description("Claim a handle (e.g. 'alice-ai', format [a-z0-9-]{3,32}) for this agent's address. First-come-first-served, permanent. Signs a transaction and spends gas.")]
    public static async Task<string> Claim(
        McpAgentContext ctx,
        [Description("The handle to claim.")] string handle,
        CancellationToken cancellationToken)
    {
        HandleValidation.Validate(handle);
        var txHash = await ctx.Chain.AgentDirectory
            .ClaimAsync(handle, ctx.Identity.Secp256k1, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new { handle, owner = ctx.Identity.Address.ToString(), tx_hash = txHash }, Json);
    }

    [McpServerTool(Name = "send_file")]
    [Description("Encrypt a local file end-to-end for a recipient, upload it to IPFS, and announce it on chain. 'to' is a handle or 0x address. Signs a transaction and spends gas. Returns the manifest CID.")]
    public static async Task<string> SendFile(
        McpAgentContext ctx,
        [Description("Absolute path to the local file to send (readable by the server process).")] string path,
        [Description("Recipient: a handle (e.g. 'bob') or a 0x Ethereum address.")] string to,
        [Description("Optional MIME type; defaults to application/octet-stream.")] string? mime,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"File not found: {path}");

        var recipient = await RecipientResolver.ResolveAsync(ctx.Chain, to, cancellationToken).ConfigureAwait(false);

        EnvelopeWriteResult write;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Use the shared singleton IPFS client — do NOT dispose it here;
            // the host owns its lifetime.
            var writer = new EnvelopeWriter(ctx.Ipfs);
            write = await writer.SendAsync(
                fs,
                ctx.Identity,
                recipient.PublicIdentity,
                filename: Path.GetFileName(path),
                mimeType: string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var contentHash = write.SignedManifest.ContentHash();
        var txHash = await ctx.Chain.FileRegistry
            .SendAsync(recipient.Address, write.ManifestCid, contentHash, ctx.Identity.Secp256k1, cancellationToken)
            .ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            recipient = recipient.Address.ToString(),
            recipient_handle = recipient.Handle,
            manifest_cid = write.ManifestCid,
            content_hash = "0x" + Convert.ToHexString(contentHash).ToLowerInvariant(),
            chunks = write.SignedManifest.Manifest.Chunks.Count,
            total_size = write.SignedManifest.Manifest.TotalSize,
            tx_hash = txHash,
        }, Json);
    }

    [McpServerTool(Name = "inbox")]
    [Description("List FileSent events addressed to this agent. Optional 'since_block' (defaults to the last 10000 blocks). Read-only.")]
    public static async Task<string> Inbox(
        McpAgentContext ctx,
        [Description("Optional starting block number; defaults to latest-10000.")] ulong? sinceBlock,
        CancellationToken cancellationToken)
    {
        const ulong lookback = 10_000;
        var latest = await ctx.Chain.FileRegistry.GetLatestBlockNumberAsync(cancellationToken).ConfigureAwait(false);
        var fromBlock = sinceBlock ?? (latest > lookback ? latest - lookback : 0UL);
        if (fromBlock > latest)
            throw new InvalidOperationException($"since_block ({fromBlock}) is past the chain head ({latest}).");

        var events = await ctx.Chain.FileRegistry
            .GetInboxAsync(ctx.Identity.Address, fromBlock, latest, cancellationToken)
            .ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            address = ctx.Identity.Address.ToString(),
            from_block = fromBlock,
            to_block = latest,
            count = events.Count,
            items = events.Select(e => new
            {
                block = e.BlockNumber,
                timestamp = e.Timestamp.ToString("u"),
                from = e.From.ToString(),
                cid = e.Cid,
                content_hash = "0x" + Convert.ToHexString(e.ContentHash).ToLowerInvariant(),
            }),
        }, Json);
    }

    [McpServerTool(Name = "receive_file")]
    [Description("Fetch a manifest CID from IPFS, verify its signature, decrypt it, and write the plaintext to a local path (atomic). Returns sender + metadata. Pass expect_hash (the content_hash from the matching inbox entry) to corroborate the bytes against the on-chain record.")]
    public static async Task<string> ReceiveFile(
        McpAgentContext ctx,
        [Description("The manifest CID to fetch (from an inbox entry).")] string cid,
        [Description("Absolute output path to write the decrypted file to.")] string outPath,
        [Description("Optional 0x content hash from the inbox entry; when given, the manifest must match it or the receive is refused.")] string? expectHash,
        CancellationToken cancellationToken)
    {
        byte[]? expectedHash = null;
        if (!string.IsNullOrEmpty(expectHash))
        {
            var span = expectHash.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
            expectedHash = Convert.FromHexString(span);
        }

        var reader = new EnvelopeReader(ctx.Ipfs);
        var result = await reader.ReceiveToFileAsync(cid, ctx.Identity, outPath, expectedHash, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            output_path = outPath,
            sender = result.Manifest.Sender.ToString(),
            filename = result.Manifest.Filename,
            mime = result.Manifest.MimeType,
            total_size = result.PlaintextBytesWritten,
            chunks = result.Manifest.Chunks.Count,
        }, Json);
    }
}
