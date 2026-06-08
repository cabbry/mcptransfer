using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;
using ModelContextProtocol.Server;

namespace MCPTransfer.Agent.Mcp;

/// <summary>
/// Read-only MCP tools: identity introspection and on-chain directory /
/// key-registry lookups. None of these sign a transaction or spend gas.
/// </summary>
[McpServerToolType]
public static class DirectoryTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [McpServerTool(Name = "whoami")]
    [Description("Return this agent's own Ethereum address and public-key fingerprints.")]
    public static string Whoami(McpAgentContext ctx)
    {
        var ec = ctx.Identity.Secp256k1.PublicKeyCompressed;
        var ml = ctx.Identity.MlKem.PublicKey.Bytes;
        return JsonSerializer.Serialize(new
        {
            address = ctx.Identity.Address.ToString(),
            secp256k1_fingerprint = Fingerprint(ec),
            mlkem_fingerprint = Fingerprint(ml),
        }, Json);
    }

    [McpServerTool(Name = "resolve")]
    [Description("Resolve an agent handle (e.g. 'alice-ai') to its Ethereum address. Returns null address if unclaimed.")]
    public static async Task<string> Resolve(
        McpAgentContext ctx,
        [Description("The handle to resolve, e.g. 'alice-ai'.")] string handle,
        CancellationToken cancellationToken)
    {
        if (!HandleValidation.IsValid(handle))
            throw new InvalidOperationException($"'{handle}' is not a valid handle.");
        var addr = await ctx.Chain.AgentDirectory.ResolveAsync(handle, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            handle,
            address = addr?.ToString(),
            claimed = addr is not null,
        }, Json);
    }

    [McpServerTool(Name = "whois")]
    [Description("Look up an agent by handle or 0x address: returns address, reverse handle, and whether both public keys are registered (with fingerprints).")]
    public static async Task<string> Whois(
        McpAgentContext ctx,
        [Description("A handle (e.g. 'alice-ai') or a 0x Ethereum address.")] string target,
        CancellationToken cancellationToken)
    {
        EthereumAddress address;
        string? handle;
        if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                address = EthereumAddress.FromHex(target);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                throw new InvalidOperationException($"'{target}' is not a valid 0x address: {ex.Message}", ex);
            }
            handle = await ctx.Chain.AgentDirectory.ReverseResolveAsync(address, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!HandleValidation.IsValid(target))
                throw new InvalidOperationException($"'{target}' is not a valid handle or address.");
            var resolved = await ctx.Chain.AgentDirectory.ResolveAsync(target, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Handle '{target}' is not claimed.");
            address = resolved;
            handle = target;
        }

        var keys = await ctx.Chain.KeyRegistry.GetAsync(address, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            address = address.ToString(),
            handle,
            registered = keys.IsRegistered,
            secp256k1_fingerprint = keys.Secp256k1Compressed.Length > 0 ? Fingerprint(keys.Secp256k1Compressed) : null,
            mlkem_fingerprint = keys.MlKem.Length > 0 ? Fingerprint(keys.MlKem) : null,
        }, Json);
    }

    internal static string Fingerprint(ReadOnlySpan<byte> data)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()[..16];
}
