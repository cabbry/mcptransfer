using System.Text.Json.Nodes;
using MCPTransfer.Agent.Commands;

namespace MCPTransfer.Tests.Commands;

public class ClaudeDesktopConfigTests
{
    [Fact]
    public void ServerInvocation_PublishedBinary_InvokedDirectly()
    {
        var (cmd, args) = ClaudeDesktopConfig.ServerInvocation(
            @"C:\tools\mcptx.exe", entryDll: @"C:\tools\mcptx.dll", "id.json", "config.json");

        Assert.Equal(@"C:\tools\mcptx.exe", cmd);
        Assert.Equal(new[] { "mcp-serve", "--identity", "id.json", "--config", "config.json" }, args);
    }

    [Fact]
    public void ServerInvocation_DotnetHost_PrependsEntryDll()
    {
        var (cmd, args) = ClaudeDesktopConfig.ServerInvocation(
            @"C:\Program Files\dotnet\dotnet.exe", entryDll: @"C:\app\mcptx.dll", "id.json", "config.json");

        Assert.EndsWith("dotnet.exe", cmd);
        Assert.Equal(@"C:\app\mcptx.dll", args[0]);
        Assert.Equal("mcp-serve", args[1]);
    }

    // Path detection must be OS-agnostic: a Windows-style path is parsed the
    // same on a Linux CI runner and vice-versa (regression: GetFileNameWithout-
    // Extension only splits on the current OS separator).
    [Fact]
    public void ServerInvocation_UnixPublishedBinary_NoExtension_InvokedDirectly()
    {
        var (cmd, args) = ClaudeDesktopConfig.ServerInvocation(
            "/usr/local/bin/mcptx", entryDll: "/usr/local/bin/mcptx.dll", "id.json", "config.json");

        Assert.Equal("/usr/local/bin/mcptx", cmd);
        Assert.Equal(new[] { "mcp-serve", "--identity", "id.json", "--config", "config.json" }, args);
    }

    [Fact]
    public void ServerInvocation_UnixDotnetHost_PrependsEntryDll()
    {
        var (_, args) = ClaudeDesktopConfig.ServerInvocation(
            "/usr/bin/dotnet", entryDll: "/app/mcptx.dll", "id.json", "config.json");

        Assert.Equal("/app/mcptx.dll", args[0]);
        Assert.Equal("mcp-serve", args[1]);
    }

    [Fact]
    public void IsConfigured_DetectsOurServer()
    {
        Assert.False(ClaudeDesktopConfig.IsConfigured(null));
        Assert.False(ClaudeDesktopConfig.IsConfigured("{}"));
        Assert.False(ClaudeDesktopConfig.IsConfigured("""{"mcpServers":{"other":{}}}"""));
        Assert.True(ClaudeDesktopConfig.IsConfigured("""{"mcpServers":{"mcptransfer":{"command":"x"}}}"""));
    }

    [Fact]
    public void Snippet_ContainsServerCommandArgsAndEnv()
    {
        var snippet = ClaudeDesktopConfig.Snippet(
            "mcptx.exe",
            new[] { "mcp-serve", "--identity", "id.json", "--config", "cfg.json" },
            new Dictionary<string, string> { ["MCPTX_MCP_ROOT"] = "/work" });

        Assert.Contains("mcpServers", snippet);
        Assert.Contains("mcptransfer", snippet);
        Assert.Contains("mcp-serve", snippet);
        Assert.Contains("id.json", snippet);
        Assert.Contains("MCPTX_MCP_ROOT", snippet);
    }

    [Fact]
    public void MergeWrite_CreatesFile_AndPreservesOtherServers()
    {
        var path = Path.Combine(Path.GetTempPath(), "claude-cfg-" + Guid.NewGuid().ToString("N"), "claude_desktop_config.json");
        try
        {
            // Pre-seed with an unrelated server to prove we don't clobber it.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{"mcpServers":{"karlia":{"command":"karlia.exe"}},"theme":"dark"}""");

            ClaudeDesktopConfig.MergeWrite(path, "mcptx.exe",
                new[] { "mcp-serve" }, new Dictionary<string, string> { ["MCPTX_MCP_ROOT"] = "/w" });

            var root = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal("karlia.exe", root["mcpServers"]!["karlia"]!["command"]!.GetValue<string>());
            Assert.Equal("mcptx.exe", root["mcpServers"]!["mcptransfer"]!["command"]!.GetValue<string>());
            Assert.Equal("dark", root["theme"]!.GetValue<string>()); // top-level keys preserved
        }
        finally
        {
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MergeWrite_NoExistingFile_CreatesValidConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), "claude-cfg-" + Guid.NewGuid().ToString("N"), "claude_desktop_config.json");
        try
        {
            ClaudeDesktopConfig.MergeWrite(path, "mcptx.exe", new[] { "mcp-serve" },
                new Dictionary<string, string>());
            Assert.True(ClaudeDesktopConfig.IsConfigured(File.ReadAllText(path)));
        }
        finally
        {
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
