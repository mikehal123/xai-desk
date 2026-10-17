using Sovereign.Desktop.Connector;
using Sovereign.Desktop.Core;
using Xunit;

namespace XaiDesk.Tests;

public sealed class DoctrineGateTests
{
    [Fact]
    public void Start_throws_specific_text_and_leaves_the_board_todo()
    {
        using var fx = new Board("Simulate the doctrine");
        var before = File.ReadAllText(fx.Swarm.BoardPath);
        var holds = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Start(fx.Id, "helper-a"));
        Assert.Equal("disagreement holds", holds.Message);
        Assert.Equal(before, File.ReadAllText(fx.Swarm.BoardPath));
        Assert.Equal(SwarmStatus.Todo, Fresh(fx).Status);
        Assert.Contains("\"verdict\":\"blocked\"", File.ReadAllText(DoctrineGate.LogPath(fx.Swarm)), StringComparison.Ordinal);

        var refused = Assert.Throws<InvalidOperationException>(() => DoctrineGate.Record(fx.Swarm, new DoctrineLogRow
        {
            TaskId = fx.Id,
            Phase = DoctrinePhase.Disagreement,
            Verdict = "overruled",
            Text = "A worker tried to overrule it.",
            Read = fx.Swarm.Goal
        }));
        Assert.Equal("the manager cannot write a disagreement row", refused.Message);
        DoctrineGate.Overrule(fx.Swarm, fx.Id);
        var assumption = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Start(fx.Id, "helper-a"));
        Assert.Equal("no assumption row", assumption.Message);
        Assert.Equal(before, File.ReadAllText(fx.Swarm.BoardPath));

        File.Delete(DoctrineGate.LogPath(fx.Swarm));
        var missing = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Start(fx.Id, "helper-a"));
        Assert.Equal("no disagreement row", missing.Message);
        Assert.Equal(SwarmStatus.Todo, Fresh(fx).Status);
    }

    [Fact]
    public void Submit_requires_rollback_with_undo_and_a_decay_row_that_matches_the_goal()
    {
        using var fx = new Board("simulate the doctrine");
        DoctrineGate.Overrule(fx.Swarm, fx.Id);
        DoctrineGate.AppendAssumption(fx.Swarm, fx.Id, "STATED", "The goal names the deliverable.");
        var manager = Assert.Throws<InvalidOperationException>(() => DoctrineGate.AppendAssumption(fx.Swarm, fx.Id, "overruled", "no"));
        Assert.Equal("the manager cannot overrule a disagreement", manager.Message);
        fx.Swarm.Start(fx.Id, "helper-a");
        Assert.Equal(SwarmStatus.Doing, Fresh(fx).Status);

        var noRollback = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Submit(fx.Id, "wrote", "proof line"));
        Assert.Equal("no rollback row", noRollback.Message);
        Assert.Equal(SwarmStatus.Doing, Fresh(fx).Status);

        DoctrineGate.Record(fx.Swarm, new DoctrineLogRow { TaskId = fx.Id, Phase = DoctrinePhase.Rollback, Verdict = "IRREVERSIBLE", Text = "", Paths = "notes.txt", Read = "notes.txt" });
        var noUndo = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Submit(fx.Id, "wrote", "proof line"));
        Assert.Equal("rollback missing undo", noUndo.Message);

        DoctrineGate.AppendRollback(fx.Swarm, fx.Id, "IRREVERSIBLE", "The file would be gone.", "notes.txt");
        Assert.Contains("\"owner_yes\":false", File.ReadAllText(DoctrineGate.LogPath(fx.Swarm)), StringComparison.Ordinal);
        var needsYes = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Submit(fx.Id, "wrote", "proof line"));
        Assert.Equal("IRREVERSIBLE needs the owner's yes", needsYes.Message);
        var refusedWrite = Assert.Throws<InvalidOperationException>(() => DoctrineGate.GuardWrite(fx.Swarm, fx.Id, "notes.txt"));
        Assert.Equal("IRREVERSIBLE needs the owner's yes", refusedWrite.Message);
        Assert.False(File.Exists(Path.Combine(fx.Root, "notes.txt")));
        Assert.Throws<InvalidOperationException>(() => DoctrineGate.Record(fx.Swarm, new DoctrineLogRow { TaskId = fx.Id, Phase = DoctrinePhase.Rollback, Verdict = "IRREVERSIBLE", Text = "sneak", Paths = "notes.txt", OwnerYes = true }));

        DoctrineGate.OwnerYes(fx.Swarm, fx.Id);
        DoctrineGate.GuardWrite(fx.Swarm, fx.Id, "notes.txt");
        var noDecay = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Submit(fx.Id, "wrote", "proof line"));
        Assert.Equal("no decay row", noDecay.Message);

        DoctrineGate.AppendDecay(fx.Swarm, fx.Id);
        fx.Swarm.Goal = "build a CN gate";
        var drifted = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Submit(fx.Id, "wrote", "proof line"));
        Assert.Equal("goal drifted", drifted.Message);
        var saved = SwarmBoard.Load(fx.Swarm.Id, fx.Root);
        Assert.Equal("simulate the doctrine", saved.Goal);
        Assert.Equal(SwarmStatus.Doing, saved.TaskById(fx.Id).Status);

        fx.Swarm.Goal = "simulate the doctrine";
        fx.Swarm.Submit(fx.Id, "wrote", "proof line");
        Assert.Equal(SwarmStatus.Review, Fresh(fx).Status);
    }

    [Fact]
    public void Verify_pass_rejects_missing_sections_missing_deliverables_and_no_artifact()
    {
        using var fx = new Board("simulate the doctrine");
        DoctrineFixture.ReadyForWork(fx.Swarm, fx.Id);
        fx.Swarm.Start(fx.Id, "helper-a");
        fx.Swarm.Submit(fx.Id, "wrote", "proof line");
        var none = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Verify(fx.Id, true, "pass because it looks done"));
        Assert.Equal("no verification row", none.Message);
        Assert.Equal(SwarmStatus.Review, Fresh(fx).Status);

        RecordVerification(fx, "Existence audit: on disk\nGap audit: looked\nDeviations: none\nUnverified claims: none");
        var raw = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Verify(fx.Id, true, "pass because it looks done"));
        Assert.Equal("verification missing raw output", raw.Message);

        RecordVerification(fx, DoctrineFixture.Report("notes.txt").Replace("the deliverable is on disk", "MISSING"));
        var missing = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Verify(fx.Id, true, "pass because it looks done"));
        Assert.Equal("verification has a MISSING deliverable", missing.Message);

        RecordVerification(fx, "Existence audit: the file exists\nRaw output: exit 0\nGap audit: looked once\nDeviations: none skipped\nUnverified claims: none");
        var artifact = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Verify(fx.Id, true, "pass because it looks done"));
        Assert.Equal("verification pass has no artifact", artifact.Message);

        RecordVerification(fx, "Existence audit: claim: on disk artifact: notes.txt\nRaw output: exit 0\nGap audit: skipped persistence\nDeviations:\nUnverified claims: none");
        var deviations = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Verify(fx.Id, true, "pass because it looks done"));
        Assert.Equal("verification missing deviations", deviations.Message);
        Assert.Equal(SwarmStatus.Review, Fresh(fx).Status);

        RecordVerification(fx, DoctrineFixture.Report(DoctrineFixture.ArtifactFile(fx.Swarm, fx.Id)));
        var noInterface = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Verify(fx.Id, true, "pass because the report exists"));
        Assert.Equal("no interface row", noInterface.Message);

        DoctrineGate.Record(fx.Swarm, new DoctrineLogRow
        {
            TaskId = fx.Id,
            Phase = DoctrinePhase.Interface,
            Verdict = "report",
            Text = "Answer: shipped\nSkips: persistence\nOpen guesses: none",
            Read = "report"
        });
        var drops = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Verify(fx.Id, true, "pass because the report exists"));
        Assert.Equal("interface drops Failures", drops.Message);

        DoctrineGate.Record(fx.Swarm, new DoctrineLogRow
        {
            TaskId = fx.Id,
            Phase = DoctrinePhase.Interface,
            Verdict = "report",
            Text = DoctrineFixture.Delivery("notes.txt"),
            Read = "report"
        });
        var done = fx.Swarm.Verify(fx.Id, true, "Claim: notes.txt is on disk\nHow tested: read the log\nEvidence: artifact: notes.txt\nPaths covered: success\nVerdict: pass because the sections are present");
        Assert.Equal(SwarmStatus.Done, done.Status);
        Assert.Equal(SwarmStatus.Done, Fresh(fx).Status);

        fx.Swarm.TaskById(fx.Id).Status = SwarmStatus.Review;
        fx.Swarm.TaskById(fx.Id).Proof = "still here";
        fx.Swarm.Save();
        var failed = fx.Swarm.Verify(fx.Id, false, "the note is a real failure");
        Assert.Equal(SwarmStatus.Todo, failed.Status);
        Assert.Equal("the note is a real failure", failed.VerifierNote);
    }

    [Fact]
    public void Rule_nine_refuses_a_green_check_that_cheated()
    {
        using var fx = new Board("simulate the doctrine");
        DoctrineFixture.ReadyForWork(fx.Swarm, fx.Id);
        fx.Swarm.Start(fx.Id, "helper-a");
        fx.Swarm.Submit(fx.Id, "wrote", "proof line");
        var real = DoctrineFixture.ArtifactFile(fx.Swarm, fx.Id);
        RecordVerification(fx, "Existence audit: claim: on disk artifact: " + real + "\nRaw output: exit 0\nGap audit: skipped persistence\nDeviations: NONE\nUnverified claims: none");
        var hidden = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Verify(fx.Id, true, "pass because the headings are filled"));
        Assert.Equal("verification deviations hide a skip", hidden.Message);

        RecordVerification(fx, "Existence audit: claim: on disk artifact: placeholder\nRaw output: exit 0\nGap audit: looked once\nDeviations: persistence was not rerun\nUnverified claims: none");
        var missingFile = Assert.Throws<InvalidOperationException>(() => fx.Swarm.Verify(fx.Id, true, "pass because the headings are filled"));
        Assert.Equal("verification artifact is not on disk", missingFile.Message);
        Assert.Equal(SwarmStatus.Review, Fresh(fx).Status);
    }

    [Fact]
    public void Create_writes_todo_and_a_holds_row()
    {
        var root = Path.Combine(Path.GetTempPath(), "xai-desk-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            var board = SwarmBoard.Create("Ship the panel", [new SwarmTask { Id = "t-1", Title = "Write", Status = SwarmStatus.Done, Role = SwarmRole.Engineer }], root, caseAgainst: "The plan fails if the file is shared.");
            var json = File.ReadAllText(board.BoardPath);
            Assert.Contains("\"status\": \"todo\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"status\": \"done\"", json, StringComparison.Ordinal);
            var log = File.ReadAllText(DoctrineGate.LogPath(board));
            Assert.Contains("\"verdict\":\"holds\"", log, StringComparison.Ordinal);
            Assert.Contains("The plan fails if the file is shared.", log, StringComparison.Ordinal);
            var blocked = Assert.Throws<InvalidOperationException>(() => board.Start("t-1", "helper-a"));
            Assert.Equal("disagreement holds", blocked.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_direct_save_cannot_mark_done_and_only_verify_assigns_done()
    {
        using var fx = new Board("simulate the doctrine");
        DoctrineFixture.ReadyForWork(fx.Swarm, fx.Id);
        fx.Swarm.Start(fx.Id, "helper-a");
        var doing = SwarmBoard.Load(fx.Swarm.Id, fx.Root);
        doing.TaskById(fx.Id).Status = SwarmStatus.Done;
        var blocked = Assert.Throws<InvalidOperationException>(() => doing.Save());
        Assert.Contains("DoctrineGate", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(SwarmStatus.Doing, SwarmBoard.Load(fx.Swarm.Id, fx.Root).TaskById(fx.Id).Status);

        var connector = Path.Combine(Repo(), "apps", "desktop", "connector");
        var assigns = 0;
        foreach (var file in Directory.EnumerateFiles(connector, "*.cs"))
        {
            var text = File.ReadAllText(file);
            var count = Count(text, "task.Status = SwarmStatus.Done");
            if (count == 0) continue;
            assigns += count;
            Assert.Equal("SwarmBoard.cs", Path.GetFileName(file));
        }
        Assert.Equal(1, assigns);
        var host = File.ReadAllText(Path.Combine(connector, "ConnectorHost.cs"));
        Assert.DoesNotContain("grokbot_doctrine_overrule", host, StringComparison.Ordinal);
        Assert.DoesNotContain("grokbot_doctrine_owner_yes", host, StringComparison.Ordinal);
        Assert.Contains("swarm_assumption", host, StringComparison.Ordinal);
        Assert.Contains("swarm_rollback", host, StringComparison.Ordinal);
        Assert.Contains("swarm_decay", host, StringComparison.Ordinal);
        Assert.DoesNotContain("DoctrineGate.Record", host, StringComparison.Ordinal);
        var loop = File.ReadAllText(Path.Combine(connector, "SwarmLoop.cs"));
        var writeAt = loop.IndexOf("File.WriteAllText(file", StringComparison.Ordinal);
        var guardAt = loop.IndexOf("DoctrineGate.GuardWrite", StringComparison.Ordinal);
        Assert.True(guardAt >= 0 && guardAt < writeAt);
    }

    [Fact]
    public void Overrule_appends_only_while_the_latest_disagreement_holds()
    {
        using var fx = new Board("simulate the doctrine");
        var log = DoctrineGate.LogPath(fx.Swarm);
        var boardBefore = File.ReadAllText(fx.Swarm.BoardPath);
        Assert.Equal("holds", DoctrineGate.Latest(fx.Swarm, fx.Id, DoctrinePhase.Disagreement)!.Verdict);
        Assert.True(DoctrineGate.OverruleEnabled(fx.Swarm, fx.Id));
        DoctrineGate.Overrule(fx.Swarm, fx.Id);
        var after = File.ReadAllText(log);
        Assert.Contains("\"verdict\":\"overruled\"", after, StringComparison.Ordinal);
        Assert.Equal("overruled", DoctrineGate.Latest(fx.Swarm, fx.Id, DoctrinePhase.Disagreement)!.Verdict);
        Assert.False(DoctrineGate.OverruleEnabled(fx.Swarm, fx.Id));
        Assert.Equal(boardBefore, File.ReadAllText(fx.Swarm.BoardPath));
        Assert.Equal(SwarmStatus.Todo, Fresh(fx).Status);
        var snapshot = File.ReadAllText(log);
        if (DoctrineGate.OverruleEnabled(fx.Swarm, fx.Id)) DoctrineGate.Overrule(fx.Swarm, fx.Id);
        Assert.Equal(snapshot, File.ReadAllText(log));
    }

    [Fact]
    public void Owner_yes_appends_the_row_and_leaves_the_task_open()
    {
        using var fx = new Board("simulate the doctrine");
        Assert.False(DoctrineGate.OwnerYesVisible(fx.Swarm, fx.Id));
        DoctrineGate.AppendRollback(fx.Swarm, fx.Id, "REVERSIBLE", "Delete the new file.", "notes.txt");
        Assert.False(DoctrineGate.OwnerYesVisible(fx.Swarm, fx.Id));
        DoctrineGate.AppendRollback(fx.Swarm, fx.Id, "IRREVERSIBLE", "The file would be gone.", "notes.txt");
        Assert.True(DoctrineGate.OwnerYesVisible(fx.Swarm, fx.Id));
        DoctrineGate.AppendRollback(fx.Swarm, fx.Id, "REVERSIBLE", "Delete the later file.", "later.txt");
        Assert.False(DoctrineGate.OwnerYesVisible(fx.Swarm, fx.Id));
        DoctrineGate.AppendRollback(fx.Swarm, fx.Id, "IRREVERSIBLE", "The file would be gone.", "notes.txt");
        Assert.True(DoctrineGate.OwnerYesVisible(fx.Swarm, fx.Id));
        var boardBefore = File.ReadAllText(fx.Swarm.BoardPath);
        var status = Fresh(fx).Status;
        DoctrineGate.OwnerYes(fx.Swarm, fx.Id);
        var saved = Fresh(fx);
        Assert.Equal(status, saved.Status);
        Assert.NotEqual(SwarmStatus.Done, saved.Status);
        Assert.Equal(boardBefore, File.ReadAllText(fx.Swarm.BoardPath));
        var yes = DoctrineGate.Latest(fx.Swarm, fx.Id, DoctrinePhase.Rollback);
        Assert.NotNull(yes);
        Assert.Equal("IRREVERSIBLE", yes.Verdict);
        Assert.True(yes.OwnerYes);
        Assert.False(DoctrineGate.OwnerYesVisible(fx.Swarm, fx.Id));
        var log = File.ReadAllText(DoctrineGate.LogPath(fx.Swarm));
        Assert.Contains("\"owner_yes\":true", log, StringComparison.Ordinal);
        Assert.DoesNotContain("\"phase\":\"verification\"", log, StringComparison.Ordinal);
        Assert.DoesNotContain("\"phase\":\"done\"", log, StringComparison.Ordinal);
        var snapshot = File.ReadAllText(DoctrineGate.LogPath(fx.Swarm));
        if (DoctrineGate.OwnerYesVisible(fx.Swarm, fx.Id)) DoctrineGate.OwnerYes(fx.Swarm, fx.Id);
        Assert.Equal(snapshot, File.ReadAllText(DoctrineGate.LogPath(fx.Swarm)));
    }

    [Fact]
    public async Task Two_helpers_cannot_claim_the_same_task()
    {
        using var fx = new Board("simulate the doctrine");
        DoctrineFixture.ReadyForWork(fx.Swarm, fx.Id);
        var gate = new Barrier(2);
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var wins = 0;
        void Claim(string name)
        {
            try
            {
                gate.SignalAndWait();
                SwarmBoard.Load(fx.Swarm.Id, fx.Root).Start(fx.Id, name);
                Interlocked.Increment(ref wins);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }
        await Task.WhenAll(Task.Run(() => Claim("helper-a")), Task.Run(() => Claim("helper-b")));
        var saved = SwarmBoard.Load(fx.Swarm.Id, fx.Root).TaskById(fx.Id);
        Assert.Equal(1, wins);
        var error = Assert.Single(errors);
        Assert.Contains("already claimed by " + saved.Assignee, error.Message, StringComparison.Ordinal);
        Assert.Equal(SwarmStatus.Doing, saved.Status);
    }

    [Fact]
    public void File_write_refuses_a_path_that_is_not_on_the_rollback_row()
    {
        using var fx = new Board("simulate the doctrine");
        var path = Path.Combine(fx.Root, "notes.txt");
        var missing = Assert.Throws<InvalidOperationException>(() => DoctrineGate.GuardWrite(fx.Swarm, fx.Id, path));
        Assert.Contains("no rollback row for", missing.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        DoctrineGate.Record(fx.Swarm, new DoctrineLogRow
        {
            TaskId = fx.Id,
            Phase = DoctrinePhase.Rollback,
            Verdict = "REVERSIBLE",
            Text = "Delete the new file.",
            Paths = path,
            Read = path
        });
        DoctrineGate.GuardWrite(fx.Swarm, fx.Id, path);
        File.WriteAllText(path, "kept");
        Assert.Equal("kept", File.ReadAllText(path));
    }

    static void RecordVerification(Board fx, string text) => DoctrineGate.Record(fx.Swarm, new DoctrineLogRow
    {
        TaskId = fx.Id,
        Phase = DoctrinePhase.Verification,
        Verdict = "pass",
        Text = text,
        Read = "verification report"
    });

    static SwarmTask Fresh(Board fx) => SwarmBoard.Load(fx.Swarm.Id, fx.Root).TaskById(fx.Id);

    static int Count(string text, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
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

    sealed class Board : IDisposable
    {
        public Board(string goal)
        {
            Root = Path.Combine(Path.GetTempPath(), "xai-desk-doctrine-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Swarm = SwarmBoard.Create(goal, [new SwarmTask { Id = "t-1", Title = "The task", Role = SwarmRole.Engineer }], Root, caseAgainst: DoctrineFixture.Case);
        }

        public string Root { get; }
        public SwarmBoard Swarm { get; }
        public string Id => "t-1";
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
