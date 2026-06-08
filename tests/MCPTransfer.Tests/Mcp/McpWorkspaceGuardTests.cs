using MCPTransfer.Agent.Mcp;

namespace MCPTransfer.Tests.Mcp;

public class McpWorkspaceGuardTests : IDisposable
{
    private readonly string _root;

    public McpWorkspaceGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcptx-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(McpWorkspaceGuard.EnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(McpWorkspaceGuard.EnvVar, null);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Unconfined_WhenEnvUnset_AllowsAnyPath()
    {
        var guard = McpWorkspaceGuard.FromEnvironment();
        Assert.True(guard.IsUnconfined);
        // Any absolute path is accepted (resolved to full path), no throw.
        var resolved = guard.Resolve("/some/where/file.bin", "p");
        Assert.NotNull(resolved);
    }

    [Fact]
    public void Confined_AllowsPathInsideRoot()
    {
        Environment.SetEnvironmentVariable(McpWorkspaceGuard.EnvVar, _root);
        var guard = McpWorkspaceGuard.FromEnvironment();
        Assert.False(guard.IsUnconfined);

        var ok = guard.Resolve("sub/file.bin", "p");
        Assert.StartsWith(_root, ok, StringComparison.Ordinal);
    }

    [Fact]
    public void Confined_AllowsRelativeResolvedAgainstRoot()
    {
        Environment.SetEnvironmentVariable(McpWorkspaceGuard.EnvVar, _root);
        var guard = McpWorkspaceGuard.FromEnvironment();

        var resolved = guard.Resolve("doc.bin", "p");
        Assert.Equal(Path.Combine(_root, "doc.bin"), resolved);
    }

    [Fact]
    public void Confined_RejectsTraversalEscape()
    {
        Environment.SetEnvironmentVariable(McpWorkspaceGuard.EnvVar, _root);
        var guard = McpWorkspaceGuard.FromEnvironment();

        var ex = Assert.Throws<InvalidOperationException>(
            () => guard.Resolve("../../etc/passwd", "p"));
        Assert.Contains("outside", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Confined_RejectsAbsolutePathOutsideRoot()
    {
        Environment.SetEnvironmentVariable(McpWorkspaceGuard.EnvVar, _root);
        var guard = McpWorkspaceGuard.FromEnvironment();

        var outside = Path.Combine(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"), "x");
        var ex = Assert.Throws<InvalidOperationException>(() => guard.Resolve(outside, "p"));
        Assert.Contains("outside", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Confined_RejectsSiblingPrefixCollision()
    {
        // A sibling dir whose name starts with the root name must NOT be
        // treated as inside (e.g. root="/a/ws", path="/a/ws-evil/x").
        Environment.SetEnvironmentVariable(McpWorkspaceGuard.EnvVar, _root);
        var guard = McpWorkspaceGuard.FromEnvironment();

        var sibling = _root + "-evil";
        var ex = Assert.Throws<InvalidOperationException>(
            () => guard.Resolve(Path.Combine(sibling, "x"), "p"));
        Assert.Contains("outside", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
