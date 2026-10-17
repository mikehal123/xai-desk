using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Client;

/// <summary>
/// Drives the installed Grok CLI (<c>grok agent --no-leader stdio</c>) as the chat agent.
/// Ideas adapted from github.com/RongleCat/grok-app (MIT):
/// src-tauri/src/acp_client.rs, agent_subagents.rs, permission.rs.
/// </summary>
public sealed class GrokBuildChatClient : IDisposable
{
    private readonly string _workspace;
    private readonly CursorProjectOptions? _project;
    private readonly Action<CursorActivityEvent>? _activity;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly CursorSubagents _subagents = new();
    private readonly Dictionary<string, CursorToolActivity> _tools = [];
    private readonly Dictionary<string, string> _toolNames = [];
    private readonly Dictionary<string, CursorSubagent> _xaiSubagents = [];
    private readonly List<string> _ignoredReplay = [];
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private Process? _process;
    private TextReader? _reader;
    private TextWriter? _writer;
    private bool _injected;
    private bool _connected;
    private bool _disposed;
    private bool _reportActivity;
    private bool _replaying;
    private int _pumpStarted;
    private int _nextId;
    private int _assistantChars;
    private string? _sessionId;
    private bool _supportsSubagents;
    private DateTimeOffset _lastProtocolAt = DateTimeOffset.UtcNow;

    public string SkillGuidance { get; set; } = "";
    public string? LastStopReason { get; private set; }
    public string? ModeWarning { get; private set; }
    public bool SupportsSubagents => _supportsSubagents;
    public bool SupportsImages { get; private set; }
    public CursorChatModel[] Models { get; private set; } = [];
    public string? ResumeSessionId { get; set; }
    public string? SessionId => _sessionId;
    public bool ClearedResumeSession { get; private set; }
    public Func<GrokBuildPlanCard, CancellationToken, Task<GrokBuildPlanAnswer>>? ExitPlanMode { get; set; }
    public Func<GrokBuildQuestionCard, CancellationToken, Task<GrokBuildQuestionAnswer>>? AskUserQuestion { get; set; }
    internal Func<DateTimeOffset> Clock { get; set; } = static () => DateTimeOffset.UtcNow;
    internal TimeSpan PromptSilence { get; set; } = GrokBuildAcp.PromptSilenceWindow;
    internal IReadOnlyList<string> IgnoredReplayUpdates => _ignoredReplay;

    public GrokBuildChatClient(string workspace, CursorProjectOptions? project = null, Action<CursorActivityEvent>? activity = null)
    {
        _workspace = workspace;
        _project = project;
        _activity = activity;
    }

    internal GrokBuildChatClient(string workspace, TextReader reader, TextWriter writer, CursorProjectOptions? project = null, Action<CursorActivityEvent>? activity = null)
        : this(workspace, project, activity)
    {
        _injected = true;
        _reader = reader;
        _writer = writer;
    }

    public static Task LoginAsync(CancellationToken ct) => RunVisibleAuthAsync("login", ct);

    public static Task LogoutAsync(CancellationToken ct) => RunVisibleAuthAsync("logout", ct);

    internal static void SetLogFile(string? path) => GrokBuildJson.SetLogFile(path);

    /// <summary>
    /// Arguments and environment for the CLI. Allowing subagents never adds
    /// <c>--no-subagents</c> and never sets <c>GROK_SUBAGENTS=0</c>.
    /// </summary>
    internal static GrokBuildLaunch PlanLaunch(CursorProjectOptions? project)
    {
        var arguments = new List<string> { "agent", "--no-leader" };
        if (project?.Mode == CursorProjectMode.Agent && project.Permissions == CursorPermissionPolicy.AlwaysAllow)
            arguments.Add("--always-approve");
        arguments.Add("stdio");
        IReadOnlyList<string> removed = project?.SubagentsAllowed == true ? new[] { "GROK_SUBAGENTS" } : Array.Empty<string>();
        return new GrokBuildLaunch(arguments, new Dictionary<string, string>(), removed);
    }

