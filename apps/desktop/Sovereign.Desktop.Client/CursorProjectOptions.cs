namespace Sovereign.Desktop.Client;

public enum CursorProjectMode { Agent, Plan }
public enum CursorPermissionPolicy { AlwaysAsk, AlwaysAllow }

/// <summary>Opt-in project agent. Cursor performs native edits directly under its permission policy.</summary>
public sealed class CursorProjectOptions
{
    public CursorProjectMode Mode { get; init; } = CursorProjectMode.Agent;
    public CursorPermissionPolicy Permissions { get; init; } = CursorPermissionPolicy.AlwaysAsk;
    internal string ModeId => Mode switch {
        CursorProjectMode.Agent => "agent", CursorProjectMode.Plan => "plan",
        _ => throw new IOException("Unknown project mode. Select Agent or Plan.")
    };
    public bool EnableSubagents { get; init; } = true;
    public string? SwarmBrief { get; init; }
    /// <summary>Loopback URL of the in-app swarm manager tool server. Not the Grok Bot connector.</summary>
    public string? SwarmManagerUrl { get; init; }
    public string? SwarmManagerToken { get; init; }
    /// <summary>Stdio command for the swarm manager tool server when the agent has no HTTP MCP capability.</summary>
    public string? SwarmManagerCommand { get; init; }
    /// <summary>Null and false use the selected model for every role. True uses role rankings.</summary>
    public bool? SwarmMixedModels { get; init; }
    public string? SwarmRoleModelsDirectory { get; init; }
    internal bool SubagentsAllowed => EnableSubagents;
    public Action<CursorToolActivity>? ToolActivity { get; init; }
    public Func<CursorPermissionRequest, CancellationToken, Task<string?>>? RequestPermission { get; init; }
}

public sealed record CursorFileChange(string Path, string? Before, string After, string? UnifiedDiff = null);
public sealed record CursorToolActivity(string Id, string Title, string Kind, string Status, string Input, CursorFileChange[] Changes, string Output = "", string Diagnostics = "");
public sealed record CursorPermissionOption(string Id, string Name, string Kind);
public sealed record CursorPermissionRequest(CursorToolActivity Tool, CursorPermissionOption[] Options, CursorSubagent? Subagent = null);
