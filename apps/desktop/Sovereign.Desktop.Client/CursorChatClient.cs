using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Client;

public sealed record CursorChatModel(string Id, string Name)
{
    public string Details => ChatModelLabel.Details(Id, Name);
    public override string ToString() => ChatModelLabel.Display(Id, Name);
}

public enum CursorTurnOutcome { Completed, Redirected }

/// <summary>Official Cursor ACP client. Credentials stay with Cursor; no xAI/API-key fallback.</summary>
public sealed class CursorChatClient(string workspace, CursorProjectOptions? project = null, Action<CursorActivityEvent>? activity = null) : IDisposable
{
    public const string CliVersion = "2026.09.23-86fc751";
    public static string RuntimeDirectory => ResolveRuntimeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// Prefer a runtime copied for SI Desk. Otherwise use the Cursor CLI already installed for SI Desk.
    /// That folder is the connector, not the chat profile or saved keys.
    /// </summary>
    public static string ResolveRuntimeDirectory(string localAppData)
    {
        var versioned = Path.Combine("CursorAgent", CliVersion, "dist-package");
        var own = Path.Combine(localAppData, XaiDeskIdentity.DataFolderName, "Tools", versioned);
        if (IsRuntime(own)) return own;
        var legacy = Path.Combine(localAppData, XaiDeskIdentity.LegacyDataFolderName, "Tools", versioned);
        if (IsRuntime(legacy)) return legacy;
        var installed = Path.Combine(localAppData, "SovereignAI.Tools", versioned);
        if (IsRuntime(installed)) return installed;
        return own;
    }

    public static bool IsRuntime(string directory) =>
        File.Exists(Path.Combine(directory, "node.exe")) && File.Exists(Path.Combine(directory, "index.js"));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private CursorAcpSession? _session;
    private bool _disposed;
    private readonly bool _injectedTransport;
    private bool _injectedConnected;
    public string? SelectedModelId => _session?.SelectedModelId;
    public string SkillGuidance { get; set; } = "";
    public string? LastStopReason => _session?.LastStopReason;
    public bool SupportsImages => _session?.SupportsImages == true;
    public bool SupportsSubagents => _session?.SupportsSubagents == true;
    public CursorChatModel[] AdvertisedModels => _session?.Models ?? [];
    internal int? InternalProcessId => _process?.Id;
    internal string? InternalSessionId => _session?.SessionId;

    // Scripted transport for isolated verification; it never starts a runtime or touches a profile.
    internal CursorChatClient(string workspace, TextReader reader, TextWriter writer,
        CursorProjectOptions? project = null, Action<CursorActivityEvent>? activity = null)
        : this(workspace, project, activity)
    {
        _injectedTransport = true;
        _session = new CursorAcpSession(reader, writer, project, activity);
    }

