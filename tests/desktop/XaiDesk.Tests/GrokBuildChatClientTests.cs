using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Sovereign.Desktop.Client;
using Xunit;

namespace XaiDesk.Tests;

public sealed class GrokBuildChatClientTests
{
    [Fact(Timeout = 15000)]
    public async Task Protocol_keeps_grok_models_and_completes_a_text_chunk()
    {
        using var transport = new LineTransport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var workspace = Path.Combine(Path.GetTempPath(), "SIDesk", "grok-protocol");
        using var client = new GrokBuildChatClient(workspace, transport.Reader, transport.Writer);

        var modelsTask = client.ModelsAsync(ct);
        var initialize = await transport.NextRequestAsync(ct);
        Assert.Equal("initialize", Method(initialize));
        transport.Reply(Id(initialize), new { protocolVersion = 1 });

        var sessionNew = await transport.NextRequestAsync(ct);
        Assert.Equal("session/new", Method(sessionNew));
        Assert.Equal(0, sessionNew.GetProperty("params").GetProperty("mcpServers").GetArrayLength());
        const string sessionId = "sess-1";
        transport.Reply(Id(sessionNew), new
        {
            sessionId,
            models = new
            {
                availableModels = new[]
                {
                    new { modelId = "grok-4.7", name = "Grok 4.7" },
                    new { modelId = "gpt-4o", name = "GPT-4o" }
                }
            }
        });

        var setMode = await transport.NextRequestAsync(ct);
        Assert.Equal("session/set_mode", Method(setMode));
        transport.Reply(Id(setMode), new { accepted = true });

        var models = await modelsTask;
        var model = Assert.Single(models);
        Assert.Equal("grok-4.7", model.Id);
        Assert.Equal("Grok 4.7", model.Name);

        var text = "";
        var sendTask = client.SendAsync("grok-4.7", "Hello", piece => text += piece, ct);
        var setModel = await transport.NextRequestAsync(ct);
        Assert.Equal("session/set_model", Method(setModel));
        transport.Reply(Id(setModel), new { accepted = true });

        var prompt = await transport.NextRequestAsync(ct);
        Assert.Equal("session/prompt", Method(prompt));
        transport.Update(sessionId, "Hi");
        transport.Reply(Id(prompt), new { stopReason = "end_turn" });

        var outcome = await sendTask;
        Assert.Equal("Hi", text);
        Assert.Equal(CursorTurnOutcome.Completed, outcome);
        Assert.DoesNotContain("Native subagents are enabled", PromptText(prompt));
        Assert.Contains("# Work Doctrine", PromptText(prompt));
        Assert.Contains("put the Work Doctrine text below at the top of that helper's task", PromptText(prompt));
    }

