using System.Text.Json;

namespace Sovereign.Desktop.Client;

/// <summary>ACP mcpServers entry for the in-app swarm manager. Empty unless swarm mode is on.</summary>
public static class SwarmManagerSession
{
    public const string ServerName = "si-desk-swarm-manager";

    public static object[] McpServers(CursorProjectOptions? project, JsonElement initialize)
    {
        if (project?.SwarmBrief is not { Length: > 0 }) return [];
        if (HttpAccepted(initialize)
            && project.SwarmManagerUrl is { Length: > 0 } url
            && project.SwarmManagerToken is { Length: > 0 } token)
        {
            if (url.Contains(":8790", StringComparison.Ordinal))
                throw new IOException("Swarm mode does not use the Grok Bot connector.");
            return
            [
                new
                {
                    type = "http",
                    name = ServerName,
                    url,
                    headers = new object[] { new { name = "Authorization", value = "Bearer " + token } }
                }
            ];
        }
        var command = string.IsNullOrWhiteSpace(project.SwarmManagerCommand) ? FindCommand() : project.SwarmManagerCommand.Trim();
        if (string.IsNullOrWhiteSpace(command) || !File.Exists(command))
            throw new IOException("Swarm mode is on, and the manager tool server is not available.");
        var dll = command.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        return
        [
            new
            {
                name = ServerName,
                command = dll ? "dotnet" : command,
                args = dll ? new[] { "exec", command } : Array.Empty<string>(),
                env = Array.Empty<object>()
            }
        ];
    }

    public static string? FindCommand()
    {
        foreach (var name in new[] { "Sovereign.Desktop.SwarmManager.exe", "Sovereign.Desktop.SwarmManager", "Sovereign.Desktop.SwarmManager.dll" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    static bool HttpAccepted(JsonElement initialize)
    {
        if (initialize.ValueKind != JsonValueKind.Object) return false;
        if (!initialize.TryGetProperty("agentCapabilities", out var capabilities)) return false;
        if (!capabilities.TryGetProperty("mcpCapabilities", out var mcp)) return false;
        return mcp.TryGetProperty("http", out var http) && http.ValueKind == JsonValueKind.True;
    }
}
