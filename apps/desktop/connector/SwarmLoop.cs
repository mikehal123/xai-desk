using System.Diagnostics;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Connector;

public sealed record SwarmWorkerView(string SessionId, string Name, string Task, string State, string Output, string RolePrompt);

public static class SwarmLoop
{
    public static SwarmBoard OpenGoal(string goal, string title, string role, string details, string root)
    {
        var task = new SwarmTask
        {
            Id = "t-" + Guid.NewGuid().ToString("N")[..8],
            Title = title,
            Role = role,
            Details = details,
            Status = SwarmStatus.Todo
        };
        return SwarmBoard.Create(goal, [task], root, null, string.IsNullOrWhiteSpace(details) ? goal : details);
    }

    public static SwarmWorkerView View(SwarmBoard board, SwarmTask task) => new(
        "swarm-" + board.Id + "-" + task.Id,
        task.Role + " · " + task.Title,
        task.Details,
        SwarmBoard.PanelState(SwarmSection.DisplayStatus(task)),
        string.IsNullOrWhiteSpace(task.Result) ? task.Proof : task.Result,
        SwarmRoles.Worker(task, board.BoardPath));

    public static SwarmWorkerView Begin(SwarmBoard board, string taskId, string assignee)
    {
        var task = board.Start(taskId, assignee);
        return View(board, task);
    }

    public static Task<SwarmTask> EngineerProofAsync(SwarmBoard board, string taskId, string workDir)
    {
        var task = board.TaskById(taskId);
        if (task.Status != SwarmStatus.Doing) throw new InvalidOperationException("Engineer proof starts from doing.");
        Directory.CreateDirectory(workDir);
        var file = Path.Combine(workDir, "proof.txt");
        DoctrineGate.GuardWrite(board, taskId, file);
        var before = File.Exists(file) ? File.ReadAllText(file).Replace("\r", "").Replace("\n", "") : "";
        var body = "swarm phase 1 " + task.Id;
        File.WriteAllText(file, body + Environment.NewLine);
        var output = ReadProof(file, body);
        if (output.ExitCode != 0)
            throw new IOException("Engineer proof file did not contain the task line.");
        var diff = "--- a/proof.txt\n+++ b/proof.txt\n-" + before + "\n+" + body;
        var proof = VerifyResultSkill.Format(
            "proof.txt contains the line for " + task.Id,
            "Read proof.txt in this process.",
            "exit " + output.ExitCode + "\n" + output.Text.Trim(),
            "Success: the new proof.txt line is in the file. Skipped: error, empty, cancel, and persistence were not part of this write.",
            "pass because the file contained the task line");
        return Task.FromResult(board.Submit(taskId, diff + "\n" + proof, proof));
    }

    public static Task<SwarmTask> VerifyProofAsync(SwarmBoard board, string taskId, string workDir)
    {
        var task = board.TaskById(taskId);
        var review = VerifyResultSkill.Review(task.Proof);
        if (!review.Pass) return Task.FromResult(board.Verify(taskId, false, review.Note));
        var file = Path.Combine(workDir, "proof.txt");
        if (!File.Exists(file))
            return Task.FromResult(board.Verify(taskId, false, VerifyResultSkill.Format(
                "The proof file exists",
                "Looked for proof.txt before rerunning findstr.",
                "(missing)",
                "The success path was not reached. Skipped: the file is not there.",
                "fail because the proof file is missing")));
        var body = File.ReadAllText(file).Trim();
        var output = ReadProof(file, body);
        var stamp = File.GetLastWriteTimeUtc(file).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var matched = output.ExitCode == 0 && task.Proof.Contains(body, StringComparison.Ordinal);
        if (!matched)
            return Task.FromResult(board.Verify(taskId, false, VerifyResultSkill.Format(
                "proof.txt matches the submitted proof",
                "Read proof.txt in this process at " + stamp + ".",
                "exit " + output.ExitCode + "\n" + output.Text.Trim(),
                "The success path failed. Skipped: no further paths.",
                "fail because the proof did not match the file")));
        RecordPass(board, taskId, file, body);
        return Task.FromResult(board.Verify(taskId, true, VerifyResultSkill.Format(
            "proof.txt contains " + body,
            "Read the current proof.txt in this process at " + stamp + ". This is the file on disk, not a cached result.",
            "exit " + output.ExitCode + "\n" + output.Text.Trim(),
            "Success: the file exists and the task line is in it. Skipped: error, empty, cancel, and persistence were not part of this file check.",
            "pass because the file contained the task line")));
    }

    static void RecordPass(SwarmBoard board, string taskId, string file, string body)
    {
        DoctrineGate.Record(board, new DoctrineLogRow
        {
            TaskId = taskId,
            Phase = DoctrinePhase.Verification,
            Verdict = "pass",
            Text = "Existence audit: claim: proof.txt contains " + body + " artifact: " + file + "\n"
                + "Raw output: read proof.txt\n" + body + "\n"
                + "Gap audit: skipped error, empty, cancel, and persistence\n"
                + "Deviations: error, empty, cancel, and persistence were not part of this file check\n"
                + "Unverified claims: none named",
            Read = file
        });
        DoctrineGate.Record(board, new DoctrineLogRow
        {
            TaskId = taskId,
            Phase = DoctrinePhase.Interface,
            Verdict = "report",
            Text = "Answer: proof.txt contains " + body + "\n"
                + "Failures: none\n"
                + "Skips: error, empty, cancel, and persistence\n"
                + "Open guesses: the file read is the check that was run\n"
                + "State changes: REVERSIBLE " + file,
            Read = file
        });
    }

    static (int ExitCode, string Text) ReadProof(string file, string body)
    {
        if (!File.Exists(file)) return (1, "missing " + file);
        var text = File.ReadAllText(file);
        return text.Contains(body, StringComparison.Ordinal) ? (0, text) : (1, text);
    }

    public static async Task<(int ExitCode, string Text)> CommandAsync(string file, string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        var text = stdout + stderr;
        return (process.ExitCode, text);
    }
}
