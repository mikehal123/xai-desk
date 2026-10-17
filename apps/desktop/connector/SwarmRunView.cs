using System.Globalization;

namespace Sovereign.Desktop.Connector;

public enum SwarmRunPhase { Running, Stalled, Done, Stopped }

public readonly record struct SwarmRunState(SwarmRunPhase Phase, string Label, TimeSpan Elapsed, DateTime? EndedUtc)
{
    public bool Finished => Phase is SwarmRunPhase.Done or SwarmRunPhase.Stopped;
}

/// <summary>
/// Display-only view of one swarm run for the panel. Times come from the run's own folder and board file,
/// never from when this window first saw the goal. Nothing here writes the board.
/// </summary>
public static class SwarmRunView
{
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(15);

    public static SwarmRunPhase Phase(SwarmBoard board)
    {
        var statuses = board.Tasks.Select(task => (string?)SwarmSection.DisplayStatus(task)).ToArray();
        if (SwarmSection.StopRunClock(board.ReviewStatus, statuses)) return SwarmRunPhase.Done;
        var review = (board.ReviewStatus ?? "").Trim();
        if (review == SwarmHandoffReview.WaitingStatus || review == "Waiting for review") return SwarmRunPhase.Running;
        return !SwarmSchedule.HasOpenWork(board) && board.Tasks.Count > 0 ? SwarmRunPhase.Stopped : SwarmRunPhase.Running;
    }

    public static string Blocker(SwarmBoard board)
    {
        var review = (board.ReviewStatus ?? "").Trim();
        if (review == SwarmHandoffReview.TimedOutStatus) return "Grokbot review timed out";
        if (review == SwarmHandoffReview.WaitingStatus || review == "Waiting for review") return "waiting on Grokbot review";
        var progress = SwarmSection.Measure(board.Tasks);
        if (progress.Review > 0) return "waiting on review";
        if (progress.Doing > 0) return progress.Doing == 1 ? "waiting on 1 running task" : "waiting on " + progress.Doing + " running tasks";
        if (progress.Todo > 0) return progress.Todo == 1 ? "waiting on 1 queued task" : "waiting on " + progress.Todo + " queued tasks";
        return "waiting on the manager";
    }

    /// <param name="boardWriteUtc">Last write of this run's board.json; a finished run ended at its last write.</param>
    /// <param name="lastProgressUtc">Last time any task status on this run changed (see <see cref="SwarmRunProgress"/>).</param>
    public static SwarmRunState Describe(SwarmBoard board, DateTime startedUtc, DateTime lastProgressUtc, DateTime boardWriteUtc, DateTime nowUtc, Func<DateTime, string>? endText = null)
    {
        var phase = Phase(board);
        if (phase is SwarmRunPhase.Done or SwarmRunPhase.Stopped)
        {
            var ended = boardWriteUtc < startedUtc ? startedUtc : boardWriteUtc;
            var elapsed = ended - startedUtc;
            var at = (endText ?? LocalTime)(ended);
            var word = phase == SwarmRunPhase.Done ? "Done " : "Stopped ";
            return new SwarmRunState(phase, word + at + " · " + SwarmSection.FormatElapsed(elapsed), elapsed, ended);
        }
        var running = nowUtc - startedUtc;
        if (nowUtc - lastProgressUtc >= StallAfter)
            return new SwarmRunState(SwarmRunPhase.Stalled, "Stalled — " + Blocker(board) + " · " + SwarmSection.FormatElapsed(running), running, null);
        return new SwarmRunState(SwarmRunPhase.Running, "Running · " + SwarmSection.FormatElapsed(running), running, null);
    }

    /// <summary>Task chip text. On a finished run, open tasks are closed, not live.</summary>
    public static string TaskLabel(string? status, bool runFinished)
    {
        var label = SwarmSection.StatusLabel(status);
        return runFinished && label is SwarmStatus.Todo or SwarmStatus.Doing or SwarmStatus.Review ? label + " · run ended" : label;
    }

    public static string ProgressKey(SwarmBoard board) =>
        string.Join("|", board.Tasks.Select(task => (task?.Id ?? "") + ":" + SwarmSection.StatusLabel(task?.Status)));

    /// <summary>Run start is the run folder's creation time, falling back to the board file.</summary>
    public static DateTime StartedUtc(string boardPath)
    {
        var write = File.GetLastWriteTimeUtc(boardPath);
        var started = File.GetCreationTimeUtc(boardPath);
        var folder = Path.GetDirectoryName(boardPath);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            var made = Directory.GetCreationTimeUtc(folder);
            if (made < started) started = made;
        }
        return started > write ? write : started;
    }

    public static string ShortGoal(string? goal, int max = 72)
    {
        var text = (goal ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    private static string LocalTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
}

/// <summary>Remembers, per run, when its task statuses last changed. Seeded with the board's write time.</summary>
public sealed class SwarmRunProgress
{
    private readonly Dictionary<string, (string Key, DateTime At)> _runs = new(StringComparer.Ordinal);

    public DateTime Observe(string runId, string progressKey, DateTime boardWriteUtc)
    {
        if (_runs.TryGetValue(runId, out var seen) && string.Equals(seen.Key, progressKey, StringComparison.Ordinal))
            return seen.At;
        _runs[runId] = (progressKey, boardWriteUtc);
        return boardWriteUtc;
    }
}
