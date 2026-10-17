using System.Text;

namespace Sovereign.Desktop.Connector;

public static class SwarmSchedule
{
    public const int MinSplit = 3;
    public const int MaxSplit = 6;
    public const int MaxWorkers = 4;

    private static readonly string[] CountWords = ["3", "4", "5", "6", "three", "four", "five", "six"];
    private const string SplitWord = "re" + "search";
    private static readonly string[] CoreRoles =
    [
        SwarmRole.Engineer,
        SwarmRole.Researcher,
        SwarmRole.Scout,
        SwarmRole.Verifier
    ];

    public static bool CanSplit(string? goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return false;
        return HasCountWord(goal)
            || (goal.Contains(" and ", StringComparison.OrdinalIgnoreCase)
                && (goal.Contains(SplitWord, StringComparison.OrdinalIgnoreCase)
                    || goal.Contains("write", StringComparison.OrdinalIgnoreCase)))
            || PieceCount(goal) >= MinSplit;
    }

    public static bool LookupAndWrite(string? goal)
    {
        if (string.IsNullOrEmpty(goal)) return false;
        if (!goal.Contains(SplitWord, StringComparison.OrdinalIgnoreCase)) return false;
        return goal.Contains("write", StringComparison.OrdinalIgnoreCase)
            || goal.Contains("comparison", StringComparison.OrdinalIgnoreCase)
            || goal.Contains(".md", StringComparison.OrdinalIgnoreCase);
    }