    internal static void ApplyLaunchEnvironment(IDictionary<string, string?> environment, GrokBuildLaunch launch)
    {
        foreach (var pair in launch.Environment)
            environment[pair.Key] = pair.Value;
        foreach (var key in launch.RemovedEnvironment)
            if (environment.TryGetValue(key, out var value) && value == "0")
                environment.Remove(key);
    }

    private static async Task RunVisibleAuthAsync(string command, CancellationToken ct)
    {
        var executable = FindExecutable() ?? throw new FileNotFoundException(DeskConnectionErrors.GrokNotInstalled);
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            CreateNoWindow = false,
            ArgumentList = { command }
        };
        using var process = Process.Start(info) ?? throw new IOException($"Grok {command} could not start.");
        try
        {
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                throw new IOException($"Grok {command} exited with code {process.ExitCode}.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
        }
    }

    public static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("XAI_DESK_GROK");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "bin", "grok.exe");
        if (File.Exists(home)) return home;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim().Trim('"'), "grok.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task<CursorChatModel[]> ModelsAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        return Models;
    }

    public async Task<CursorTurnOutcome> SendAsync(string model, string text, Action<string> delta, CancellationToken ct, IReadOnlyList<ChatImage>? images = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureConnectedAsync(ct);
        if (!Models.Any(m => m.Id == model)) throw new IOException("Choose a Grok model advertised by Grok Build.");
        LastStopReason = null;
        _assistantChars = 0;
        await CallAsync("session/set_model", new { sessionId = _sessionId, modelId = model }, null, ct);
        var prompt = Sovereign.Desktop.Core.WorkDoctrine.PromptPrefix() + (string.IsNullOrWhiteSpace(SkillGuidance) ? "" : SkillGuidance.Trim() + "\n\n") + SessionPreface(model) + text;
        _reportActivity = true;
        var length = 0;
        try
        {
            var reply = await CallAsync("session/prompt", new
            {
                sessionId = _sessionId,
                prompt = new object[] { new { type = "text", text = prompt } }
                    .Concat((images ?? []).Select(image => (object)new { type = "image", mimeType = image.MimeType, data = image.Data }))
                    .ToArray()
            }, piece =>
            {
                length += piece.Length;
                _assistantChars = length;
                if (length > 200000) throw new IOException("Grok Build response exceeded the chat limit.");
                delta(piece);
            }, ct);
            LastStopReason = reply.TryGetProperty("stopReason", out var reason) ? GrokBuildJson.ScalarText(reason) : null;
            if (length == 0 && GrokBuildJson.IsUsageFailure(LastStopReason))
                throw new GrokBuildUsageException(LastStopReason!);
            if (length == 0 && GrokBuildJson.TryReadUsageFailure(reply, out var usageText))
                throw new GrokBuildUsageException(usageText);
        }
        finally
        {
            foreach (var child in FinishXaiSubagents(ct.IsCancellationRequested))
                Report(new("subagent", Subagent: child));
            _reportActivity = false;
            foreach (var child in _subagents.FinishRunning(ct.IsCancellationRequested ? "Stop requested" : "Parent turn ended"))
                Report(new("subagent", Subagent: child));
        }
        ct.ThrowIfCancellationRequested();
        if (LastStopReason == "cancelled") return CursorTurnOutcome.Redirected;
        if (LastStopReason is not ("end_turn" or "end" or null) && length == 0)
            throw new IOException("Grok Build response is incomplete (" + LastStopReason + ").");
        if (length == 0) throw new IOException("Grok Build returned no answer.");
        return CursorTurnOutcome.Completed;
    }

    public async Task<bool> RequestSteeringAsync(CancellationToken ct)
    {
        if (_sessionId is null) return false;
        await WriteAsync(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId = _sessionId } }, ct);
        return true;
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_connected) return;
            if (!_injected)
            {
                var executable = FindExecutable() ?? throw new FileNotFoundException(DeskConnectionErrors.GrokNotInstalled);
                Directory.CreateDirectory(_workspace);
                var info = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetFullPath(_workspace),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                var launch = PlanLaunch(_project);
                foreach (var argument in launch.Arguments)
                    info.ArgumentList.Add(argument);
                ApplyLaunchEnvironment(info.Environment, launch);
                _process = Process.Start(info) ?? throw new IOException("Grok CLI could not start.");
                _ = DrainErrorsAsync(_process);
                _reader = _process.StandardOutput;
                _writer = _process.StandardInput;
            }
            var init = await CallAsync("initialize", new
            {
                protocolVersion = 1,
                clientCapabilities = new
                {
                    fs = new { readTextFile = false, writeTextFile = false },
                    terminal = false,
                    _meta = new { subagents = _project?.SubagentsAllowed == true }
                },
                clientInfo = new { name = "SI Desk", version = DesktopClientVersion.Current }
            }, null, ct);
            _supportsSubagents = init.TryGetProperty("agentCapabilities", out var capabilities)
                && capabilities.TryGetProperty("sessionCapabilities", out var sessions)
                && sessions.TryGetProperty("subagents", out _);
            var session = await OpenSessionAsync(init, ct);
            _sessionId = session.GetProperty("sessionId").GetString() ?? throw new IOException("Grok Build returned no session.");
            Models = ReadModels(session);
            if (Models.Length == 0) throw new IOException("Grok Build did not advertise a Grok model.");
            var modeId = _project?.ModeId ?? "ask";
            try
            {
                await CallAsync("session/set_mode", new { sessionId = _sessionId, modeId }, null, ct);
                ModeWarning = null;
            }
            catch (IOException ex) when (ex is not GrokBuildUsageException)
            {
                ModeWarning = "Grok Build stayed in its current mode because " + modeId + " was not accepted (" + ex.Message + "). File tools follow that mode.";
            }
            _connected = true;
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonElement> OpenSessionAsync(JsonElement initialize, CancellationToken ct)
    {
        var cwd = Path.GetFullPath(_workspace);
        var resume = ResumeSessionId;
        if (!string.IsNullOrWhiteSpace(resume) && GrokBuildAcp.SupportsLoadSession(initialize))
        {
            try
            {
                var loaded = await CallAsync("session/load", new { sessionId = resume, cwd, mcpServers = SwarmManagerSession.McpServers(_project, initialize) }, null, ct);
                return EnsureSessionId(loaded, resume);
            }
            catch (IOException)
            {
                ClearedResumeSession = true;
                ResumeSessionId = null;
            }
        }
        return await CallAsync("session/new", new { cwd, mcpServers = SwarmManagerSession.McpServers(_project, initialize) }, null, ct);
    }

    /// <summary>
    /// A successful load already names the session in the request. Keep that id when the result omits it.
    /// </summary>
    private static JsonElement EnsureSessionId(JsonElement loaded, string sessionId)
    {
        if (loaded.ValueKind == JsonValueKind.Object
            && loaded.TryGetProperty("sessionId", out var loadedId)
            && !string.IsNullOrWhiteSpace(loadedId.GetString()))
            return loaded;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", sessionId);
            if (loaded.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in loaded.EnumerateObject())
                {
                    if (property.NameEquals("sessionId")) continue;
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        stream.Position = 0;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static CursorChatModel[] ReadModels(JsonElement session)
    {
        if (!session.TryGetProperty("models", out var models) || !models.TryGetProperty("availableModels", out var available))
            return [];
        return available.EnumerateArray()
            .Select(m => new CursorChatModel(
                m.TryGetProperty("modelId", out var id) ? id.GetString() ?? "" : "",
                m.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""))
            .Where(m => XaiDeskIdentity.IsGrokModel(m.Id) || XaiDeskIdentity.IsGrokModel(m.Name))
            .ToArray();
    }

    private async Task<JsonElement> CallAsync(string method, object parameters, Action<string>? delta, CancellationToken ct)
    {
        var id = ++_nextId;
        var prompt = method == "session/prompt";
        var replay = method == "session/load";
        if (replay) _replaying = true;
        try
        {
            if (prompt) _lastProtocolAt = Clock();
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
            StartPump();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line;
                try { line = await ReadNextAsync(prompt, ct); }
                catch (PromptSilenceException)
                {
                    if (_assistantChars > 0) return EndTurnResult();
                    throw new IOException(DeskConnectionErrors.GrokSilence);
                }
                if (line is null) throw new IOException("Grok Build disconnected. Reconnect before sending again.");
                _lastProtocolAt = Clock();
                if (line.Length > 2_000_000) throw new IOException("Grok Build protocol message exceeded its size limit.");
                if (string.IsNullOrWhiteSpace(line)) continue;
                GrokBuildJson.Log(line);
                JsonDocument json;
                try { json = JsonDocument.Parse(line); }
                catch (JsonException ex)
                {
                    GrokBuildJson.Log("parse-skip: JsonException: " + ex.Message);
                    continue;
                }
                using (json)
                {
                    var message = json.RootElement;
                    try
                    {
                        GrokBuildJson.ReadMeters(message);
                        if (!message.TryGetProperty("method", out _))
                        {
                            if (!message.TryGetProperty("id", out var responseId) || !GrokBuildJson.TryGetInt32(responseId, out var value) || value != id)
                                continue;
                            if (message.TryGetProperty("error", out var error))
                                throw ProviderFailure(error);
                            if (!message.TryGetProperty("result", out var payload))
                                throw new IOException("Grok Build returned no result.");
                            return payload.Clone();
                        }
                        var serverMethod = message.GetProperty("method");
                        await HandleServerAsync(GrokBuildJson.ScalarText(serverMethod), message, delta, ct);
                    }
                    catch (GrokBuildUsageException) { throw; }
                    catch (Exception ex) when (ex is InvalidOperationException or JsonException or KeyNotFoundException or FormatException)
                    {
                        GrokBuildJson.Log("parse-skip: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }
        finally { if (replay) _replaying = false; }
    }

    private static JsonElement EndTurnResult()
    {
        using var document = JsonDocument.Parse("""{"stopReason":"end_turn"}""");
        return document.RootElement.Clone();
    }

    private void StartPump()
    {
        if (Interlocked.Exchange(ref _pumpStarted, 1) != 0) return;
        var reader = _reader ?? throw new IOException("Grok Build is not connected.");
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? line;
                    try { line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                    {
                        _inbound.Writer.TryComplete(ex);
                        return;
                    }
                    if (line is null)
                    {
                        _inbound.Writer.TryComplete();
                        return;
                    }
                    if (!_inbound.Writer.TryWrite(line)) return;
                }
            }
            catch (Exception ex)
            {
                _inbound.Writer.TryComplete(ex);
            }
        });
    }

    private async Task<string?> ReadNextAsync(bool watchSilence, CancellationToken ct)
    {
        StartPump();
        if (watchSilence && Clock() - _lastProtocolAt >= PromptSilence)
            throw new PromptSilenceException();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wait = _inbound.Reader.WaitToReadAsync(linked.Token).AsTask();
        if (watchSilence)
        {
            while (!wait.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                await Task.WhenAny(wait, Task.Delay(25, ct));
                if (wait.IsCompleted) break;
                ct.ThrowIfCancellationRequested();
                if (Clock() - _lastProtocolAt >= PromptSilence)
                {
                    linked.Cancel();
                    try { await wait; }
                    catch (OperationCanceledException) { }
                    throw new PromptSilenceException();
                }
            }
        }
        try
        {
            if (!await wait) return null;
        }
        catch (ChannelClosedException)
        {
            throw new IOException("Grok Build disconnected. Reconnect before sending again.");
        }
        if (!_inbound.Reader.TryRead(out var line))
            throw new IOException("Grok Build disconnected. Reconnect before sending again.");
        return line;
    }

    private static Exception ProviderFailure(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            var text = error.GetString()?.Trim();
            if (string.IsNullOrEmpty(text)) return new IOException("Grok Build: request failed.");
            return GrokBuildJson.IsUsageFailure(text) ? new GrokBuildUsageException(text) : new IOException("Grok Build: " + text + ".");
        }
        if (error.ValueKind != JsonValueKind.Object) return new IOException("Grok Build: request failed.");
        if (GrokBuildJson.TryReadUsageFailure(error, out var usage)) return new GrokBuildUsageException(usage);
        var message = error.TryGetProperty("message", out var messageElement) ? GrokBuildJson.ScalarText(messageElement) : null;
        return new IOException("Grok Build: " + (string.IsNullOrWhiteSpace(message) ? "request failed" : message) + ".");
    }

    private async Task HandleServerAsync(string? method, JsonElement message, Action<string>? delta, CancellationToken ct)
    {
        if (TryRequestId(message, out var requestId) && !GrokBuildAcp.IsSessionTraffic(method))
        {
            await AnswerRequestAsync(method, requestId, message, ct);
            return;
        }
        if (GrokBuildAcp.IsPromptComplete(method)) return;
        if (!GrokBuildAcp.IsSessionUpdate(method)) return;
        if (!message.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
        {
            if (_replaying) _ignoredReplay.Add("update");
            return;
        }
        var update = parameters.TryGetProperty("update", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : parameters;
        if (_replaying)
        {
            var replayKind = update.TryGetProperty("sessionUpdate", out var replayName) ? GrokBuildJson.ScalarText(replayName) : null;
            _ignoredReplay.Add(string.IsNullOrEmpty(replayKind) ? "update" : replayKind);
            return;
        }
        var eventSession = parameters.TryGetProperty("sessionId", out var sid) ? GrokBuildJson.ScalarText(sid) ?? "" : "";
        var kind = update.TryGetProperty("sessionUpdate", out var sessionUpdate) ? GrokBuildJson.ScalarText(sessionUpdate) : null;
        if (GrokBuildAcp.TryParseSubagent(update, kind, out var parsed))
        {
            if (_project?.SubagentsAllowed == true)
                Report(new("subagent", Subagent: RememberSubagent(parsed)));
            return;
        }
        if (_assistantChars == 0 && !IsAssistantChunk(update) && GrokBuildJson.TryReadUsageFailure(update, out var usageText))
            throw new GrokBuildUsageException(usageText);
        if (_sessionId is not null && (eventSession != _sessionId || kind is "subagent_spawned" or "subagent_state_update"))
        {
            if (_project?.SubagentsAllowed == true && _supportsSubagents)
            {
                var child = _subagents.Observe(_sessionId, eventSession, update, kind == "subagent_spawned" ? SubagentDisplayName(update) : null);
                var tool = kind is "tool_call" or "tool_call_update" && update.TryGetProperty("toolCallId", out var toolId)
                    ? _subagents.GetTool(eventSession, GrokBuildJson.ScalarText(toolId) ?? "")
                    : null;
                if (child is not null) Report(new("subagent", Subagent: child, Tool: tool));
            }
            return;
        }
        if (_project is not null && kind is "tool_call" or "tool_call_update")
        {
            var activity = WithCachedToolName(update, CursorToolParser.Merge(update, _tools));
            _project.ToolActivity?.Invoke(activity);
            Report(new("tool", Tool: activity));
        }
        if (kind == "agent_thought_chunk" && TextChunk(update) is { } thought) Report(new("reasoning", thought));
        if (kind == "agent_message_chunk" && TextChunk(update) is { } text)
        {
            Report(new("message"));
            delta?.Invoke(text);
        }
        if (kind == "plan" && update.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            Report(new("plan", Plan: entries.EnumerateArray().Take(100).Select((entry, i) => new CursorPlanItem(
                entry.TryGetProperty("id", out var planId) ? GrokBuildJson.ScalarText(planId) ?? i.ToString() : i.ToString(),
                entry.TryGetProperty("content", out var content) ? GrokBuildJson.ScalarText(content) ?? "" : "",
                entry.TryGetProperty("status", out var status) ? GrokBuildJson.ScalarText(status) ?? "pending" : "pending")).ToArray()));
    }

    private static bool TryRequestId(JsonElement message, out JsonElement id)
    {
        if (message.TryGetProperty("id", out id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            return true;
        id = default;
        return false;
    }

    private async Task AnswerRequestAsync(string? method, JsonElement requestId, JsonElement message, CancellationToken ct)
    {
        if (!message.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            parameters = default;
        if (_replaying && method is "session/request_permission" or "_x.ai/exit_plan_mode" or "_x.ai/ask_user_question")
        {
            _ignoredReplay.Add(method);
            if (method == "session/request_permission")
                await WriteRpcAsync(requestId, new { outcome = new { outcome = "cancelled" } }, ct);
            else
                await WriteRpcAsync(requestId, new { outcome = "cancelled" }, ct);
            return;
        }
        if (method == "session/request_permission")
        {
            string? chosen = null;
            if (parameters.ValueKind == JsonValueKind.Object)
            {
                try { chosen = await ChoosePermissionAsync(parameters, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException or KeyNotFoundException or FormatException)
                {
                    GrokBuildJson.Log("permission-skip: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            object outcome = chosen is null ? new { outcome = "cancelled" } : new { outcome = "selected", optionId = chosen };
            await WriteRpcAsync(requestId, new { outcome }, ct);
            return;
        }
        if (method == "_x.ai/exit_plan_mode")
        {
            var card = new GrokBuildPlanCard(
                parameters.ValueKind == JsonValueKind.Object ? GrokBuildAcp.PlanText(parameters) : "",
                parameters.ValueKind == JsonValueKind.Object ? GrokBuildAcp.ToolCallId(parameters) : null);
            GrokBuildPlanAnswer answer;
            try { answer = ExitPlanMode is null ? GrokBuildPlanAnswer.Cancelled() : await ExitPlanMode(card, ct); }
            catch (OperationCanceledException) { answer = GrokBuildPlanAnswer.Cancelled(); }
            catch (Exception ex) { GrokBuildJson.Log("exit-plan-skip: " + ex.GetType().Name + ": " + ex.Message); answer = GrokBuildPlanAnswer.Cancelled(); }
            await WriteRpcAsync(requestId, PlanResult(answer), ct);
            return;
        }
        if (method == "_x.ai/ask_user_question")
        {
            var questions = parameters.ValueKind == JsonValueKind.Object ? GrokBuildAcp.Questions(parameters) : [];
            GrokBuildQuestionAnswer answer;
            if (questions.Length == 0) answer = GrokBuildQuestionAnswer.Cancelled();
            else
            {
                var card = new GrokBuildQuestionCard(questions, GrokBuildAcp.ToolCallId(parameters));
                try { answer = AskUserQuestion is null ? GrokBuildQuestionAnswer.Cancelled() : await AskUserQuestion(card, ct); }
                catch (OperationCanceledException) { answer = GrokBuildQuestionAnswer.Cancelled(); }
                catch (Exception ex) { GrokBuildJson.Log("ask-user-skip: " + ex.GetType().Name + ": " + ex.Message); answer = GrokBuildQuestionAnswer.Cancelled(); }
            }
            await WriteRpcAsync(requestId, QuestionResult(answer), ct);
            return;
        }
        await WriteAsync(new
        {
            jsonrpc = "2.0",
            id = requestId.Clone(),
            error = new { code = -32601, message = "Method not found: " + method }
        }, ct);
    }

    private static Dictionary<string, object?> PlanResult(GrokBuildPlanAnswer answer)
    {
        var outcome = answer.Outcome is "approved" or "cancelled" or "abandoned" ? answer.Outcome : "cancelled";
        var body = new Dictionary<string, object?> { ["outcome"] = outcome };
        if (outcome == "cancelled" && !string.IsNullOrWhiteSpace(answer.Feedback))
            body["feedback"] = answer.Feedback;
        return body;
    }

    private static Dictionary<string, object?> QuestionResult(GrokBuildQuestionAnswer answer)
    {
        if (answer.Outcome == "accepted" && answer.Answers is { Count: > 0 })
            return new Dictionary<string, object?>
            {
                ["outcome"] = "accepted",
                ["answers"] = answer.Answers,
                ["partial_answers"] = new Dictionary<string, object?>()
            };
        return new Dictionary<string, object?> { ["outcome"] = "cancelled" };
    }

    private Task WriteRpcAsync(JsonElement id, object result, CancellationToken ct) =>
        WriteAsync(new { jsonrpc = "2.0", id = id.Clone(), result }, ct);

    private static bool IsAssistantChunk(JsonElement update) =>
        update.ValueKind == JsonValueKind.Object
        && update.TryGetProperty("sessionUpdate", out var kind)
        && string.Equals(GrokBuildJson.ScalarText(kind), "agent_message_chunk", StringComparison.Ordinal);

    private CursorToolActivity WithCachedToolName(JsonElement update, CursorToolActivity activity)
    {
        var name = GrokBuildAcp.ToolName(update);
        if (!string.IsNullOrEmpty(name)) _toolNames[activity.Id] = name;
        var supplied = update.TryGetProperty("title", out var title)
            && title.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(title.GetString());
        if (supplied || !_toolNames.TryGetValue(activity.Id, out var cached)) return activity;
        if (!string.IsNullOrWhiteSpace(activity.Title) && activity.Title != "Tool") return activity;
        var named = activity with { Title = cached };
        _tools[activity.Id] = named;
        return named;
    }

    private CursorSubagent RememberSubagent(GrokBuildSubagent parsed)
    {
        _xaiSubagents.TryGetValue(parsed.Id, out var prior);
        var now = Clock();
        var finished = parsed.Phase == "finished";
        var child = new CursorSubagent(
            parsed.Id,
            Clip(parsed.Type ?? parsed.Description ?? prior?.Name ?? "Subagent", 120),
            Clip(parsed.Description ?? prior?.Task ?? "", 2000),
            parsed.State,
            Clip(parsed.Output ?? prior?.Output ?? "", 6000),
            Clip(parsed.Progress, 240),
            StartedAt: prior?.StartedAt ?? now,
            UpdatedAt: now,
            CompletedAt: finished ? now : prior?.CompletedAt);
        return _xaiSubagents[parsed.Id] = child;
    }

    private IEnumerable<CursorSubagent> FinishXaiSubagents(bool stopped)
    {
        var now = Clock();
        var work = stopped ? "Stop requested" : "Parent turn ended";
        var state = stopped ? "stop requested" : "disconnected";
        foreach (var child in _xaiSubagents.Values.ToArray())
        {
            if (child.State is not ("running" or "pending" or "queued" or "waiting")) continue;
            var done = child with { State = state, CurrentWork = work, UpdatedAt = now, CompletedAt = now };
            _xaiSubagents[child.SessionId] = done;
            yield return done;
        }
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    private async Task<string?> ChoosePermissionAsync(JsonElement permission, CancellationToken ct)
    {
        if (_project is null) return null;
        var choices = permission.GetProperty("options").EnumerateArray().Select(option => new CursorPermissionOption(
            option.GetProperty("optionId").GetString()!, option.GetProperty("name").GetString()!, option.GetProperty("kind").GetString()!)).ToArray();
        var tool = CursorToolParser.Merge(permission.GetProperty("toolCall"), _tools);
        if (_project.Mode == CursorProjectMode.Agent && _project.Permissions == CursorPermissionPolicy.AlwaysAllow)
            return choices.FirstOrDefault(option => option.Kind == "allow_once")?.Id;
        if (_project.RequestPermission is null) return null;
        var chosen = await _project.RequestPermission(new(tool, choices), ct);
        return choices.Any(option => option.Id == chosen && GrokBuildAcp.IsSelectablePermissionKind(option.Kind)) ? chosen : null;
    }

    private string SessionPreface(string parentModelId)
    {
        if (_project is null)
            return "SI Desk conversation: answer using the supplied conversation. Do not call tools, access files, or act on accounts.\n\n";
        var preface = _project.Mode == CursorProjectMode.Plan
            ? "SI Desk planning session: inspect the selected workspace and propose a plan. Stay read-only; do not edit files or implement the plan. The user must leave Plan by choosing Always ask or Always allow in the composer and send a new instruction before implementation.\n\n"
            : "SI Desk project session: work in the selected workspace. Make requested file changes using your tools and report actual results. Follow the project's instructions. Do not commit, push, publish, or act on external accounts unless the user explicitly asks.\n\n";
        var swarmHire = SwarmSuffix(parentModelId);
        if (_project.SubagentsAllowed != true || !_supportsSubagents) return preface + swarmHire;
        var helperModelClause = swarmHire.Length > 0
            ? "Use the runtime-advertised generalPurpose type and pass one modelId from the swarm hire catalog on the helper tool's model field. "
            : "Use the runtime-advertised generalPurpose type and omit model overrides. ";
        return preface + "Native subagents are enabled. Delegate independent work to up to three direct generalPurpose helpers concurrently, at most sixteen identities per session. " + helperModelClause + "Helpers inherit this chat's mode and permission policy and must follow the same workspace instructions; they must not spawn nested helpers or act beyond the user's request. Give implementation helpers separate files or tasks to avoid conflicting edits. Summarize their actual results. Stop response stops the entire session. Helpers load the work-doctrine skill by default. Put that skill at the top of every helper task.\n\n"
            + (_project.Mode == CursorProjectMode.Plan
                ? "Plan helpers must stay read-only: inspect and report, never edit files or execute commands.\n\n"
                : _project.Permissions == CursorPermissionPolicy.AlwaysAsk
                    ? "Helpers use Always ask: request the user's approval through runtime permissions when required, just like the parent.\n\n"
                    : "Helpers use this chat's Always allow setting, subject to the existing runtime sandbox and project policy.\n\n") + swarmHire;
    }

    private string SwarmSuffix(string parentModelId) =>
        _project?.SwarmBrief is { Length: > 0 } swarm ? swarm + "\n\n" + SwarmModelHire.Catalog(Models, parentModelId) + "\n\n" : "";

    private string? SubagentDisplayName(JsonElement update)
    {
        if (!update.TryGetProperty("_meta", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("cursor", out var cursor) || cursor.ValueKind != JsonValueKind.Object ||
            !cursor.TryGetProperty("toolCallId", out var id) || id.ValueKind != JsonValueKind.String) return null;
        var toolId = id.GetString()!;
        var tool = CursorToolParser.Find(_tools, toolId);
        if (tool is null)
        {
            var matches = _tools.Values.Where(item => item.Id.StartsWith(toolId + "\n", StringComparison.Ordinal)).Take(2).ToArray();
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

    private static string? TextChunk(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) return null;
        if (!content.TryGetProperty("type", out var type) || !string.Equals(GrokBuildJson.ScalarText(type), "text", StringComparison.Ordinal)) return null;
        if (!content.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return null;
        return text.GetString();
    }

    private async Task WriteAsync(object value, CancellationToken ct)
    {
        await _writerGate.WaitAsync(ct);
        try
        {
            var writer = _writer ?? throw new IOException("Grok Build is not connected.");
            await writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct);
            await writer.FlushAsync(ct);
        }
        finally { _writerGate.Release(); }
    }

    private void Report(CursorActivityEvent update)
    {
        if (_reportActivity) _activity?.Invoke(update);
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        try { while (await process.StandardError.ReadLineAsync() is not null) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process?.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
        _process?.Dispose();
        _gate.Dispose();
        _writerGate.Dispose();
    }

    private sealed class PromptSilenceException : Exception { }
}
