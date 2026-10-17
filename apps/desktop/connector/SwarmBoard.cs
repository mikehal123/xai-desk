using System.Text.Json;
using System.Text.Json.Serialization;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Connector;

public static class SwarmStatus
{
    public const string Todo = "todo";
    public const string Doing = "doing";
    public const string Review = "review";
    public const string Done = "done";
    public const string Failed = "failed";
}

public static class SwarmRole
{
    public const string Engineer = "engineer";
    public const string Investigator = "investigator";
    public const string Researcher = "researcher";
    /// <summary>Second, independent lookup agent. Cross-checks facts and finds sources the researcher missed.</summary>
    public const string Scout = "scout";
    public const string Verifier = "verifier";

    /// <summary>
    /// Grok Bot is the grandparent above the manager, never a swarm worker. Older boards or a
    /// manager that ignores the brief may still write a grokbot role; those are legacy and get remapped.
    /// </summary>
    public static bool IsLegacyGrokbot(string? role)
    {
        var text = (role ?? "").Trim();
        return text.StartsWith("grokbot", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("grok-bot", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("grok_bot", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("grok bot", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Trims the role and maps any legacy grokbot role to scout so after-links stay intact.</summary>
    public static string Normalize(string? role)
    {
        var text = (role ?? "").Trim();
        return IsLegacyGrokbot(text) ? Scout : text;
    }
}

public sealed class SwarmTask
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = SwarmStatus.Todo;
    [JsonPropertyName("assignee")] public string Assignee { get; set; } = "";
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("proof")] public string Proof { get; set; } = "";
    [JsonPropertyName("verifier_note")] public string VerifierNote { get; set; } = "";
    [JsonPropertyName("details")] public string Details { get; set; } = "";
    [JsonPropertyName("retries")] public int Retries { get; set; }
    [JsonPropertyName("interrupt_retries")] public int InterruptRetries { get; set; }
    [JsonPropertyName("files")] public List<string> Files { get; set; } = [];
    [JsonPropertyName("after")] public List<string> After { get; set; } = [];
    [JsonPropertyName("phase")] public string Phase { get; set; } = "";
    [JsonPropertyName("pinned_goal")] public string PinnedGoal { get; set; } = "";
    /// <summary>Display status from <see cref="SwarmBoard.Read"/>. Not stored in board.json.</summary>
    [JsonIgnore] public string Shown { get; set; } = "";
}

public sealed class SwarmBoard
{
    public const int MaxRetries = 2;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true
    };

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("goal")] public string Goal { get; set; } = "";
    [JsonPropertyName("tasks")] public List<SwarmTask> Tasks { get; set; } = [];
    [JsonPropertyName("review_loops")] public int ReviewLoops { get; set; }
    [JsonPropertyName("review_handled")] public int ReviewHandled { get; set; }
    [JsonPropertyName("review_status")] public string ReviewStatus { get; set; } = "";
    [JsonPropertyName("review_verdict")] public string ReviewVerdict { get; set; } = "";
    [JsonPropertyName("review_issues")] public string ReviewIssues { get; set; } = "";
    [JsonPropertyName("review_next")] public string ReviewNext { get; set; } = "";
    [JsonPropertyName("review_note")] public string ReviewNote { get; set; } = "";

    [JsonIgnore] public string Root { get; set; } = DefaultRoot();
    [JsonIgnore] public string BoardPath => Path.Combine(Root, Id, "board.json");

    public static string DefaultRoot() =>
        Path.Combine(Sovereign.Desktop.Core.XaiDeskIdentity.DataDirectory(), "swarm");

    public static string PanelState(string status) => status switch
    {
        SwarmStatus.Todo => "queued",
        SwarmStatus.Doing or SwarmStatus.Review => "running",
        SwarmStatus.Done => "completed",
        SwarmStatus.Failed => "failed",
        _ => status
    };

    public static SwarmBoard Create(string goal, IEnumerable<SwarmTask> tasks, string? root = null, string? id = null, string? caseAgainst = null)
    {
        if (string.IsNullOrWhiteSpace(goal)) throw new ArgumentException("Swarm goal is required.", nameof(goal));
        if (string.IsNullOrWhiteSpace(caseAgainst))
            throw new ArgumentException("The disagreement case against the plan is required.", nameof(caseAgainst));
        var board = new SwarmBoard
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim(),
            Goal = goal.Trim(),
            Root = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root,
            Tasks = tasks.Select(Copy).ToList()
        };
        if (board.Tasks.Count == 0) throw new ArgumentException("A swarm needs at least one task.", nameof(tasks));
        if (board.Tasks.Any(task => string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Title)))
            throw new ArgumentException("Every task needs an id and a title.", nameof(tasks));
        foreach (var task in board.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Status) || task.Status is SwarmStatus.Done or SwarmStatus.Failed)
                task.Status = SwarmStatus.Todo;
        }
        if (File.Exists(board.BoardPath))
            throw new IOException("A swarm board already exists at that path. Start a new run instead of overwriting it.");
        board.Save();
        foreach (var task in board.Tasks)
            DoctrineGate.AppendHolds(board, task.Id, caseAgainst);
        return board;
    }

    public static SwarmBoard Read(string path)
    {
        var board = JsonSerializer.Deserialize<SwarmBoard>(File.ReadAllText(path), Json) ?? throw new IOException("Swarm board is empty.");
        var directory = Path.GetDirectoryName(path);
        board.Root = string.IsNullOrEmpty(directory) ? "" : Path.GetDirectoryName(directory) ?? "";
        var directoryName = string.IsNullOrEmpty(directory) ? "" : Path.GetFileName(directory);
        board.Id = string.IsNullOrWhiteSpace(board.Id) ? directoryName : board.Id;
        board.Tasks ??= [];
        foreach (var task in board.Tasks)
        {
            if (task is null) continue;
            task.Role = SwarmRole.Normalize(task.Role);
            task.Files ??= [];
            task.After ??= [];
            task.Phase ??= "";
            task.PinnedGoal ??= "";
            task.Shown = DoctrineGate.ShownStatus(board, task);
        }
        return board;
    }

    public static SwarmBoard Load(string id, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Swarm id is required.", nameof(id));
        var folder = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root;
        var path = Path.Combine(folder, id.Trim(), "board.json");
        if (!File.Exists(path)) throw new KeyNotFoundException("That swarm board is not on disk.");
        var board = Read(path);
        board.Root = folder;
        board.Id = string.IsNullOrWhiteSpace(board.Id) ? id.Trim() : board.Id;
        board.Tasks ??= [];
        return board;
    }

    public static IReadOnlyList<string> ListIds(string? root = null)
    {
        var folder = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root;
        if (!Directory.Exists(folder)) return [];
        return Directory.GetDirectories(folder)
            .Select(path => Path.GetFileName(path))
            .Where(name => File.Exists(Path.Combine(folder, name, "board.json")))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public static string ReadText(string? swarmId, string? root = null, string? runId = null)
    {
        var folder = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root;
        if (!string.IsNullOrWhiteSpace(swarmId) && !string.IsNullOrWhiteSpace(runId))
        {
            var path = RunBoardPath(swarmId, runId, folder);
            if (!File.Exists(path)) throw new KeyNotFoundException("That swarm board is not on disk.");
            return File.ReadAllText(path);
        }
        if (string.IsNullOrWhiteSpace(swarmId))
        {
            var lines = ListIds(folder).Select(id =>
            {
                var board = Load(id, folder);
                return id + "\t" + board.Goal.Replace('\n', ' ') + "\t" + string.Join(",", board.Tasks.Select(task => task.Id + ":" + task.Status));
            });
            var text = string.Join("\n", lines);
            return text.Length == 0 ? "No swarm boards." : text;
        }
        return File.ReadAllText(Load(swarmId.Trim(), folder).BoardPath);
    }

    public static SwarmBoard RequireTask(string taskId, string? root, out SwarmTask task, string? swarmId = null, string? runId = null)
    {
        if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("Task id is required.", nameof(taskId));
        var wanted = taskId.Trim();
        if (!string.IsNullOrWhiteSpace(runId))
        {
            if (string.IsNullOrWhiteSpace(swarmId)) throw new ArgumentException("Swarm id is required.", nameof(swarmId));
            var pinned = RunBoardPath(swarmId, runId, root);
            if (!File.Exists(pinned)) throw new KeyNotFoundException("That swarm task is not on a board.");
            return Take(Read(pinned), wanted, out task);
        }
        if (!string.IsNullOrWhiteSpace(swarmId))
        {
            foreach (var path in RunBoardFiles(swarmId, root))
            {
                var board = Read(path);
                var found = board.Tasks.FirstOrDefault(item => item.Id == wanted);
                if (found is null) continue;
                task = found;
                return board;
            }
            throw new KeyNotFoundException("That swarm task is not on a board.");
        }
        foreach (var id in ListIds(root))
        {
            var board = Load(id, root);
            var found = board.Tasks.FirstOrDefault(item => item.Id == wanted);
            if (found is null) continue;
            task = found;
            return board;
        }
        throw new KeyNotFoundException("That swarm task is not on a board.");
    }

    public static string RunBoardPath(string swarmId, string runId, string? root = null)
    {
        if (!IsBoardId(swarmId)) throw new ArgumentException("Swarm id is required.", nameof(swarmId));
        if (!IsBoardId(runId)) throw new ArgumentException("Run id is required.", nameof(runId));
        var folder = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root;
        return Path.Combine(folder, swarmId.Trim(), "runs", runId.Trim(), "board.json");
    }

    public static IReadOnlyList<string> RunBoardFiles(string swarmId, string? root = null)
    {
        if (!IsBoardId(swarmId)) return [];
        var folder = string.IsNullOrWhiteSpace(root) ? DefaultRoot() : root;
        var runs = Path.Combine(folder, swarmId.Trim(), "runs");
        if (!Directory.Exists(runs)) return [];
        return Directory.GetDirectories(runs)
            .Select(dir => Path.Combine(dir, "board.json"))
            .Where(File.Exists)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsBoardId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Guid.TryParseExact(value.Trim(), "N", out _);

    private static SwarmBoard Take(SwarmBoard board, string taskId, out SwarmTask task)
    {
        var found = board.Tasks.FirstOrDefault(item => item.Id == taskId);
        if (found is null) throw new KeyNotFoundException("That swarm task is not on a board.");
        task = found;
        return board;
    }

    public SwarmTask TaskById(string taskId) =>
        Tasks.FirstOrDefault(task => task.Id == taskId) ?? throw new KeyNotFoundException("That task is not on this board.");

    public SwarmTask Start(string taskId, string assignee)
    {
        if (string.IsNullOrWhiteSpace(assignee))
            throw new ArgumentException("A claim needs the helper's name.", nameof(assignee));
        var name = assignee.Trim();
        using var gate = SwarmBoardLock.Acquire(BoardPath);
        var live = Reload();
        var task = live.TaskById(taskId);
        if (task.Status == SwarmStatus.Doing)
        {
            var owner = string.IsNullOrWhiteSpace(task.Assignee) ? "an unnamed helper" : task.Assignee.Trim();
            throw new InvalidOperationException("Task " + task.Id + " is already claimed by " + owner + ".");
        }
        if (task.Status != SwarmStatus.Todo) throw new InvalidOperationException("Only a todo task can move to doing.");
        if (!SwarmSchedule.CanStart(live, taskId))
            throw new InvalidOperationException("That task is not ready. At most 4 tasks can be doing, and a task waits while its files are in use or its earlier tasks are not done.");
        DoctrineGate.RequireForStart(live, taskId);
        task.Status = SwarmStatus.Doing;
        task.Assignee = name;
        DoctrineWrite.Allow = true;
        try { live.Save(); }
        finally { DoctrineWrite.Allow = false; }
        CopyFrom(live);
        return TaskById(taskId);
    }

    public SwarmTask Submit(string taskId, string result, string proof)
    {
        var task = TaskById(taskId);
        if (task.Status is not (SwarmStatus.Doing or SwarmStatus.Todo))
            throw new InvalidOperationException("Only a todo or doing task can move to review.");
        if (string.IsNullOrWhiteSpace(proof)) throw new ArgumentException("Nothing is done without proof.");
        DoctrineGate.RequireForSubmit(this, taskId);
        task.Result = result ?? "";
        task.Proof = proof.Trim();
        task.Status = SwarmStatus.Review;
        Save();
        return task;
    }

    public SwarmTask Verify(string taskId, bool pass, string note)
    {
        var task = TaskById(taskId);
        if (task.Status != SwarmStatus.Review) throw new InvalidOperationException("Only the verifier can finish a task, and only from review.");
        if (pass)
        {
            if (string.IsNullOrWhiteSpace(task.Proof)) throw new InvalidOperationException("Nothing is done without proof.");
            DoctrineGate.RequireForVerifyPass(this, taskId);
            task.VerifierNote = note ?? "";
            task.Status = SwarmStatus.Done;
            DoctrineWrite.Allow = true;
            try { Save(); }
            finally { DoctrineWrite.Allow = false; }
            return task;
        }
        task.VerifierNote = note ?? "";
        if (task.Retries >= MaxRetries)
        {
            task.Status = SwarmStatus.Failed;
        }
        else
        {
            task.Retries++;
            task.Status = SwarmStatus.Todo;
            task.Assignee = "";
        }
        Save();
        return task;
    }

    /// <summary>
    /// One decision. The first interrupt returns the task to todo with a narrower brief.
    /// The next interrupt or failure marks it failed and leaves the other tasks alone.
    /// </summary>
    public SwarmTask Interrupt(string taskId, string? reason)
    {
        var task = TaskById(taskId);
        if (task.Status != SwarmStatus.Doing) return task;
        var why = ClipReason(reason);
        if (task.InterruptRetries >= 1)
        {
            task.Status = SwarmStatus.Failed;
            task.Result = why;
            Save();
            return task;
        }
        task.InterruptRetries = 1;
        task.Status = SwarmStatus.Todo;
        task.Assignee = "";
        task.Result = "Interrupted once. " + why;
        var title = string.IsNullOrWhiteSpace(task.Title) ? task.Id : task.Title.Trim();
        var line = "Narrowed after an interrupt: do only \"" + title + "\". Drop side work. Reason: " + why;
        task.Details = string.IsNullOrWhiteSpace(task.Details) ? line : task.Details.Trim() + "\n" + line;
        Save();
        return task;
    }

    /// <summary>
    /// Button path. The first use restarts a doing, todo, or review task into one narrower retry.
    /// Using it again on that task fails only that task. Done and failed stay as they are.
    /// </summary>
    public SwarmTask Respawn(string taskId, string? reason)
    {
        var task = TaskById(taskId);
        if (task.Status is SwarmStatus.Done or SwarmStatus.Failed) return task;
        if (task.InterruptRetries >= 1 && task.Status != SwarmStatus.Doing)
        {
            task.Status = SwarmStatus.Failed;
            task.Result = ClipReason(reason);
            Save();
            return task;
        }
        if (task.Status is SwarmStatus.Todo or SwarmStatus.Review && task.InterruptRetries == 0)
        {
            var why = ClipReason(reason);
            task.InterruptRetries = 1;
            task.Status = SwarmStatus.Todo;
            task.Assignee = "";
            task.Result = "Interrupted once. " + why;
            var title = string.IsNullOrWhiteSpace(task.Title) ? task.Id : task.Title.Trim();
            var line = "Narrowed after an interrupt: do only \"" + title + "\". Drop side work. Reason: " + why;
            task.Details = string.IsNullOrWhiteSpace(task.Details) ? line : task.Details.Trim() + "\n" + line;
            Save();
            return task;
        }
        return Interrupt(taskId, reason);
    }

    private SwarmBoard Reload()
    {
        if (!File.Exists(BoardPath)) return this;
        var live = Read(BoardPath);
        live.Root = Root;
        live.Id = Id;
        return live;
    }

    private void CopyFrom(SwarmBoard live)
    {
        Id = live.Id;
        Goal = live.Goal;
        Tasks = live.Tasks;
        ReviewLoops = live.ReviewLoops;
        ReviewHandled = live.ReviewHandled;
        ReviewStatus = live.ReviewStatus;
        ReviewVerdict = live.ReviewVerdict;
        ReviewIssues = live.ReviewIssues;
        ReviewNext = live.ReviewNext;
        ReviewNote = live.ReviewNote;
        Root = live.Root;
    }

    private void RejectUngated(SwarmBoard? existing)
    {
        if (DoctrineWrite.Allow) return;
        foreach (var task in Tasks)
        {
            var prior = existing?.Tasks.FirstOrDefault(item => item is not null && item.Id == task.Id);
            var wasDoing = prior is not null && prior.Status == SwarmStatus.Doing;
            var wasDone = prior is not null && prior.Status == SwarmStatus.Done;
            if (task.Status == SwarmStatus.Doing && !wasDoing)
                throw new InvalidOperationException("Only DoctrineGate can move a task into work.");
            if (task.Status == SwarmStatus.Done && !wasDone)
                throw new InvalidOperationException("Only DoctrineGate can mark a task done.");
        }
    }

    private static string ClipReason(string? reason)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "The helper was interrupted." : reason.Trim().Replace("\r", " ").Replace("\n", " ");
        return why.Length <= 240 ? why : why[..240];
    }

    public void Save()
    {
        var path = BoardPath;
        SwarmBoard? existing = null;
        if (File.Exists(path))
        {
            existing = Read(path);
            SwarmSchedule.EnsureSameRun(existing, this);
            RejectUngated(existing);
            File.Copy(path, path + ".bak", true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, true);
    }

    private static SwarmTask Copy(SwarmTask source) => new()
    {
        Id = source.Id.Trim(),
        Title = source.Title.Trim(),
        Role = SwarmRole.Normalize(source.Role),
        Status = string.IsNullOrWhiteSpace(source.Status) ? SwarmStatus.Todo : source.Status.Trim(),
        Assignee = source.Assignee ?? "",
        Result = source.Result ?? "",
        Proof = source.Proof ?? "",
        VerifierNote = source.VerifierNote ?? "",
        Details = source.Details ?? "",
        Retries = source.Retries,
        InterruptRetries = source.InterruptRetries,
        Files = source.Files is null ? [] : [.. source.Files],
        After = source.After is null ? [] : [.. source.After],
        Phase = source.Phase ?? "",
        PinnedGoal = source.PinnedGoal ?? ""
    };
}
