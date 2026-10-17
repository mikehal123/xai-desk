using Sovereign.Desktop.Client;
using Sovereign.Desktop.Connector;

namespace Sovereign.Desktop;

internal static class SwarmPanels
{
    public static CursorSubagent Snapshot(SwarmBoard board, SwarmTask task)
    {
        var view = SwarmLoop.View(board, task);
        var shown = SwarmSection.DisplayStatus(task);
        return new CursorSubagent(
            view.SessionId,
            view.Name,
            string.IsNullOrWhiteSpace(view.Task) ? task.Title : view.Task,
            view.State,
            view.Output,
            shown,
            StartedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            CompletedAt: shown is SwarmStatus.Done or SwarmStatus.Failed ? DateTimeOffset.UtcNow : null);
    }
}

internal sealed partial class ChatConversation
{
    internal void ShowSwarmWorker(SwarmBoard board, SwarmTask task) =>
        ObserveSubagent(new CursorActivityEvent("subagent", Subagent: SwarmPanels.Snapshot(board, task)));
}