    private ProcessStartInfo LaunchInfo(params string[] arguments)
    {
        var runtime = RuntimeDirectory;
        if (!IsRuntime(runtime))
            throw new FileNotFoundException(DeskConnectionErrors.CursorNotInstalled);
        Directory.CreateDirectory(workspace);
        var info = new ProcessStartInfo(Path.Combine(runtime, "node.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workspace,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add(Path.Combine(runtime, "index.js"));
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        foreach (var name in new[] { "CURSOR_API_KEY", "CURSOR_AUTH_TOKEN", "CURSOR_API_ENDPOINT", "NODE_OPTIONS", "ELECTRON_RUN_AS_NODE" }) info.Environment.Remove(name);
        return info;
    }

    public async Task LoginAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var process = Process.Start(LaunchInfo("login")) ?? throw new IOException("Cursor sign-in could not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct); var error = process.StandardError.ReadToEndAsync(ct);
        try {
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(output, error); // Drain without exposing login URLs/tokens in application logs.
            if (process.ExitCode != 0) throw new IOException(DeskConnectionErrors.CursorAuthFailed);
        } finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null)
        {
            if (_injectedTransport && !_injectedConnected)
            {
                await _session.ConnectAsync(Path.GetFullPath(workspace), ct);
                _injectedConnected = true;
            }
            return;
        }
        await PrepareWorkspaceAsync(workspace, project is not null, ct);
        _process = Process.Start(LaunchInfo(SessionArguments(project))) ?? throw new IOException("Cursor CLI could not start.");
        _ = DrainErrorsAsync(_process);
        _session = new CursorAcpSession(_process.StandardOutput, _process.StandardInput, project, activity);
        await _session.ConnectAsync(Path.GetFullPath(workspace), ct);
    }

    internal static string[] SessionArguments(CursorProjectOptions? options)
    {
        if (options is null) return ["acp"];
        // --force is Cursor's session-only autorun switch and still respects explicit
        // denies/admin policy. Permission callbacks alone cannot grant a command that
        // Cursor's allowlist rejects before emitting a request. Never write user config.
        return options.Mode == CursorProjectMode.Agent && options.Permissions == CursorPermissionPolicy.AlwaysAllow
            ? ["--sandbox", "enabled", "--force", "acp"]
            : ["--sandbox", "enabled", "acp"];
    }

    internal static async Task PrepareWorkspaceAsync(string workspace, bool project, CancellationToken ct)
    {
        if (project)
        {
            if (!Directory.Exists(workspace)) throw new DirectoryNotFoundException("The selected project folder no longer exists.");
            return; // User project configuration belongs to its owner, never to the chat client.
        }
        Directory.CreateDirectory(Path.Combine(workspace, ".cursor"));
        // This dedicated chat workspace contains no Sovereign sources, profiles, or attachments.
        await File.WriteAllTextAsync(Path.Combine(workspace, ".cursor", "cli.json"), JsonSerializer.Serialize(new {
            permissions = new { allow = Array.Empty<string>(), deny = new[] { "Shell(*)", "Read(**)", "Write(**)", "WebFetch(*)", "Mcp(*:*)" } }
        }), ct);
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        try { while (await process.StandardError.ReadLineAsync() is not null) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public static string SwarmHireCatalog(bool? mixed, string? roleModelsDirectory, IReadOnlyList<CursorChatModel> models, string? chatModelId)
    {
        if (mixed == true && !string.IsNullOrWhiteSpace(roleModelsDirectory))
            return SwarmRoleModels.Catalog(models, chatModelId, SwarmRoleModels.Load(roleModelsDirectory));
        if (mixed == true)
            return SwarmModelHire.Catalog(models, chatModelId);
        return SwarmRoleModels.SingleCatalog(models, chatModelId);
    }

    public async Task<CursorChatModel[]> ModelsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await StartAsync(ct); return _session!.Models; }
        catch { Dispose(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<CursorTurnOutcome> SendAsync(string model, string text, Action<string> delta, CancellationToken ct, IReadOnlyList<ChatImage>? images = null)
    {
        await _gate.WaitAsync(ct);
        try { await StartAsync(ct); return await _session!.SendAsync(model, SkillLibrary.ApplyGuidance(text, SkillGuidance), delta, ct, images); }
        catch (Exception) when (project?.SubagentsAllowed == true)
        {
            // Notify the native parent so Cursor can cancel its helpers, then close
            // this app-owned process tree even if the notification cannot be sent.
            try
            {
                using var cancelWrite = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                if (_session is not null) await _session.CancelAsync(cancelWrite.Token);
                await Task.Delay(150);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { Dispose(); }
            throw;
        }
        // Stop only this app-owned CLI tree. Partial output never counts as a completed response.
        catch { Dispose(); throw; }
        finally { _gate.Release(); }
    }

    /// <summary>Interrupts only an outstanding prompt. Its owner drains the acknowledgement before sending again.</summary>
    public async Task<bool> RequestSteeringAsync(CancellationToken ct)
    {
        var session = _session;
        if (_disposed || session is null) return false;
        // The prompt owns _gate and stdout; cancellation only writes a notification.
        try { return await session.RequestSteeringAsync(ct); }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        _disposed = true;
        var process = _process; _process = null;
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { process?.Dispose(); }
    }
}

internal sealed class CursorAcpSession(TextReader reader, TextWriter writer, CursorProjectOptions? project = null,
    Action<CursorActivityEvent>? activity = null, TimeSpan? steeringDrainTimeout = null)
{
    private int _nextId;
    private string? _sessionId;
    private readonly Dictionary<string, CursorToolActivity> _tools = [];
    private bool _reportActivity;
    private readonly CursorSubagents _subagents = new();
    private bool _supportsSubagents;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private PromptTurn? _activePrompt;
    private volatile bool _failed;

    private sealed class PromptTurn(CancellationToken hardStop) : IDisposable
    {
        public readonly CancellationTokenSource Drain = new();
        public readonly CancellationTokenSource Permissions = CancellationTokenSource.CreateLinkedTokenSource(hardStop);
        public readonly CancellationTokenSource Read = CancellationTokenSource.CreateLinkedTokenSource(hardStop);
        public int SteeringRequested;
        public bool SteeringSent;
        public Exception? SteeringFailure;

        public void Dispose() { Read.Dispose(); Permissions.Dispose(); Drain.Dispose(); }
    }
    public CursorChatModel[] Models { get; private set; } = [];
    public string? SelectedModelId { get; private set; }
    public string? LastStopReason { get; private set; }
    public bool SupportsImages { get; private set; }
    public bool SupportsSubagents => _supportsSubagents;
    internal string? SessionId => _sessionId;

    public async Task ConnectAsync(string workspace, CancellationToken ct)
    {
        var init = await CallAsync("initialize", new { protocolVersion = 1,
            clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false, _meta = new { subagents = project?.SubagentsAllowed == true } },
            clientInfo = new { name = "SI Desk", version = DesktopClientVersion.Current } }, null, ct);
        if (init.GetProperty("protocolVersion").GetInt32() != 1) throw new IOException("Unsupported Cursor ACP protocol.");
        SupportsImages = init.TryGetProperty("agentCapabilities", out var capabilities) && capabilities.TryGetProperty("promptCapabilities", out var promptCaps) && promptCaps.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.True;
        _supportsSubagents = capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("sessionCapabilities", out var sessions) && sessions.TryGetProperty("subagents", out _);
        // Older ACP runtimes can still serve ordinary project chats. Helpers are offered only after negotiation.
        var session = await CallAsync("session/new", new { cwd = workspace, mcpServers = SwarmManagerSession.McpServers(project, init) }, null, ct);
        _sessionId = session.GetProperty("sessionId").GetString() ?? throw new IOException("Cursor returned no session.");
        var listing = CursorModelCatalog.Parse(session.GetProperty("models"));
        Models = listing.Models;
        await CallAsync("session/set_mode", new { sessionId = _sessionId, modeId = project?.ModeId ?? "ask" }, null, ct);
    }

    public async Task<CursorTurnOutcome> SendAsync(string model, string text, Action<string> delta, CancellationToken ct, IReadOnlyList<ChatImage>? images = null)
    {
        ThrowIfFailed();
        if (_sessionId is null || !Models.Any(m => m.Id == model)) throw new IOException("Choose a model advertised by Cursor. No automatic model fallback is used.");
        if (images is { Count: > 0 } && !SupportsImages) throw new IOException("Cursor did not advertise image input. Remove images or choose another connection.");
        LastStopReason = null;
        await CallAsync("session/set_model", new { sessionId = _sessionId, modelId = model }, null, ct);
        SelectedModelId = model;
        CursorToolParser.Reset(_tools);
        _reportActivity = true;
        var length = 0;
        JsonElement reply;
        var swarmBrief = project?.SwarmBrief;
        var swarmHire = "";
        if (swarmBrief is { Length: > 0 })
        {
            var swarmCatalog = CursorChatClient.SwarmHireCatalog(project?.SwarmMixedModels, project?.SwarmRoleModelsDirectory, Models, model);
            swarmHire = swarmBrief + "\n\n" + swarmCatalog + "\n\n";
        }
        var helperModelClause = swarmHire.Length > 0
            ? "Use the runtime-advertised generalPurpose type and pass one modelId from the swarm hire catalog on the helper tool's model field. "
            : "Use the runtime-advertised generalPurpose type and omit model overrides. ";
        using var turn = new PromptTurn(ct);
        using var drainRegistration = turn.Drain.Token.Register(() => turn.Read.Cancel());
        try { reply = await CallAsync("session/prompt", new { sessionId = _sessionId, prompt = new object[] { new {
            type = "text", text = Sovereign.Desktop.Core.WorkDoctrine.PromptPrefix() + (project is null
                ? "SI Desk conversation: answer using only the supplied conversation. Do not call tools, access files, or act on accounts.\n\n"
                : project.Mode == CursorProjectMode.Plan
                    ? "SI Desk planning session: inspect the selected workspace and propose a plan. Stay read-only; do not edit files or implement the plan. The user must leave Plan by choosing Always ask or Always allow in the composer and send a new instruction before implementation.\n\n"
            : "SI Desk project session: work in the selected workspace. Make requested file changes using your tools and report actual results. Follow the project's instructions. Do not commit, push, publish, or act on external accounts unless the user explicitly asks.\n\n") + (project?.SubagentsAllowed == true && _supportsSubagents
            ? "Native subagents are enabled. Delegate independent work to up to three direct generalPurpose helpers concurrently, at most sixteen identities per session. " + helperModelClause + "Helpers inherit this chat's mode and permission policy and must follow the same workspace instructions; they must not spawn nested helpers or act beyond the user's request. Give implementation helpers separate files or tasks to avoid conflicting edits. Summarize their actual results. Stop response stops the entire session. Helpers load the work-doctrine skill by default. Put that skill at the top of every helper task.\n\n" +
                (project.Mode == CursorProjectMode.Plan ? "Plan helpers must stay read-only: inspect and report, never edit files or execute commands.\n\n" :
                    project.Permissions == CursorPermissionPolicy.AlwaysAsk ? "Helpers use Always ask: request the user's approval through runtime permissions when required, just like the parent.\n\n" :
                    "Helpers use this chat's Always allow setting, subject to the existing runtime sandbox and project policy.\n\n") : "") + swarmHire + text
        } }.Concat((images ?? []).Select(i => (object)new { type = "image", mimeType = i.MimeType, data = i.Data })).ToArray() }, piece => { length += piece.Length; if (length > 200000) throw new IOException("Cursor response exceeded the chat limit."); delta(piece); }, ct, turn);
            foreach (var child in _subagents.FinishRunning(turn.SteeringSent ? "Correction requested · completion unconfirmed" : "Parent turn ended · completion unconfirmed"))
                Report(new("subagent", Subagent: child));
        }
        catch
        {
            foreach (var child in _subagents.FinishRunning(ct.IsCancellationRequested ? "Stop requested · completion unconfirmed" : "Connection interrupted · completion unconfirmed"))
                Report(new("subagent", Subagent: child));
            throw;
        }
        finally { _reportActivity = false; CursorToolParser.Finish(_tools); }
        ct.ThrowIfCancellationRequested();
        LastStopReason = reply.GetProperty("stopReason").GetString();
        if (LastStopReason == "cancelled" && turn.SteeringSent) return CursorTurnOutcome.Redirected;
        if (LastStopReason != "end_turn" || length == 0) throw new IOException("Cursor response is incomplete (" + LastStopReason + ").");
        return CursorTurnOutcome.Completed;
    }

    private async Task WriteAsync(object value, CancellationToken ct)
    {
        await _writerGate.WaitAsync(ct);
        try { await WriteUnlockedAsync(value, ct); }
        finally { _writerGate.Release(); }
    }

    private async Task WriteUnlockedAsync(object value, CancellationToken ct)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct);
        await writer.FlushAsync(ct);
    }

    internal Task CancelAsync(CancellationToken ct) => _sessionId is null ? Task.CompletedTask
        : WriteAsync(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId = _sessionId } }, ct);