    [Fact(Timeout = 15000)]
    public async Task Swarm_mode_session_new_attaches_the_manager_server_and_not_the_grok_bot_connector()
    {
        using var transport = new LineTransport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var command = Path.Combine(Path.GetTempPath(), "swarm-manager-cmd-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(command, "manager", ct);
        var workspace = Path.Combine(Path.GetTempPath(), "SIDesk", "grok-swarm-new");
        using var client = new GrokBuildChatClient(workspace, transport.Reader, transport.Writer, new CursorProjectOptions
        {
            SwarmBrief = "Swarm mode.",
            SwarmManagerCommand = command
        });
        try
        {
            var modelsTask = client.ModelsAsync(ct);
            var initialize = await transport.NextRequestAsync(ct);
            transport.Reply(Id(initialize), new { protocolVersion = 1 });
            var sessionNew = await transport.NextRequestAsync(ct);
            Assert.Equal("session/new", Method(sessionNew));
            var server = Assert.Single(sessionNew.GetProperty("params").GetProperty("mcpServers").EnumerateArray());
            Assert.Equal(SwarmManagerSession.ServerName, server.GetProperty("name").GetString());
            Assert.Equal(command, server.GetProperty("command").GetString());
            var raw = sessionNew.GetRawText();
            Assert.DoesNotContain("8790", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("grokbot", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("overrule", raw, StringComparison.OrdinalIgnoreCase);
            transport.Reply(Id(sessionNew), new
            {
                sessionId = "sess-swarm",
                models = new { availableModels = new[] { new { modelId = "grok-4.7", name = "Grok 4.7" } } }
            });
            var setMode = await transport.NextRequestAsync(ct);
            transport.Reply(Id(setMode), new { accepted = true });
            await modelsTask;
        }
        finally { if (File.Exists(command)) File.Delete(command); }
    }

    [Fact(Timeout = 15000)]
    public async Task Swarm_mode_session_load_attaches_the_manager_server()
    {
        using var transport = new LineTransport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var command = Path.Combine(Path.GetTempPath(), "swarm-manager-load-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(command, "manager", ct);
        var workspace = Path.Combine(Path.GetTempPath(), "SIDesk", "grok-swarm-load");
        using var client = new GrokBuildChatClient(workspace, transport.Reader, transport.Writer, new CursorProjectOptions
        {
            SwarmBrief = "Swarm mode.",
            SwarmManagerCommand = command
        });
        client.ResumeSessionId = "stored-swarm";
        try
        {
            var modelsTask = client.ModelsAsync(ct);
            var initialize = await transport.NextRequestAsync(ct);
            transport.Frame("{\"jsonrpc\":\"2.0\",\"id\":" + Id(initialize) + ",\"result\":{\"loadSession\":true}}");
            var load = await transport.NextRequestAsync(ct);
            Assert.Equal("session/load", Method(load));
            var server = Assert.Single(load.GetProperty("params").GetProperty("mcpServers").EnumerateArray());
            Assert.Equal(SwarmManagerSession.ServerName, server.GetProperty("name").GetString());
            Assert.Equal(command, server.GetProperty("command").GetString());
            Assert.DoesNotContain("8790", load.GetRawText(), StringComparison.Ordinal);
            transport.Reply(Id(load), new
            {
                sessionId = "stored-swarm",
                models = new { availableModels = new[] { new { modelId = "grok-4.7", name = "Grok 4.7" } } }
            });
            var setMode = await transport.NextRequestAsync(ct);
            transport.Reply(Id(setMode), new { accepted = true });
            await modelsTask;
        }
        finally { if (File.Exists(command)) File.Delete(command); }
    }

    [Fact(Timeout = 15000)]
    public async Task Project_session_tells_grok_to_use_tools_and_subagents_when_the_runtime_allows_them()
    {
        using var transport = new LineTransport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var workspace = Path.Combine(Path.GetTempPath(), "SIDesk", "grok-tools");
        using var client = new GrokBuildChatClient(workspace, transport.Reader, transport.Writer, new CursorProjectOptions
        {
            Mode = CursorProjectMode.Agent,
            Permissions = CursorPermissionPolicy.AlwaysAllow,
            EnableSubagents = true
        });

        var modelsTask = client.ModelsAsync(ct);
        var initialize = await transport.NextRequestAsync(ct);
        transport.Reply(Id(initialize), new
        {
            protocolVersion = 1,
            agentCapabilities = new { sessionCapabilities = new { subagents = new { } } }
        });
        var sessionNew = await transport.NextRequestAsync(ct);
        transport.Reply(Id(sessionNew), new
        {
            sessionId = "sess-tools",
            models = new { availableModels = new[] { new { modelId = "grok-4.7", name = "Grok 4.7" } } }
        });
        var setMode = await transport.NextRequestAsync(ct);
        transport.Reply(Id(setMode), new { accepted = true });
        await modelsTask;

        var sendTask = client.SendAsync("grok-4.7", "Edit the project", _ => { }, ct);
        var setModel = await transport.NextRequestAsync(ct);
        transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await transport.NextRequestAsync(ct);
        var preface = PromptText(prompt);
        Assert.Contains("Make requested file changes using your tools", preface);
        Assert.Contains("Native subagents are enabled", preface);
        Assert.Contains("Always allow", preface);
        Assert.Contains("# Work Doctrine", preface);
        Assert.Contains("Helpers load the work-doctrine skill by default", preface);
        transport.Update("sess-tools", "Done");
        transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await sendTask);
    }

    [Fact(Timeout = 15000)]
    public async Task String_jsonrpc_id_still_completes_a_text_chunk()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await ConnectAsync(timeout.Token, stringInitializeId: true);
        var text = "";
        var send = ready.Client.SendAsync("grok-4.7", "Hello", piece => text += piece, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.ReplyString(Id(setModel).ToString(), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Update(ready.SessionId, "Hi");
        ready.Transport.ReplyString(Id(prompt).ToString(), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
        Assert.Equal("Hi", text);
    }

    [Fact(Timeout = 15000)]
    public async Task Usage_limit_numeric_strings_do_not_fail_the_turn()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await ConnectAsync(timeout.Token);
        var text = "";
        var send = ready.Client.SendAsync("grok-4.7", "Hello", piece => text += piece, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Frame(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "session/update",
            @params = new
            {
                sessionId = ready.SessionId,
                update = new
                {
                    sessionUpdate = "usage_update",
                    used = "12",
                    limit = "100",
                    remaining = "88",
                    tokens = "12",
                    quota = "100",
                    size = "n/a"
                }
            }
        }));
        ready.Transport.Update(ready.SessionId, "Hi");
        ready.Transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
        Assert.Equal("Hi", text);
    }

    [Fact(Timeout = 15000)]
    public async Task Unknown_session_update_is_ignored()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await ConnectAsync(timeout.Token);
        var text = "";
        var send = ready.Client.SendAsync("grok-4.7", "Hello", piece => text += piece, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Frame("""{"jsonrpc":"2.0","method":"x.ai/notification","params":{"notice":"config_option_update","usage":{"limit":"2"}}}""");
        ready.Transport.Frame("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"" + ready.SessionId + "\",\"update\":{\"sessionUpdate\":\"config_option_update\",\"quota\":\"8\"}}}");
        ready.Transport.Frame("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":1,"update":{"sessionUpdate":"plan","entries":"nope"}}}""");
        ready.Transport.Update(ready.SessionId, "Hi");
        ready.Transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
        Assert.Equal("Hi", text);
    }

    [Theory(Timeout = 15000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Matching_usage_error_is_the_provider_text(bool codeIsString)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await ConnectAsync(timeout.Token);
        var send = ready.Client.SendAsync("grok-4.7", "Hello", _ => { }, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Error(Id(prompt), codeIsString ? "\"429\"" : "429", "free-usage-exhausted");
        var error = await Assert.ThrowsAsync<GrokBuildUsageException>(() => send);
        Assert.Contains("free-usage-exhausted", error.Message);
        Assert.DoesNotContain("run failed locally", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(error.Message, ProjectAgentException.Describe(error));
        Assert.DoesNotContain("run failed locally", ProjectAgentException.Describe(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 15000)]
    public async Task Stop_reason_usage_failure_uses_the_exact_text()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await ConnectAsync(timeout.Token);
        var send = ready.Client.SendAsync("grok-4.7", "Hello", _ => { }, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(prompt), new { stopReason = "rate limit" });
        var error = await Assert.ThrowsAsync<GrokBuildUsageException>(() => send);
        Assert.Equal("rate limit", error.Message);
        Assert.DoesNotContain("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 15000)]
    public async Task Bearer_token_is_redacted_in_the_protocol_log()
    {
        var log = Path.Combine(Path.GetTempPath(), "SIDesk", "grok-log-" + Guid.NewGuid().ToString("N"), "grokbuild.log");
        GrokBuildChatClient.SetLogFile(log);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var ready = await ConnectAsync(timeout.Token);
            var send = ready.Client.SendAsync("grok-4.7", "Hello", _ => { }, timeout.Token);
            var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
            ready.Transport.Reply(Id(setModel), new { accepted = true });
            var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
            ready.Transport.Frame("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess-1","update":{"sessionUpdate":"notice","detail":"Authorization: Bearer super-secret-token"}}}""");
            ready.Transport.Update(ready.SessionId, "Hi");
            ready.Transport.Reply(Id(prompt), new { stopReason = "end_turn" });
            Assert.Equal(CursorTurnOutcome.Completed, await send);
            var body = File.ReadAllText(log);
            Assert.DoesNotContain("super-secret-token", body);
            Assert.Contains("Bearer [redacted]", body);
        }
        finally
        {
            GrokBuildChatClient.SetLogFile(null);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task Extension_notification_methods_stream_assistant_text()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await ConnectAsync(timeout.Token);
        var text = "";
        var send = ready.Client.SendAsync("grok-4.7", "Hello", piece => text += piece, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Frame("{\"jsonrpc\":\"2.0\",\"method\":\"_x.ai/session_notification\",\"params\":{\"sessionId\":\"" + ready.SessionId + "\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"A\"}}}}");
        ready.Transport.Frame("{\"jsonrpc\":\"2.0\",\"method\":\"_x.ai/session/update\",\"params\":{\"sessionId\":\"" + ready.SessionId + "\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"B\"}}}}");
        ready.Transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
        Assert.Equal("AB", text);
    }

    [Fact(Timeout = 15000)]
    public async Task Subagent_spawned_progress_and_finished_report_worker_cards()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var activity = new List<CursorActivityEvent>();
        using var ready = await OpenAsync(timeout.Token, project: new CursorProjectOptions { EnableSubagents = true }, activity: activity.Add);
        Assert.False(ready.Client.SupportsSubagents);
        var send = ready.Client.SendAsync("grok-4.7", "Delegate", _ => { }, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Frame("{\"jsonrpc\":\"2.0\",\"method\":\"_x.ai/session_notification\",\"params\":{\"sessionId\":\"" + ready.SessionId + "\",\"update\":{\"sessionUpdate\":\"subagent_spawned\",\"subagent_id\":\"child-1\",\"type\":\"generalPurpose\",\"description\":\"Read the logs\"}}}");
        ready.Transport.Frame("{\"jsonrpc\":\"2.0\",\"method\":\"_x.ai/session/update\",\"params\":{\"sessionId\":\"" + ready.SessionId + "\",\"update\":{\"sessionUpdate\":\"subagent_progress\",\"subagent_id\":\"child-1\",\"status\":\"running\",\"turns\":2,\"tool_calls\":4,\"tokens_used\":1200,\"duration_ms\":3500}}}");
        ready.Transport.Frame("{\"jsonrpc\":\"2.0\",\"method\":\"_x.ai/session_notification\",\"params\":{\"sessionId\":\"" + ready.SessionId + "\",\"update\":{\"sessionUpdate\":\"subagent_finished\",\"subagent_id\":\"child-1\",\"status\":\"completed\",\"output\":\"Found 3 errors\",\"turns\":3,\"tool_calls\":5,\"tokens_used\":1800,\"duration_ms\":9000}}}");
        ready.Transport.Update(ready.SessionId, "Done");
        ready.Transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
        var workers = activity.Where(item => item.Kind == "subagent" && item.Subagent?.SessionId == "child-1").Select(item => item.Subagent!).ToArray();
        Assert.Equal(3, workers.Length);
        Assert.Equal("generalPurpose", workers[0].Name);
        Assert.Equal("Read the logs", workers[0].Task);
        Assert.Equal("running", workers[0].State);
        Assert.Contains("2 turns", workers[1].CurrentWork);
        Assert.Contains("4 tool calls", workers[1].CurrentWork);
        Assert.Contains("1200 tokens", workers[1].CurrentWork);
        Assert.Contains("3500 ms", workers[1].CurrentWork);
        Assert.Equal("completed", workers[2].State);
        Assert.Equal("Found 3 errors", workers[2].Output);
        Assert.Equal("generalPurpose", workers[2].Name);
        Assert.Equal("Read the logs", workers[2].Task);
    }

    [Fact(Timeout = 15000)]
    public async Task Prompt_complete_does_not_by_itself_finish_the_turn()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await ConnectAsync(timeout.Token);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ready.Client.Clock = () => now;
        var text = "";
        var send = ready.Client.SendAsync("grok-4.7", "Hello", piece => text += piece, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Update(ready.SessionId, "Hi");
        await UntilAsync(() => text.Contains('H'), send);
        ready.Transport.Frame("{\"jsonrpc\":\"2.0\",\"method\":\"_x.ai/session/prompt_complete\",\"params\":{\"sessionId\":\"" + ready.SessionId + "\",\"stopReason\":\"end_turn\"}}");
        ready.Transport.Update(ready.SessionId, "!");
        await UntilAsync(() => text.Contains("Hi!"), send);
        Assert.False(send.IsCompleted);
        ready.Transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
        Assert.Equal("Hi!", text);
    }

    [Fact(Timeout = 15000)]
    public async Task Silence_timeout_resets_on_an_update_and_keeps_partial_text()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await ConnectAsync(timeout.Token);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ready.Client.Clock = () => now;
        var text = "";
        var send = ready.Client.SendAsync("grok-4.7", "Hello", piece => text += piece, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Update(ready.SessionId, "Hi");
        await UntilAsync(() => text == "Hi", send);
        Assert.False(send.IsCompleted);
        now = now.AddSeconds(2);
        await Task.Delay(400);
        Assert.False(send.IsCompleted);
        ready.Transport.Update(ready.SessionId, " there");
        await UntilAsync(() => text == "Hi there", send);
        Assert.False(send.IsCompleted);
        now = now.AddSeconds(2);
        await Task.Delay(400);
        Assert.False(send.IsCompleted);
        now = now.AddSeconds(1);
        Assert.Equal(CursorTurnOutcome.Completed, await send.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Equal("Hi there", text);
    }

    [Fact(Timeout = 15000)]
    public async Task Silence_with_no_text_is_an_incomplete_response()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await ConnectAsync(timeout.Token);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ready.Client.Clock = () => now;
        var send = ready.Client.SendAsync("grok-4.7", "Hello", _ => { }, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        await ready.Transport.NextRequestAsync(timeout.Token);
        Assert.False(send.IsCompleted);
        now = now.AddSeconds(3);
        var error = await Assert.ThrowsAsync<IOException>(() => send.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("silence", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PATH", error.Message);
        Assert.Contains("signed in", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connect / refresh models", error.Message);
        Assert.Equal(error.Message, ProjectAgentException.Describe(error));
        Assert.DoesNotContain("run failed locally", ProjectAgentException.Describe(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 15000)]
    public async Task Permission_reply_uses_the_allow_always_option_id()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await OpenAsync(timeout.Token, project: new CursorProjectOptions
        {
            Mode = CursorProjectMode.Agent,
            Permissions = CursorPermissionPolicy.AlwaysAsk,
            RequestPermission = (request, _) => Task.FromResult<string?>(request.Options.Single(option => option.Kind == "allow_always_command").Id)
        });
        var send = ready.Client.SendAsync("grok-4.7", "Run", _ => { }, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Frame("""
            {"jsonrpc":"2.0","id":80,"method":"session/request_permission","params":{"sessionId":"sess-1","toolCall":{"toolCallId":"call-1","title":"Run tests","kind":"execute","status":"pending"},"options":[{"optionId":"once-1","name":"Allow once","kind":"allow_once"},{"optionId":"perm-allow-always-7","name":"Always allow this command","kind":"allow_always_command"}]}}
            """);
        var reply = await ready.Transport.NextRequestAsync(timeout.Token);
        Assert.Equal(80, reply.GetProperty("id").GetInt32());
        Assert.Equal("perm-allow-always-7", reply.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
        ready.Transport.Update(ready.SessionId, "Ran");
        ready.Transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
    }

    [Fact(Timeout = 15000)]
    public async Task Always_allow_still_picks_allow_once_when_that_option_exists()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await OpenAsync(timeout.Token, project: new CursorProjectOptions
        {
            Mode = CursorProjectMode.Agent,
            Permissions = CursorPermissionPolicy.AlwaysAllow
        });
        var send = ready.Client.SendAsync("grok-4.7", "Run", _ => { }, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Frame("""
            {"jsonrpc":"2.0","id":81,"method":"session/request_permission","params":{"sessionId":"sess-1","toolCall":{"toolCallId":"call-2","title":"Run tests","kind":"execute","status":"pending"},"options":[{"optionId":"once-1","name":"Allow once","kind":"allow_once"},{"optionId":"perm-allow-always-7","name":"Always","kind":"allow_always"}]}}
            """);
        var reply = await ready.Transport.NextRequestAsync(timeout.Token);
        Assert.Equal("once-1", reply.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
        ready.Transport.Update(ready.SessionId, "Ran");
        ready.Transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
    }

    [Fact(Timeout = 15000)]
    public async Task Tool_name_from_xai_tool_meta_is_kept_on_later_updates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var titles = new List<string>();
        using var ready = await OpenAsync(timeout.Token, project: new CursorProjectOptions { ToolActivity = tool => titles.Add(tool.Title) });
        var send = ready.Client.SendAsync("grok-4.7", "Read", _ => { }, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Frame("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"" + ready.SessionId + "\",\"update\":{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"call-read\",\"_meta\":{\"x.ai/tool\":\"read_file\"}}}}");
        ready.Transport.Frame("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"" + ready.SessionId + "\",\"update\":{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"call-read\",\"status\":\"completed\"}}}");
        ready.Transport.Update(ready.SessionId, "Read");
        ready.Transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
        Assert.Equal(new[] { "read_file", "read_file" }, titles);
    }

    [Fact(Timeout = 15000)]
    public async Task LoadSession_true_chooses_session_load()
    {
        await AssertSessionOpenAsync("""{"loadSession":true}""", "stored-1", "session/load");
        await AssertSessionOpenAsync("""{"agentCapabilities":{"sessionCapabilities":{"loadSession":true}}}""", "stored-2", "session/load");
        await AssertSessionOpenAsync("""{"agentCapabilities":{"loadSession":true}}""", "stored-3", "session/load");
    }

    [Fact(Timeout = 15000)]
    public async Task Missing_or_false_loadSession_chooses_session_new()
    {
        await AssertSessionOpenAsync("""{"protocolVersion":1}""", "stored-x", "session/new");
        await AssertSessionOpenAsync("""{"loadSession":false}""", "stored-y", "session/new");
        await AssertSessionOpenAsync("""{"agentCapabilities":{"sessionCapabilities":{"loadSession":{}}}}""", "stored-z", "session/new");
    }

    [Fact(Timeout = 15000)]
    public async Task Failed_session_load_falls_back_to_session_new_and_clears_the_stored_id()
    {
        using var transport = new LineTransport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var workspace = Path.Combine(Path.GetTempPath(), "SIDesk", "grok-load-" + Guid.NewGuid().ToString("N"));
        using var client = new GrokBuildChatClient(workspace, transport.Reader, transport.Writer);
        client.ResumeSessionId = "old-session";
        var models = client.ModelsAsync(timeout.Token);
        var initialize = await transport.NextRequestAsync(timeout.Token);
        transport.Frame("{\"jsonrpc\":\"2.0\",\"id\":" + Id(initialize) + ",\"result\":{\"loadSession\":true}}");
        var load = await transport.NextRequestAsync(timeout.Token);
        Assert.Equal("session/load", Method(load));
        Assert.Equal("old-session", load.GetProperty("params").GetProperty("sessionId").GetString());
        transport.Error(Id(load), "\"-32602\"", "session not found");
        var opened = await transport.NextRequestAsync(timeout.Token);
        Assert.Equal("session/new", Method(opened));
        transport.Reply(Id(opened), new
        {
            sessionId = "sess-new",
            models = new { availableModels = new[] { new { modelId = "grok-4.7", name = "Grok 4.7" } } }
        });
        var setMode = await transport.NextRequestAsync(timeout.Token);
        transport.Reply(Id(setMode), new { accepted = true });
        await models;
        Assert.Equal("sess-new", client.SessionId);
        Assert.True(client.ClearedResumeSession);
        Assert.Null(client.ResumeSessionId);
    }

    [Fact(Timeout = 15000)]
    public async Task Successful_load_without_session_id_keeps_the_stored_id()
    {
        using var transport = new LineTransport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var workspace = Path.Combine(Path.GetTempPath(), "SIDesk", "grok-load-id-" + Guid.NewGuid().ToString("N"));
        using var client = new GrokBuildChatClient(workspace, transport.Reader, transport.Writer);
        client.ResumeSessionId = "old-session";
        var models = client.ModelsAsync(timeout.Token);
        var initialize = await transport.NextRequestAsync(timeout.Token);
        transport.Frame("{\"jsonrpc\":\"2.0\",\"id\":" + Id(initialize) + ",\"result\":{\"loadSession\":true}}");
        var load = await transport.NextRequestAsync(timeout.Token);
        Assert.Equal("session/load", Method(load));
        transport.Reply(Id(load), new
        {
            models = new { availableModels = new[] { new { modelId = "grok-4.7", name = "Grok 4.7" } } }
        });
        var setMode = await transport.NextRequestAsync(timeout.Token);
        Assert.Equal("session/set_mode", Method(setMode));
        Assert.Equal("old-session", setMode.GetProperty("params").GetProperty("sessionId").GetString());
        transport.Reply(Id(setMode), new { accepted = true });
        await models;
        Assert.Equal("old-session", client.SessionId);
        Assert.False(client.ClearedResumeSession);
        Assert.Equal("old-session", client.ResumeSessionId);
    }

    [Fact(Timeout = 15000)]
    public async Task Replay_updates_are_marked_ignored_until_load_completes()
    {
        using var transport = new LineTransport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var tools = new List<string>();
        var workspace = Path.Combine(Path.GetTempPath(), "SIDesk", "grok-replay-" + Guid.NewGuid().ToString("N"));
        using var client = new GrokBuildChatClient(workspace, transport.Reader, transport.Writer, new CursorProjectOptions
        {
            ToolActivity = tool => tools.Add(tool.Title)
        });
        client.ResumeSessionId = "old-session";
        var models = client.ModelsAsync(ct);
        var initialize = await transport.NextRequestAsync(ct);
        transport.Frame("{\"jsonrpc\":\"2.0\",\"id\":" + Id(initialize) + ",\"result\":{\"agentCapabilities\":{\"sessionCapabilities\":{\"loadSession\":true}}}}");
        var load = await transport.NextRequestAsync(ct);
        Assert.Equal("session/load", Method(load));
        transport.Frame("""{"jsonrpc":"2.0","method":"_x.ai/session/update","params":{"sessionId":"old-session","update":{"sessionUpdate":"tool_call","toolCallId":"replay-1","title":"Replay tool","_meta":{"x.ai/tool":"read_file"}}}}""");
        transport.Frame("""{"jsonrpc":"2.0","method":"_x.ai/session_notification","params":{"sessionId":"old-session","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"old"}}}}""");
        transport.Reply(Id(load), new
        {
            sessionId = "old-session",
            models = new { availableModels = new[] { new { modelId = "grok-4.7", name = "Grok 4.7" } } }
        });
        var setMode = await transport.NextRequestAsync(ct);
        transport.Reply(Id(setMode), new { accepted = true });
        await models;
        Assert.Empty(tools);
        Assert.Contains("tool_call", client.IgnoredReplayUpdates);
        Assert.Contains("agent_message_chunk", client.IgnoredReplayUpdates);
        var send = client.SendAsync("grok-4.7", "Hi", _ => { }, ct);
        var setModel = await transport.NextRequestAsync(ct);
        transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await transport.NextRequestAsync(ct);
        transport.Frame("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"old-session","update":{"sessionUpdate":"tool_call","toolCallId":"live-1","_meta":{"x.ai/tool":"read_file"}}}}""");
        transport.Update("old-session", "Now");
        transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
        Assert.DoesNotContain("Replay tool", tools);
        Assert.Contains("read_file", tools);
    }

    [Fact(Timeout = 15000)]
    public async Task Plan_and_question_results_use_the_request_id()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string? plan = null;
        using var ready = await ConnectAsync(timeout.Token);
        ready.Client.ExitPlanMode = (card, _) =>
        {
            plan = card.Plan;
            return Task.FromResult(GrokBuildPlanAnswer.Approved());
        };
        ready.Client.AskUserQuestion = (card, _) => Task.FromResult(GrokBuildQuestionAnswer.Accepted(
            new Dictionary<string, object> { [card.Questions[0].Question] = "Red" }));
        var send = ready.Client.SendAsync("grok-4.7", "Plan", _ => { }, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Frame("""{"jsonrpc":"2.0","id":70,"method":"_x.ai/exit_plan_mode","params":{"planContent":"Ship the parser.","toolCallId":"plan-1"}}""");
        var planReply = await ready.Transport.NextRequestAsync(timeout.Token);
        Assert.Equal(70, planReply.GetProperty("id").GetInt32());
        Assert.Equal("approved", planReply.GetProperty("result").GetProperty("outcome").GetString());
        Assert.Equal("Ship the parser.", plan);
        ready.Transport.Frame("""{"jsonrpc":"2.0","id":71,"method":"_x.ai/ask_user_question","params":{"toolCallId":"q-1","questions":[{"question":"Which colour?","options":[{"label":"Red","description":"Red banner"}],"multiSelect":false}]}}""");
        var askReply = await ready.Transport.NextRequestAsync(timeout.Token);
        Assert.Equal(71, askReply.GetProperty("id").GetInt32());
        Assert.Equal("accepted", askReply.GetProperty("result").GetProperty("outcome").GetString());
        Assert.Equal("Red", askReply.GetProperty("result").GetProperty("answers").GetProperty("Which colour?").GetString());
        ready.Transport.Update(ready.SessionId, "Done");
        ready.Transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
    }

    [Fact(Timeout = 15000)]
    public async Task Missing_card_callback_still_replies_cancelled()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var ready = await ConnectAsync(timeout.Token);
        var send = ready.Client.SendAsync("grok-4.7", "Plan", _ => { }, timeout.Token);
        var setModel = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Reply(Id(setModel), new { accepted = true });
        var prompt = await ready.Transport.NextRequestAsync(timeout.Token);
        ready.Transport.Frame("""{"jsonrpc":"2.0","id":70,"method":"_x.ai/exit_plan_mode","params":{"planContent":"Ship it."}}""");
        var planReply = await ready.Transport.NextRequestAsync(timeout.Token);
        Assert.Equal(70, planReply.GetProperty("id").GetInt32());
        Assert.Equal("cancelled", planReply.GetProperty("result").GetProperty("outcome").GetString());
        ready.Transport.Frame("""{"jsonrpc":"2.0","id":71,"method":"_x.ai/ask_user_question","params":{"questions":[{"question":"Which colour?","options":["Red"]}]}}""");
        var askReply = await ready.Transport.NextRequestAsync(timeout.Token);
        Assert.Equal(71, askReply.GetProperty("id").GetInt32());
        Assert.Equal("cancelled", askReply.GetProperty("result").GetProperty("outcome").GetString());
        ready.Transport.Update(ready.SessionId, "Done");
        ready.Transport.Reply(Id(prompt), new { stopReason = "end_turn" });
        Assert.Equal(CursorTurnOutcome.Completed, await send);
    }

    [Fact]
    public void Launch_omits_subagent_disables_when_subagents_are_allowed()
    {
        var allowed = GrokBuildChatClient.PlanLaunch(new CursorProjectOptions
        {
            Mode = CursorProjectMode.Agent,
            Permissions = CursorPermissionPolicy.AlwaysAllow,
            EnableSubagents = true
        });
        Assert.Equal(new[] { "agent", "--no-leader", "--always-approve", "stdio" }, allowed.Arguments);
        Assert.DoesNotContain("--no-subagents", allowed.Arguments);
        Assert.DoesNotContain(allowed.Environment, pair => pair.Key == "GROK_SUBAGENTS" && pair.Value == "0");
        Assert.Contains("GROK_SUBAGENTS", allowed.RemovedEnvironment);
        var inherited = new Dictionary<string, string?> { ["GROK_SUBAGENTS"] = "0", ["PATH"] = "x" };
        GrokBuildChatClient.ApplyLaunchEnvironment(inherited, allowed);
        Assert.False(inherited.ContainsKey("GROK_SUBAGENTS"));
        Assert.Equal("x", inherited["PATH"]);
        var kept = new Dictionary<string, string?> { ["GROK_SUBAGENTS"] = "1" };
        GrokBuildChatClient.ApplyLaunchEnvironment(kept, allowed);
        Assert.Equal("1", kept["GROK_SUBAGENTS"]);

        var denied = GrokBuildChatClient.PlanLaunch(new CursorProjectOptions { EnableSubagents = false });
        Assert.Equal(new[] { "agent", "--no-leader", "stdio" }, denied.Arguments);
        Assert.DoesNotContain("--no-subagents", denied.Arguments);
        Assert.Empty(denied.RemovedEnvironment);
        Assert.False(denied.Environment.ContainsKey("GROK_SUBAGENTS"));
        var left = new Dictionary<string, string?> { ["GROK_SUBAGENTS"] = "0" };
        GrokBuildChatClient.ApplyLaunchEnvironment(left, denied);
        Assert.Equal("0", left["GROK_SUBAGENTS"]);
    }

    private static Task<ReadyChat> ConnectAsync(CancellationToken ct, bool stringInitializeId = false) =>
        OpenAsync(ct, stringInitializeId: stringInitializeId);

    private static async Task<ReadyChat> OpenAsync(
        CancellationToken ct,
        bool stringInitializeId = false,
        CursorProjectOptions? project = null,
        Action<CursorActivityEvent>? activity = null,
        string? resumeSessionId = null,
        string? initializeJson = null,
        bool expectLoad = false,
        string sessionId = "sess-1")
    {
        var transport = new LineTransport();
        var workspace = Path.Combine(Path.GetTempPath(), "SIDesk", "grok-" + Guid.NewGuid().ToString("N"));
        var client = new GrokBuildChatClient(workspace, transport.Reader, transport.Writer, project, activity);
        client.ResumeSessionId = resumeSessionId;
        try
        {
            var modelsTask = client.ModelsAsync(ct);
            var initialize = await transport.NextRequestAsync(ct);
            if (stringInitializeId)
            {
                Assert.Equal(1, Id(initialize));
                transport.ReplyString("1", new { protocolVersion = 1 });
            }
            else if (initializeJson is null) transport.Reply(Id(initialize), new { protocolVersion = 1 });
            else transport.Frame("{\"jsonrpc\":\"2.0\",\"id\":" + Id(initialize) + ",\"result\":" + initializeJson + "}");
            var opened = await transport.NextRequestAsync(ct);
            Assert.Equal(expectLoad ? "session/load" : "session/new", Method(opened));
            if (expectLoad)
                Assert.Equal(resumeSessionId, opened.GetProperty("params").GetProperty("sessionId").GetString());
            transport.Reply(Id(opened), new
            {
                sessionId,
                models = new { availableModels = new[] { new { modelId = "grok-4.7", name = "Grok 4.7" } } }
            });
            var setMode = await transport.NextRequestAsync(ct);
            transport.Reply(Id(setMode), new { accepted = true });
            await modelsTask;
            return new ReadyChat(client, transport, sessionId);
        }
        catch
        {
            client.Dispose();
            transport.Dispose();
            throw;
        }
    }

    private static async Task AssertSessionOpenAsync(string initializeJson, string resumeId, string expectedMethod)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var sessionId = expectedMethod == "session/load" ? resumeId : "sess-1";
        using var ready = await OpenAsync(timeout.Token, resumeSessionId: resumeId, initializeJson: initializeJson, expectLoad: expectedMethod == "session/load", sessionId: sessionId);
        Assert.Equal(sessionId, ready.Client.SessionId);
    }

    private static async Task UntilAsync(Func<bool> ready, Task pending)
    {
        var deadline = Environment.TickCount64 + 4000;
        while (!ready() && !pending.IsCompleted && Environment.TickCount64 < deadline)
            await Task.Delay(15);
    }

    private sealed class ReadyChat(GrokBuildChatClient client, LineTransport transport, string sessionId) : IDisposable
    {
        public GrokBuildChatClient Client { get; } = client;
        public LineTransport Transport { get; } = transport;
        public string SessionId { get; } = sessionId;
        public void Dispose()
        {
            Client.Dispose();
            Transport.Dispose();
        }
    }

    private static string PromptText(JsonElement frame)
    {
        var prompt = frame.GetProperty("params").GetProperty("prompt");
        return prompt.ValueKind == JsonValueKind.Array
            ? prompt[0].GetProperty("text").GetString() ?? ""
            : "";
    }

    private static string Method(JsonElement frame) =>
        frame.TryGetProperty("method", out var method) ? method.GetString() ?? "" : "";

    private static int Id(JsonElement frame) => frame.GetProperty("id").GetInt32();

    private sealed class LineTransport : IDisposable
    {
        private readonly Channel<string> _replies = Channel.CreateUnbounded<string>();
        private readonly Channel<JsonElement> _requests = Channel.CreateUnbounded<JsonElement>();

        public LineTransport()
        {
            Reader = new ReplyReader(_replies.Reader);
            Writer = new RequestWriter(_requests.Writer);
        }

        public TextReader Reader { get; }
        public TextWriter Writer { get; }

        public void Reply(int id, object result) =>
            Write(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));

        public void ReplyString(string id, object result) =>
            Write("{\"jsonrpc\":\"2.0\",\"id\":" + JsonSerializer.Serialize(id) + ",\"result\":" + JsonSerializer.Serialize(result) + "}");

        public void Frame(string json) => Write(json);

        public void Error(int id, string codeJson, string message) =>
            Write("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":" + codeJson + ",\"message\":" + JsonSerializer.Serialize(message) + "}}");

        public void Update(string sessionId, string text) =>
            Write(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "session/update",
                @params = new
                {
                    sessionId,
                    update = new
                    {
                        sessionUpdate = "agent_message_chunk",
                        content = new { type = "text", text }
                    }
                }
            }));

        public async Task<JsonElement> NextRequestAsync(CancellationToken cancellationToken) =>
            await _requests.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        public void Dispose()
        {
            _replies.Writer.TryComplete();
            _requests.Writer.TryComplete();
        }

        private void Write(string line)
        {
            if (!_replies.Writer.TryWrite(line))
                throw new InvalidOperationException("The line transport is closed.");
        }

        private sealed class ReplyReader(ChannelReader<string> lines) : TextReader
        {
            public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
            {
                try { return await lines.ReadAsync(cancellationToken).ConfigureAwait(false); }
                catch (ChannelClosedException) { return null; }
            }
        }

        private sealed class RequestWriter(ChannelWriter<JsonElement> requests) : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;

            public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(buffer);
                if (!requests.TryWrite(document.RootElement.Clone()))
                    throw new InvalidOperationException("The line transport is closed.");
                return Task.CompletedTask;
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
