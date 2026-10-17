using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmManagerToolTests
{
    [Fact]
    public void Manager_tool_set_has_no_overrule_or_owner_yes()
    {
        Assert.Equal(
            new[] { "create_run", "start", "submit", "verify", "swarm_assumption", "swarm_rollback", "swarm_decay" },
            SwarmManagerTools.Names);
        var defined = SwarmManagerTools.Definitions();
        Assert.Equal(SwarmManagerTools.Names.Length, defined.Length);
        var listed = JsonSerializer.Serialize(new { tools = defined });
        foreach (var name in SwarmManagerTools.Names)
        {
            Assert.Contains("\"" + name + "\"", listed, StringComparison.Ordinal);
            Assert.DoesNotContain("overrule", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("owner_yes", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("owneryes", name, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("overrule", listed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner_yes", listed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OwnerYes", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("8790", listed, StringComparison.Ordinal);

        var source = File.ReadAllText(Path.Combine(Repo(), "apps", "desktop", "connector", "SwarmManagerTools.cs"));
        Assert.Contains("SwarmSchedule.OpenRun", source, StringComparison.Ordinal);
        Assert.Contains("board.Start(", source, StringComparison.Ordinal);
        Assert.Contains("board.Submit(", source, StringComparison.Ordinal);
        Assert.Contains("board.Verify(", source, StringComparison.Ordinal);
        Assert.Contains("DoctrineGate.AppendAssumption", source, StringComparison.Ordinal);
        Assert.Contains("DoctrineGate.AppendRollback", source, StringComparison.Ordinal);
        Assert.Contains("DoctrineGate.AppendDecay", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DoctrineGate.Overrule", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DoctrineGate.OwnerYes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Write", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteAllText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectorHost", source, StringComparison.Ordinal);

        var manager = SwarmRoles.Manager(Path.Combine(Path.GetTempPath(), "swarm-folder"));
        foreach (var name in SwarmManagerTools.Names)
            Assert.Contains(name, manager, StringComparison.Ordinal);
        Assert.Contains("no overrule tool", manager, StringComparison.Ordinal);
        Assert.Contains("no owner-yes tool", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("board.json", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("current.txt", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("Write the board", manager, StringComparison.Ordinal);

        var panel = File.ReadAllText(Path.Combine(Repo(), "apps", "desktop", "Sovereign.Desktop", "ChatSwarmSection.cs"));
        Assert.Contains("SwarmSection.PanelStatus(board, task)", panel, StringComparison.Ordinal);
        Assert.Contains("SwarmSection.TaskChip(label, finished)", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_run_writes_the_holds_row_so_overrule_can_be_enabled()
    {
        var root = Temp();
        try
        {
            var conversation = Guid.NewGuid().ToString("N");
            var text = SwarmManagerTools.Call("create_run", JsonSerializer.SerializeToElement(new
            {
                swarm_id = conversation,
                goal = "Write the note",
                case_against = DoctrineFixture.Case,
                tasks = new[] { new { id = "t-1", title = "Write the note", role = "engineer" } }
            }), root);
            Assert.Contains("run\t", text, StringComparison.Ordinal);
            var board = SwarmSection.TryLoad(conversation, root);
            Assert.NotNull(board);
            Assert.Equal(SwarmStatus.Todo, board!.TaskById("t-1").Status);
            Assert.True(DoctrineGate.OverruleEnabled(board, "t-1"));
            Assert.Contains("\"verdict\":\"holds\"", File.ReadAllText(DoctrineGate.LogPath(board)), StringComparison.Ordinal);
            Assert.Equal("todo", SwarmSection.PanelStatus(board, board.TaskById("t-1")));

            var runId = board.Id;
            var denied = Assert.Throws<InvalidOperationException>(() => SwarmManagerTools.Call("start", JsonSerializer.SerializeToElement(new
            {
                swarm_id = conversation,
                run_id = runId,
                task_id = "t-1",
                assignee = "helper-1"
            }), root));
            Assert.Equal("disagreement holds", denied.Message);
            var again = SwarmBoard.Read(board.BoardPath);
            Assert.Equal(SwarmStatus.Todo, again.TaskById("t-1").Status);
            Assert.True(DoctrineGate.OverruleEnabled(again, "t-1"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Manager_server_lists_only_its_tools_and_create_run_writes_holds()
    {
        var root = Temp();
        await using var host = await SwarmManagerHost.StartAsync(root);
        using var http = new HttpClient();
        try
        {
            var bare = await http.PostAsync(host.Url, new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, bare.StatusCode);

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);
            var listed = await PostAsync(http, host.Url!, new { jsonrpc = "2.0", id = 2, method = "tools/list" });
            var names = listed.GetProperty("result").GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
            Assert.Equal(SwarmManagerTools.Names, names);
            Assert.DoesNotContain(names, name => name is not null && name.Contains("overrule", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("8790", host.Url, StringComparison.Ordinal);

            var conversation = Guid.NewGuid().ToString("N");
            var called = await PostAsync(http, host.Url!, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = "create_run",
                    arguments = new
                    {
                        swarm_id = conversation,
                        goal = "Write the note",
                        case_against = DoctrineFixture.Case,
                        tasks = new[] { new { id = "t-1", title = "Write the note", role = "engineer" } }
                    }
                }
            });
            Assert.False(called.GetProperty("result").GetProperty("isError").GetBoolean());
            var board = SwarmSection.TryLoad(conversation, root);
            Assert.NotNull(board);
            Assert.True(DoctrineGate.OverruleEnabled(board!, "t-1"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Stdio_tools_list_returns_the_manager_set()
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 7, method = "tools/list" });
        using var outbound = new MemoryStream();
        SwarmManagerMcp.WriteMessage(outbound, request);
        outbound.Position = 0;
        var body = SwarmManagerMcp.ReadMessage(outbound);
        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body);
        var result = SwarmManagerMcp.Handle("tools/list", document.RootElement, null);
        using var inbound = new MemoryStream();
        SwarmManagerMcp.WriteMessage(inbound, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 7, result }));
        inbound.Position = 0;
        var reply = SwarmManagerMcp.ReadMessage(inbound);
        Assert.NotNull(reply);
        using var parsed = JsonDocument.Parse(reply);
        var names = parsed.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Equal(SwarmManagerTools.Names, names);
    }

    [Fact]
    public void Hand_written_done_board_is_unverified_on_read_and_on_the_panel_path()
    {
        var root = Temp();
        try
        {
            var conversation = Guid.NewGuid().ToString("N");
            var run = Guid.NewGuid().ToString("N");
            var folder = Path.Combine(root, conversation, "runs", run);
            Directory.CreateDirectory(folder);
            var boardPath = Path.Combine(folder, "board.json");
            var json = """
            {
              "id": "RUN",
              "goal": "Hand written",
              "tasks": [
                {"id":"t-done","title":"Pretend done","role":"verifier","status":"done","files":[],"after":[]},
                {"id":"t-doing","title":"Pretend doing","role":"engineer","status":"doing","files":[],"after":[]},
                {"id":"t-review","title":"Pretend review","role":"researcher","status":"review","files":[],"after":[]}
              ]
            }
            """.Replace("RUN", run);
            File.WriteAllText(boardPath, json);
            File.WriteAllText(Path.Combine(root, conversation, "current.txt"), run);

            var read = SwarmBoard.Read(boardPath);
            Assert.Equal(json, File.ReadAllText(boardPath));
            foreach (var id in new[] { "t-done", "t-doing", "t-review" })
            {
                var task = read.TaskById(id);
                Assert.Equal("unverified", task.Shown);
                Assert.Equal("unverified", SwarmSection.PanelStatus(read, task));
                Assert.NotEqual("done", SwarmSection.PanelStatus(read, task));
                Assert.Equal("blocked", SwarmSection.PillState(SwarmSection.PanelStatus(read, task)));
                Assert.DoesNotContain("\u2713", SwarmSection.TaskChip(SwarmSection.PanelStatus(read, task), false), StringComparison.Ordinal);
                Assert.Null(SwarmSection.FeedLine(null, task));
            }
            Assert.Equal(SwarmStatus.Done, read.TaskById("t-done").Status);
            Assert.Equal("unverified", SwarmLoop.View(read, read.TaskById("t-done")).State);
            Assert.NotEqual("completed", SwarmLoop.View(read, read.TaskById("t-done")).State);
            Assert.Equal(0, SwarmSection.Measure(read.Tasks).Done);
            Assert.Equal(3, SwarmSection.Measure(read.Tasks).Unverified);

            var loaded = SwarmSection.TryLoad(conversation, root);
            Assert.NotNull(loaded);
            Assert.Equal("unverified", loaded!.TaskById("t-done").Shown);
            Assert.Equal("unverified", SwarmSection.PanelStatus(loaded, loaded.TaskById("t-done")));
            Assert.NotEqual(SwarmStatus.Done, SwarmSection.TaskChip(SwarmSection.PanelStatus(loaded, loaded.TaskById("t-done")), false));

            File.WriteAllText(boardPath, json.Replace("\"status\":\"done\"", "\"status\":\"unverified\""));
            var word = SwarmBoard.Read(boardPath);
            Assert.Equal("unverified", word.TaskById("t-done").Shown);
            Assert.NotEqual("done", SwarmSection.PanelStatus(word, word.TaskById("t-done")));
            Assert.DoesNotContain("\u2713", SwarmSection.TaskChip(SwarmSection.PanelStatus(word, word.TaskById("t-done")), false), StringComparison.Ordinal);

            File.WriteAllText(boardPath, json);
            var rewritten = SwarmBoard.Read(boardPath);
            Assert.Equal("done", rewritten.TaskById("t-done").Status);
            Assert.Equal("unverified", rewritten.TaskById("t-done").Shown);
            Assert.Equal("unverified", SwarmSection.PanelStatus(rewritten, rewritten.TaskById("t-done")));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void A_real_verify_door_still_shows_done()
    {
        var root = Temp();
        try
        {
            var board = SwarmBoard.Create("Write the note", [new SwarmTask { Id = "t-1", Title = "Write the note", Role = "engineer" }], root, null, DoctrineFixture.Case);
            DoctrineFixture.FinishFromTodo(board, "t-1");
            var read = SwarmBoard.Read(board.BoardPath);
            Assert.Equal(SwarmStatus.Done, read.TaskById("t-1").Status);
            Assert.Equal(SwarmStatus.Done, read.TaskById("t-1").Shown);
            Assert.Equal(SwarmStatus.Done, SwarmSection.PanelStatus(read, read.TaskById("t-1")));
            Assert.StartsWith("\u2713 ", SwarmSection.TaskChip(SwarmSection.PanelStatus(read, read.TaskById("t-1")), false), StringComparison.Ordinal);
            Assert.Equal("completed", SwarmLoop.View(read, read.TaskById("t-1")).State);
            Assert.Equal(1, SwarmSection.Measure(read.Tasks).Done);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Swarm_session_servers_stay_empty_until_swarm_mode_and_never_use_port_8790()
    {
        Assert.Empty(SwarmManagerSession.McpServers(null, default));
        Assert.Empty(SwarmManagerSession.McpServers(new CursorProjectOptions(), default));
        var missing = Assert.Throws<IOException>(() => SwarmManagerSession.McpServers(new CursorProjectOptions { SwarmBrief = "Swarm mode." }, default));
        Assert.Contains("manager tool server is not available", missing.Message, StringComparison.Ordinal);

        using var httpInit = JsonDocument.Parse("""{"agentCapabilities":{"mcpCapabilities":{"http":true}}}""");
        var refused = Assert.Throws<IOException>(() => SwarmManagerSession.McpServers(new CursorProjectOptions
        {
            SwarmBrief = "Swarm mode.",
            SwarmManagerUrl = "http://127.0.0.1:8790/mcp",
            SwarmManagerToken = "token"
        }, httpInit.RootElement));
        Assert.Contains("Grok Bot connector", refused.Message, StringComparison.Ordinal);

        var servers = SwarmManagerSession.McpServers(new CursorProjectOptions
        {
            SwarmBrief = "Swarm mode.",
            SwarmManagerUrl = "http://127.0.0.1:9/mcp",
            SwarmManagerToken = "token"
        }, httpInit.RootElement);
        var json = JsonSerializer.Serialize(servers);
        Assert.Contains(SwarmManagerSession.ServerName, json, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:9/mcp", json, StringComparison.Ordinal);
        Assert.DoesNotContain("8790", json, StringComparison.Ordinal);
        Assert.DoesNotContain("grokbot", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Click_error_names_the_button_and_the_append_failure()
    {
        Assert.Equal("Overrule failed: disk full", DoctrineGate.ClickError("Overrule", "disk full"));
        Assert.Equal("Owner yes failed: The append did not finish.", DoctrineGate.ClickError("Owner yes", " "));
    }

    static async Task<JsonElement> PostAsync(HttpClient http, string url, object body)
    {
        var response = await http.PostAsync(url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "apps", "desktop", "connector", "SwarmBoard.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("The connector sources were not above the test output.");
    }

    static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "swarm-manager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
    }
}