    internal async Task<bool> RequestSteeringAsync(CancellationToken ct)
    {
        var turn = Volatile.Read(ref _activePrompt);
        if (turn is null) return false;
        await _writerGate.WaitAsync(ct);
        try
        {
            // A queued cancel must never target a later prompt or a set_model request.
            if (!ReferenceEquals(turn, _activePrompt)) return false;
            if (Volatile.Read(ref turn.SteeringRequested) != 0) return true;
            Volatile.Write(ref turn.SteeringRequested, 1);
            try
            {
                turn.Drain.CancelAfter(steeringDrainTimeout ?? TimeSpan.FromSeconds(15));
                turn.Permissions.Cancel();
                using var writeStop = CancellationTokenSource.CreateLinkedTokenSource(ct, turn.Read.Token);
                await WriteUnlockedAsync(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId = _sessionId } }, writeStop.Token);
                turn.SteeringSent = true;
                return true;
            }
            catch (Exception ex)
            {
                turn.SteeringFailure = ex;
                _failed = true;
                turn.Drain.Cancel();
                throw;
            }
        }
        finally { _writerGate.Release(); }
    }

    private void ThrowIfFailed()
    {
        if (_failed) throw new IOException("Cursor connection is no longer usable. Reconnect before sending again.");
    }

    private static bool IsSteeringRequested(PromptTurn? turn) => turn is not null && Volatile.Read(ref turn.SteeringRequested) != 0;

    private async Task<JsonElement> CallAsync(string method, object parameters, Action<string>? delta, CancellationToken ct, PromptTurn? turn = null)
    {
        ThrowIfFailed();
        var id = ++_nextId;
        var readToken = turn?.Read.Token ?? ct;
        try
        {
        await _writerGate.WaitAsync(ct);
        try
        {
            await WriteUnlockedAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
            if (turn is not null) Volatile.Write(ref _activePrompt, turn);
        }
        finally { _writerGate.Release(); }
        while (true)
        {
            readToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(readToken) ?? throw new IOException("Cursor disconnected. Reconnect before sending again.");
            if (line.Length > 2_000_000) throw new IOException("Cursor protocol message exceeded its size limit.");
            using var json = JsonDocument.Parse(line);
            var message = json.RootElement;
            if (message.TryGetProperty("method", out var serverMethod))
            {
                if (message.TryGetProperty("id", out var requestId))
                {
                    if (serverMethod.GetString() == "session/request_permission")
                    {
                        string? chosen = null;
                        if (project is not null)
                        {
                            var permission = message.GetProperty("params");
                            var permissionSession = permission.GetProperty("sessionId").GetString()!;
                            var childPermission = permissionSession != _sessionId;
                            var child = childPermission ? _subagents.Get(permissionSession) : null;
                            if (childPermission && (project.SubagentsAllowed != true || !_supportsSubagents || child?.State != "running"))
                                throw new IOException("Cursor permission belongs to an inactive or unknown subagent.");
                            CursorToolActivity tool;
                            if (childPermission)
                            {
                                child = _subagents.ObserveTool(permissionSession, permission.GetProperty("toolCall"));
                                tool = _subagents.GetTool(permissionSession, permission.GetProperty("toolCall").GetProperty("toolCallId").GetString()!)!;
                                Report(new("subagent", Subagent: child, Tool: tool));
                            }
                            else tool = MergeTool(permission.GetProperty("toolCall"));
                            var choices = permission.GetProperty("options").EnumerateArray().Select(o => new CursorPermissionOption(
                                o.GetProperty("optionId").GetString()!, o.GetProperty("name").GetString()!, o.GetProperty("kind").GetString()!)).ToArray();
                            if (childPermission && project.Mode == CursorProjectMode.Plan && tool.Kind is not ("read" or "search"))
                            {
                                child = _subagents.SetWork(permissionSession, "Blocked by Plan · " + tool.Title);
                                Report(new("subagent", Subagent: child));
                                await WriteAsync(new { jsonrpc = "2.0", id = requestId.Clone(), result = new { outcome = new { outcome = "cancelled" } } }, readToken);
                                continue;
                            }
                            if (!IsSteeringRequested(turn) && project.Mode == CursorProjectMode.Agent && project.Permissions == CursorPermissionPolicy.AlwaysAllow)
                            {
                                // Approve only this advertised request. Never write global/project allow rules.
                                chosen = choices.FirstOrDefault(o => o.Kind == "allow_once")?.Id;
                                if (chosen is not null) Report(new("resumed", "Allowed by this chat's Always allow setting: " + tool.Title));
                            }
                            if (!IsSteeringRequested(turn) && chosen is null && project.RequestPermission is not null)
                            {
                                if (childPermission) Report(new("subagent", Subagent: _subagents.SetWork(permissionSession, "Waiting for your approval · " + tool.Title)));
                                Report(new("permission", tool.Title));
                                var permissionToken = turn?.Permissions.Token ?? ct;
                                try { chosen = await project.RequestPermission(new(tool, choices, child), permissionToken).WaitAsync(permissionToken).WaitAsync(readToken); }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested && IsSteeringRequested(turn)) { chosen = null; }
                                finally
                                {
                                    if (childPermission) Report(new("subagent", Subagent: _subagents.SetWork(permissionSession, "Waiting for runtime · " + tool.Title)));
                                    Report(new("resumed", "Waiting for Cursor"));
                                }
                            }
                            // No persistent grants and no option IDs invented by a client callback.
                            if (!choices.Any(o => o.Id == chosen && o.Kind is "allow_once" or "reject_once")) chosen = null;
                        }
                        // Decide under the same writer gate as steering, so no approval follows its cancel.
                        await _writerGate.WaitAsync(readToken);
                        try
                        {
                            if (IsSteeringRequested(turn)) chosen = null;
                            object outcome = chosen is null ? new { outcome = "cancelled" } : new { outcome = "selected", optionId = chosen };
                            await WriteUnlockedAsync(new { jsonrpc = "2.0", id = requestId.Clone(), result = new { outcome } }, readToken);
                        }
                        finally { _writerGate.Release(); }
                    }
                    else if (project is not null && serverMethod.GetString() == "cursor/create_plan")
                    {
                        var plan = message.GetProperty("params");
                        if (plan.TryGetProperty("sessionId", out var sid) && sid.GetString() != _sessionId)
                        {
                            var child = _subagents.Get(sid.GetString()!);
                            if (child?.State != "running") throw new IOException("Cursor plan belongs to another session.");
                            Report(new("subagent", Subagent: _subagents.SetWork(child.SessionId, "Plan ready for review")));
                            await WriteAsync(new { jsonrpc = "2.0", id = requestId.Clone(), result = new { outcome = new {
                                outcome = "rejected", reason = "Return the proposal to the parent for review. Do not change modes or implement a Plan." } } }, readToken);
                            continue;
                        }
                        if (plan.TryGetProperty("todos", out var todos)) Report(new("plan", Plan: ReadPlan(todos)));
                        // Show the actual proposal and return promptly. A plan is never approval to implement.
                        delta?.Invoke("\n\n" + plan.GetProperty("plan").GetString() + "\n\n[Plan ready for review. Choose Always ask or Always allow beside the message box, then send your instruction to implement it.]\n");
                        await WriteAsync(new { jsonrpc = "2.0", id = requestId.Clone(), result = new { outcome = new {
                            outcome = "rejected", reason = "Plan displayed for review. Do not implement or switch modes. The user must leave Plan in the composer and send a new instruction." } } }, readToken);
                    }
                    else if (project is not null && serverMethod.GetString() == "cursor/ask_question")
                        await WriteAsync(new { jsonrpc = "2.0", id = requestId.Clone(), result = new { outcome = new { outcome = "skipped" } } }, readToken);
                    else await WriteAsync(new { jsonrpc = "2.0", id = requestId.Clone(), error = new { code = -32601, message = "Unsupported ACP client request. Ask the user in the conversation if input is needed." } }, readToken);
                    continue;
                }
                if (serverMethod.GetString() == "cursor/update_todos" && _reportActivity)
                {
                    var todos = message.GetProperty("params");
                    if (todos.TryGetProperty("sessionId", out var sid) && sid.GetString() != _sessionId)
                    {
                        var child = _subagents.Get(sid.GetString()!);
                        if (child?.State != "running") throw new IOException("Cursor plan belongs to another session.");
                        Report(new("subagent", Subagent: _subagents.SetWork(child.SessionId, "Updating plan")));
                        continue;
                    }
                    Report(new("plan", Plan: ReadPlan(todos.GetProperty("todos")), Merge: todos.TryGetProperty("merge", out var merge) && merge.GetBoolean()));
                    continue;
                }
                if (serverMethod.GetString() == "cursor/task") continue; // Lifecycle arrives through session/update.
                if (serverMethod.GetString() != "session/update") continue;
                var p = message.GetProperty("params");
                var eventSession = p.GetProperty("sessionId").GetString()!;
                var update = p.GetProperty("update");
                var kind = update.GetProperty("sessionUpdate").GetString();
                if (eventSession != _sessionId || kind is "subagent_spawned" or "subagent_state_update")
                {
                    if (project?.SubagentsAllowed != true || !_supportsSubagents) throw new IOException("Unexpected subagent update while disabled.");
                    if (kind == "current_mode_update" && update.GetProperty("currentModeId").GetString() != project.ModeId)
                        throw new IOException("Cursor subagent changed mode unexpectedly. Reconnect using the mode selected in this chat.");
                    // Child current_model_update and _meta.cursor.model are runtime metadata.
                    // Do not fill missing identity from SelectedModelId, Models, or task overrides.
                    var child = _subagents.Observe(_sessionId!, eventSession, update, kind == "subagent_spawned" ? SubagentDisplayName(update) : null);
                    var tool = kind is "tool_call" or "tool_call_update" ? _subagents.GetTool(eventSession, update.GetProperty("toolCallId").GetString()!) : null;
                    if (child is not null) Report(new("subagent", Subagent: child, Tool: tool));
                    continue;
                }
                if (kind == "current_mode_update" && update.GetProperty("currentModeId").GetString() != (project?.ModeId ?? "ask"))
                    throw new IOException("Cursor changed mode unexpectedly. Reconnect using the mode selected in this chat.");
                if (project is not null && kind is "tool_call" or "tool_call_update")
                {
                    var activity = MergeTool(update);
                    project.ToolActivity?.Invoke(activity);
                    Report(new("tool", Tool: activity));
                }
                if (kind == "agent_thought_chunk" && update.GetProperty("content").GetProperty("type").GetString() == "text")
                    Report(new("reasoning", update.GetProperty("content").GetProperty("text").GetString() ?? ""));
                if (kind == "plan") Report(new("plan", Plan: ReadPlan(update.GetProperty("entries"))));
                if (kind == "agent_message_chunk"
                    && update.GetProperty("content").GetProperty("type").GetString() == "text")
                {
                    Report(new("message"));
                    delta?.Invoke(update.GetProperty("content").GetProperty("text").GetString() ?? "");
                }
                continue;
            }
            if (!message.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var value) || value != id)
                throw new IOException("Unexpected Cursor response identity.");
            if (message.TryGetProperty("error", out var error)) throw new IOException("Cursor: " + error.GetProperty("message").GetString() + ". Check Cursor sign-in and account allowance.");
            return message.GetProperty("result").Clone();
        }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && turn?.Drain.IsCancellationRequested == true)
        {
            _failed = true;
            var steeringFailure = turn!.SteeringFailure;
            throw new IOException(steeringFailure is null
                ? "Cursor did not acknowledge the correction within 15 seconds. Reconnect before sending again."
                : "Cursor could not receive the correction. Reconnect before sending again.", steeringFailure ?? ex);
        }
        catch { _failed = true; throw; }
        finally
        {
            if (turn is not null)
            {
                // Synchronize completion with an in-flight cancellation write before disposing its tokens.
                await _writerGate.WaitAsync(CancellationToken.None);
                try { if (ReferenceEquals(_activePrompt, turn)) Volatile.Write(ref _activePrompt, null); }
                finally { _writerGate.Release(); }
                if (turn.SteeringFailure is not null && !ct.IsCancellationRequested)
                    throw new IOException("Cursor could not receive the correction. Reconnect before sending again.", turn.SteeringFailure);
            }
        }
    }

    private void Report(CursorActivityEvent update) { if (_reportActivity) activity?.Invoke(update); }
    private static CursorPlanItem[] ReadPlan(JsonElement entries) => entries.EnumerateArray().Take(100).Select((entry, i) => new CursorPlanItem(
        entry.TryGetProperty("id", out var id) ? id.GetString() ?? i.ToString() : i.ToString(),
        entry.GetProperty("content").GetString() ?? "", entry.GetProperty("status").GetString() ?? "pending")).ToArray();

    private CursorToolActivity MergeTool(JsonElement update) => CursorToolParser.Merge(update, _tools);

    private string? SubagentDisplayName(JsonElement update)
    {
        if (!update.TryGetProperty("_meta", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("cursor", out var cursor) || cursor.ValueKind != JsonValueKind.Object ||
            !cursor.TryGetProperty("toolCallId", out var id) || id.ValueKind != JsonValueKind.String) return null;
        var toolId = id.GetString()!;
        var tool = CursorToolParser.Find(_tools, toolId);
        if (tool is null)
        {
            // Native lifecycle uses the call identity; ACP may append a newline
            // and provider function identity. Require an unambiguous delimiter match.
            var matches = _tools.Values.Where(t => t.Id.StartsWith(toolId + "\n", StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length != 1) return null;
            tool = matches[0];
        }
        if (string.IsNullOrWhiteSpace(tool.Input)) return null;
        try
        {
            using var input = JsonDocument.Parse(tool.Input);
            return input.RootElement.ValueKind == JsonValueKind.Object && input.RootElement.TryGetProperty("description", out var description) &&
                description.ValueKind == JsonValueKind.String ? description.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
