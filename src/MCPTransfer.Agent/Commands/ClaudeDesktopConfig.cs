using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCPTransfer.Agent.Commands;

/// <summary>
/// Helpers to wire <c>mcptx mcp-serve</c> into Claude Desktop's
/// <c>claude_desktop_config.json</c> — resolving the per-OS config path,
/// building the exact stdio invocation (handling both a published
/// <c>mcptx</c> binary and a <c>dotnet &lt;dll&gt;</c> launch), rendering the
/// snippet to paste, and merge-writing the entry without clobbering other
/// servers. Used by <c>mcptx setup</c> (print + opt-in --write) and detected by
/// <c>mcptx doctor</c>.
/// </summary>
internal static class ClaudeDesktopConfig
{
    public const string ServerName = "mcptransfer";

    /// <summary>Per-OS path to Claude Desktop's config file.</summary>
    public static string ConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
        return Path.Combine(home, ".config", "Claude", "claude_desktop_config.json");
    }

    /// <summary>
    /// The command + args Claude Desktop should run. Pure given the launcher
    /// paths so it is testable: a published <c>mcptx[.exe]</c> is invoked
    /// directly; a <c>dotnet</c> host gets the entry <c>.dll</c> prepended.
    /// </summary>
    public static (string Command, IReadOnlyList<string> Args) ServerInvocation(
        string processPath, string? entryDll, string identityPath, string configPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        var baseArgs = new List<string>();
        if (!fileName.StartsWith("mcptx", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entryDll))
            baseArgs.Add(entryDll); // launched via the dotnet host → run the dll
        baseArgs.AddRange(new[] { "mcp-serve", "--identity", identityPath, "--config", configPath });
        return (processPath, baseArgs);
    }

    /// <summary>Resolve <see cref="ServerInvocation(string,string?,string,string)"/>
    /// for the current process.</summary>
    public static (string Command, IReadOnlyList<string> Args) ServerInvocation(string identityPath, string configPath)
        => ServerInvocation(
            Environment.ProcessPath ?? "mcptx",
            Assembly.GetEntryAssembly()?.Location,
            identityPath, configPath);

    /// <summary>Render the <c>mcpServers</c> entry as pretty JSON to paste.</summary>
    public static string Snippet(string command, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        var entry = BuildEntry(command, args, env);
        var root = new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = entry } };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>True if <paramref name="configJson"/> already declares our server.</summary>
    public static bool IsConfigured(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return false;
        try
        {
            var node = JsonNode.Parse(configJson);
            return node?["mcpServers"]?[ServerName] is not null;
        }
        catch (JsonException)
        {
            // Fall back to a substring check on a malformed file.
            return configJson.Contains(ServerName, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Merge our server entry into the config at <paramref name="path"/>,
    /// preserving any other <c>mcpServers</c> and top-level keys. Creates the
    /// file (and parent dir) if absent. Throws <see cref="JsonException"/> if an
    /// existing file is present but not valid JSON (so the caller can refuse to
    /// clobber it).
    /// </summary>
    public static void MergeWrite(
        string path, string command, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            root = string.IsNullOrWhiteSpace(existing)
                ? new JsonObject()
                : JsonNode.Parse(existing) as JsonObject
                  ?? throw new JsonException("top-level JSON is not an object.");
        }
        else
        {
            root = new JsonObject();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }
        servers[ServerName] = BuildEntry(command, args, env);

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject BuildEntry(string command, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        var argsArray = new JsonArray();
        foreach (var a in args)
            argsArray.Add(a);

        var entry = new JsonObject { ["command"] = command, ["args"] = argsArray };
        if (env.Count > 0)
        {
            var envObj = new JsonObject();
            foreach (var (k, v) in env)
                envObj[k] = v;
            entry["env"] = envObj;
        }
        return entry;
    }
}
