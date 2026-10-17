using System.Text.Json;

namespace Sovereign.Desktop.Connector;

public readonly record struct SwarmNote(string Claim, string Evidence, string Verdict);

public readonly record struct SwarmProgress(int Done, int Total, int Todo, int Doing, int Review, int Failed, int Unverified = 0)
{
    public double Fraction => Total == 0 ? 0 : (double)Done / Total;
    public string Counts => "todo " + Todo + " · doing " + Doing + " · review " + Review + " · done " + Done + " · failed " + Failed
        + (Unverified > 0 ? " · unverified " + Unverified : "");
}

/// <summary>The conversation board is {swarm root}/{conversation id}/board.json.</summary>
public static class SwarmSection
{
    public static string? BoardFile(string? conversationId, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || !Guid.TryParseExact(conversationId, "N", out _)) return null;
        var folder = string.IsNullOrWhiteSpace(root) ? SwarmBoard.DefaultRoot() : root;
        return Path.Combine(folder, conversationId, "board.json");
    }

    public static string? ConversationFolder(string? conversationId, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || !Guid.TryParseExact(conversationId, "N", out _)) return null;
        var folder = string.IsNullOrWhiteSpace(root) ? SwarmBoard.DefaultRoot() : root;
        return Path.Combine(folder, conversationId);
    }

    public static bool IsConversationBoard(string? conversationId, string? path, string? root = null)
    {
        var folder = ConversationFolder(conversationId, root);
        if (folder is null || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var fullFolder = Path.GetFullPath(folder);
            var fullPath = Path.GetFullPath(path);
            var name = Path.GetFileName(fullPath);
            if (!name.Equals("board.json", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("current.txt", StringComparison.OrdinalIgnoreCase))
                return false;

            var prefix = fullFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (prefix.Length == 0) return false;
            prefix += Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return fullPath.StartsWith(prefix, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string? ActiveBoardFile(string? conversationId, string? root = null)
    {
        var folder = ConversationFolder(conversationId, root);
        if (folder is null) return null;

        var currentPath = Path.Combine(folder, "current.txt");
        if (File.Exists(currentPath))
        {
            var runId = File.ReadAllText(currentPath).Trim();
            if (Guid.TryParseExact(runId, "N", out _))
            {
                var runBoard = Path.Combine(folder, "runs", runId, "board.json");
                if (File.Exists(runBoard)) return runBoard;
            }
        }

        var runs = Path.Combine(folder, "runs");
        if (Directory.Exists(runs))
        {
            string? newest = null;
            var newestWrite = DateTime.MinValue;
            foreach (var candidate in Directory.EnumerateFiles(runs, "board.json", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(candidate).Equals("board.json", StringComparison.OrdinalIgnoreCase)) continue;
                var parent = Path.GetDirectoryName(candidate);
                if (string.Equals(Path.GetFileName(parent), "archived", StringComparison.OrdinalIgnoreCase)) continue;
                var write = File.GetLastWriteTimeUtc(candidate);
                if (newest is null || write > newestWrite)
                {
                    newest = candidate;
                    newestWrite = write;
                }
            }
            if (newest is not null) return newest;
        }

        var legacy = Path.Combine(folder, "board.json");
        return File.Exists(legacy) ? legacy : null;
    }

    public static SwarmBoard? TryLoad(string? conversationId, string? root = null)
    {
        var path = ActiveBoardFile(conversationId, root);
        if (path is null || !File.Exists(path)) return null;
        try { return SwarmBoard.Read(path); }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string WorkerSessionId(string boardId, string taskId) => "swarm-" + boardId + "-" + taskId;

    public static string? MatchingTaskId(SwarmBoard? board, string? sessionId, string? name, string? taskText)
    {
        if (board is null) return null;
        if (!string.IsNullOrWhiteSpace(sessionId) && sessionId.StartsWith("swarm-" + board.Id + "-", StringComparison.Ordinal))
            return sessionId[("swarm-" + board.Id + "-").Length..];
        foreach (var item in board.Tasks)
        {
            if (!string.IsNullOrWhiteSpace(sessionId) && string.Equals(item.Assignee, sessionId, StringComparison.OrdinalIgnoreCase))
                return item.Id;
            var title = (item.Title ?? "").Trim();
            if (title.Length < 4) continue;
            if (!string.IsNullOrWhiteSpace(name) && name.Contains(title, StringComparison.OrdinalIgnoreCase)) return item.Id;
            if (!string.IsNullOrWhiteSpace(taskText) && taskText.Contains(title, StringComparison.OrdinalIgnoreCase)) return item.Id;
        }
        return null;
    }

    public static bool IsSwarmWorker(string? sessionId, string? name, string? taskText, SwarmBoard? board) =>
        (!string.IsNullOrWhiteSpace(sessionId) && sessionId.StartsWith("swarm-", StringComparison.Ordinal))
        || MatchingTaskId(board, sessionId, name, taskText) is not null;

    public static string StatusLabel(string? status)
    {
        var text = (status ?? "").Trim().ToLowerInvariant();
        return text is "todo" or "doing" or "review" or "done" or "failed" or "unverified" ? text : "todo";
    }

    /// <summary>
    /// Read marks a door status with no doctrine row as unverified. Any other Shown value is ignored,
    /// so a later Start or Verify on the same object is not stuck on the status from that read.
    /// </summary>
    public static string DisplayStatus(SwarmTask? task)
    {
        if (task is null) return StatusLabel(null);
        if (string.Equals(task.Shown, "unverified", StringComparison.Ordinal)) return "unverified";
        return StatusLabel(task.Status);
    }

    /// <summary>Panel path. doing, review, and done without the doctrine row for that door are unverified.</summary>
    public static string PanelStatus(SwarmBoard board, SwarmTask task) => DoctrineGate.ShownStatus(board, task);

    /// <summary>Chip text the swarm panel shows. A done label is the only one with a check mark.</summary>
    public static string TaskChip(string? label, bool runFinished)
    {
        var chip = SwarmRunView.TaskLabel(label, runFinished);
        return label == SwarmStatus.Done ? "\u2713 " + chip : chip;
    }

    public static string PillState(string? status) => StatusLabel(status) switch
    {
        "doing" => "running",
        "review" or "unverified" => "blocked",
        "done" => "completed",
        "failed" => "failed",
        _ => "queued"
    };

    public static SwarmProgress Measure(IEnumerable<SwarmTask> tasks)
    {
        var list = tasks as ICollection<SwarmTask> ?? tasks.ToList();
        var todo = 0;
        var doing = 0;
        var review = 0;
        var done = 0;
        var failed = 0;
        var unverified = 0;
        foreach (var task in list)
        {
            switch (DisplayStatus(task))
            {
                case "doing": doing++; break;
                case "review": review++; break;
                case "done": done++; break;
                case "failed": failed++; break;
                case "unverified": unverified++; break;
                default: todo++; break;
            }
        }
        return new SwarmProgress(done, list.Count, todo, doing, review, failed, unverified);
    }

    public static string? FeedLine(string? previousStatus, SwarmTask task)
    {
        var status = DisplayStatus(task);
        if (previousStatus is not null && StatusLabel(previousStatus) == status) return null;
        var role = string.IsNullOrWhiteSpace(task.Role) ? "worker" : task.Role.Trim();
        var title = string.IsNullOrWhiteSpace(task.Title) ? task.Id : task.Title.Trim();
        if (status == "todo" && previousStatus == "doing")
            return role + " interrupted " + title + ". Respawn once, narrower.";
        if (status == "doing") return role + " started " + title;
        if (status == "done" && role.Equals(SwarmRole.Verifier, StringComparison.OrdinalIgnoreCase))
            return "verifier passed " + task.Id;
        if (status == "done") return role + " finished " + title;
        if (status == "review") return role + " submitted " + title;
        if (status == "failed") return role + " failed " + title;
        return null;
    }

    public static string LatestLine(SwarmTask task)
    {
        var source = string.IsNullOrWhiteSpace(task.Result) ? task.Proof : task.Result;
        foreach (var raw in (source ?? "").Replace("\r", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            return line.Length <= 96 ? line : line[..96];
        }
        return DisplayStatus(task);
    }

    public static bool StopRunClock(string? reviewStatus, IEnumerable<string?>? taskStatuses)
    {
        var review = (reviewStatus ?? "").Trim();
        if (string.Equals(review, SwarmHandoffReview.ReviewedDone, StringComparison.Ordinal)) return true;
        if (string.Equals(review, SwarmHandoffReview.WaitingStatus, StringComparison.Ordinal)
            || string.Equals(review, "Waiting for review", StringComparison.Ordinal))
            return false;
        if (taskStatuses is null) return false;
        var any = false;
        foreach (var status in taskStatuses)
        {
            any = true;
            if (StatusLabel(status) != SwarmStatus.Done) return false;
        }
        return any;
    }

    public static string FormatElapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString() + ":" + span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00")
            : span.Minutes.ToString() + ":" + span.Seconds.ToString("00");
    }

    public static SwarmNote ReadNote(string? note)
    {
        var claim = "";
        var evidence = "";
        var verdict = "";
        foreach (var raw in (note ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Claim:", StringComparison.OrdinalIgnoreCase)) claim = line["Claim:".Length..].Trim();
            else if (line.StartsWith("Evidence:", StringComparison.OrdinalIgnoreCase)) evidence = line["Evidence:".Length..].Trim();
            else if (line.StartsWith("Verdict:", StringComparison.OrdinalIgnoreCase)) verdict = line["Verdict:".Length..].Trim();
        }
        return new SwarmNote(claim, evidence, verdict);
    }
}