    public static void CheckPlan(string goal, IReadOnlyList<SwarmTask> tasks)
    {
        if (string.IsNullOrWhiteSpace(goal)) throw new ArgumentException("Swarm goal is required.", nameof(goal));
        if (tasks is null || tasks.Count == 0) throw new ArgumentException("A swarm needs at least one task.", nameof(tasks));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Title))
                throw new ArgumentException("Every task needs an id and a title.", nameof(tasks));
            if (!ids.Add(task.Id)) throw new ArgumentException("Duplicate task ids are illegal.", nameof(tasks));
            if (SwarmRole.IsLegacyGrokbot(task.Role))
                throw new ArgumentException("Grok Bot is not a swarm worker. Use scout for the second lookup.", nameof(tasks));
        }

        if (!CanSplit(goal))
        {
            if (tasks.Count != 1) throw new ArgumentException("That goal stays one task.", nameof(tasks));
        }
        else if (LookupAndWrite(goal))
        {
            if (tasks.Count is < 4 or > MaxSplit)
                throw new ArgumentException("Lookup and write needs 4 to 6 tasks.", nameof(tasks));
            if (!HasAllCoreRoles(tasks))
                throw new ArgumentException("Lookup and write needs engineer, researcher, scout, and verifier.", nameof(tasks));
            if (!VerifierHasAfter(tasks))
                throw new ArgumentException("A verifier task needs an After list.", nameof(tasks));
        }
        else
        {
            if (tasks.Count is < MinSplit or > MaxSplit)
                throw new ArgumentException("A split goal needs 3 to 6 tasks.", nameof(tasks));
            if (DistinctCoreRoles(tasks) < 2 || !HasRole(tasks, SwarmRole.Verifier))
                throw new ArgumentException("A split goal needs a verifier and another role.", nameof(tasks));
        }

        foreach (var task in tasks)
        {
            if (task.After is null) continue;
            foreach (var after in task.After)
            {
                if (string.Equals(after, task.Id, StringComparison.Ordinal))
                    throw new ArgumentException("A task must not list itself in After.", nameof(tasks));
                if (after is null || !ids.Contains(after))
                    throw new ArgumentException("After ids must be task ids on this plan.", nameof(tasks));
            }
        }
    }

    public static IReadOnlyList<string> NormalizeFiles(SwarmTask task)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        if (task.Files is null) return files;
        foreach (var entry in task.Files)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var normalized = entry.Trim().Replace('/', '\\');
            if (seen.Add(normalized)) files.Add(normalized);
        }
        return files;
    }

    public static bool CanStart(SwarmBoard board, string taskId)
    {
        var task = board.TaskById(taskId);
        if (task.Status != SwarmStatus.Todo) return false;
        if (DoingCount(board) >= MaxWorkers) return false;
        if (!PredecessorsSatisfied(board, task)) return false;
        var busy = BusyFiles(board);
        foreach (var file in NormalizeFiles(task))
            if (BusySetBlocks(board, task, file, busy)) return false;
        return true;
    }

    public static IReadOnlyList<string> Ready(SwarmBoard board)
    {
        var slots = MaxWorkers - DoingCount(board);
        if (slots <= 0) return [];
        var busy = BusyFiles(board);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ready = new List<string>();
        foreach (var task in board.Tasks)
        {
            if (ready.Count == slots) break;
            if (!CanStart(board, task.Id)) continue;
            var files = NormalizeFiles(task);
            if (files.Any(file => reserved.Contains(file) || BusySetBlocks(board, task, file, busy))) continue;
            ready.Add(task.Id);
            foreach (var file in files)
            {
                busy.Add(file);
                reserved.Add(file);
            }
        }
        return ready;
    }

    public static SwarmBoard OpenRun(string conversationId, string goal, IEnumerable<SwarmTask> tasks, string? root = null, string? caseAgainst = null)
    {
        if (string.IsNullOrEmpty(conversationId) || !Guid.TryParseExact(conversationId, "N", out _))
            throw new ArgumentException("Conversation id must be a Guid in N format.", nameof(conversationId));
        if (tasks is null) throw new ArgumentException("A swarm needs at least one task.", nameof(tasks));
        var plan = tasks.ToList();
        CheckPlan(goal, plan);
        var folder = string.IsNullOrWhiteSpace(root) ? SwarmBoard.DefaultRoot() : root;
        var runId = Guid.NewGuid().ToString("N");
        var conversationDir = Path.Combine(folder, conversationId);
        var board = SwarmBoard.Create(goal, plan, Path.Combine(conversationDir, "runs"), runId, caseAgainst);
        Directory.CreateDirectory(conversationDir);
        File.WriteAllText(Path.Combine(conversationDir, "current.txt"), board.Id, new UTF8Encoding(false));
        return board;
    }

    public static bool HasOpenWork(SwarmBoard board) =>
        board.Tasks.Any(task => SwarmSection.DisplayStatus(task) is not ("done" or "failed"));

    public static void EnsureSameRun(SwarmBoard existing, SwarmBoard incoming)
    {
        if (!HasOpenWork(existing)) return;
        var sameId = string.Equals(existing.Id, incoming.Id, StringComparison.Ordinal);
        var kept = existing.Tasks.All(task => incoming.Tasks.Any(next => next.Id == task.Id));
        if (DoctrineWrite.Allow && sameId && kept) return;
        var sameGoal = sameId && string.Equals(existing.Goal, incoming.Goal, StringComparison.Ordinal);
        if (sameGoal && kept) return;
        throw new IOException("That run still has an open task. Leave its board.json alone and start a new run.");
    }

    private static bool HasCountWord(string goal)
    {
        var length = goal.Length;
        for (var i = 0; i < length;)
        {
            if (!IsWordChar(goal[i])) { i++; continue; }
            var start = i;
            while (i < length && IsWordChar(goal[i])) i++;
            if (IsCountWord(goal.AsSpan(start, i - start))) return true;
        }
        return false;
    }

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool IsCountWord(ReadOnlySpan<char> word)
    {
        foreach (var candidate in CountWords)
            if (word.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int PieceCount(string goal)
    {
        var count = 0;
        var pending = false;
        for (var i = 0; i < goal.Length;)
        {
            var width = BreakWidth(goal, i);
            if (width > 0)
            {
                if (pending) count++;
                pending = false;
                i += width;
                continue;
            }
            if (!char.IsWhiteSpace(goal[i])) pending = true;
            i++;
        }
        if (pending) count++;
        return count;
    }

    private static int BreakWidth(string goal, int index)
    {
        var c = goal[index];
        if (c == '\r') return index + 1 < goal.Length && goal[index + 1] == '\n' ? 2 : 1;
        if (c == '\n') return 1;
        if (index + 1 < goal.Length && goal[index + 1] == ' ' && (c == '.' || c == ';')) return 2;
        return 0;
    }

    private static bool HasAllCoreRoles(IReadOnlyList<SwarmTask> tasks)
    {
        foreach (var role in CoreRoles)
            if (!HasRole(tasks, role)) return false;
        return true;
    }

    private static int DistinctCoreRoles(IReadOnlyList<SwarmTask> tasks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var role = task.Role?.Trim() ?? "";
            if (CoreRoles.Any(core => string.Equals(core, role, StringComparison.OrdinalIgnoreCase)))
                seen.Add(role);
        }
        return seen.Count;
    }

    private static bool HasRole(IReadOnlyList<SwarmTask> tasks, string role) =>
        tasks.Any(task => string.Equals(task.Role?.Trim(), role, StringComparison.OrdinalIgnoreCase));

    private static bool VerifierHasAfter(IReadOnlyList<SwarmTask> tasks)
    {
        foreach (var task in tasks)
        {
            if (!string.Equals(task.Role?.Trim(), SwarmRole.Verifier, StringComparison.OrdinalIgnoreCase)) continue;
            if (task.After is null) continue;
            foreach (var after in task.After)
                if (!string.IsNullOrWhiteSpace(after)) return true;
        }
        return false;
    }

    private static bool PredecessorsSatisfied(SwarmBoard board, SwarmTask task)
    {
        if (task.After is null) return true;
        foreach (var after in task.After)
        {
            var prior = board.Tasks.FirstOrDefault(item => item.Id == after);
            if (prior is null) return false;
            if (OwnEngineer(task, prior))
            {
                if (prior.Status is not (SwarmStatus.Review or SwarmStatus.Done)) return false;
            }
            else if (prior.Status != SwarmStatus.Done)
            {
                return false;
            }
        }
        return true;
    }

    private static bool BusySetBlocks(SwarmBoard board, SwarmTask task, string file, HashSet<string> busy) =>
        busy.Contains(file) && !HeldOnlyByOwnEngineerInReview(board, task, file);

    private static bool HeldOnlyByOwnEngineerInReview(SwarmBoard board, SwarmTask task, string file)
    {
        if (!SameRole(task, SwarmRole.Verifier)) return false;
        var held = false;
        foreach (var other in board.Tasks)
        {
            if (string.Equals(other.Id, task.Id, StringComparison.Ordinal)) continue;
            if (other.Status is not (SwarmStatus.Doing or SwarmStatus.Review)) continue;
            if (!ListsFile(other, file)) continue;
            held = true;
            if (other.Status != SwarmStatus.Review || !OwnEngineer(task, other)) return false;
        }
        return held;
    }

    private static bool OwnEngineer(SwarmTask task, SwarmTask prior)
    {
        if (!SameRole(task, SwarmRole.Verifier) || !SameRole(prior, SwarmRole.Engineer)) return false;
        if (task.After is null) return false;
        foreach (var after in task.After)
            if (string.Equals(after, prior.Id, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool SameRole(SwarmTask task, string role) =>
        string.Equals(task.Role?.Trim(), role, StringComparison.OrdinalIgnoreCase);

    private static bool ListsFile(SwarmTask task, string file)
    {
        foreach (var candidate in NormalizeFiles(task))
            if (string.Equals(candidate, file, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int DoingCount(SwarmBoard board) => board.Tasks.Count(task => task.Status == SwarmStatus.Doing);

    private static HashSet<string> BusyFiles(SwarmBoard board)
    {
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in board.Tasks)
        {
            if (task.Status is not (SwarmStatus.Doing or SwarmStatus.Review)) continue;
            foreach (var file in NormalizeFiles(task)) busy.Add(file);
        }
        return busy;
    }
}
