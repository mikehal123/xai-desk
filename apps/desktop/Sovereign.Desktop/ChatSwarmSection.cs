using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Connector;
using Sovereign.Desktop.Core;
using static Sovereign.Desktop.MainWindow;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    private const int SubagentCardInsertIndex = 2;
    private readonly Button _swarmPanelTab = new() { Content = "Swarm", Visibility = Visibility.Collapsed, Background = DeskTheme.Surface };
    private readonly StackPanel _swarmRows = new() { Spacing = DeskTheme.Space8 };
    private readonly Dictionary<string, StackPanel> _swarmChatHosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _swarmStatus = [];
    private readonly Dictionary<string, string> _swarmLines = [];
    private readonly List<string> _swarmFeed = [];
    private readonly List<Storyboard> _swarmMotion = [];
    private Grid? _swarmHost;
    private TextBlock? _swarmGoalText;
    private ProgressBar? _swarmProgress;
    private TextBlock? _swarmCounts;
    private TextBlock? _swarmElapsed;
    private TextBlock? _swarmReviewStatus;
    private TextBlock? _swarmReviewNote;
    private TextBlock? _swarmWakePanel;
    private TextBlock? _swarmWakeWarning;
    private Button? _swarmContinueWithoutReview;
    private StackPanel? _swarmFeedLines;
    private TextBlock? _swarmActivityPreview;
    private RotateTransform? _swarmActivityChevron;
    private bool _swarmActivityOpen;
    private StackPanel? _swarmMemoryLines;
    private TextBlock? _swarmMemoryPreview;
    private RotateTransform? _swarmMemoryChevron;
    private bool _swarmMemoryOpen;
    private string? _swarmMemoryCaptured;
    private FileSystemWatcher? _swarmWatch;
    private DispatcherQueue? _swarmQueue;
    private DispatcherQueueTimer? _swarmClock;
    private DispatcherQueueTimer? _swarmReloadTimer;
    private string? _swarmGoal;
    private string? _swarmBoardId;
    private readonly SwarmRunProgress _swarmRunProgress = new();
    private StackPanel? _swarmOtherRuns;
    private SwarmBoard? _swarmClockBoard;
    private DateTime _swarmClockStarted;
    private DateTime _swarmClockWrite;
    private DateTime _swarmClockProgress;
    private bool _showSwarm;
    private bool _suppressAgentReveal;
    private int _swarmMotionEpoch;

    private string? SwarmManagerBrief()
    {
        if (!_saved.SwarmEnabled || !_saved.SubagentsEnabled) return null;
        var folder = _projectFolder;
        var memory = folder is null ? null : SwarmMemory.Brief(SwarmMemory.Load(_profileDirectory, folder));
        return SwarmRoles.Manager(SwarmSection.ConversationFolder(_saved.Id) ?? SwarmBoard.DefaultRoot(), memory, SwarmRoleModels.IsMixed(_saved.SwarmHire));
    }

    private Grid BuildSwarmHost()
    {
        _swarmGoalText = new TextBlock
        {
            FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeHeading, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = DeskTheme.Text, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis
        };
        _swarmProgress = new ProgressBar
        {
            Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, MaxHeight = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = DeskTheme.Accent, Background = DeskTheme.Raised
        };
        AutomationName(_swarmProgress, "Swarm progress 0 of 0");
        _swarmCounts = SubagentCaption("");
        _swarmElapsed = SubagentCaption("");
        _swarmElapsed.Foreground = DeskTheme.Text;
        AutomationName(_swarmElapsed, "Swarm run state");
        _swarmOtherRuns = new StackPanel { Spacing = 0, Visibility = Visibility.Collapsed };
        AutomationName(_swarmOtherRuns, "Other swarm runs");
        _swarmReviewStatus = SubagentCaption("");
        _swarmReviewStatus.Foreground = DeskTheme.Text;
        _swarmReviewStatus.Visibility = Visibility.Collapsed;
        AutomationName(_swarmReviewStatus, "Swarm review status");
        _swarmReviewNote = SubagentCaption("");
        _swarmReviewNote.IsTextSelectionEnabled = true;
        _swarmReviewNote.Visibility = Visibility.Collapsed;
        AutomationName(_swarmReviewNote, "Swarm review note");
        _swarmWakePanel = SubagentCaption("");
        _swarmWakePanel.IsTextSelectionEnabled = true;
        _swarmWakePanel.Visibility = Visibility.Collapsed;
        AutomationName(_swarmWakePanel, "Grokbot wake");
        _swarmWakeWarning = SubagentCaption("");
        _swarmWakeWarning.Foreground = DeskTheme.Text;
        _swarmWakeWarning.Visibility = Visibility.Collapsed;
        AutomationName(_swarmWakeWarning, "Grokbot wake warning");
        _swarmContinueWithoutReview = new Button
        {
            Content = "Continue without review",
            FontFamily = DeskTheme.UiFont,
            FontSize = DeskTheme.SizeCaption,
            Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4),
            MinHeight = DeskTheme.Space24,
            VerticalAlignment = VerticalAlignment.Center,
            Background = DeskTheme.Raised,
            Foreground = DeskTheme.Text,
            BorderBrush = DeskTheme.Divider,
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };
        AutomationName(_swarmContinueWithoutReview, "Continue without review");
        _swarmContinueWithoutReview.Click += (_, _) => ContinueSwarmWithoutReview();
        var reviewRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = DeskTheme.Space8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _swarmReviewStatus, _swarmContinueWithoutReview }
        };
        var swarmNote = SubagentCaption("Tasks for this goal. Expand a task to read its subagent chat.");
        AutomationName(swarmNote, "Swarm tab note");
        var clearSwarm = new Button
        {
            Content = "Clear swarm",
            FontFamily = DeskTheme.UiFont,
            FontSize = DeskTheme.SizeCaption,
            Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4),
            MinHeight = DeskTheme.Space24,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = DeskTheme.Raised,
            Foreground = DeskTheme.Text,
            BorderBrush = DeskTheme.Divider,
            BorderThickness = new Thickness(1)
        };
        AutomationName(clearSwarm, "Clear swarm");
        clearSwarm.Click += ClearSwarmClick;
        var headerBar = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerBar.Children.Add(swarmNote);
        Grid.SetColumn(clearSwarm, 1);
        headerBar.Children.Add(clearSwarm);
        var header = new StackPanel
        {
            Spacing = DeskTheme.Space4, HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { headerBar, _swarmGoalText, _swarmElapsed, _swarmProgress, _swarmCounts, reviewRow, _swarmWakePanel, _swarmWakeWarning, _swarmReviewNote, _swarmOtherRuns, BuildProjectMemory() }
        };
        var rows = new ScrollViewer
        {
            Content = _swarmRows, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var feed = BuildSwarmActivityFeed();
        var host = new Grid
        {
            Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, RowSpacing = DeskTheme.Space8
        };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.Children.Add(header);
        Grid.SetRow(rows, 1); host.Children.Add(rows);
        Grid.SetRow(feed, 2); host.Children.Add(feed);
        AutomationName(host, "Swarm");
        _swarmHost = host;
        return host;
    }

    private StackPanel BuildSwarmActivityFeed()
    {
        _swarmActivityOpen = ReadSwarmActivityFold();
        var activity = new TextBlock
        {
            Text = "Activity", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = DeskTheme.Text,
            VerticalAlignment = VerticalAlignment.Center, MaxLines = 1, TextWrapping = TextWrapping.NoWrap
        };
        _swarmActivityPreview = SubagentCaption("");
        _swarmActivityPreview.MaxLines = 1;
        _swarmActivityPreview.TextWrapping = TextWrapping.NoWrap;
        _swarmActivityPreview.TextTrimming = TextTrimming.CharacterEllipsis;
        _swarmActivityPreview.VerticalAlignment = VerticalAlignment.Center;
        var chevron = new FontIcon
        {
            Glyph = "\uE70D", FontFamily = DeskTheme.IconFont, FontSize = DeskTheme.SizeCaption,
            Foreground = DeskTheme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        _swarmActivityChevron = new RotateTransform();
        var chevronBox = new Grid
        {
            Width = DeskTheme.Space16, Height = DeskTheme.Space16, VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _swarmActivityChevron, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            Children = { chevron }
        };
        var barContent = new Grid { ColumnSpacing = DeskTheme.Space8, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        barContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barContent.Children.Add(activity);
        Grid.SetColumn(_swarmActivityPreview, 1); barContent.Children.Add(_swarmActivityPreview);
        Grid.SetColumn(chevronBox, 2); barContent.Children.Add(chevronBox);
        var bar = new SwarmActivityBar
        {
            Content = barContent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center, Background = DeskTheme.Surface, Foreground = DeskTheme.Text,
            BorderBrush = DeskTheme.Divider, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(DeskTheme.Radius10),
            Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4),
            MinHeight = DeskTheme.Space24, MinWidth = 0, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody,
            IsTabStop = true, UseSystemFocusVisuals = true, FocusVisualPrimaryBrush = DeskTheme.Accent, FocusVisualSecondaryBrush = DeskTheme.Background
        };
        bar.Click += (_, _) => ToggleSwarmActivityFold();
        AutomationName(bar, "Activity");
        _swarmFeedLines = new StackPanel { Spacing = DeskTheme.Space4, Margin = new Thickness(0, 0, 0, DeskTheme.Space4) };
        var feed = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch, Children = { _swarmFeedLines, bar } };
        AutomationName(feed, "Swarm activity");
        ApplySwarmActivityFold();
        return feed;
    }

    private void ApplyRightPane()
    {
        var agents = _showAgents && !_showSwarm;
        var swarm = _showSwarm;
        if (!agents && !swarm && _changeFilesScroll is not null) Grid.SetRowSpan(_changeFilesScroll, 1);
        if (_changeDetailScroll is not null) _changeDetailScroll.Visibility = agents || swarm ? Visibility.Collapsed : Visibility.Visible;
        _subagentList.Visibility = agents ? Visibility.Visible : Visibility.Collapsed;
        _changeFiles.Visibility = agents || swarm ? Visibility.Collapsed : Visibility.Visible;
        _changeDetail.Visibility = agents || swarm ? Visibility.Collapsed : Visibility.Visible;
        if (_changeFilesScroll is not null) _changeFilesScroll.Visibility = swarm ? Visibility.Collapsed : Visibility.Visible;
        if (_swarmHost is not null) _swarmHost.Visibility = swarm ? Visibility.Visible : Visibility.Collapsed;
        _changesPanelTab.Background = agents || swarm ? DeskTheme.Surface : DeskTheme.Pressed;
        _agentsPanelTab.Background = agents ? DeskTheme.Pressed : DeskTheme.Surface;
        _swarmPanelTab.Background = swarm ? DeskTheme.Pressed : DeskTheme.Surface;
        SyncHelperHost();
        if (swarm && _helperSidePane is not null)
        {
            if (_helperDetailHost is not null) _helperDetailHost.Visibility = Visibility.Collapsed;
            _helperHeightSplitter.Visibility = Visibility.Collapsed;
            _helperHeightSplitter.IsTabStop = false;
            _helperSidePane.RowDefinitions[2].Height = new GridLength(0);
            _helperSidePane.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            _helperSidePane.RowDefinitions[3].Height = new GridLength(0);
        }
        RefreshSubagentElapsed();
    }

    private void ShowSwarmTab()
    {
        _showSwarm = true;
        _showAgents = false;
        ApplyRightPane();
        SetChangesOpen(true);
    }

    private void AttachSwarmWatch()
    {
        _swarmQueue = DispatcherQueue.GetForCurrentThread();
        if (_swarmQueue is not null)
        {
            _swarmClock = _swarmQueue.CreateTimer();
            _swarmClock.Interval = TimeSpan.FromSeconds(1);
            _swarmClock.IsRepeating = true;
            _swarmClock.Tick += (_, _) => { RefreshSwarmElapsed(); FlushRunWakes(); };
            _swarmClock.Start();
            _swarmReloadTimer = _swarmQueue.CreateTimer();
            _swarmReloadTimer.Interval = TimeSpan.FromMilliseconds(180);
            _swarmReloadTimer.IsRepeating = false;
            _swarmReloadTimer.Tick += (_, _) => ReloadSwarmSection();
        }
        ReloadSwarmSection();
        var root = SwarmBoard.DefaultRoot();
        try
        {
            Directory.CreateDirectory(root);
            _swarmWatch = new FileSystemWatcher(root)
            {
                Filter = "board.json",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            FileSystemEventHandler changed = (_, e) => QueueSwarmReload(e.FullPath);
            _swarmWatch.Changed += changed;
            _swarmWatch.Created += changed;
            _swarmWatch.Deleted += changed;
            _swarmWatch.Renamed += (_, e) => QueueSwarmReload(e.FullPath);
            _swarmWatch.EnableRaisingEvents = true;
            WarnIfSkillsMissing();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _swarmWatch?.Dispose();
            _swarmWatch = null;
        }
    }

    private void QueueSwarmReload(string? path)
    {
        if (_disposed || path is null) return;
        if (!SwarmSection.IsConversationBoard(_saved.Id, path)) return;
        _swarmQueue?.TryEnqueue(ScheduleSwarmReload);
    }

    private void ScheduleSwarmReload()
    {
        if (_disposed || _swarmReloadTimer is null) return;
        _swarmReloadTimer.Stop();
        _swarmReloadTimer.Start();
    }

    private void ReloadSwarmSection()
    {
        if (_disposed || _swarmHost is null) return;
        try
        {
            var board = SwarmSection.TryLoad(_saved.Id);
            if (board is null || (!_saved.SwarmEnabled && !SwarmRunActive(board)))
            {
                HideSwarmBoard();
                return;
            }
            BindSwarmBoard(board);
            var open = _swarmPanelTab.Visibility == Visibility.Collapsed;
            _swarmPanelTab.Visibility = Visibility.Visible;
            if (open) ShowSwarmTab();
            ReattachSwarmChats();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Mounting a worker chat before its card is on screen must not hide the board.
        }
        catch (Exception)
        {
            HideSwarmBoard();
        }
    }

    private void ContinueSwarmWithoutReview()
    {
        SwarmBoard? board;
        try { board = SwarmSection.TryLoad(_saved.Id); }
        catch (Exception) { return; }
        if (!SwarmHandoffReview.ContinueWithoutReview(board)) return;
        ReleaseHandoffReviewHold();
        CancelHandoffWatch();
    }

    private void HideSwarmBoard()
    {
        _swarmClockBoard = null;
        _swarmPanelTab.Visibility = Visibility.Collapsed;
        StopSwarmMotion();
        _swarmRows.Children.Clear();
        if (_showSwarm) ShowAgents(false);
    }

    private void BindSwarmBoard(SwarmBoard board)
    {
        var goalText = _swarmGoalText;
        var progressBar = _swarmProgress;
        var counts = _swarmCounts;
        var elapsed = _swarmElapsed;
        var feedLines = _swarmFeedLines;
        var reviewStatus = _swarmReviewStatus;
        if (goalText is null || progressBar is null || counts is null || elapsed is null || feedLines is null || reviewStatus is null) return;
        NoteSwarmBoard(board);
        var review = (board.ReviewStatus ?? "").Trim();
        reviewStatus.Text = review;
        reviewStatus.Visibility = review.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (review.Length > 0) AutomationName(reviewStatus, review);
        var noteText = (board.ReviewNote ?? "").Trim();
        if (_swarmReviewNote is not null)
        {
            _swarmReviewNote.Text = noteText;
            _swarmReviewNote.Visibility = noteText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_swarmContinueWithoutReview is not null)
            _swarmContinueWithoutReview.Visibility = review == SwarmHandoffReview.TimedOutStatus ? Visibility.Visible : Visibility.Collapsed;
        if (!string.Equals(_swarmBoardId, board.Id, StringComparison.Ordinal))
        {
            _swarmBoardId = board.Id;
            _swarmGoal = null;
        }
        var goal = board.Goal ?? "";
        if (!string.Equals(_swarmGoal, goal, StringComparison.Ordinal))
        {
            _swarmGoal = goal;
            _swarmStatus.Clear();
            _swarmLines.Clear();
            _swarmFeed.Clear();
        }
        goalText.Text = goal;
        var runState = RememberSwarmClock(board, DateTime.UtcNow);
        var finished = runState.Finished;
        elapsed.Text = runState.Label;
        var progress = SwarmSection.Measure(board.Tasks);
        progressBar.Minimum = 0;
        progressBar.Maximum = 1;
        progressBar.Value = progress.Fraction;
        counts.Text = progress.Done + "/" + progress.Total + " · " + progress.Counts;
        AutomationName(progressBar, "Swarm progress " + progress.Done + " of " + progress.Total);
        StopSwarmMotion();
        _swarmChatHosts.Clear();
        _swarmRows.Children.Clear();
        foreach (var task in board.Tasks)
        {
            var id = task.Id ?? "";
            var label = SwarmSection.StatusLabel(task.Status);
            var known = _swarmStatus.TryGetValue(id, out var previous);
            var transition = known && !string.Equals(previous, label, StringComparison.Ordinal);
            if (!known || transition) AppendSwarmFeed(SwarmSection.FeedLine(known ? previous : null, task));
            var line = SwarmSection.LatestLine(task);
            if (!_swarmLines.TryGetValue(id, out var previousLine) || !string.Equals(previousLine, line, StringComparison.Ordinal))
            {
                if (!string.Equals(line, label, StringComparison.OrdinalIgnoreCase))
                {
                    var roleText = string.IsNullOrWhiteSpace(task.Role) ? "worker" : task.Role.Trim();
                    AppendSwarmFeed(roleText + " · " + line);
                }
            }
            _swarmStatus[id] = label;
            _swarmLines[id] = line;
            _swarmRows.Children.Add(SwarmTaskRow(board, task, label, finished, transition && label == "done", transition && label == "failed"));
            try
            {
                _suppressAgentReveal = true;
                ShowSwarmWorker(board, task);
            }
            catch (Exception) { }
            finally { _suppressAgentReveal = false; }
        }
        ReattachSwarmChats();
        RenderSwarmFeed();
        CaptureSwarmMemory(board);
        RenderProjectMemory();
    }

    private Expander SwarmTaskRow(SwarmBoard board, SwarmTask task, string label, bool finished, bool pop, bool shake)
    {
        var titleText = string.IsNullOrWhiteSpace(task.Title) ? task.Id : task.Title.Trim();
        var roleText = string.IsNullOrWhiteSpace(task.Role) ? "worker" : task.Role.Trim();
        var sessionId = SwarmSection.WorkerSessionId(board.Id, task.Id);
        var title = new TextBlock
        {
            Text = titleText, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Text,
            MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        var activity = SubagentCaption(SwarmSection.LatestLine(task));
        activity.MaxLines = 1;
        activity.TextWrapping = TextWrapping.NoWrap;
        activity.TextTrimming = TextTrimming.CharacterEllipsis;
        var copy = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Children = { title, activity } };
        var face = new SubagentBlobFace();
        face.Apply(sessionId, roleText + " " + titleText);
        face.SetLife(SwarmSection.PillState(label));
        var faceBox = new Viewbox { Width = 28, Height = 28, Stretch = Stretch.Uniform, Child = face.Root, VerticalAlignment = VerticalAlignment.Center };
        var role = new TextBlock
        {
            Text = roleText, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, Foreground = DeskTheme.Muted,
            MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        var identity = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space4, VerticalAlignment = VerticalAlignment.Center,
            Children = { faceBox, role }
        };
        var chip = SwarmRunView.TaskLabel(label, finished);
        var state = new TextBlock
        {
            Text = label == "done" ? "\u2713 " + chip : chip, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption,
            Foreground = DeskTheme.StatusForeground(SwarmSection.PillState(label)), MaxLines = 1, TextWrapping = TextWrapping.NoWrap
        };
        var pill = SubagentStatusChip(state);
        pill.VerticalAlignment = VerticalAlignment.Center;
        var retries = new TextBlock
        {
            Text = task.InterruptRetries > 0 ? "retries " + task.Retries + " · respawn " + task.InterruptRetries : "retries " + task.Retries, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption,
            Foreground = DeskTheme.Muted, VerticalAlignment = VerticalAlignment.Center
        };
        var header = new Grid { ColumnSpacing = DeskTheme.Space8, HorizontalAlignment = HorizontalAlignment.Stretch, Background = DeskTheme.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(copy);
        Grid.SetColumn(identity, 1); header.Children.Add(identity);
        Grid.SetColumn(pill, 2); header.Children.Add(pill);
        var doctrine = DoctrineTaskButtons(board, task, titleText);
        Grid.SetColumn(doctrine, 3); header.Children.Add(doctrine);
        var respawn = new Button
        {
            Content = "Respawn", FontFamily = DeskTheme.UiFont, FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MinWidth = 44, MinHeight = 16,
            Padding = new Thickness(4, 0, 4, 0),
            Background = DeskTheme.Raised, Foreground = DeskTheme.Text, BorderBrush = DeskTheme.Text,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = label is "done" or "failed" ? Visibility.Collapsed : Visibility.Visible
        };
        var respawnTaskId = task.Id;
        var respawnBoard = board;
        respawn.Click += (_, _) => RespawnSwarmTask(respawnBoard, respawnTaskId, null);
        respawn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => e.Handled = true), true);
        AutomationName(respawn, "Respawn swarm worker " + titleText);
        ToolTipService.SetToolTip(respawn, "Stops only this helper and restarts its task once with a narrower scope. A second use fails this task. The main agent and other workers keep running.");
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(respawn, 4); header.Children.Add(respawn);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(retries, 5); header.Children.Add(retries);
        var note = SwarmSection.ReadNote(task.VerifierNote);
        var chatHost = new StackPanel { Spacing = DeskTheme.Space8 };
        var chatLabel = SubagentCaption("Worker chat");
        chatHost.Children.Add(chatLabel);
        _swarmChatHosts[task.Id] = chatHost;
        AutomationName(chatHost, "Swarm worker chat " + titleText);
        var notes = new StackPanel
        {
            Spacing = DeskTheme.Space4,
            Margin = new Thickness(DeskTheme.Space12, DeskTheme.Space4, DeskTheme.Space12, DeskTheme.Space12),
            Children = { SwarmNoteLine("Claim", note.Claim), SwarmNoteLine("Evidence", note.Evidence), SwarmNoteLine("Verdict", note.Verdict), chatHost }
        };
        if (CursorChat && SwarmRoleModels.IsMixed(_saved.SwarmHire)) InsertCursorModelHire(notes, task);
        var row = SubagentExpander(SubagentHeaderChrome(header), notes, false);
        AutomationName(row, "Swarm task " + titleText + " " + label);
        if (label == "doing") PulseSwarmPill(pill, 1, 0.45, 780);
        else if (label == "review") PulseSwarmPill(pill, 0.55, 1, 1100);
        else if (pop) PopSwarmPill(pill);
        else if (shake) ShakeSwarmPill(pill);
        return row;
    }

    private void InsertCursorModelHire(StackPanel notes, SwarmTask task)
    {
        SwarmRoleModels.EnsureDefaults(_profileDirectory);
        var loaded = SwarmRoleModels.Load(_profileDirectory);
        var advertised = AdvertisedForHire();
        var chatModelId = SelectedChatModel;
        if (string.IsNullOrWhiteSpace(chatModelId)) chatModelId = _saved.Model;
        string? overrideId = null;
        if (!string.IsNullOrWhiteSpace(_saved.Id) && !string.IsNullOrWhiteSpace(task.Id))
            loaded.Overrides.TryGetValue(_saved.Id + "/" + task.Id, out overrideId);
        var picked = SwarmRoleModels.ForRole(advertised, loaded.Roles, task.Role, chatModelId, overrideId);
        var hireLabel = string.IsNullOrWhiteSpace(picked) ? "No model hire" : ChatModelLabel.Display(picked, AdvertisedName(advertised, picked));
        var caption = SubagentCaption("Model: " + hireLabel);
        notes.Children.Insert(0, caption);
        if (advertised.Length == 0) return;

        var combo = new ComboBox
        {
            FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, MinWidth = 180, MaxWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Left, PlaceholderText = "Choose a model"
        };
        var selected = -1;
        for (var i = 0; i < advertised.Length; i++)
        {
            var model = advertised[i];
            combo.Items.Add(new ComboBoxItem { Content = ChatModelLabel.Display(model.Id, model.Name), Tag = model.Id });
            if (selected < 0 && string.Equals(model.Id, picked, StringComparison.Ordinal)) selected = i;
        }
        if (selected >= 0) combo.SelectedIndex = selected;
        var ready = false;
        var shown = picked;
        combo.SelectionChanged += (_, _) =>
        {
            if (!ready) return;
            if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not string selectedId || string.IsNullOrWhiteSpace(selectedId)) return;
            if (string.Equals(selectedId, shown, StringComparison.Ordinal)) return;
            shown = selectedId;
            SwarmRoleModels.SaveOverride(_profileDirectory, _saved.Id, task.Id, selectedId);
            caption.Text = "Model: " + ChatModelLabel.Display(selectedId, AdvertisedName(advertised, selectedId));
        };
        combo.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => e.Handled = true), true);
        var hireTitle = string.IsNullOrWhiteSpace(task.Title) ? task.Id : task.Title.Trim();
        AutomationName(combo, "Model for swarm worker " + hireTitle);
        notes.Children.Insert(1, combo);
        ready = true;
    }

    private CursorChatModel[] AdvertisedForHire()
    {
        var live = _cursorChat?.AdvertisedModels;
        if (live is { Length: > 0 }) return live;
        return CursorModelCatalog.Load(_profileDirectory);
    }

    private static string? AdvertisedName(IReadOnlyList<CursorChatModel> advertised, string id)
    {
        foreach (var model in advertised)
            if (model is not null && string.Equals(model.Id, id, StringComparison.Ordinal)) return model.Name;
        return null;
    }

    private static TextBlock SwarmNoteLine(string name, string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? name + ": (none)" : name + ": " + value.Trim();
        var block = SubagentCaption(text);
        block.IsTextSelectionEnabled = true;
        return block;
    }

    private void MountSwarmWorker(CursorSubagent? previous, CursorSubagent child)
    {
        if (child.SessionId.StartsWith("swarm-", StringComparison.Ordinal)) return;
        var taskId = SwarmSection.MatchingTaskId(SwarmSection.TryLoad(_saved.Id), child.SessionId, child.Name, child.Task);
        if (taskId is null || !_swarmChatHosts.TryGetValue(taskId, out var host)) return;
        HostSwarmChat(previous, child, host);
    }

    private void ReattachSwarmChats()
    {
        var board = SwarmSection.TryLoad(_saved.Id);
        foreach (var pair in _subagentRows)
        {
            if (pair.Key.StartsWith("swarm-", StringComparison.Ordinal)) continue;
            var taskId = SwarmSection.MatchingTaskId(board, pair.Value.SessionId, pair.Value.Name, pair.Value.Task);
            if (taskId is null || !_swarmChatHosts.TryGetValue(taskId, out var host)) continue;
            HostSwarmChat(null, pair.Value, host);
        }
    }

    private void PulseSwarmPill(FrameworkElement pill, double from, double to, int milliseconds)
    {
        var pulse = new DoubleAnimation
        {
            From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(pulse, pill);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(pulse);
        PlaySwarmMotion(pill, storyboard);
    }

    private void PopSwarmPill(FrameworkElement pill)
    {
        var scale = new ScaleTransform { ScaleX = 0.7, ScaleY = 0.7 };
        pill.RenderTransform = scale;
        pill.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var storyboard = new Storyboard();
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var grow = new DoubleAnimation { From = 0.7, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(220)) };
            Storyboard.SetTarget(grow, scale);
            Storyboard.SetTargetProperty(grow, property);
            storyboard.Children.Add(grow);
        }
        PlaySwarmMotion(pill, storyboard);
    }

    private void ShakeSwarmPill(FrameworkElement pill)
    {
        var shift = new TranslateTransform();
        pill.RenderTransform = shift;
        var storyboard = new Storyboard();
        (double From, double To, int At)[] keys = [(0, -4, 0), (-4, 4, 55), (4, -3, 110), (-3, 0, 165)];
        foreach (var (from, to, at) in keys)
        {
            var step = new DoubleAnimation
            {
                From = from, To = to, BeginTime = TimeSpan.FromMilliseconds(at),
                Duration = new Duration(TimeSpan.FromMilliseconds(55))
            };
            Storyboard.SetTarget(step, shift);
            Storyboard.SetTargetProperty(step, "X");
            storyboard.Children.Add(step);
        }
        PlaySwarmMotion(pill, storyboard);
    }

    private void PlaySwarmMotion(FrameworkElement host, Storyboard storyboard)
    {
        var epoch = _swarmMotionEpoch;
        _swarmMotion.Add(storyboard);
        void Begin()
        {
            if (epoch != _swarmMotionEpoch || _disposed) return;
            try { storyboard.Begin(); }
            catch (Exception) { }
        }
        if (host.IsLoaded) Begin();
        else host.Loaded += (_, _) => Begin();
    }

    private void StopSwarmMotion()
    {
        _swarmMotionEpoch++;
        foreach (var storyboard in _swarmMotion)
        {
            try { storyboard.Stop(); }
            catch (Exception) { }
        }
        _swarmMotion.Clear();
    }

    private void AppendSwarmFeed(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (_swarmFeed.Count > 0 && _swarmFeed[^1] == line) return;
        _swarmFeed.Add(line);
        if (_swarmFeed.Count > 8) _swarmFeed.RemoveAt(0);
    }

    private void RenderSwarmFeed()
    {
        var lines = _swarmFeedLines;
        if (lines is null) return;
        lines.Children.Clear();
        foreach (var line in _swarmFeed) lines.Children.Add(SubagentCaption(line));
        if (_swarmActivityPreview is not null) _swarmActivityPreview.Text = SwarmActivityFold.Preview(_swarmFeed);
    }

    private void ToggleSwarmActivityFold()
    {
        _swarmActivityOpen = !_swarmActivityOpen;
        ApplySwarmActivityFold();
        try { SwarmActivityFold.Remember(_profileDirectory, _saved.Id, _swarmActivityOpen); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private void ApplySwarmActivityFold()
    {
        if (_swarmFeedLines is not null)
            _swarmFeedLines.Visibility = _swarmActivityOpen ? Visibility.Visible : Visibility.Collapsed;
        if (_swarmActivityPreview is not null)
            _swarmActivityPreview.Visibility = _swarmActivityOpen ? Visibility.Collapsed : Visibility.Visible;
        if (_swarmActivityChevron is not null)
            _swarmActivityChevron.Angle = _swarmActivityOpen ? 0 : 180;
    }

    private bool ReadSwarmActivityFold()
    {
        try { return SwarmActivityFold.Load(_profileDirectory, _saved.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private bool SwarmRunActive(SwarmBoard board)
    {
        var phase = RememberSwarmClock(board, DateTime.UtcNow).Phase;
        return phase is SwarmRunPhase.Running or SwarmRunPhase.Stalled;
    }

    private SwarmRunState RememberSwarmClock(SwarmBoard board, DateTime now)
    {
        _swarmClockBoard = board;
        var path = File.Exists(board.BoardPath) ? board.BoardPath : SwarmSection.ActiveBoardFile(_saved.Id);
        if (!string.IsNullOrEmpty(path))
        {
            _swarmClockStarted = SwarmRunView.StartedUtc(path);
            _swarmClockWrite = File.GetLastWriteTimeUtc(path);
            _swarmClockProgress = _swarmRunProgress.Observe(board.Id, SwarmRunView.ProgressKey(board), _swarmClockWrite);
        }
        else
        {
            _swarmClockStarted = now;
            _swarmClockWrite = now;
            _swarmClockProgress = now;
        }
        return SwarmRunView.Describe(board, _swarmClockStarted, _swarmClockProgress, _swarmClockWrite, now);
    }

    private async void ClearSwarmClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            Title = "Clear swarm?",
            Content = "Stop helpers still running for this run, then move this run's files into archived. Nothing is deleted.",
            PrimaryButtonText = "Clear swarm",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = button.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (_disposed) return;
        var loaded = SwarmSection.TryLoad(_saved.Id);
        var runId = loaded is not null && !string.IsNullOrWhiteSpace(loaded.Id) ? loaded.Id : _swarmBoardId;
        var folder = SwarmSection.ConversationFolder(_saved.Id);
        FinishSubagents("stopped");
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(runId)) return;
        if (SwarmRunArchive.Move(folder, runId).Moved) ReloadSwarmSection();
    }

    private void RefreshSwarmElapsed()
    {
        var elapsed = _swarmElapsed;
        var board = _swarmClockBoard;
        if (_disposed || board is null || elapsed is null) return;
        elapsed.Text = SwarmRunView.Describe(board, _swarmClockStarted, _swarmClockProgress, _swarmClockWrite, DateTime.UtcNow).Label;
    }

    private void WarnIfSkillsMissing()
    {
        _ = WorkDoctrine.Text();
        _ = VerifyResultSkill.Text();
        var notes = new[] { WorkDoctrine.Warning, VerifyResultSkill.Warning }
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .ToArray();
        if (notes.Length == 0) return;
        _chatStatus.Text = "Warning: " + string.Join(" ", notes);
    }

    private StackPanel BuildProjectMemory()
    {
        var title = new TextBlock
        {
            Text = "Project memory", FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = DeskTheme.Text,
            VerticalAlignment = VerticalAlignment.Center, MaxLines = 1
        };
        _swarmMemoryPreview = SubagentCaption("");
        _swarmMemoryPreview.MaxLines = 1;
        _swarmMemoryPreview.TextWrapping = TextWrapping.NoWrap;
        _swarmMemoryPreview.TextTrimming = TextTrimming.CharacterEllipsis;
        var chevron = new FontIcon
        {
            Glyph = "\uE70D", FontFamily = DeskTheme.IconFont, FontSize = DeskTheme.SizeCaption,
            Foreground = DeskTheme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        _swarmMemoryChevron = new RotateTransform();
        var chevronBox = new Grid
        {
            Width = DeskTheme.Space16, Height = DeskTheme.Space16, VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _swarmMemoryChevron, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            Children = { chevron }
        };
        var barContent = new Grid { ColumnSpacing = DeskTheme.Space8, IsHitTestVisible = false };
        barContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barContent.Children.Add(title);
        Grid.SetColumn(_swarmMemoryPreview, 1); barContent.Children.Add(_swarmMemoryPreview);
        Grid.SetColumn(chevronBox, 2); barContent.Children.Add(chevronBox);
        var bar = new SwarmActivityBar
        {
            Content = barContent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = DeskTheme.Surface, BorderBrush = DeskTheme.Divider, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(DeskTheme.Radius10),
            Padding = new Thickness(DeskTheme.Space8, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4),
            MinHeight = DeskTheme.Space24, FontFamily = DeskTheme.UiFont
        };
        bar.Click += (_, _) =>
        {
            _swarmMemoryOpen = !_swarmMemoryOpen;
            ApplyProjectMemoryFold();
        };
        AutomationName(bar, "Project memory");
        _swarmMemoryLines = new StackPanel { Spacing = DeskTheme.Space4, Margin = new Thickness(0, DeskTheme.Space4, 0, 0) };
        var host = new StackPanel { Spacing = 0, Children = { bar, _swarmMemoryLines } };
        RenderProjectMemory();
        return host;
    }

    private void ApplyProjectMemoryFold()
    {
        if (_swarmMemoryLines is not null)
            _swarmMemoryLines.Visibility = _swarmMemoryOpen ? Visibility.Visible : Visibility.Collapsed;
        if (_swarmMemoryPreview is not null)
            _swarmMemoryPreview.Visibility = _swarmMemoryOpen ? Visibility.Collapsed : Visibility.Visible;
        if (_swarmMemoryChevron is not null)
            _swarmMemoryChevron.Angle = _swarmMemoryOpen ? 0 : 180;
    }

    private void RenderProjectMemory()
    {
        var lines = _swarmMemoryLines;
        if (lines is null) return;
        lines.Children.Clear();
        var notes = _projectFolder is null ? [] : SwarmMemory.Load(_profileDirectory, _projectFolder);
        if (_swarmMemoryPreview is not null)
            _swarmMemoryPreview.Text = notes.Count == 0 ? "None saved" : SwarmMemory.Label(notes[^1].Kind) + ": " + notes[^1].Text;
        foreach (var note in notes)
            lines.Children.Add(SubagentCaption(SwarmMemory.Label(note.Kind) + ": " + note.Text));
        if (notes.Count == 0)
            lines.Children.Add(SubagentCaption(_projectFolder is null ? "Open a project folder to keep notes." : "None saved."));
        ApplyProjectMemoryFold();
    }

    private void CaptureSwarmMemory(SwarmBoard board)
    {
        if (_projectFolder is null || string.IsNullOrWhiteSpace(board.Id) || board.Id == _swarmMemoryCaptured) return;
        if (board.Tasks.Count == 0 || board.Tasks.Any(task => task.Status is not (SwarmStatus.Done or SwarmStatus.Failed))) return;
        SwarmMemory.RememberRun(_profileDirectory, _projectFolder, board, _saved.Turns.LastOrDefault()?.Reply);
        _swarmMemoryCaptured = board.Id;
    }

    private StackPanel DoctrineTaskButtons(SwarmBoard board, SwarmTask task, string titleText)
    {
        var taskId = task.Id ?? "";
        var overrule = new Button
        {
            Content = "Overrule",
            FontFamily = DeskTheme.UiFont,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            MinWidth = 44,
            MinHeight = 16,
            Padding = new Thickness(4, 0, 4, 0),
            Background = DeskTheme.Raised,
            Foreground = DeskTheme.Text,
            BorderBrush = DeskTheme.Text,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = DoctrineGate.OverruleEnabled(board, taskId)
        };
        AutomationName(overrule, "Overrule " + titleText);
        ToolTipService.SetToolTip(overrule, "Appends overruled when the latest disagreement row holds. A second click does nothing.");
        overrule.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => e.Handled = true), true);
        overrule.Click += (_, _) => OverruleSwarmTask(board, taskId, overrule);
        var ownerYes = new Button
        {
            Content = "Owner yes",
            FontFamily = DeskTheme.UiFont,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            MinWidth = 44,
            MinHeight = 16,
            Padding = new Thickness(4, 0, 4, 0),
            Background = DeskTheme.Raised,
            Foreground = DeskTheme.Text,
            BorderBrush = DeskTheme.Text,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = DoctrineGate.OwnerYesVisible(board, taskId) ? Visibility.Visible : Visibility.Collapsed
        };
        AutomationName(ownerYes, "Owner yes " + titleText);
        ToolTipService.SetToolTip(ownerYes, "Appends the owner's yes on the latest IRREVERSIBLE rollback row. Does not mark the task done.");
        ownerYes.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => e.Handled = true), true);
        ownerYes.Click += (_, _) => OwnerYesSwarmTask(board, taskId, ownerYes);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = DeskTheme.Space4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { overrule, ownerYes }
        };
    }

    private void OverruleSwarmTask(SwarmBoard board, string taskId, Button button)
    {
        if (!button.IsEnabled) return;
        try
        {
            if (!DoctrineGate.OverruleEnabled(board, taskId))
            {
                button.IsEnabled = false;
                return;
            }
            DoctrineGate.Overrule(board, taskId);
            button.IsEnabled = false;
            ReloadSwarmSection();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
        {
        }
    }

    private void OwnerYesSwarmTask(SwarmBoard board, string taskId, Button button)
    {
        if (button.Visibility != Visibility.Visible) return;
        try
        {
            if (!DoctrineGate.OwnerYesVisible(board, taskId))
            {
                button.Visibility = Visibility.Collapsed;
                return;
            }
            DoctrineGate.OwnerYes(board, taskId);
            button.Visibility = Visibility.Collapsed;
            ReloadSwarmSection();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
        {
        }
    }

    private sealed class SwarmActivityBar : Button
    {
        public SwarmActivityBar() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    private void ReleaseSwarmWatch()
    {
        _swarmReloadTimer?.Stop();
        _swarmClock?.Stop();
        StopReviewResumeTimer();
        StopSwarmMotion();
        if (_swarmWatch is null) return;
        _swarmWatch.EnableRaisingEvents = false;
        _swarmWatch.Dispose();
        _swarmWatch = null;
    }
}
