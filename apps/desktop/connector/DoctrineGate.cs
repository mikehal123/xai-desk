using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Connector;

public sealed class DoctrineLogRow
{
    [JsonPropertyName("task_id")] public string TaskId { get; set; } = "";
    [JsonPropertyName("phase")] public string Phase { get; set; } = "";
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("paths")] public string Paths { get; set; } = "";
    [JsonPropertyName("pinned_goal")] public string PinnedGoal { get; set; } = "";
    [JsonPropertyName("owner_yes")] public bool OwnerYes { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("read")] public string Read { get; set; } = "";
    [JsonPropertyName("at")] public string At { get; set; } = "";
}

/// <summary>
/// Append-only doctrine.jsonl next to board.json. Start, Submit, and Verify are the only doors
/// that move a task; they call the checks here and Save only after the check returns.
/// Record does not move a task and is not a model tool.
/// </summary>
public static class DoctrineGate
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string LogPath(SwarmBoard board) =>
        Path.Combine(Path.GetDirectoryName(board.BoardPath) ?? "", "doctrine.jsonl");

    public static void Record(SwarmBoard board, DoctrineLogRow row)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(row);
        if (string.Equals(row.Phase, DoctrinePhase.Disagreement, StringComparison.Ordinal))
            throw new InvalidOperationException("the manager cannot write a disagreement row");
        if (row.OwnerYes)
            throw new InvalidOperationException("the manager cannot set the owner's yes");
        Append(board, row);
    }

    /// <summary>Latest non-blocked row for one task and phase, or null when none is on the log.</summary>
    public static DoctrineLogRow? Latest(SwarmBoard board, string taskId, string phase)
    {
        ArgumentNullException.ThrowIfNull(board);
        var rows = For(board, taskId, phase);
        return rows.Count == 0 ? null : rows[^1];
    }

    /// <summary>True only when the latest disagreement row for the task is holds. The Overrule button uses this.</summary>
    public static bool OverruleEnabled(SwarmBoard board, string taskId)
    {
        var row = Latest(board, taskId, DoctrinePhase.Disagreement);
        return row is not null && string.Equals(row.Verdict, "holds", StringComparison.Ordinal);
    }

    /// <summary>True only when the latest rollback row is IRREVERSIBLE and owner_yes is false. The Owner yes button uses this.</summary>
    public static bool OwnerYesVisible(SwarmBoard board, string taskId)
    {
        var row = Latest(board, taskId, DoctrinePhase.Rollback);
        return row is not null && row.Verdict == "IRREVERSIBLE" && !row.OwnerYes;
    }

    /// <summary>Desk Overrule button only. Appends disagreement verdict overruled. No connector tool may call this.</summary>
    public static void Overrule(SwarmBoard board, string taskId)
    {
        ArgumentNullException.ThrowIfNull(board);
        _ = board.TaskById(taskId);
        Append(board, new DoctrineLogRow
        {
            TaskId = taskId.Trim(),
            Phase = DoctrinePhase.Disagreement,
            Verdict = "overruled",
            Text = "The owner overruled the case against the plan.",
            Read = board.Goal ?? ""
        });
    }

    /// <summary>Desk Owner yes button only. Appends the latest IRREVERSIBLE rollback again with the owner's yes. Does not move the task. No connector tool may call this.</summary>
    public static void OwnerYes(SwarmBoard board, string taskId)
    {
        ArgumentNullException.ThrowIfNull(board);
        DoctrineLogRow? prior = null;
        foreach (var row in For(board, taskId, DoctrinePhase.Rollback))
            if (row.Verdict == "IRREVERSIBLE") prior = row;
        if (prior is null) throw new InvalidOperationException("no IRREVERSIBLE rollback row");
        Append(board, new DoctrineLogRow
        {
            TaskId = taskId.Trim(),
            Phase = DoctrinePhase.Rollback,
            Verdict = "IRREVERSIBLE",
            Text = prior.Text,
            Paths = prior.Paths,
            OwnerYes = true,
            Read = prior.Paths
        });
    }

    public static void AppendAssumption(SwarmBoard board, string taskId, string verdict, string text)
    {
        ArgumentNullException.ThrowIfNull(board);
        _ = board.TaskById(taskId);
        if (string.Equals(verdict, "overruled", StringComparison.Ordinal) || string.Equals(verdict, "holds", StringComparison.Ordinal))
            throw new InvalidOperationException("the manager cannot overrule a disagreement");
        if (!Closed(verdict, DoctrineAnswer.Assumption))
            throw new ArgumentException("Assumption verdict must be STATED, INFERRED, GUESSED, or OVERTURNED.");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Assumption text is required.");
        Append(board, new DoctrineLogRow
        {
            TaskId = taskId.Trim(),
            Phase = DoctrinePhase.Assumption,
            Verdict = verdict,
            Text = text.Trim(),
            Read = text.Trim()
        });
    }

    public static void AppendRollback(SwarmBoard board, string taskId, string verdict, string undo, string path)
    {
        ArgumentNullException.ThrowIfNull(board);
        _ = board.TaskById(taskId);
        if (!Closed(verdict, DoctrineAnswer.Rollback))
            throw new ArgumentException("Rollback class must be REVERSIBLE, RECOVERABLE, or IRREVERSIBLE.");
        if (string.IsNullOrWhiteSpace(undo)) throw new ArgumentException("Rollback undo text is required.");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Rollback path is required.");
        Append(board, new DoctrineLogRow
        {
            TaskId = taskId.Trim(),
            Phase = DoctrinePhase.Rollback,
            Verdict = verdict,
            Text = undo.Trim(),
            Paths = path.Trim(),
            OwnerYes = false,
            Read = path.Trim()
        });
    }

    public static void AppendDecay(SwarmBoard board, string taskId)
    {
        ArgumentNullException.ThrowIfNull(board);
        _ = board.TaskById(taskId);
        var pin = board.Goal ?? "";
        Append(board, new DoctrineLogRow
        {
            TaskId = taskId.Trim(),
            Phase = DoctrinePhase.Decay,
            Verdict = "check",
            Text = pin,
            PinnedGoal = pin,
            Read = pin
        });
    }

    internal static void AppendHolds(SwarmBoard board, string taskId, string caseAgainst) =>
        Append(board, new DoctrineLogRow
        {
            TaskId = taskId.Trim(),
            Phase = DoctrinePhase.Disagreement,
            Verdict = "holds",
            Text = caseAgainst.Trim(),
            Read = board.Goal ?? ""
        });

    public static void RequireForStart(SwarmBoard board, string taskId)
    {
        var rows = For(board, taskId, DoctrinePhase.Disagreement);
        if (rows.Count == 0) throw Fail(board, taskId, "start", "no disagreement row", board.Goal);
        var verdict = rows[^1].Verdict ?? "";
        if (string.Equals(verdict, "holds", StringComparison.Ordinal))
            throw Fail(board, taskId, "start", "disagreement holds", verdict);
        if (!string.Equals(verdict, "overruled", StringComparison.Ordinal))
            throw Fail(board, taskId, "start", "no disagreement row", verdict);
        if (!For(board, taskId, DoctrinePhase.Assumption).Any(row => Closed(row.Verdict, DoctrineAnswer.Assumption)))
            throw Fail(board, taskId, "start", "no assumption row", board.Goal);
    }

    public static void RequireForSubmit(SwarmBoard board, string taskId)
    {
        var rolls = For(board, taskId, DoctrinePhase.Rollback);
        if (rolls.Count == 0 || !Closed(rolls[^1].Verdict, DoctrineAnswer.Rollback))
            throw Fail(board, taskId, "submit", "no rollback row", board.Goal);
        if (string.IsNullOrWhiteSpace(rolls[^1].Text))
            throw Fail(board, taskId, "submit", "rollback missing undo", board.Goal);
        if (rolls[^1].Verdict == "IRREVERSIBLE" && !rolls[^1].OwnerYes)
            throw Fail(board, taskId, "submit", "IRREVERSIBLE needs the owner's yes", board.Goal);
        var decay = For(board, taskId, DoctrinePhase.Decay);
        if (decay.Count == 0) throw Fail(board, taskId, "submit", "no decay row", board.Goal);
        if (!string.Equals(decay[^1].PinnedGoal ?? "", board.Goal ?? "", StringComparison.Ordinal))
            throw Fail(board, taskId, "submit", "goal drifted", decay[^1].PinnedGoal + " -> " + board.Goal);
    }

    public static void RequireForVerifyPass(SwarmBoard board, string taskId)
    {
        var reports = For(board, taskId, DoctrinePhase.Verification);
        if (reports.Count == 0) throw Fail(board, taskId, "verify", "no verification row", board.Goal);
        var report = reports[^1];
        if (!string.Equals(report.Verdict, "pass", StringComparison.Ordinal))
            throw Fail(board, taskId, "verify", "verification verdict is not pass", report.Verdict);
        if (!DoctrineStage.TrySections(report.Text, DoctrineAnswer.VerificationSections, out var sections, out var error))
            throw Fail(board, taskId, "verify", "verification missing " + Tail(error), report.Text);
        foreach (var heading in DoctrineAnswer.VerificationSections)
        {
            if (!sections.TryGetValue(heading, out var body) || body.Trim().Length == 0)
                throw Fail(board, taskId, "verify", "verification missing " + heading.ToLowerInvariant(), report.Text);
        }
        if (Regex.IsMatch(report.Text ?? "", @"\bMISSING\b"))
            throw Fail(board, taskId, "verify", "verification has a MISSING deliverable", report.Text);
        if (!Regex.IsMatch(report.Text ?? "", @"artifact:\s*\S+", RegexOptions.IgnoreCase))
            throw Fail(board, taskId, "verify", "verification pass has no artifact", report.Text);
        if (!ArtifactsOnDisk(board, report.Text ?? ""))
            throw Fail(board, taskId, "verify", "verification artifact is not on disk", report.Text);
        var deviations = sections["Deviations"].Trim().TrimEnd('.', '!', '?');
        if (deviations.Equals("none", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(report.Text ?? "", @"\b(skip|skipped|stub|deferred|todo)\b", RegexOptions.IgnoreCase))
            throw Fail(board, taskId, "verify", "verification deviations hide a skip", report.Text);
        var faces = For(board, taskId, DoctrinePhase.Interface);
        if (faces.Count == 0) throw Fail(board, taskId, "verify", "no interface row", board.Goal);
        var face = faces[^1].Text ?? "";
        var first = FirstLine(face);
        if (!first.StartsWith("Answer:", StringComparison.OrdinalIgnoreCase))
            throw Fail(board, taskId, "verify", "interface answer is not first", first);
        foreach (var heading in new[] { "Failures", "Skips", "Open guesses" })
        {
            if (!DoctrineStage.TrySections(face, [heading], out var one, out _) || one[heading].Trim().Length == 0)
                throw Fail(board, taskId, "verify", "interface drops " + heading, face);
        }
    }

    public static void GuardWrite(SwarmBoard board, string taskId, string path)
    {
        ArgumentNullException.ThrowIfNull(board);
        var wanted = Norm(path);
        var rolls = For(board, taskId, DoctrinePhase.Rollback);
        DoctrineLogRow? match = null;
        foreach (var row in rolls)
            if (PathsMatch(row.Paths, wanted) && !string.IsNullOrWhiteSpace(row.Text)) match = row;
        if (match is null)
            throw Fail(board, taskId, "write", "no rollback row for " + wanted, wanted);
        if (match.Verdict == "IRREVERSIBLE" && !match.OwnerYes)
            throw Fail(board, taskId, "write", "IRREVERSIBLE needs the owner's yes", wanted);
    }

    static List<DoctrineLogRow> For(SwarmBoard board, string taskId, string phase)
    {
        var found = new List<DoctrineLogRow>();
        foreach (var row in Read(board))
        {
            if (!string.Equals(row.TaskId, taskId, StringComparison.Ordinal)) continue;
            if (string.Equals(row.Verdict, "blocked", StringComparison.Ordinal)) continue;
            if (!string.Equals(row.Phase, phase, StringComparison.Ordinal)) continue;
            found.Add(row);
        }
        return found;
    }

    static InvalidOperationException Fail(SwarmBoard board, string taskId, string door, string reason, string? read)
    {
        Append(board, new DoctrineLogRow
        {
            TaskId = taskId ?? "",
            Phase = door,
            Verdict = "blocked",
            Reason = reason,
            Read = read ?? "",
            At = Stamp()
        });
        return new InvalidOperationException(reason);
    }

    static void Append(SwarmBoard board, DoctrineLogRow row)
    {
        if (string.IsNullOrWhiteSpace(row.At)) row.At = Stamp();
        var path = LogPath(board);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var line = JsonSerializer.Serialize(row, Json) + "\n";
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        stream.Write(bytes);
        stream.Flush(true);
    }

    static List<DoctrineLogRow> Read(SwarmBoard board)
    {
        var path = LogPath(board);
        if (!File.Exists(path)) return [];
        var rows = new List<DoctrineLogRow>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var row = JsonSerializer.Deserialize<DoctrineLogRow>(line, Json);
                if (row is not null) rows.Add(row);
            }
            catch (JsonException)
            {
                continue;
            }
        }
        return rows;
    }

    static bool Closed(string? verdict, string[] allowed)
    {
        foreach (var item in allowed)
            if (string.Equals(item, verdict, StringComparison.Ordinal)) return true;
        return false;
    }

    static string Tail(string error)
    {
        const string prefix = "missing ";
        var text = error ?? "";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];
        return text.ToLowerInvariant();
    }

    static string FirstLine(string text)
    {
        foreach (var line in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            if (line.Trim().Length > 0) return line.Trim();
        return "";
    }

    static bool ArtifactsOnDisk(SwarmBoard board, string report)
    {
        var matches = Regex.Matches(report, @"artifact:\s*(\S+)", RegexOptions.IgnoreCase);
        if (matches.Count == 0) return false;
        var folder = Path.GetDirectoryName(board.BoardPath) ?? "";
        foreach (Match match in matches)
        {
            var raw = match.Groups[1].Value.Trim().TrimEnd('.', ',', ';');
            if (raw.Length == 0) return false;
            if (File.Exists(raw)) continue;
            if (folder.Length > 0 && File.Exists(Path.Combine(folder, raw))) continue;
            return false;
        }
        return true;
    }

    static string Norm(string? path) => (path ?? "").Trim().Replace('/', '\\');

    static bool PathsMatch(string? left, string? right) =>
        string.Equals(Norm(left), Norm(right), StringComparison.OrdinalIgnoreCase);

    static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
