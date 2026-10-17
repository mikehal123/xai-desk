using System.Text.Json;

namespace Sovereign.Desktop.Connector;

/// <summary>
/// Tool set for the in-app swarm manager. Not the Grok Bot connector.
/// create_run calls SwarmSchedule.OpenRun. start, submit, and verify call the board doors.
/// </summary>
public static class SwarmManagerTools
{
    public const string CreateRun = "create_run";
    public const string Start = "start";
    public const string Submit = "submit";
    public const string Verify = "verify";
    public const string Assumption = "swarm_assumption";
    public const string Rollback = "swarm_rollback";
    public const string Decay = "swarm_decay";

    public static readonly string[] Names =
    [
        CreateRun,
        Start,
        Submit,
        Verify,
        Assumption,
        Rollback,
        Decay
    ];

    public static object[] Definitions() =>
    [
        Schema(CreateRun, "Create run. Opens a swarm run. swarm_id is the conversation id. case_against is the disagreement. tasks need id, title, and role.",
            Props(("swarm_id", "string"), ("goal", "string"), ("case_against", "string"), ("tasks", "array")),
            "swarm_id", "goal", "case_against", "tasks"),
        Schema(Start, "Move a todo task to doing. Calls the board start door. assignee is the helper session id.",
            Props(("swarm_id", "string"), ("run_id", "string"), ("task_id", "string"), ("assignee", "string")),
            "swarm_id", "run_id", "task_id", "assignee"),
        Schema(Submit, "Move a todo or doing task to review. Calls the board submit door. proof is required.",
            Props(("swarm_id", "string"), ("run_id", "string"), ("task_id", "string"), ("result", "string"), ("proof", "string")),
            "swarm_id", "run_id", "task_id", "result", "proof"),
        Schema(Verify, "Finish a task from review. Calls the board verify door. pass is true or false. A fail sends the task back.",
            Props(("swarm_id", "string"), ("run_id", "string"), ("task_id", "string"), ("pass", "boolean"), ("note", "string")),
            "swarm_id", "run_id", "task_id", "pass", "note"),
        Schema(Assumption, "Append an assumption row. Verdict is STATED, INFERRED, GUESSED, or OVERTURNED.",
            Props(("swarm_id", "string"), ("run_id", "string"), ("task_id", "string"), ("verdict", "string"), ("text", "string")),
            "swarm_id", "run_id", "task_id", "verdict", "text"),
        Schema(Rollback, "Append a rollback row. Class is REVERSIBLE, RECOVERABLE, or IRREVERSIBLE. The owner's yes stays off.",
            Props(("swarm_id", "string"), ("run_id", "string"), ("task_id", "string"), ("verdict", "string"), ("undo", "string"), ("path", "string")),
            "swarm_id", "run_id", "task_id", "verdict", "undo", "path"),
        Schema(Decay, "Append a decay row. pinned_goal is copied from the board goal in code.",
            Props(("swarm_id", "string"), ("run_id", "string"), ("task_id", "string")),
            "swarm_id", "run_id", "task_id")
    ];

    public static string Call(string name, JsonElement args, string? root = null) => name switch
    {
        CreateRun => Create(args, root),
        Start => Begin(args, root),
        Submit => HandIn(args, root),
        Verify => Check(args, root),
        Assumption => Assume(args, root),
        Rollback => Roll(args, root),
        Decay => DecayRow(args, root),
        _ => throw new ArgumentException("Unknown tool.")
    };

    static string Create(JsonElement args, string? root)
    {
        var board = SwarmSchedule.OpenRun(Text(args, "swarm_id"), Text(args, "goal"), Tasks(args), root, Text(args, "case_against"));
        return "run\t" + board.Id + "\t" + string.Join(",", board.Tasks.Select(task => task.Id));
    }

    static string Begin(JsonElement args, string? root)
    {
        var board = Open(args, root);
        var task = board.Start(Text(args, "task_id"), Text(args, "assignee"));
        return "doing\t" + task.Id + "\t" + task.Assignee;
    }

    static string HandIn(JsonElement args, string? root)
    {
        var board = Open(args, root);
        var task = board.Submit(Text(args, "task_id"), Text(args, "result"), Text(args, "proof"));
        return "review\t" + task.Id;
    }

    static string Check(JsonElement args, string? root)
    {
        var board = Open(args, root);
        var task = board.Verify(Text(args, "task_id"), Flag(args, "pass"), Text(args, "note"));
        return task.Status + "\t" + task.Id;
    }

    static string Assume(JsonElement args, string? root)
    {
        var board = Open(args, root);
        var taskId = Text(args, "task_id");
        var verdict = Text(args, "verdict");
        DoctrineGate.AppendAssumption(board, taskId, verdict, Text(args, "text"));
        return "assumption\t" + taskId.Trim() + "\t" + verdict.Trim();
    }

    static string Roll(JsonElement args, string? root)
    {
        var board = Open(args, root);
        var taskId = Text(args, "task_id");
        var verdict = Text(args, "verdict");
        DoctrineGate.AppendRollback(board, taskId, verdict, Text(args, "undo"), Text(args, "path"));
        return "rollback\t" + taskId.Trim() + "\t" + verdict.Trim();
    }

    static string DecayRow(JsonElement args, string? root)
    {
        var board = Open(args, root);
        var taskId = Text(args, "task_id");
        DoctrineGate.AppendDecay(board, taskId);
        return "decay\t" + taskId.Trim() + "\t" + (board.Goal ?? "");
    }

    static SwarmBoard Open(JsonElement args, string? root)
    {
        var path = SwarmBoard.RunBoardPath(Text(args, "swarm_id"), Text(args, "run_id"), root);
        if (!File.Exists(path)) throw new KeyNotFoundException("That swarm board is not on disk.");
        return SwarmBoard.Read(path);
    }

    static List<SwarmTask> Tasks(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("tasks are required.");
        var plan = JsonSerializer.Deserialize<List<SwarmTask>>(tasks.GetRawText()) ?? [];
        if (plan.Count == 0 || plan.Any(task => task is null))
            throw new ArgumentException("A swarm needs at least one task.");
        foreach (var task in plan)
        {
            task.Files ??= [];
            task.After ??= [];
        }
        return plan;
    }

    static string Text(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    static bool Flag(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var value))
            throw new ArgumentException(key + " is required.");
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        throw new ArgumentException(key + " must be true or false.");
    }

    static Dictionary<string, object> Props(params (string Name, string Type)[] fields)
    {
        var properties = new Dictionary<string, object>();
        foreach (var field in fields)
            properties[field.Name] = field.Name == "tasks"
                ? new { type = "array", items = new { type = "object" } }
                : new { type = field.Type };
        return properties;
    }

    static object Schema(string name, string description, Dictionary<string, object> properties, params string[] required) =>
        new { name, description, inputSchema = new { type = "object", properties, required } };
}
