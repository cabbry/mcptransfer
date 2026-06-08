using MCPTransfer.Agent.Mcp;
using MCPTransfer.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCPTransfer.Agent.Commands;

/// <summary>
/// <c>mcptx mcp-serve</c> — run an MCP server over stdio so an MCP host
/// (Claude Desktop, Cursor, an agent SDK) can drive MCPTransfer as tools.
/// </summary>
/// <remarks>
/// stdio is the JSON-RPC channel, so this command must NEVER write to
/// stdout — all diagnostics go to stderr (configured below). The agent
/// identity and config are loaded once at startup and shared with every
/// tool via DI singletons; the host disposes them on shutdown.
/// </remarks>
internal static class McpServeCommand
{
    public const string Usage =
        "  mcptx mcp-serve [--identity PATH] [--config PATH]\n"
      + "      Run an MCP server over stdio exposing MCPTransfer as tools.\n"
      + "      Configure your MCP host to launch this command (see docs/MCP.md).";

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // Load identity + config up front so we fail fast (to stderr) rather
        // than starting a half-broken server.
        var identity = await Common.TryLoadIdentityAsync(args, cancellationToken).ConfigureAwait(false);
        if (identity is null) return Common.ExitError;
        var config = await Common.TryLoadConfigAsync(args, cancellationToken).ConfigureAwait(false);
        if (config is null) return Common.ExitError;

        var ipfs = Common.TryBuildIpfsClient(config, out var ipfsErr);
        if (ipfs is null)
            return Common.Fail(ipfsErr ?? "could not build IPFS client.");

        // Owns disposable resources (the IPFS HttpClient). Until the host's DI
        // container takes ownership (a successful Build), WE are responsible
        // for disposing it on any failure path — otherwise a throw in
        // BuildChainClient (bad address) or Build() leaks the HttpClient.
        McpAgentContext? context = null;
        try
        {
            var chain = Common.BuildChainClient(config); // may throw on malformed address
            context = new McpAgentContext(identity, config, chain, ipfs);

            var builder = Host.CreateApplicationBuilder(args);

            // CRITICAL: stdout is the MCP protocol channel. Route all logs to
            // stderr so they never corrupt the JSON-RPC stream.
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

            builder.Services.AddSingleton(context);
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithToolsFromAssembly();

            using var host = builder.Build();
            // The container now owns `context` and will dispose it on host
            // shutdown — relinquish our finally-disposal to avoid double work.
            context = null;
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return Common.ExitSuccess;
        }
        catch (InvalidOperationException ex)
        {
            return Common.Fail(ex.Message);
        }
        finally
        {
            context?.Dispose();
        }
    }
}
