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
    [Description("Publish this agent's keys to the on-chain KeyRegistry: secp256k1 in clear plus a keccak256 commitment to the ML-KEM-768 key (whose full bytes are pinned to IPFS first). Signs a transaction and spends gas. Required before this agent can receive files.")]
    public static async Task<string> RegisterKey(McpAgentContext ctx, CancellationToken cancellationToken)
    {
        KeyPublication.Result result;
        await ctx.SigningLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            result = await KeyPublication
                .PublishAsync(ctx.Chain.KeyRegistry, ctx.Ipfs, ctx.Identity, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { ctx.SigningLock.Release(); }

        return JsonSerializer.Serialize(new
        {
            address = ctx.Identity.Address.ToString(),
            tx_hash = result.TxHash,
            secp256k1_fingerprint = DirectoryTools.Fingerprint(ctx.Identity.Secp256k1.PublicKeyCompressed),
            mlkem_hash = "0x" + Convert.ToHexString(result.MlKemHash).ToLowerInvariant(),
            mlkem_cid = result.MlKemCid,
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
        string txHash;
        await ctx.SigningLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            txHash = await ctx.Chain.AgentDirectory
                .ClaimAsync(handle, ctx.Identity.Secp256k1, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { ctx.SigningLock.Release(); }
        return JsonSerializer.Serialize(new { handle, owner = ctx.Identity.Address.ToString(), tx_hash = txHash }, Json);
    }

    [McpServerTool(Name = "send_file")]
    [Description("Encrypt a local file end-to-end for a recipient, upload it to IPFS, and announce it on chain. 'to' is a handle or 0x address. Signs a transaction and spends gas. Returns the manifest CID. If MCPTX_MCP_ROOT is set, 'path' must be inside it.")]
    public static async Task<string> SendFile(
        McpAgentContext ctx,
        [Description("Path to the local file to send (readable by the server). Confined to MCPTX_MCP_ROOT when set.")] string path,
        [Description("Recipient: a handle (e.g. 'bob') or a 0x Ethereum address.")] string to,
        [Description("Optional MIME type; defaults to application/octet-stream.")] string? mime,
        CancellationToken cancellationToken)
    {
        // Confine the read to the workspace root (no-op when unconfined).
        var resolvedPath = ctx.Workspace.Resolve(path, nameof(path));
        if (!File.Exists(resolvedPath))
            throw new InvalidOperationException($"File not found: {resolvedPath}");

        var recipient = await RecipientResolver.ResolveAsync(ctx.Chain, ctx.Ipfs, to, cancellationToken).ConfigureAwait(false);

        EnvelopeWriteResult write;
        await using (var fs = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Use the shared singleton IPFS client — do NOT dispose it here;
            // the host owns its lifetime.
            var writer = new EnvelopeWriter(ctx.Ipfs);
            write = await writer.SendAsync(
                fs,
                ctx.Identity,
                recipient.PublicIdentity,
                filename: Path.GetFileName(resolvedPath),
                mimeType: string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var contentHash = write.SignedManifest.ContentHash();
        string txHash;
        await ctx.SigningLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            txHash = await ctx.Chain.FileRegistry
                .SendAsync(recipient.Address, write.ManifestCid, contentHash, ctx.Identity.Secp256k1, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { ctx.SigningLock.Release(); }

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
        var latest = await ctx.Chain.FileRegistry.GetLatestBlockNumberAsync(cancellationToken).ConfigureAwait(false);
        var (fromBlock, _) = InboxWindow.Compute(latest, sinceBlock);

        // Falls back to a ~450-block window when the RPC caps eth_getLogs.
        var scan = await ctx.Chain.FileRegistry
            .GetInboxWithFallbackAsync(ctx.Identity.Address, fromBlock, latest, cancellationToken)
            .ConfigureAwait(false);
        fromBlock = scan.FromBlock;
        var raw = scan.Events;

        // Drop events from senders on this agent's on-chain blocklist
        // (no-op when no Blocklist contract is configured).
        var filtered = await InboxFilter
            .ApplyAsync(ctx.Chain.Blocklist, ctx.Identity.Address, raw, cancellationToken)
            .ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            address = ctx.Identity.Address.ToString(),
            from_block = fromBlock,
            to_block = latest,
            count = filtered.Kept.Count,
            blocked_hidden = filtered.Hidden,
            items = filtered.Kept.Select(e => new
            {
                block = e.BlockNumber,
                timestamp = e.Timestamp.ToString("u"),
                from = e.From.ToString(),
                cid = e.Cid,
                content_hash = "0x" + Convert.ToHexString(e.ContentHash).ToLowerInvariant(),
            }),
        }, Json);
    }

    [McpServerTool(Name = "block_sender")]
    [Description("Block a sender (handle or 0x address) on this agent's on-chain blocklist: their FileSent events are hidden from the inbox tool. Signs a transaction and spends gas. Reversible with unblock_sender.")]
    public static Task<string> BlockSender(
        McpAgentContext ctx,
        [Description("The sender to block: a handle or a 0x Ethereum address.")] string sender,
        CancellationToken cancellationToken)
        => SetBlockedAsync(ctx, sender, blocked: true, cancellationToken);

    [McpServerTool(Name = "unblock_sender")]
    [Description("Remove a sender (handle or 0x address) from this agent's on-chain blocklist. Signs a transaction and spends gas.")]
    public static Task<string> UnblockSender(
        McpAgentContext ctx,
        [Description("The sender to unblock: a handle or a 0x Ethereum address.")] string sender,
        CancellationToken cancellationToken)
        => SetBlockedAsync(ctx, sender, blocked: false, cancellationToken);

    private static async Task<string> SetBlockedAsync(
        McpAgentContext ctx, string sender, bool blocked, CancellationToken cancellationToken)
    {
        var blocklist = ctx.Chain.Blocklist
            ?? throw new InvalidOperationException(
                "No Blocklist contract configured (set 'blocklist_address' in the config "
                + "or the MCPTX_BLOCKLIST env var).");

        // Resolve a 0x address or a handle (shared parser; clean errors).
        var (address, handle) = await HandleResolution
            .ResolveRequiredAsync(ctx.Chain.AgentDirectory, sender, cancellationToken).ConfigureAwait(false);

        string txHash;
        await ctx.SigningLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            txHash = await blocklist
                .SetBlockedAsync(address, blocked, ctx.Identity.Secp256k1, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { ctx.SigningLock.Release(); }

        return JsonSerializer.Serialize(new
        {
            sender = address.ToString(),
            sender_handle = handle,
            blocked,
            tx_hash = txHash,
        }, Json);
    }

    [McpServerTool(Name = "receive_file")]
    [Description("Fetch a manifest CID from IPFS, verify its signature, decrypt it, and write the plaintext to a local path (atomic). Returns sender + metadata. Pass expect_hash (the content_hash from the matching inbox entry) to corroborate the bytes against the on-chain record. If MCPTX_MCP_ROOT is set, out_path must be inside it.")]
    public static async Task<string> ReceiveFile(
        McpAgentContext ctx,
        [Description("The manifest CID to fetch (from an inbox entry).")] string cid,
        [Description("Output path to write the decrypted file to. Confined to MCPTX_MCP_ROOT when set.")] string outPath,
        [Description("Optional 0x content hash from the inbox entry; when given, the manifest must match it or the receive is refused.")] string? expectHash,
        CancellationToken cancellationToken)
    {
        // Confine the write to the workspace root (no-op when unconfined) so a
        // host cannot clobber identity.json / autostart scripts / etc.
        var resolvedOut = ctx.Workspace.Resolve(outPath, nameof(outPath));

        byte[]? expectedHash = null;
        if (!string.IsNullOrEmpty(expectHash))
        {
            var span = expectHash.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
            try
            {
                expectedHash = Convert.FromHexString(span);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("expect_hash must be a hex string (optionally 0x-prefixed).");
            }
            if (expectedHash.Length != MCPTransfer.Core.Crypto.Hashes.Keccak256ByteLength)
            {
                throw new InvalidOperationException(
                    $"expect_hash must be {MCPTransfer.Core.Crypto.Hashes.Keccak256ByteLength} bytes "
                    + $"(a Keccak-256 hash); got {expectedHash.Length}.");
            }
        }

        // Auto-corroborate against the on-chain FileSent event when no explicit
        // hash was given: pin the expected content hash and learn the sender.
        // Best-effort — an unreachable/capped RPC degrades to signature-only
        // verification (onchain_corroborated=false) instead of failing the tool.
        string? corroboratedHandle = null;
        var corroborated = false;
        if (expectedHash is null)
        {
            try
            {
                var latest = await ctx.Chain.FileRegistry.GetLatestBlockNumberAsync(cancellationToken).ConfigureAwait(false);
                var fromBlock = latest > FileRegistryQueries.DefaultLookback
                    ? latest - FileRegistryQueries.DefaultLookback
                    : 0UL;
                // Falls back to a ~450-block scan when the RPC caps eth_getLogs.
                var ev = await ctx.Chain.FileRegistry
                    .FindByCidWithFallbackAsync(ctx.Identity.Address, cid, fromBlock, latest, cancellationToken)
                    .ConfigureAwait(false);
                if (ev is not null)
                {
                    expectedHash = ev.ContentHash;
                    corroborated = true;
                    corroboratedHandle = await ctx.Chain.AgentDirectory
                        .ReverseResolveAsync(ev.From, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Proceed on the manifest signature alone.
            }
        }

        var reader = new EnvelopeReader(ctx.Ipfs);
        var result = await reader.ReceiveToFileAsync(cid, ctx.Identity, resolvedOut, expectedHash, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            output_path = resolvedOut,
            sender = result.Manifest.Sender.ToString(),
            sender_handle = corroboratedHandle,
            onchain_corroborated = corroborated,
            filename = result.Manifest.Filename,
            mime = result.Manifest.MimeType,
            total_size = result.PlaintextBytesWritten,
            chunks = result.Manifest.Chunks.Count,
        }, Json);
    }
}
