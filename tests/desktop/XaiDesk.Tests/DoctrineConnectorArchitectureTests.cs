using System.Text.RegularExpressions;
using Xunit;

namespace XaiDesk.Tests;

public sealed class DoctrineConnectorArchitectureTests
{
    static readonly Regex OwnerCall = new(@"DoctrineGate\s*\.\s*(Overrule|OwnerYes)\s*\(", RegexOptions.CultureInvariant);
    static readonly Regex BareOverrule = new(@"\bOverrule\s*\(", RegexOptions.CultureInvariant);
    static readonly Regex BareOwnerYes = new(@"\bOwnerYes\s*\(", RegexOptions.CultureInvariant);

    [Fact]
    public void Connector_tools_and_dispatch_do_not_call_Overrule_or_OwnerYes()
    {
        var root = Repo();
        var connector = Path.Combine(root, "apps", "desktop", "connector");
        var hostPath = Path.Combine(connector, "ConnectorHost.cs");
        var host = File.ReadAllText(hostPath);
        Assert.DoesNotContain("grokbot_doctrine_overrule", host, StringComparison.Ordinal);
        Assert.DoesNotContain("grokbot_doctrine_owner_yes", host, StringComparison.Ordinal);
        Assert.Contains("\"swarm_assumption\"", host, StringComparison.Ordinal);
        Assert.Contains("\"swarm_rollback\"", host, StringComparison.Ordinal);
        Assert.Contains("\"swarm_decay\"", host, StringComparison.Ordinal);
        Assert.Contains("DoctrineGate.AppendAssumption", host, StringComparison.Ordinal);
        Assert.Contains("DoctrineGate.AppendRollback", host, StringComparison.Ordinal);
        Assert.Contains("DoctrineGate.AppendDecay", host, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerYes = true", host, StringComparison.Ordinal);
        Assert.DoesNotContain("\"overruled\"", host, StringComparison.Ordinal);
        foreach (Match match in Regex.Matches(host, "\"([a-z0-9_]+)\""))
        {
            var name = match.Groups[1].Value;
            Assert.DoesNotContain("overrule", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("owner_yes", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("owneryes", name, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(OwnerCall.IsMatch(host), "ConnectorHost dispatch calls Overrule or OwnerYes.");

        var deskPath = Path.Combine(root, "apps", "desktop", "Sovereign.Desktop", "ChatSwarmSection.cs");
        var desk = File.ReadAllText(deskPath);
        var apps = Path.Combine(root, "apps");
        foreach (var file in Directory.EnumerateFiles(apps, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            var text = File.ReadAllText(file);
            if (!OwnerCall.IsMatch(text)) continue;
            Assert.Equal("ChatSwarmSection.cs", Path.GetFileName(file));
        }
        Assert.Contains("Content = \"Overrule\"", desk, StringComparison.Ordinal);
        Assert.Contains("Content = \"Owner yes\"", desk, StringComparison.Ordinal);
        Assert.Contains("DoctrineGate.OverruleEnabled", desk, StringComparison.Ordinal);
        Assert.Contains("DoctrineGate.OwnerYesVisible", desk, StringComparison.Ordinal);
        Assert.Contains("IsEnabled = DoctrineGate.OverruleEnabled(board, taskId)", desk, StringComparison.Ordinal);
        Assert.Contains("Visibility = DoctrineGate.OwnerYesVisible(board, taskId) ? Visibility.Visible : Visibility.Collapsed", desk, StringComparison.Ordinal);

        var overStart = desk.IndexOf("void OverruleSwarmTask", StringComparison.Ordinal);
        var yesStart = desk.IndexOf("void OwnerYesSwarmTask", StringComparison.Ordinal);
        var yesEnd = desk.IndexOf("private sealed class SwarmActivityBar", StringComparison.Ordinal);
        Assert.True(overStart >= 0 && yesStart > overStart && yesEnd > yesStart);
        var overBody = desk[overStart..yesStart];
        var yesBody = desk[yesStart..yesEnd];
        Assert.Contains("DoctrineGate.Overrule(", overBody, StringComparison.Ordinal);
        Assert.Contains("ReloadSwarmSection()", overBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DoctrineGate.OwnerYes(", overBody, StringComparison.Ordinal);
        Assert.Contains("DoctrineGate.OwnerYes(", yesBody, StringComparison.Ordinal);
        Assert.Contains("ReloadSwarmSection()", yesBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Verify", yesBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SwarmStatus.Done", yesBody, StringComparison.Ordinal);
        Assert.DoesNotContain(".Status", yesBody, StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(connector, "*.cs"))
        {
            if (Path.GetFileName(file) == "DoctrineGate.cs") continue;
            var text = File.ReadAllText(file);
            Assert.False(BareOverrule.IsMatch(text), Path.GetFileName(file) + " calls Overrule.");
            Assert.False(BareOwnerYes.IsMatch(text), Path.GetFileName(file) + " calls OwnerYes.");
        }

        var gate = File.ReadAllText(Path.Combine(connector, "DoctrineGate.cs"));
        Assert.Contains("public static void Overrule(", gate, StringComparison.Ordinal);
        Assert.Contains("public static void OwnerYes(", gate, StringComparison.Ordinal);
        Assert.Contains("OwnerYes = false", gate, StringComparison.Ordinal);
    }

    static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

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
}
