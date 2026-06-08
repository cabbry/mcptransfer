namespace MCPTransfer.Agent.Mcp;

/// <summary>
/// Optional filesystem confinement for the MCP <c>send_file</c> /
/// <c>receive_file</c> tools. When a root is configured (env
/// <c>MCPTX_MCP_ROOT</c>), every path an AI host passes to those tools must
/// resolve to a location <em>inside</em> that root — defeating arbitrary
/// file read (exfiltration) and arbitrary file write (clobbering
/// <c>identity.json</c>, autostart scripts, …) by a compromised or
/// prompt-injected host.
/// </summary>
/// <remarks>
/// When no root is configured the guard is a no-op and the tools accept any
/// path the server process can access — documented as a deliberate trust
/// grant in <c>docs/MCP.md</c>. Operators exposing the server to an
/// untrusted host SHOULD set a root.
/// </remarks>
public sealed class McpWorkspaceGuard
{
    public const string EnvVar = "MCPTX_MCP_ROOT";

    /// <summary>The confinement root (full path), or null if unconfined.</summary>
    public string? Root { get; }

    private McpWorkspaceGuard(string? root) => Root = root;

    /// <summary>Build from the <c>MCPTX_MCP_ROOT</c> environment variable.</summary>
    public static McpWorkspaceGuard FromEnvironment()
    {
        var root = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(root))
            return new McpWorkspaceGuard(null);

        var full = Path.GetFullPath(root);
        Directory.CreateDirectory(full);
        return new McpWorkspaceGuard(full);
    }

    /// <summary>True when no root is set (tools accept any accessible path).</summary>
    public bool IsUnconfined => Root is null;

    /// <summary>
    /// Resolve <paramref name="path"/> to a full path and, if a root is
    /// configured, require it to be inside the root. Throws
    /// <see cref="InvalidOperationException"/> on an escape attempt
    /// (absolute paths outside the root, <c>..</c> traversal, etc.).
    /// Returns the resolved full path to use.
    /// </summary>
    public string Resolve(string path, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Root is null)
            return Path.GetFullPath(path);

        // Interpret relative paths against the root; resolve .. / symlinks-ish.
        var combined = Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
        var full = Path.GetFullPath(combined);

        var rootWithSep = Root.EndsWith(Path.DirectorySeparatorChar)
            ? Root
            : Root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal)
            && !string.Equals(full, Root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{paramName}' resolves to '{full}', which is outside the configured "
                + $"MCP workspace root '{Root}'. Refusing to access files outside the sandbox.");
        }
        return full;
    }
}
