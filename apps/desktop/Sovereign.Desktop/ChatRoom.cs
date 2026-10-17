using System.Reflection;
using static Sovereign.Desktop.MainWindow;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Connector;

namespace Sovereign.Desktop;

internal sealed partial class ChatConversation
{
    internal readonly Grid _chatPage = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(DeskTheme.Space24, DeskTheme.Space12, DeskTheme.Space24, DeskTheme.Space12), RowSpacing = DeskTheme.Space8 };
    internal readonly ComboBox _chatProvider = new() { Width = 230 };
    internal readonly ComboBox _chatModel = new() { MinWidth = 150, MaxWidth = 360, PlaceholderText = "Choose a model", HorizontalAlignment = HorizontalAlignment.Left };
    internal readonly TextBox _chatInput = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 140, PlaceholderText = "Message your agent…" };
    internal readonly TextBlock _chatStatus = Note("Choose a room and connect. Chats are separate from the offline engine.");
    private Button? _switchCursorGrok;
    // Leave room beneath the last message for the floating reading controls.
    internal readonly StackPanel _chatMessages = new() { Spacing = DeskTheme.Space24, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, DeskTheme.Space16, 0, DeskTheme.Space24 + DeskTheme.Space24 + DeskTheme.Space8) };
    internal readonly List<ChatMessage> _ollamaHistory = [];
    internal readonly OllamaChatClient _ollamaChat = new();
    internal OllamaChatOptions? _ollamaQaOptions;
    internal OllamaChatReceipt? _lastOllamaReceipt;
    internal CodexChatClient? _codexChat;
    internal CursorChatClient? _cursorChat;
    internal GrokBuildChatClient? _grokBuild;
    internal XaiApiChatClient? _xaiChat;
    internal CancellationTokenSource? _chatCancellation;
    private readonly ProgressRing _composerSpinner = new() { Width = 14, Height = 14, IsActive = false };
    private Grid? _modelPickerArea;
    private TextBlock _liveStatus = new() { FontSize = DeskTheme.SizeCaption, Opacity = 1, MaxLines = 1,
        TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed, Margin = new Thickness(DeskTheme.Space4, 0, DeskTheme.Space4, DeskTheme.Space4) };
    private Microsoft.UI.Xaml.Shapes.Ellipse _liveIndicator = new()
    {
        Width = 6, Height = 6, Fill = DeskTheme.Accent, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
    };
    private Grid _liveStatusRow = new() { ColumnSpacing = DeskTheme.Space8, Visibility = Visibility.Collapsed };
    private Microsoft.UI.Xaml.Media.Animation.Storyboard _livePulse = new();
    private StackPanel? _liveStatusOwner;
    private bool _livePulseRunning;
    private bool _liveIndicatorWorking;
    private readonly StackPanel _chatTitleActions = new() { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space12 };
    private bool _classicChat;
    private string _lastPrompt = "";
    private DateTimeOffset? _tuiBusySince;
    private int _tuiFrame;
    private bool _tuiClockHooked;
    private DispatcherTimer? _tuiClock;
    private TextBlock? _tuiContext;
    private TextBlock? _tuiTokens;
    private Button? _tuiStop;
    private Grid? _tuiStatusHost;
    private StackPanel? _chatHeading;
    private Grid? _chatConversationHost;
    private StackPanel? _composerArea;
    private Border? _composerShell;
    private TextBlock? _composerHint;
    private Grid? _promptRow;
    private TextBlock? _promptMark;
    private Grid? _composerBar;
    private StackPanel? _composerInner;
    private Border? _readingControls;
    private static readonly Microsoft.UI.Xaml.Media.FontFamily TuiFont = DeskTheme.MonoFont;
    private void SetComposerStatus(string status)
    {
        var responding = _chatBusy && _chatCancellation is not null;
        PlaceLiveStatus();
        SetLiveIndicator(responding);
        if (!_classicChat)
        {
            RefreshTuiStatus();
            DescribeSelectedModel();
            AutomationName(_composerSpinner, status);
            return;
        }
        if (_tuiTokens is not null) _tuiTokens.Visibility = Visibility.Collapsed;
        if (_tuiStop is not null) _tuiStop.Visibility = Visibility.Collapsed;
        if (_tuiStatusHost is not null) _tuiStatusHost.Visibility = responding ? Visibility.Visible : Visibility.Collapsed;
        _liveStatus.Text = responding ? SelectedChatLabel + " \u00B7 " + status : "";
        _liveStatus.Visibility = responding ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_liveStatus, _liveStatus.Text);
        DescribeSelectedModel();
        if (_modelPickerArea is not null && responding) ToolTipService.SetToolTip(_modelPickerArea, status);
        AutomationName(_composerSpinner, status);
    }
    private Grid? _composerProgress;
    private Button? _chatSend;
    private Button? _chatStop;
    private Flyout? _connectionFlyout;
    private const double ConnectionFlyoutWidth = 360;
    private const double ConnectionFlyoutHeight = 480;
    private Button? _chatConnect;
    private Button? _chatLogin;
    private Button? _chatInstall;
    private Button? _chatNew;
    internal bool _chatReady;
    private bool _cursorConnecting;
    private bool _reconnectOnSend;
    internal bool _chatBusy;
    private bool _chatComposing;
    private ScrollViewer? _chatScroll;
    private ChatActivityView? _activeChatTurn;
    private readonly List<ChatActivityView> _chatTurns = [];
    private Button? _chatLatest;
    private Button? _chatClearReasoning;
    private bool _chatFollowLatest = true;
    internal CodingPlanChatClient? _planChat;
    private readonly PasswordBox _planKey = new() { Header = "Coding-plan key", Width = 300 };
    private readonly ComboBox _planEndpoint = new() { MinWidth = 300, Header = "Provider endpoint" };
    private StackPanel? _planControls;
    internal string PlanProvider => "grok";
    internal bool PlanChat => false;
    internal bool CodexChat => false;
    internal bool CursorChat => _chatProvider.SelectedIndex == DeskChatConnections.Cursor;
    internal bool GrokBuildChat => _chatProvider.SelectedIndex == DeskChatConnections.GrokBuild;
    internal bool XaiApiChat => _chatProvider.SelectedIndex == DeskChatConnections.XaiApi;
    internal string SelectedChatLabel => ReadPicker(_chatModel.SelectedItem).Label
        ?? (PlanChat && !string.IsNullOrWhiteSpace(_chatModel.Text) ? ChatModelLabel.Display(_chatModel.Text.Trim(), _chatModel.Text.Trim()) : "Agent");
    internal string? SelectedChatDetails => ReadPicker(_chatModel.SelectedItem).Details
        ?? (SelectedChatModel is { Length: > 0 } id ? ChatModelLabel.Details(id) : null);
    internal string? SelectedChatModel => PlanChat ? _chatModel.Text.Trim() : ReadPicker(_chatModel.SelectedItem).Id;

    internal bool LocalChat => false;
    internal void ResetChat()
    {
        _codexChat?.Dispose(); _codexChat = null;
        _cursorChat?.Dispose(); _cursorChat = null;
        _grokBuild?.Dispose(); _grokBuild = null;
        _xaiChat?.Dispose(); _xaiChat = null;
        _planChat?.Dispose(); _planChat = null;
        _chatModel.Items.Clear();
        _activeChatTurn = null; _projectSessionEpoch++; _needsContext = _saved.Turns.Length > 0;
        _chatFollowLatest = true;
        if (_chatLatest is not null) _chatLatest.Visibility = Visibility.Collapsed;
        _chatReady = false; _reconnectOnSend = false; if (_chatSend is not null) _chatSend.IsEnabled = false;
        if (_chatLogin is not null) _chatLogin.IsEnabled = !LocalChat && !PlanChat;
        var contextNote = _saved.Turns.Length > 0 ? " Saved messages are kept in this conversation." : " Ready for a new message.";
        if (_chatLogin is not null)
        {
            var label = CursorChat ? "Sign in with Cursor" : "Sign in with Grok";
            _chatLogin.Content = label; AutomationName(_chatLogin, label); _chatLogin.IsEnabled = CursorChat || GrokBuildChat;
        }
        _chatStatus.Text = (CursorChat
            ? "Cursor · your Cursor account · models from your subscription. Open Folder for tools, subagents, and file edits."
            : XaiApiChat
                ? "xAI API · Grok models · the key stays in Windows Credential Manager."
                : "Grok Build · your installed Grok CLI · choose a Grok model. File edits stay in the open project folder.") + contextNote;
        UpdateProjectHeader();
        if (_planControls is not null) _planControls.Visibility = PlanChat ? Visibility.Visible : Visibility.Collapsed;
        _chatModel.IsEditable = PlanChat;
        _planKey.Password = ""; _planEndpoint.Items.Clear();
        if(PlanChat)
        {
            foreach(var endpoint in CodingPlanChatClient.Endpoints(PlanProvider)) _planEndpoint.Items.Add(endpoint);
            _planEndpoint.SelectedItem = ConnectionSetup.Endpoint(PlanProvider);
            _planKey.Password = "";
            _planKey.Header = PlanProvider == "moonshot" ? "Kimi Platform API key" : "Coding-plan key";
            _chatStatus.Text=PlanProvider == "moonshot" ? "Kimi Platform API · uses your platform.kimi.ai API balance, not a Kimi Code subscription. Paste your API key and connect." : "Coding-plan connection. Open Folder to enable project tools. Import your IMIGROK key or enter it here. Access and usage limits depend on the provider; no general paid-API fallback.";
        }
        RefreshAttachmentStatus();
        RefreshCursorInstallButton();
    }
    private void FitConnectionFlyout()
    {
        if (_connectionFlyout?.Content is not FrameworkElement panel || _chatConnectionButton is not { XamlRoot: { } root } button)
            return;
        var origin = button.TransformToVisual(null).TransformPoint(default);
        var below = root.Size.Height - origin.Y - button.ActualHeight - DeskTheme.Space16;
        var above = origin.Y - DeskTheme.Space16;
        var room = Math.Max(below, above);
        if (room < 160) room = 160;
        panel.MaxHeight = Math.Min(ConnectionFlyoutHeight, room);
    }

    private void BuildChatPage()
    {
        _chatPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chatPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chatPage.RowDefinitions.Add(new RowDefinition());
        _chatPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var label in DeskChatConnections.Labels) _chatProvider.Items.Add(label);
        _chatProvider.SelectedIndex = DeskChatConnections.Cursor;
        _chatProvider.SelectionChanged += (_, _) => ProviderChanged();
        _chatConnect = Button("Connect / refresh models", ConnectChatAsync);
        _chatLogin = Button("Sign in with ChatGPT", async () => { await StartChatSignInAsync(); });
        _chatInstall = Button(CursorInstall.ButtonLabel, InstallCursorAsync);
        _chatInstall.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(_chatInstall, "Install the pinned Cursor connector. Then click Connect / refresh models.");
        foreach (var button in new[] { _chatConnect, _chatLogin, _chatInstall })
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
        _chatNew = Button("New conversation", () => { _connectionFlyout?.Hide(); _newChat(_chatProvider.SelectedIndex, _projectFolder); });
        _chatNew.HorizontalAlignment = HorizontalAlignment.Stretch;
        var import = Button("Import IMIGROK key", async () =>
        {
            try {
                var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
                var file=await picker.PickSingleFileAsync(); if(file is null) return;
                _planKey.Password=ImigrokCredentialImport.ReadKey(file.Path,PlanProvider);
                _chatStatus.Text="Selected provider key loaded. Confirm the endpoint, then Connect. Other credentials were not imported.";
            } catch(Exception ex) { _chatStatus.Text="Import unavailable: "+ex.Message; }
        });
        _planControls=new StackPanel { Spacing = DeskTheme.Space8, Visibility=Visibility.Collapsed, Children={_planKey,_planEndpoint,import,Note("Select IMIGROK’s .env or auth.json. Connect stores only this key in Windows Credential Manager. Changing endpoint or key requires reconnecting.")} };
        _planKey.PasswordChanged += (_,_) => { if(PlanChat) { _chatReady=false; if(_chatSend is not null) _chatSend.IsEnabled=false; RefreshAttachmentStatus(); } };
        _planEndpoint.SelectionChanged += (_,_) => { if(PlanChat) { _chatReady=false; if(_chatSend is not null) _chatSend.IsEnabled=false; RefreshAttachmentStatus(); } };
        _chatProvider.MaxDropDownHeight = 240;
        _planEndpoint.MinWidth = 0;
        _planEndpoint.MaxWidth = ConnectionFlyoutWidth;
        _planEndpoint.HorizontalAlignment = HorizontalAlignment.Stretch;
        _planEndpoint.MaxDropDownHeight = 240;
        _connectionFlyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            Content = new ScrollViewer
            {
                MaxWidth = ConnectionFlyoutWidth,
                MaxHeight = ConnectionFlyoutHeight,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Spacing = DeskTheme.Space12,
                    MaxWidth = ConnectionFlyoutWidth,
                    Children =
                    {
                        Head("Connection"), _chatProvider,
                        new StackPanel { Spacing = DeskTheme.Space8, Children = { _chatConnect, _chatLogin, _chatInstall } },
                        BuildProjectToolbar(), _planControls
                    }
                }
            }
        };
        _connectionFlyout.Opened += (_, _) => FitConnectionFlyout();
        ((FrameworkElement)_connectionFlyout.Content).RequestedTheme = ElementTheme.Dark;
        DeskTheme.Install(((FrameworkElement)_connectionFlyout.Content).Resources);
        _chatConnectionButton = new Button { Content = "Connection", Flyout = _connectionFlyout, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationName(_chatConnectionButton, "Connection settings");
        ToolTipService.SetToolTip(_chatConnectionButton, "Connection settings");
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8, Children = { _subagentsToggle, _changesToggle, _chatConnectionButton } };
        _chatHeadingActions = headerActions;
        var titleRow = new Grid { ColumnSpacing = DeskTheme.Space12 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition()); titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _chatTitleActions.Children.Add(Head("Chat & projects"));
        _chatTitleActions.Children.Add(Button("Select / copy chat", ShowChatTextAsync));
        titleRow.Children.Add(_chatTitleActions); Grid.SetColumn(headerActions, 1); titleRow.Children.Add(headerActions);
        var pathRow = new Grid();
        pathRow.ColumnDefinitions.Add(new ColumnDefinition());
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.Children.Add(_projectDescription);
        _tuiContext = new TextBlock { FontSize = DeskTheme.SizeCaption, Opacity = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(DeskTheme.Space16, 0, 0, 0), Visibility = Visibility.Collapsed };
        Grid.SetColumn(_tuiContext, 1);
        pathRow.Children.Add(_tuiContext);
        _chatHeading = new StackPanel { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch, Spacing = DeskTheme.Space4, Children = { titleRow, pathRow } };
        _chatPage.Children.Add(_chatHeading);
        _chatHeading.SizeChanged += (_, _) => RefreshChatHeaderTabs();
        _chatHeading.Loaded += (_, _) => RefreshChatHeaderTabs();
        _chatStatus.MaxWidth = 860; _chatStatus.HorizontalAlignment = HorizontalAlignment.Stretch;
        _chatStatus.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => ToolTipService.SetToolTip(_chatStatus, _chatStatus.Text));
        _chatStatus.MaxLines = 2; _chatStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        _switchCursorGrok = Button("Switch to Cursor Grok 4.7", SwitchToCursorGrok47);
        _switchCursorGrok.Visibility = Visibility.Collapsed;
        _switchCursorGrok.HorizontalAlignment = HorizontalAlignment.Left;
        _switchCursorGrok.Margin = new Thickness(0, DeskTheme.Space8, 0, 0);
        AutomationName(_switchCursorGrok, "Switch to Cursor Grok 4.7");
        var statusHost = new StackPanel { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch, Children = { _chatStatus, _switchCursorGrok } };
        Grid.SetRow(statusHost, 1); _chatPage.Children.Add(statusHost);
        _chatScroll = new ScrollViewer { Content = _chatMessages, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // Constrain normal text to the pane while allowing explicitly wide content to scroll.
        void FitTranscript()
        {
            var available = _chatScroll.ViewportWidth > 0 ? _chatScroll.ViewportWidth : _chatScroll.ActualWidth;
            if (available > 0 && (double.IsNaN(_chatMessages.Width) || Math.Abs(_chatMessages.Width - available) > .5))
                _chatMessages.Width = available;
        }
        _chatScroll.Loaded += (_, _) => FitTranscript();
        _chatScroll.SizeChanged += (_, _) => FitTranscript();
        _chatScroll.RegisterPropertyChangedCallback(ScrollViewer.ViewportWidthProperty, (_, _) => FitTranscript());
        _chatScroll.ViewChanged += (_, _) => {
            _chatFollowLatest = _chatScroll.ScrollableHeight - _chatScroll.VerticalOffset < 70;
            if (_chatLatest is not null) _chatLatest.Visibility = _chatFollowLatest ? Visibility.Collapsed : Visibility.Visible;
            SyncReadingControls();
        };
        _chatLatest = Button("Jump to latest ↓", () => { _chatFollowLatest = true; FollowChatIfNeeded(); });
        _chatLatest.HorizontalAlignment = HorizontalAlignment.Center;
        _chatLatest.VerticalAlignment = VerticalAlignment.Bottom;
        _chatLatest.Margin = new Thickness(0, 0, 0, DeskTheme.Space8); _chatLatest.Visibility = Visibility.Collapsed;
        _chatClearReasoning = Button("Clear reasoning", ClearChatReasoningDisplay);
        _chatClearReasoning.IsEnabled = false;
        _chatClearReasoning.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(_chatClearReasoning, "Clear this response's reasoning panel and reset its display limit. The agent, answers and tool activity are unchanged.");
        _chatLatest.Margin = new Thickness(0);
        foreach (var button in new[] { _chatClearReasoning, _chatLatest })
        {
            button.Height = button.MinHeight = DeskTheme.Space24;
            button.MinWidth = 0;
            button.FontSize = DeskTheme.SizeCaption;
            button.Padding = new Thickness(DeskTheme.Space8, 0, DeskTheme.Space8, 0);
            button.Foreground = DeskTheme.Muted;
            button.CornerRadius = new CornerRadius(DeskTheme.Radius6);
        }
        var readingActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space4,
            Children = { _chatClearReasoning, _chatLatest } };
        _readingControls = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0), CornerRadius = new CornerRadius(DeskTheme.Radius6), Background = DeskTheme.Transparent, Child = readingActions };
        _chatConversationHost = new Grid { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch, Children = { _chatScroll } };
        var conversation = _chatConversationHost;
        conversation.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(async (_, e) => {
            var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
            var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
            if (e.Key == VirtualKey.A && control)
            { e.Handled = true; await ShowChatTextAsync(); return; }
            if (_classicChat) return;
            var action = GrokTuiPresentation.Key(e.Key.ToString(), shift, control);
            if (action == GrokTuiKeyAction.ClearView) { e.Handled = true; _chatMessages.Children.Clear(); }
            else if (action == GrokTuiKeyAction.Stop) { e.Handled = true; StopChatResponse(); }
        }), true);
        Grid.SetRow(conversation, 2); _chatPage.Children.Add(conversation);
        _chatInput.TextCompositionStarted += (_, _) => _chatComposing = true;
        _chatInput.TextCompositionEnded += (_, _) => _chatComposing = false;
        _chatInput.PreviewKeyDown += ChatInputKeyDown;
        _chatSend = Button("Send message", SendChatAsync); _chatSend.IsEnabled = false;
        _chatStop = Button("Stop response", StopChatResponse);
        _chatStop.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(_chatSend, "Send message · Enter"); ToolTipService.SetToolTip(_chatStop, "Stop the main agent and its helpers");
        StyleComposerAction(_chatSend, stop: false);
        StyleComposerAction(_chatStop, stop: true);
        _chatInput.BorderThickness = new Thickness(0); _chatInput.Background = DeskTheme.Raised; _chatInput.Padding = new Thickness(DeskTheme.Space4,DeskTheme.Space4,DeskTheme.Space4,DeskTheme.Space8);
        _chatInput.Resources["TextControlBorderBrushFocused"] = DeskTheme.Raised;
        _chatInput.Resources["TextControlBackgroundFocused"] = DeskTheme.Raised;
        _chatInput.Resources["TextControlBorderBrushPointerOver"] = DeskTheme.Raised;
        StyleComposerPicker(_chatModel);

        _promptMark = new TextBlock { Text = "›", FontSize = DeskTheme.SizeHeading, Margin = new Thickness(DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space8, 0), Opacity = 1, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
        _promptRow = new Grid();
        _promptRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _promptRow.ColumnDefinitions.Add(new ColumnDefinition());
        _promptRow.Children.Add(_promptMark);
        Grid.SetColumn(_chatInput, 1);
        _promptRow.Children.Add(_chatInput);
        _composerBar = new Grid { ColumnSpacing = DeskTheme.Space8 };
        var composerBar = _composerBar;
        composerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composerBar.ColumnDefinitions.Add(new ColumnDefinition());
        composerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composerBar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Spacing = DeskTheme.Space4, Children = { BuildAttachmentButton(), BuildProjectControls() } });
        _projectBehavior.VerticalAlignment = VerticalAlignment.Center;
        if (_attachFiles is not null) _attachFiles.VerticalAlignment = VerticalAlignment.Center;
        _chatModel.HorizontalAlignment = HorizontalAlignment.Right;
        _chatModel.VerticalAlignment = VerticalAlignment.Center;
        // Reserve the indicator space inside the picker so starting a response does not shift controls.
        _chatModel.Padding = new Thickness(DeskTheme.Space24 + DeskTheme.Space4, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4);
        _composerProgress = new Grid { Width = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false, Children = { _composerSpinner } };
        _modelPickerArea = new Grid { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Children = { _chatModel, _composerProgress } };
        SetComposerStatus("Model for this conversation");
        Grid.SetColumn(_modelPickerArea, 1); composerBar.Children.Add(_modelPickerArea);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = DeskTheme.Space4, Children = { _chatSend, _chatStop } };
        Grid.SetColumn(actions, 2); composerBar.Children.Add(actions);
        _attachmentStatus.Visibility = string.IsNullOrEmpty(_attachmentStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
        _attachmentStatus.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            _attachmentStatus.Visibility = string.IsNullOrEmpty(_attachmentStatus.Text) ? Visibility.Collapsed : Visibility.Visible);
        _composerInner = new StackPanel { Spacing = DeskTheme.Space8, Children = { new ScrollViewer { Content = _attachmentChips, MaxHeight = 125 }, _attachmentStatus, _promptRow, composerBar } };
        _composerShell = new Border { Background = DeskTheme.Raised, CornerRadius = new CornerRadius(DeskTheme.Radius10), Padding = new Thickness(DeskTheme.Space12,DeskTheme.Space8,DeskTheme.Space12,DeskTheme.Space12), Child = _composerInner };
        var composer = _composerShell;
        _composerHint = Note("Enter to send · Shift+Enter for a new line");
        EnsureTuiStatusHost();
        _composerArea = new StackPanel { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch, Spacing = DeskTheme.Space4, Children = { _readingControls!, composer, _composerHint } };
        var composerArea = _composerArea;
        Grid.SetRow(composerArea, 3); _chatPage.Children.Add(composerArea);
        BuildChangesPanel();
        AutomationName(_chatInput, "Chat message"); AutomationName(_chatProvider, "Chat provider"); AutomationName(_chatModel, "Chat model");
        ApplyChatChrome();
    }
    internal void ReloadSavedConnections()
    {
        if (_chatBusy || !XaiApiChat) return;
        _planKey.Password = "";
    }
    private static void StyleComposerPicker(ComboBox picker)
    {
        picker.FontSize = DeskTheme.SizeCode; picker.MinHeight = 32; picker.Padding = new Thickness(DeskTheme.Space8,DeskTheme.Space4,DeskTheme.Space8,DeskTheme.Space4);
        picker.CornerRadius = new CornerRadius(DeskTheme.Radius10); picker.BorderThickness = new Thickness(0); picker.Background = DeskTheme.Hover;
    }
    internal void BindChatModels(IReadOnlyList<(string Id, string? Name)> models, bool editableIds)
    {
        _chatModel.Items.Clear();
        if (editableIds)
        {
            foreach (var model in models) _chatModel.Items.Add(model.Id);
            return;
        }
        foreach (var choice in ChatModelLabel.Assign(models))
        {
            var label = DeskChatConnections.PickerLabel(_chatProvider.SelectedIndex, choice.Label);
            var item = new ComboBoxItem { Content = label, Tag = choice.Id };
            ToolTipService.SetToolTip(item, choice.Details);
            AutomationName(item, label);
            AutomationProperties.SetFullDescription(item, choice.Details);
            _chatModel.Items.Add(item);
        }
        DescribeSelectedModel();
    }
    internal bool HasChatModel(string id) => _chatModel.Items.Cast<object>().Any(item => ReadPicker(item).Id == id);
    internal bool SelectChatModel(string id)
    {
        foreach (var item in _chatModel.Items)
            if (ReadPicker(item).Id == id) { _chatModel.SelectedItem = item; DescribeSelectedModel(); return true; }
        if (PlanChat) { _chatModel.Text = id; return true; }
        return false;
    }
    private static (string? Id, string? Label, string? Details) ReadPicker(object? item) => item switch
    {
        ComboBoxItem box when box.Tag is string id => (id, box.Content as string, ToolTipService.GetToolTip(box) as string),
        ComboBoxItem box when box.Tag is CursorChatModel cursor => (cursor.Id, box.Content as string ?? cursor.ToString(), cursor.Details),
        CursorChatModel cursor => (cursor.Id, cursor.ToString(), cursor.Details),
        string id => (id, ChatModelLabel.Display(id, id), ChatModelLabel.Details(id, id)),
        _ => (null, null, null)
    };
    private void DescribeSelectedModel()
    {
        var label = SelectedChatLabel;
        var details = SelectedChatDetails;
        AutomationName(_chatModel, label is "Agent" or "" ? "Chat model" : "Chat model, " + label);
        if (!string.IsNullOrEmpty(details)) AutomationProperties.SetFullDescription(_chatModel, details);
        if (_modelPickerArea is not null && !(_chatBusy && _chatCancellation is not null))
            ToolTipService.SetToolTip(_modelPickerArea, details ?? "Model for this conversation");
        RefreshTuiFooter();
    }
    internal async Task<bool> StartChatSignInAsync()
    {
        if ((CursorChat || GrokBuildChat) && IsWorking) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        SetChatBusy(true);
        try
        {
            if (XaiApiChat)
            {
                _chatStatus.Text = "Save the xAI API key in Settings, then Connect.";
                return false;
            }
            if (GrokBuildChat)
            {
                _grokBuild?.Dispose(); _grokBuild = null; _chatReady = false;
                _chatStatus.Text = "Complete Grok sign-in, then click Connect.";
                await GrokBuildChatClient.LoginAsync(timeout.Token);
                _chatStatus.Text = "Grok sign-in finished. Click Connect.";
                return true;
            }
            _cursorChat?.Dispose(); _cursorChat = null; _chatReady = false;
            _chatStatus.Text = "Complete Cursor sign-in in your browser. Waiting for Cursor…";
            using var login = new CursorChatClient(Path.Combine(_profileDirectory, "cursor-chat-workspace"));
            await login.LoginAsync(timeout.Token);
            _chatStatus.Text = "Cursor sign-in finished. Click Connect / refresh models.";
            return true;
        }
        catch (Exception ex) { _chatStatus.Text = DeskConnectionErrors.ForConnect(ex, CurrentConnectKind()); return false; }
        finally { SetChatBusy(false); }
    }

    private async Task InstallCursorAsync()
    {
        if (IsWorking)
        {
            _chatStatus.Text = "Finish or stop the current response before installing Cursor.";
            return;
        }
        var plan = CursorInstall.Plan(AppContext.BaseDirectory, CursorChatClient.IsRuntime(CursorChatClient.RuntimeDirectory));
        if (plan.Step == CursorInstallStep.AlreadyInstalled)
        {
            _chatStatus.Text = CursorInstall.AlreadyInstalledStatus;
            RefreshCursorInstallButton();
            return;
        }
        SetChatBusy(true);
        try
        {
            if (plan.Step == CursorInstallStep.OpenPage)
            {
                var opened = plan.Page is not null && await Launcher.LaunchUriAsync(plan.Page);
                _chatStatus.Text = opened ? CursorInstall.PageOpenedStatus : CursorInstall.PageFailedStatus;
                return;
            }
            _chatStatus.Text = "Installing the Cursor connector…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await CursorInstall.RunInstallerAsync(AppContext.BaseDirectory, timeout.Token);
            _chatStatus.Text = CursorChatClient.IsRuntime(CursorChatClient.RuntimeDirectory)
                ? CursorInstall.InstalledStatus
                : CursorInstall.FailedStatus;
        }
        catch (OperationCanceledException)
        {
            _chatStatus.Text = CursorInstall.CanceledStatus;
        }
        catch (Exception)
        {
            _chatStatus.Text = plan.Step == CursorInstallStep.OpenPage
                ? CursorInstall.PageFailedStatus
                : CursorChatClient.IsRuntime(CursorChatClient.RuntimeDirectory)
                    ? CursorInstall.InstalledStatus
                    : CursorInstall.FailedStatus;
        }
        finally { SetChatBusy(false); }
    }

    private void RefreshCursorInstallButton()
    {
        if (_chatInstall is null) return;
        var show = CursorInstall.Offer(CursorChat, CursorChatClient.IsRuntime(CursorChatClient.RuntimeDirectory));
        _chatInstall.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _chatInstall.IsEnabled = show && !_chatBusy;
    }

    private async Task<(string? Url, string? Token, string? Command)> SwarmToolEndpointAsync(CancellationToken ct)
    {
        if (SwarmManagerBrief() is not { Length: > 0 }) return (null, null, null);
        string? url = null;
        string? token = null;
        try
        {
            var host = await SwarmManagerHost.SharedAsync(ct);
            url = host.Url;
            token = host.Token;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            url = null;
            token = null;
        }
        return (url, token, SwarmManagerSession.FindCommand());
    }

    private static string ReadXaiApiKey()
    {
        try
        {
            return DeskVault.ReadPassword(
                Sovereign.Desktop.Core.XaiDeskIdentity.XaiApiCredentialResource,
                Sovereign.Desktop.Core.XaiDeskIdentity.XaiApiCredentialAccount)
                ?? throw new InvalidOperationException(DeskConnectionErrors.XaiKeyMissing);
        }
        catch (InvalidOperationException) { throw; }
        catch { throw new InvalidOperationException(DeskConnectionErrors.XaiKeyMissing); }
    }

    private DeskConnectKind CurrentConnectKind() =>
        XaiApiChat ? DeskConnectKind.XaiApi :
        CursorChat ? DeskConnectKind.Cursor :
        GrokBuildChat ? DeskConnectKind.GrokBuild :
        DeskConnectKind.Other;

    internal async Task ConnectChatAsync()
    {
        var agentConnection = CursorChat || GrokBuildChat || CodexChat;
        if (agentConnection)
        {
            // Open Folder owns its busy marker and deliberately connects inside that operation.
            if (_cursorSendLoop || _codexSendLoop || _cursorConnecting) return;
            _cursorConnecting = true;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_phoneSubmission?.Token ?? CancellationToken.None);
        timeout.CancelAfter(TimeSpan.FromSeconds(50));
        SetChatBusy(true); _chatReady = false;
        try
        {
            _chatStatus.Text = "Connecting…";
            if (_phoneSubmission is not null && _phoneConnectionProbe is not null)
            {
                await _phoneConnectionProbe(timeout.Token);
                return;
            }
            string[] models;
            if (XaiApiChat)
            {
                var key = ReadXaiApiKey();
                _xaiChat?.Dispose();
                _xaiChat = new XaiApiChatClient(key);
                models = await _xaiChat.ModelsAsync(timeout.Token);
                BindChatModels(models.Select(model => (model, (string?)model)).ToArray(), false);
                if (models.Length > 0) _chatModel.SelectedIndex = 0;
                _chatReady = models.Length > 0;
                _chatStatus.Text = _chatReady ? "xAI API connected. Choose a Grok model." : "xAI API did not advertise a Grok model.";
                return;
            }
            if (GrokBuildChat)
            {
                _grokBuild?.Dispose();
                _needsContext = _saved.Turns.Length > 0;
                var epoch = ++_projectSessionEpoch;
                var swarmTools = await SwarmToolEndpointAsync(timeout.Token);
                var grok = new GrokBuildChatClient(_projectFolder ?? Path.Combine(_profileDirectory, "grok-build-workspace"),
                    _projectFolder is null ? null : new CursorProjectOptions {
                        Mode = _saved.ProjectMode, Permissions = _saved.Permissions, EnableSubagents = _saved.SubagentsEnabled,
                        SwarmBrief = SwarmManagerBrief(),
                        SwarmManagerUrl = swarmTools.Url,
                        SwarmManagerToken = swarmTools.Token,
                        SwarmManagerCommand = swarmTools.Command,
                        RequestPermission = RequestProjectPermissionAsync
                    }, activity => { if (epoch == _projectSessionEpoch) { _activeChatTurn?.Observe(activity); _partialActivity.Enqueue(activity); } });
                _grokBuild = grok;
                grok.ResumeSessionId = _saved.GrokBuildSessionId;
                grok.ExitPlanMode = ShowGrokExitPlanCardAsync;
                grok.AskUserQuestion = ShowGrokQuestionCardAsync;
                try
                {
                    var grokModels = DeskChatConnections.GrokModels(await grok.ModelsAsync(timeout.Token));
                    BindChatModels(grokModels.Select(model => (model.Id, (string?)model.Name)).ToArray(), false);
                    if (grokModels.Length > 0) _chatModel.SelectedIndex = 0;
                    _chatReady = grokModels.Length > 0;
                    var grokStatus = _chatReady ? (_projectFolder is null ? "Grok Build connected. Open Folder to work on files."
                        : ProjectConnectionStatus("Grok Build can edit files in this folder.")) : DeskConnectionErrors.GrokNotSignedIn;
                    if (grok.ModeWarning is { Length: > 0 } modeWarning) grokStatus += " " + modeWarning;
                    _chatStatus.Text = grokStatus;
                    return;
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(grok.SessionId))
                        _saved = _saved with { GrokBuildSessionId = grok.SessionId };
                    else if (grok.ClearedResumeSession)
                        _saved = _saved with { GrokBuildSessionId = null };
                }
            }
            if (CursorChat)
            {
                _cursorChat?.Dispose();
                _needsContext = _saved.Turns.Length > 0;
                var epoch = ++_projectSessionEpoch;
                var swarmTools = await SwarmToolEndpointAsync(timeout.Token);
                _cursorChat = new CursorChatClient(_projectFolder ?? Path.Combine(_profileDirectory, "cursor-chat-workspace"),
                    _projectFolder is null ? null : new CursorProjectOptions {
                        Mode = _saved.ProjectMode, Permissions = _saved.Permissions, EnableSubagents = _saved.SubagentsEnabled,
                        SwarmBrief = SwarmManagerBrief(),
                        SwarmManagerUrl = swarmTools.Url,
                        SwarmManagerToken = swarmTools.Token,
                        SwarmManagerCommand = swarmTools.Command,
                        SwarmRoleModelsDirectory = _profileDirectory,
                        SwarmMixedModels = SwarmRoleModels.IsMixed(_saved.SwarmHire),
                        RequestPermission = RequestProjectPermissionAsync
                    }, activity => { if (epoch == _projectSessionEpoch) { _activeChatTurn?.Observe(activity); _partialActivity.Enqueue(activity); } });
                CursorChatModel[]? live = null;
                string? cursorError = null;
                try
                {
                    live = await _cursorChat.ModelsAsync(timeout.Token);
                }
                catch (Exception ex) when (!PhoneSendCancelled)
                {
                    cursorError = DeskConnectionErrors.ForConnect(ex, DeskConnectKind.Cursor);
                    _cursorChat?.Dispose();
                    _cursorChat = null;
                }
                var cached = CursorModelCatalog.Load(_profileDirectory);
                var usingCache = live is not { Length: > 0 };
                var cursorModels = CursorModelCatalog.Resolve(live, cached);
                if (live is { Length: > 0 }) CursorModelCatalog.Save(_profileDirectory, live);
                BindChatModels(cursorModels.Select(model => (model.Id, (string?)model.Name)).ToArray(), false);
                var pick = CursorModelCatalog.SelectDefault(cursorModels, _saved.Model, null);
                if (pick is not null) SelectChatModel(pick);
                _chatReady = !usingCache && cursorModels.Length > 0;
                var cursorStatus = cursorError ?? DeskConnectionErrors.CursorNotSignedIn;
                _chatStatus.Text = _chatReady
                    ? (_projectFolder is null ? "Cursor chat connected. Open Folder to work on files."
                        : ProjectConnectionStatus("Uses your Cursor account. Tools, subagents, and file edits stay in this folder."))
                    : usingCache && cursorModels.Length > 0
                        ? DeskConnectionErrors.WithCachedModels(cursorStatus)
                        : cursorStatus;
                return;
            }
            if(PlanChat)
            {
                var endpoint=_planEndpoint.SelectedItem as string ?? throw new IOException("Select an endpoint.");
                _planChat?.Dispose(); _planChat=new CodingPlanChatClient(PlanProvider,endpoint,_planKey.Password);
                models=await _planChat.ModelsAsync(timeout.Token);
                ConnectionSetup.SaveEndpoint(PlanProvider, endpoint);
                _planKey.Password = "";
                BindChatModels(models.Select(model => (model, (string?)model)).ToArray(), true);
                if(models.Length>0) _chatModel.SelectedIndex=0; else _chatModel.Text="";
                _chatReady=true;
                _chatStatus.Text=models.Length>0 ? "Provider model list received. Choose a model; message delivery is verified when it responds." : "Key saved. This endpoint does not list models; enter the model ID from your plan. Access is not yet verified.";
                return;
            }
            else if (LocalChat) models = await _ollamaChat.ModelsAsync(timeout.Token);
            else
            {
                _codexChat?.Dispose();
                _needsContext = _saved.Turns.Length > 0;
                var epoch = ++_projectSessionEpoch;
                _codexChat = new CodexChatClient(_projectFolder ?? Path.Combine(_profileDirectory, "chat-workspace"),
                    _projectFolder is null ? null : new CursorProjectOptions {
                        Mode = _saved.ProjectMode, Permissions = _saved.Permissions, EnableSubagents = _saved.SubagentsEnabled,
                        SwarmBrief = SwarmManagerBrief(),
                        RequestPermission = RequestProjectPermissionAsync
                    }, activity => { if (epoch == _projectSessionEpoch) { _activeChatTurn?.Observe(activity); _partialActivity.Enqueue(activity); } }, profile: _profileDirectory);
                if (!await _codexChat.IsSubscriptionAsync(timeout.Token)) { _chatStatus.Text = "Click Sign in with ChatGPT. API-key billing will not be used in this room."; return; }
                models = await _codexChat.ModelsAsync(timeout.Token);
            }
            BindChatModels(models.Select(model => (model, (string?)model)).ToArray(), false);
            if (models.Length > 0) _chatModel.SelectedIndex = 0;
            _chatReady = models.Length > 0;
            _chatStatus.Text = _chatReady ? (LocalChat ? "Connected to local Ollama. Choose a downloaded model." : CodexReadyStatus("Uses your ChatGPT subscription limits."))
                : "No models available. Install a local Ollama model or check your Codex account access.";
        }
        catch (OperationCanceledException) when (PhoneSendCancelled) { _chatStatus.Text = "Phone send stopped before dispatch."; }
        catch (Exception ex) { _chatStatus.Text = DeskConnectionErrors.ForConnect(ex, CurrentConnectKind()); }
        finally { if (agentConnection) _cursorConnecting = false; RestoreModelSelection(); if (_chatReady) _reconnectOnSend = false; SetChatBusy(false); Save(); if (_chatReady) _connectionFlyout?.Hide(); if (_chatReady) TryStartQueuedReview(); }
    }
    internal void SetChatBusy(bool busy)
    {
        _chatBusy = busy; _changed();
        _chatProvider.IsEnabled = !busy; _chatModel.IsEnabled = !busy;
        if (_chatConnect is not null) _chatConnect.IsEnabled = !busy;
        if (_chatLogin is not null) _chatLogin.IsEnabled = !busy && (CursorChat || GrokBuildChat);
        RefreshCursorInstallButton();
        _planKey.IsEnabled = !busy; _planEndpoint.IsEnabled = !busy;
        if (_chatNew is not null) _chatNew.IsEnabled = true;
        if (_openProject is not null) _openProject.IsEnabled = !busy;
        if (_closeProject is not null) _closeProject.IsEnabled = !busy && _projectFolder is not null;
        if (_chatSend is not null) _chatSend.IsEnabled = CursorComposerAvailable && !_attachmentBusy && (_chatReady || _reconnectOnSend);
        var responding = busy && _chatCancellation is not null;
        _composerSpinner.IsActive = responding;
        if (_composerProgress is not null) _composerProgress.Visibility = responding ? Visibility.Visible : Visibility.Collapsed;
        if (!_classicChat)
        {
            if (responding) _tuiBusySince ??= DateTimeOffset.Now;
            else { _tuiBusySince = null; _tuiFrame = 0; }
            EnsureTuiClock();
            if (responding) _tuiClock!.Start();
            else _tuiClock!.Stop();
        }
        SetComposerStatus(responding ? "Main agent waiting for response" : "Model for this conversation");
        RefreshAttachmentStatus();
        RefreshComposerAction();
        SetAttachmentBusy(_attachmentBusy);
        UpdateProjectHeader();
    }

    private void RefreshComposerAction()
    {
        var working = (_chatBusy && _chatCancellation is not null) ||
            ((CursorChat || GrokBuildChat) && _cursorSendLoop) || (CodexChat && _codexSendLoop);
        var hasDraft = !string.IsNullOrWhiteSpace(_chatInput.Text) || _draftFiles.Count > 0;
        var followup = working && hasDraft && (CursorChat || GrokBuildChat || CodexChat) && CursorComposerAvailable;
        var showStop = working && !followup;
        if (_chatSend is not null)
        {
            _chatSend.Visibility = showStop ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(_chatSend, followup ? "Send follow-up · Enter" : "Send message · Enter");
            AutomationName(_chatSend, followup ? "Send follow-up to the main agent" : "Send message");
        }
        if (_chatStop is not null) _chatStop.Visibility = showStop ? Visibility.Visible : Visibility.Collapsed;
    }

    private string CodexReadyStatus(string accountNote)
    {
        var existing = _projectFolder is null
            ? "Connected with ChatGPT subscription. Choose a model; sending uses your subscription limits."
            : ProjectConnectionStatus(accountNote);
        var signedIn = DescribeSignedIn(ReadClientProperty(_codexChat, "SubscriptionSignedIn"));
        var tools = DescribeProjectTools(ReadClientProperty(_codexChat, "ProjectToolsStatus"));
        if (signedIn is null && tools is null) return existing;
        var parts = new List<string>();
        if (signedIn is not null) parts.Add(signedIn);
        if (tools is not null) parts.Add(tools);
        parts.Add(existing);
        return string.Join(" · ", parts);
    }

    private static object? ReadClientProperty(object? client, string name)
    {
        var property = client?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        return property is { CanRead: true } ? property.GetValue(client) : null;
    }

    private static string? DescribeSignedIn(object? value) => value switch
    {
        null => null,
        bool signed => signed ? "Signed in" : "Not signed in",
        string text when text.Equals("true", StringComparison.OrdinalIgnoreCase) => "Signed in",
        string text when text.Equals("false", StringComparison.OrdinalIgnoreCase) => "Not signed in",
        string text when text.Trim().Length > 0 => text.Trim(),
        _ => null
    };

    private static string? DescribeProjectTools(object? value)
    {
        if (value is null) return null;
        if (value is bool ready) return ready ? "Project tools ready" : "Project tools failed";
        var text = value.ToString()?.Trim() ?? "";
        if (text.Length == 0 || text.Equals("not-requested", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("ready", StringComparison.OrdinalIgnoreCase) || text.Equals("ok", StringComparison.OrdinalIgnoreCase))
            return "Project tools ready";
        if (text.StartsWith("failed:", StringComparison.OrdinalIgnoreCase))
            return ClipStatus("Project tools failed" + text["failed".Length..]);
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Contains("fail", StringComparison.OrdinalIgnoreCase) || text.Contains("error", StringComparison.OrdinalIgnoreCase))
            return "Project tools failed";
        return ClipStatus("Project tools " + text);
    }

    private static string ClipStatus(string text) => text.Length <= 240 ? text : text[..240];

    private async void ChatInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
        var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        if (_classicChat)
        {
            if (e.Key != VirtualKey.Enter || _chatComposing || shift) return;
            e.Handled = true;
            if (e.KeyStatus.WasKeyDown || !CursorComposerAvailable) return;
            await SendChatAsync();
            return;
        }
        switch (GrokTuiPresentation.Key(e.Key.ToString(), shift, control))
        {
            case GrokTuiKeyAction.NewLine:
                return;
            case GrokTuiKeyAction.Send:
                if (_chatComposing) return;
                e.Handled = true;
                if (e.KeyStatus.WasKeyDown || !CursorComposerAvailable) return;
                await SendChatAsync();
                return;
            case GrokTuiKeyAction.Stop:
                e.Handled = true;
                StopChatResponse();
                return;
            case GrokTuiKeyAction.Recall:
                if (_lastPrompt.Length == 0 || _chatInput.SelectionStart != 0 || _chatInput.SelectionLength != 0) return;
                e.Handled = true;
                _chatInput.Text = _lastPrompt;
                _chatInput.SelectionStart = _chatInput.Text.Length;
                return;
            case GrokTuiKeyAction.ClearView:
                e.Handled = true;
                _chatMessages.Children.Clear();
                return;
        }
    }
    internal async Task SendChatAsync()
    {
        if (CursorChat || GrokBuildChat) { await SendCursorChatAsync(); return; }
        if (CodexChat) { await SendCodexChatAsync(); return; }
        if (_chatBusy || _attachmentBusy) return;
        var text = _chatInput.Text.Trim();
        var model=SelectedChatModel;
        if (text.Length == 0 && _draftFiles.Count == 0) return;
        if (text.Length == 0) text = "Please examine the attached files.";
        _lastPrompt = text;
        if (_reconnectOnSend)
        {
            // Only a new explicit Send reconnects; never replay the canceled request automatically.
            var requestedModel = model ?? _saved.Model;
            _saved = _saved with { Model = requestedModel };
            _needsContext = _saved.Turns.Length > 0;
            await ConnectChatAsync();
            if (_disposed || !_chatReady || PhoneSendCancelled) return; // Keep the draft and attachments on connection failure.
            model = SelectedChatModel;
            if (model != requestedModel)
            {
                _chatStatus.Text = "Your previous model is unavailable. Choose a model before sending again.";
                return;
            }
        }
        if (!_chatReady || string.IsNullOrWhiteSpace(model)) { _chatStatus.Text="Connect and choose or enter a model before sending."; return; }
        if (PhoneSendCancelled) return;
        var sentFiles = _draftFiles.ToArray();
        string sentText; List<ChatImage> images;
        SetChatBusy(true); SetAttachmentBusy(true);
        try { (sentText, images) = await PrepareAttachmentsAsync(text, sentFiles); }
        catch (Exception ex) { _attachmentStatus.Text = ex.Message; return; }
        finally { SetAttachmentBusy(false); SetChatBusy(false); }
        if (_disposed || PhoneSendCancelled) return;
        // Project investigations may take hours. Stop/dispose and transport failures
        // still cancel; elapsed wall time alone must not kill an active agent.
        _chatCancellation = (CodexChat || CursorChat || PlanChat) && _projectFolder is not null
            ? new CancellationTokenSource()
            : new CancellationTokenSource(TimeSpan.FromMinutes(10));
        NoteReviewSendStarted(text);
        SetChatBusy(true); _chatInput.Text = "";
        var local = LocalChat;
        _chatFollowLatest = true;
        _chatMessages.Children.Add(UserMessage(text));
        if (sentFiles.Length > 0) _chatMessages.Children.Add(AttachmentHistory(sentFiles));
        var turn = new ChatActivityView(SelectedChatLabel, UpdateChangesPanel, FollowChatIfNeeded,
            SetComposerStatus,
            () => { _chatFollowLatest = false; if (_chatLatest is not null) _chatLatest.Visibility = Visibility.Visible; }, Path.Combine(_profileDirectory, "chat-activity"), _classicChat);
        _activeChatTurn = turn; TrackChatTurn(turn); _chatMessages.Children.Add(turn.Root);
        FollowChatIfNeeded();
        var restoredPrompt = WithTeamContext(_needsContext ? ChatArchive.RestoreContext(_saved, sentText) : sentText);
        _draftFiles.Clear(); RenderDraftFiles();
        BeginSavedTurn(text, model, sentFiles, sentText);
        var content = new System.Text.StringBuilder();
        _lastOllamaReceipt = null;
        _chatStatus.Text = "Main agent · working. Follow the live activity below. Stop stops the main agent and its helpers and keeps edits already made.";
        var outcome = "completed";
        string? failureReason = null;
        try
        {
            void Delta(string s) { content.Append(s); turn.Append(s); _partialReply.Enqueue(s); }
            if (XaiApiChat)
            {
                var history = _ollamaHistory.Append(new ChatMessage("user", sentText)).ToList();
                await (_xaiChat ?? throw new IOException("Connect the xAI API before sending.")).SendAsync(model, history, Delta, _chatCancellation.Token);
                _ollamaHistory.Add(new("user", sentText));
                _ollamaHistory.Add(new("assistant", content.ToString()));
            }
            else if (local || PlanChat)
            {
                var history = _ollamaHistory.Append(new ChatMessage("user", sentText, images.Select(i => i.Data).ToArray())).ToList();
                if (PlanChat && _projectFolder is not null)
                {
                    var epoch = _projectSessionEpoch;
                    var options = new CursorProjectOptions { Mode = _saved.ProjectMode, Permissions = _saved.Permissions,
                        SwarmBrief = SwarmManagerBrief(),
                        RequestPermission = RequestProjectPermissionAsync,
                        ToolActivity = tool => { if (epoch == _projectSessionEpoch) {
                            var activity = new CursorActivityEvent("tool", Tool: tool);
                            turn.Observe(activity); _partialActivity.Enqueue(activity);
                        } } };
                    await _planChat!.SendProjectAsync(model, history, _projectFolder, options, Delta, _chatCancellation.Token);
                }
                else if(PlanChat) await _planChat!.SendAsync(model, history, Delta, _chatCancellation.Token);
                else await _ollamaChat.SendAsync(model, history, Delta, _chatCancellation.Token, _ollamaQaOptions, receipt => _lastOllamaReceipt = receipt);
                _ollamaHistory.Add(new("user", sentText, images.Select(i => i.Data).ToArray())); _ollamaHistory.Add(new("assistant", content.ToString()));
            }
            else if (CursorChat) { ApplySkillGuidance(restoredPrompt); await _cursorChat!.SendAsync(model, restoredPrompt, Delta, _chatCancellation.Token, images); }
            else { ApplySkillGuidance(restoredPrompt); await _codexChat!.SendAsync(model, restoredPrompt, Delta, _chatCancellation.Token, images); }
            _needsContext = false;
            _chatStatus.Text = (CodexChat || CursorChat) && _projectFolder is not null ? "Response complete. Review Changed files in this turn." : "Response complete.";
        }
        catch (OperationCanceledException) { outcome = "stopped"; _chatStatus.Text = "Response stopped or timed out. Partial text is incomplete. File edits already made are kept. You can edit the draft and send again."; if (!local && !PlanChat) { _reconnectOnSend = true; _needsContext = true; _projectSessionEpoch++; _chatReady = false; _codexChat?.Dispose(); _cursorChat?.Dispose(); _codexChat = null; _cursorChat = null; } }
        catch (Exception ex) { failureReason = ProjectAgentException.Describe(ex); turn.ShowFailure(failureReason); outcome = "failed"; _chatStatus.Text = "Response failed: " + ex.Message; if (CursorChat || CodexChat) { _reconnectOnSend = true; _needsContext = true; _projectSessionEpoch++; _chatReady = false; _cursorChat?.Dispose(); _cursorChat = null; _codexChat?.Dispose(); _codexChat = null; } }
        finally
        {
            if (outcome != "completed" && string.IsNullOrWhiteSpace(_chatInput.Text)) { _chatInput.Text = text; _draftFiles.AddRange(sentFiles); RenderDraftFiles(); }
            var reply = content.ToString();
            var review = ArmHandoffReview(reply, outcome);
            try
            {
                try { turn.Finish(outcome); FinishSavedTurn(reply, outcome, turn, failureReason); }
                finally { FinishSubagents(outcome); }
            }
            finally { ReleaseHandoffReviewHold(); }
            _activeChatTurn = null; _chatCancellation?.Dispose(); _chatCancellation = null;
            if (review) await AwaitHandoffReviewAsync(reply);
            SetChatBusy(false);
            TryStartQueuedReview();
        }
    }

    private CancellationTokenSource? _phoneSubmission;
    private bool PhoneSendCancelled => _phoneSubmission?.IsCancellationRequested == true;
    private Func<CancellationToken, Task>? _phoneConnectionProbe;

    internal async Task<string?> SendFromPhoneAsync(string text)
    {
        if (_disposed || _deleted) return "This conversation is closed.";
        if (IsWorking || _acceptingCursorInstruction || _phoneSubmission is not null) return "This chat is already working. Stop it before sending.";
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return "Write a message first.";
        if (!string.IsNullOrWhiteSpace(_chatInput.Text)) return "This chat has an unsent draft on the desktop.";
        _chatInput.Text = trimmed;
        _phoneSubmission = new CancellationTokenSource();
        _ = CompletePhoneSendAsync(_phoneSubmission);
        await Task.Yield();
        return null;
    }

    private async Task CompletePhoneSendAsync(CancellationTokenSource submission)
    {
        try { await SendChatAsync(); }
        catch (Exception ex) { _chatStatus.Text = "Response failed: " + ex.Message; }
        finally
        {
            if (ReferenceEquals(_phoneSubmission, submission)) _phoneSubmission = null;
            submission.Dispose();
        }
    }

    private void FollowChatIfNeeded()
    {
        if (!_chatFollowLatest || _chatScroll is null) return;
        _chatScroll.UpdateLayout();
        _chatScroll.ChangeView(null, _chatScroll.ScrollableHeight, null, true);
    }

    private Task<GrokBuildPlanAnswer> ShowGrokExitPlanCardAsync(GrokBuildPlanCard card, CancellationToken ct)
    {
        var done = new TaskCompletionSource<GrokBuildPlanAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => done.TrySetResult(GrokBuildPlanAnswer.Cancelled()));
        _ = done.Task.ContinueWith(_ => registration.Dispose());
        if (!_owner.DispatcherQueue.TryEnqueue(() =>
        {
            try { MountExitPlanCard(card, done); }
            catch (Exception) { done.TrySetResult(GrokBuildPlanAnswer.Cancelled()); }
        }))
            done.TrySetResult(GrokBuildPlanAnswer.Cancelled());
        return done.Task;
    }

    private Task<GrokBuildQuestionAnswer> ShowGrokQuestionCardAsync(GrokBuildQuestionCard card, CancellationToken ct)
    {
        var done = new TaskCompletionSource<GrokBuildQuestionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => done.TrySetResult(GrokBuildQuestionAnswer.Cancelled()));
        _ = done.Task.ContinueWith(_ => registration.Dispose());
        if (!_owner.DispatcherQueue.TryEnqueue(() =>
        {
            try { MountQuestionCard(card, done); }
            catch (Exception) { done.TrySetResult(GrokBuildQuestionAnswer.Cancelled()); }
        }))
            done.TrySetResult(GrokBuildQuestionAnswer.Cancelled());
        return done.Task;
    }

    private void MountExitPlanCard(GrokBuildPlanCard card, TaskCompletionSource<GrokBuildPlanAnswer> done)
    {
        if (done.Task.IsCompleted) return;
        var body = string.IsNullOrWhiteSpace(card.Plan) ? "Grok Build left plan mode without plan text." : card.Plan;
        if (body.Length > 20000) body = body[..20000] + "\n[Plan display truncated]";
        var status = CardStatus("Grok Build is waiting for this plan.");
        var controls = new List<Control>();
        void Finish(GrokBuildPlanAnswer answer, string label)
        {
            if (!done.TrySetResult(answer)) return;
            status.Text = label;
            foreach (var control in controls) control.IsEnabled = false;
        }
        var approve = Button("Approve plan", () => Finish(GrokBuildPlanAnswer.Approved(), "Plan approved."));
        var cancel = Button("Cancel plan", () => Finish(GrokBuildPlanAnswer.Cancelled(), "Plan cancelled."));
        controls.Add(approve);
        controls.Add(cancel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8 };
        buttons.Children.Add(approve);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Spacing = DeskTheme.Space12 };
        panel.Children.Add(CardTitle("Exit plan mode"));
        panel.Children.Add(new ScrollViewer { MaxHeight = 280, Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Text } });
        panel.Children.Add(status);
        panel.Children.Add(buttons);
        AddGrokCard(panel, "Grok Build exit plan");
    }

    private void MountQuestionCard(GrokBuildQuestionCard card, TaskCompletionSource<GrokBuildQuestionAnswer> done)
    {
        if (done.Task.IsCompleted) return;
        var answers = new Dictionary<string, object>(StringComparer.Ordinal);
        var controls = new List<Control>();
        var status = CardStatus("Grok Build is waiting for your answer.");
        void Finish(GrokBuildQuestionAnswer answer, string label)
        {
            if (!done.TrySetResult(answer)) return;
            status.Text = label;
            foreach (var control in controls) control.IsEnabled = false;
        }
        var panel = new StackPanel { Spacing = DeskTheme.Space12 };
        panel.Children.Add(CardTitle("Question"));
        foreach (var question in card.Questions)
        {
            panel.Children.Add(new TextBlock { Text = question.Question, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeBody, Foreground = DeskTheme.Text });
            if (question.Options.Length == 0)
            {
                var box = new TextBox { PlaceholderText = "Your answer", AcceptsReturn = question.MultiSelect, TextWrapping = TextWrapping.Wrap };
                controls.Add(box);
                box.TextChanged += (_, _) =>
                {
                    if (question.MultiSelect)
                    {
                        var lines = box.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (lines.Length == 0) answers.Remove(question.Question);
                        else answers[question.Question] = lines;
                    }
                    else if (string.IsNullOrWhiteSpace(box.Text)) answers.Remove(question.Question);
                    else answers[question.Question] = box.Text.Trim();
                };
                panel.Children.Add(box);
                continue;
            }
            if (question.MultiSelect)
            {
                var picked = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in question.Options)
                {
                    var label = option;
                    var box = new CheckBox { Content = label };
                    controls.Add(box);
                    box.Checked += (_, _) => { picked.Add(label); answers[question.Question] = picked.ToArray(); };
                    box.Unchecked += (_, _) =>
                    {
                        picked.Remove(label);
                        if (picked.Count == 0) answers.Remove(question.Question);
                        else answers[question.Question] = picked.ToArray();
                    };
                    panel.Children.Add(box);
                }
                continue;
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8 };
            foreach (var option in question.Options)
            {
                var label = option;
                var choice = Button(label, () => { answers[question.Question] = label; status.Text = "Selected · " + label; });
                controls.Add(choice);
                row.Children.Add(choice);
            }
            panel.Children.Add(row);
        }
        var send = Button("Send answers", () =>
        {
            if (card.Questions.Any(question => !answers.ContainsKey(question.Question)))
            {
                status.Text = "Choose an answer for each question.";
                return;
            }
            Finish(GrokBuildQuestionAnswer.Accepted(new Dictionary<string, object>(answers)), "Answer sent.");
        });
        var cancel = Button("Cancel question", () => Finish(GrokBuildQuestionAnswer.Cancelled(), "Question cancelled."));
        controls.Add(send);
        controls.Add(cancel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DeskTheme.Space8 };
        buttons.Children.Add(send);
        buttons.Children.Add(cancel);
        panel.Children.Add(status);
        panel.Children.Add(buttons);
        AddGrokCard(panel, "Grok Build question");
    }

    private static TextBlock CardTitle(string text) => new()
    {
        Text = text, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeHeading, Foreground = DeskTheme.Text
    };

    private static TextBlock CardStatus(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontFamily = DeskTheme.UiFont, FontSize = DeskTheme.SizeCaption, Foreground = DeskTheme.Muted
    };

    private void AddGrokCard(UIElement body, string name)
    {
        var border = new Border
        {
            Background = DeskTheme.Surface,
            BorderBrush = DeskTheme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(DeskTheme.Radius10),
            Padding = new Thickness(DeskTheme.Space16),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = body
        };
        AutomationName(border, name);
        _chatFollowLatest = true;
        _chatMessages.Children.Add(border);
        FollowChatIfNeeded();
    }

    internal void ApplyClassicChatSetting()
    {
        _classicChat = ClassicChatPreference.Load(_profileDirectory);
        ApplyChatChrome();
        ApplySubagentChrome(_classicChat);
        ApplyLineReviewChrome(_classicChat);
        foreach (var turn in _chatTurns) turn.ApplyClassicChrome(_classicChat);
    }

    private void ApplyChatChrome()
    {
        var wide = double.PositiveInfinity;
        if (_chatHeading is not null) _chatHeading.MaxWidth = _classicChat ? 860 : wide;
        if (_chatConversationHost is not null) _chatConversationHost.MaxWidth = _classicChat ? 860 : wide;
        if (_composerArea is not null) _composerArea.MaxWidth = _classicChat ? 860 : wide;
        _chatPage.Background = _classicChat ? null : DeskTheme.Background;
        _chatTitleActions.Visibility = Visibility.Collapsed;
        _chatMessages.Spacing = DeskTheme.Space16;
        _chatMessages.Padding = new Thickness(0, _classicChat ? DeskTheme.Space16 : DeskTheme.Space8, 0, _classicChat ? DeskTheme.Space24 + DeskTheme.Space24 + DeskTheme.Space8 : DeskTheme.Space12);
        var transcriptFont = _classicChat ? DeskTheme.UiFont : DeskTheme.MonoFont;
        _projectDescription.FontFamily = transcriptFont;
        if (_tuiContext is not null) _tuiContext.FontFamily = transcriptFont;
        if (_promptMark is not null) _promptMark.FontFamily = transcriptFont;
        _liveStatus.FontFamily = transcriptFont;
        if (_tuiContext is not null) _tuiContext.Visibility = _classicChat ? Visibility.Collapsed : Visibility.Visible;
        if (_promptMark is not null) _promptMark.Visibility = _classicChat ? Visibility.Collapsed : Visibility.Visible;
        if (_composerBar is not null)
        {
            _composerBar.Visibility = Visibility.Visible;
            if (_composerBar.Children.Count > 2 && _composerBar.Children[2] is UIElement sendActions)
                sendActions.Visibility = Visibility.Visible;
            if (_chatSend is not null) StyleComposerAction(_chatSend, stop: false);
            if (_chatStop is not null) StyleComposerAction(_chatStop, stop: true);
        }
        StyleComposerChrome();
        PlaceComposerBar();
        ApplyPendingChrome();
        if (_composerShell is not null)
        {
            _composerShell.CornerRadius = _classicChat ? new CornerRadius(DeskTheme.Radius10) : new CornerRadius(0);
            _composerShell.BorderThickness = _classicChat ? new Thickness(0) : new Thickness(1);
            _composerShell.BorderBrush = DeskTheme.Divider;
            _composerShell.Background = _classicChat ? DeskTheme.Raised : DeskTheme.Background;
            _composerShell.Padding = _classicChat ? new Thickness(14, 10, 14, 12) : new Thickness(8, 2, 8, 2);
        }
        _chatInput.FontFamily = _classicChat ? DeskTheme.UiFont : TuiFont;
        _chatInput.PlaceholderText = _classicChat ? "Message your agent…" : "";
        _chatInput.MinHeight = _classicChat ? 44 : 28;
        _chatInput.Background = _classicChat ? DeskTheme.Raised : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _chatInput.BorderThickness = new Thickness(0);
        if (_tuiTokens is not null) _tuiTokens.Visibility = _classicChat ? Visibility.Collapsed : Visibility.Visible;
        if (_tuiStop is not null) _tuiStop.Visibility = Visibility.Collapsed;
        if (_tuiStatusHost is not null && !(_chatBusy && _chatCancellation is not null)) _tuiStatusHost.Visibility = Visibility.Collapsed;
        RefreshTuiFooter();
        UpdateProjectHeader();
        RefreshComposerAction();
    }

    private void EnsureTuiStatusHost()
    {
        if (_tuiStatusHost is not null) return;
        _tuiTokens = new TextBlock { FontSize = DeskTheme.SizeCaption, Opacity = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(DeskTheme.Space12, 0, DeskTheme.Space8, 0), FontFamily = TuiFont };
        _tuiStop = new Button
        {
            Content = "[stop]", FontSize = DeskTheme.SizeCaption, Padding = new Thickness(DeskTheme.Space4, 0, DeskTheme.Space4, 0), MinHeight = 24,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0), Opacity = 1, FontFamily = TuiFont
        };
        _tuiStop.Click += (_, _) => StopChatResponse();
        AutomationName(_tuiStop, "Stop the main agent and its helpers");
        _liveStatus.FontFamily = TuiFont;
        _liveStatus.Opacity = 1;
        _tuiStatusHost = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(DeskTheme.Space4, 0, DeskTheme.Space4, DeskTheme.Space4) };
        _tuiStatusHost.ColumnDefinitions.Add(new ColumnDefinition());
        _tuiStatusHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _tuiStatusHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _liveStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _liveStatusRow.ColumnDefinitions.Add(new ColumnDefinition());
        _liveStatusRow.Children.Add(_liveIndicator);
        Grid.SetColumn(_liveStatus, 1); _liveStatusRow.Children.Add(_liveStatus);
        var pulse = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 1, To = .35, Duration = new Duration(TimeSpan.FromMilliseconds(780)), AutoReverse = true,
            RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(pulse, _liveIndicator);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(pulse, "Opacity");
        _livePulse.Children.Add(pulse);
        var ownedRow = _liveStatusRow;
        var ownedPulse = _livePulse;
        ownedRow.Loaded += (_, _) => { if (ReferenceEquals(_liveStatusRow, ownedRow)) SetLiveIndicator(_liveIndicatorWorking); };
        ownedRow.Unloaded += (_, _) => { ownedPulse.Stop(); if (ReferenceEquals(_livePulse, ownedPulse)) _livePulseRunning = false; };
        _tuiStatusHost.Children.Add(_liveStatusRow);
        Grid.SetColumn(_tuiTokens, 1); _tuiStatusHost.Children.Add(_tuiTokens);
        Grid.SetColumn(_tuiStop, 2); _tuiStatusHost.Children.Add(_tuiStop);
    }

    private void EnsureTuiClock()
    {
        _tuiClock ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        if (_tuiClockHooked) return;
        _tuiClockHooked = true;
        _tuiClock.Tick += (_, _) => RefreshTuiStatus();
    }

    private void RefreshTuiStatus()
    {
        if (_classicChat || _tuiStatusHost is null) return;
        var responding = _chatBusy && _chatCancellation is not null && _tuiBusySince is not null;
        PlaceLiveStatus();
        SetLiveIndicator(responding);
        _tuiStatusHost.Visibility = responding ? Visibility.Visible : Visibility.Collapsed;
        if (_tuiTokens is not null) _tuiTokens.Visibility = Visibility.Visible;
        if (_tuiStop is not null) _tuiStop.Visibility = Visibility.Collapsed;
        if (!responding)
        {
            _liveStatus.Text = "";
            _liveStatus.Visibility = Visibility.Collapsed;
            return;
        }
        var elapsed = DateTimeOffset.Now - _tuiBusySince!.Value;
        var estimate = _activeChatTurn?.DisplayTokenEstimate ?? 0;
        var status = GrokTuiPresentation.Status(elapsed, estimate > 0 ? estimate : null, _tuiFrame++);
        _liveStatus.Text = status.Left;
        _liveStatus.Visibility = Visibility.Visible;
        if (_tuiTokens is not null) _tuiTokens.Text = status.Right;
        if (_tuiStop is not null) _tuiStop.Content = status.Stop;
    }

    private void PlaceLiveStatus()
    {
        var target = _activeChatTurn?.Root;
        if (ReferenceEquals(_liveStatusOwner, target)) return;
        if (_tuiStatusHost is not null) _tuiStatusHost.Visibility = Visibility.Collapsed;
        _livePulse.Stop(); _livePulseRunning = false;
        _liveStatusOwner = target;
        if (target is null) return;
        // Each response owns its own status controls. Never reparent an animated
        // WinUI element from a previous response while its visual tree unloads.
        _liveStatus = new TextBlock { FontSize = DeskTheme.SizeCaption, Opacity = 1, MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(DeskTheme.Space4, 0, DeskTheme.Space4, DeskTheme.Space4) };
        _liveIndicator = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = DeskTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _liveStatusRow = new Grid { ColumnSpacing = DeskTheme.Space8, Visibility = Visibility.Collapsed };
        _livePulse = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        _tuiStatusHost = null;
        EnsureTuiStatusHost();
        target.Children.Insert(0, _tuiStatusHost);
    }

    private void SetLiveIndicator(bool working)
    {
        _liveIndicatorWorking = working;
        _liveStatusRow.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        if (working && _liveStatusRow.IsLoaded)
        {
            if (_livePulseRunning) return;
            _livePulse.Begin();
            _livePulseRunning = true;
        }
        else
        {
            if (_livePulseRunning) _livePulse.Stop();
            _livePulseRunning = false;
            _liveIndicator.Opacity = 1;
        }
    }

    private void StyleComposerChrome()
    {
        if (_classicChat)
        {
            _projectBehavior.Resources.Clear();
            _chatModel.Resources.Clear();
            _projectBehavior.ClearValue(Control.ForegroundProperty);
            _chatModel.ClearValue(Control.ForegroundProperty);
            _projectBehavior.ClearValue(Control.BorderBrushProperty);
            _chatModel.ClearValue(Control.BorderBrushProperty);
            StyleComposerPicker(_projectBehavior);
            StyleComposerPicker(_chatModel);
            _projectBehavior.Width = 156;
            _chatModel.MinWidth = 150;
            _chatModel.Padding = new Thickness(DeskTheme.Space24 + DeskTheme.Space4, DeskTheme.Space4, DeskTheme.Space8, DeskTheme.Space4);
        }
        else
        {
            FlattenPicker(_projectBehavior, 2);
            FlattenPicker(_chatModel, 22);
            _projectBehavior.Width = double.NaN;
            _projectBehavior.MinWidth = 0;
            _chatModel.MinWidth = 0;
        }
        StyleAttachButton(!_classicChat);
    }

    private static void StyleComposerAction(Button button, bool stop)
    {
        button.Width = 36;
        button.Height = 34;
        button.MinWidth = button.MinHeight = 0;
        button.Padding = new Thickness(DeskTheme.Radius6);
        button.CornerRadius = new CornerRadius(17);
        button.BorderThickness = new Thickness(0);
        button.Background = DeskTheme.Hover;
        button.Foreground = DeskTheme.Text;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.Content = stop ? new SymbolIcon(Symbol.Stop) : ComposerSendArrow();
        button.Resources["ButtonBackground"] = DeskTheme.Hover;
        button.Resources["ButtonBackgroundPointerOver"] = DeskTheme.Pressed;
        button.Resources["ButtonBackgroundPressed"] = DeskTheme.Surface;
        button.Resources["ButtonBackgroundDisabled"] = DeskTheme.Raised;
        button.Resources["ButtonForeground"] = DeskTheme.Text;
        button.Resources["ButtonForegroundPointerOver"] = DeskTheme.Text;
        button.Resources["ButtonForegroundPressed"] = DeskTheme.Text;
        button.Resources["ButtonForegroundDisabled"] = DeskTheme.Muted;
        button.UseSystemFocusVisuals = true;
        button.FocusVisualPrimaryBrush = DeskTheme.Accent;
        button.FocusVisualSecondaryBrush = DeskTheme.Background;
    }

    private static UIElement ComposerSendArrow()
    {
        // Draw the paper-plane arrow directly; theme font changes cannot turn it into a missing-glyph box.
        var outline = new Microsoft.UI.Xaml.Media.PathFigure { StartPoint = new Windows.Foundation.Point(2, 2), IsClosed = true, IsFilled = false };
        outline.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Windows.Foundation.Point(19, 10) });
        outline.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Windows.Foundation.Point(2, 18) });
        outline.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Windows.Foundation.Point(6, 10) });
        var shaft = new Microsoft.UI.Xaml.Media.PathFigure { StartPoint = new Windows.Foundation.Point(6, 10), IsFilled = false };
        shaft.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Windows.Foundation.Point(19, 10) });
        var geometry = new Microsoft.UI.Xaml.Media.PathGeometry();
        geometry.Figures.Add(outline); geometry.Figures.Add(shaft);
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry, Width = 20, Height = 20, Stroke = DeskTheme.Text, StrokeThickness = 1.5,
            StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round, Stretch = Microsoft.UI.Xaml.Media.Stretch.None,
            IsHitTestVisible = false
        };
    }

    private static void FlattenPicker(ComboBox picker, double leftPad)
    {
        var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var dim = DeskTheme.Muted;
        picker.FontFamily = TuiFont;
        picker.FontSize = DeskTheme.SizeCaption;
        picker.MinHeight = 24;
        picker.Padding = new Thickness(leftPad, 0, DeskTheme.Space4, 0);
        picker.CornerRadius = new CornerRadius(0);
        picker.BorderThickness = new Thickness(0);
        picker.Background = transparent;
        picker.BorderBrush = transparent;
        picker.Foreground = dim;
        void Set(string key, object value) => picker.Resources[key] = value;
        Set("ComboBoxBackground", transparent);
        Set("ComboBoxBackgroundPointerOver", DeskTheme.Raised);
        Set("ComboBoxBackgroundPressed", DeskTheme.Raised);
        Set("ComboBoxBackgroundDisabled", transparent);
        Set("ComboBoxBackgroundFocused", transparent);
        Set("ComboBoxBorderBrush", transparent);
        Set("ComboBoxBorderBrushPointerOver", transparent);
        Set("ComboBoxBorderBrushPressed", transparent);
        Set("ComboBoxBorderBrushDisabled", transparent);
        Set("ComboBoxBorderBrushFocused", transparent);
        Set("ComboBoxForeground", dim);
        Set("ComboBoxForegroundPointerOver", dim);
        Set("ComboBoxForegroundPressed", dim);
        Set("ComboBoxForegroundFocused", dim);
        Set("ComboBoxForegroundDisabled", DeskTheme.Muted);
        Set("ComboBoxDropDownGlyphForeground", DeskTheme.Muted);
        Set("ComboBoxDropDownGlyphForegroundDisabled", DeskTheme.Muted);
        Set("ComboBoxPlaceHolderForeground", DeskTheme.Muted);
        Set("ComboBoxPlaceHolderForegroundFocused", DeskTheme.Muted);
        Set("ComboBoxPlaceHolderForegroundPointerOver", DeskTheme.Muted);
    }

    private void StyleAttachButton(bool tui)
    {
        if (_attachFiles is null) return;
        if (!tui)
        {
            _attachFiles.Resources.Clear();
            _attachFiles.ClearValue(Control.BackgroundProperty);
            _attachFiles.ClearValue(Control.BorderThicknessProperty);
            _attachFiles.ClearValue(Control.ForegroundProperty);
            _attachFiles.ClearValue(Control.PaddingProperty);
            _attachFiles.Width = 34;
            _attachFiles.Height = 32;
            _attachFiles.MinWidth = 0;
            _attachFiles.MinHeight = 0;
            _attachFiles.CornerRadius = new CornerRadius(DeskTheme.Radius10);
            _attachFiles.FontFamily = DeskTheme.UiFont;
            _attachFiles.FontSize = DeskTheme.SizeBody;
            return;
        }
        var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var dim = DeskTheme.Muted;
        _attachFiles.Width = double.NaN;
        _attachFiles.Height = double.NaN;
        _attachFiles.MinWidth = 24;
        _attachFiles.MinHeight = 24;
        _attachFiles.CornerRadius = new CornerRadius(0);
        _attachFiles.Padding = new Thickness(DeskTheme.Space4, 0, DeskTheme.Space4, 0);
        _attachFiles.Background = transparent;
        _attachFiles.BorderThickness = new Thickness(0);
        _attachFiles.FontFamily = TuiFont;
        _attachFiles.FontSize = DeskTheme.SizeHeading;
        _attachFiles.Foreground = dim;
        _attachFiles.Resources["ButtonBackground"] = transparent;
        _attachFiles.Resources["ButtonBackgroundPointerOver"] = DeskTheme.Raised;
        _attachFiles.Resources["ButtonBackgroundPressed"] = DeskTheme.Raised;
        _attachFiles.Resources["ButtonBackgroundDisabled"] = transparent;
        _attachFiles.Resources["ButtonBorderBrush"] = transparent;
        _attachFiles.Resources["ButtonBorderBrushPointerOver"] = transparent;
        _attachFiles.Resources["ButtonBorderBrushPressed"] = transparent;
        _attachFiles.Resources["ButtonBorderBrushFocused"] = transparent;
        _attachFiles.Resources["ButtonForeground"] = dim;
        _attachFiles.Resources["ButtonForegroundPointerOver"] = DeskTheme.Text;
    }

    private void PlaceComposerBar()
    {
        if (_composerBar is null || _composerInner is null || _composerArea is null || _composerHint is null) return;
        if (!_classicChat && _composerBar.Parent == _composerInner)
        {
            _composerInner.Children.Remove(_composerBar);
            var index = _composerArea.Children.IndexOf(_composerHint);
            if (index < 0) _composerArea.Children.Add(_composerBar);
            else _composerArea.Children.Insert(index, _composerBar);
        }
        else if (_classicChat && _composerBar.Parent == _composerArea)
        {
            _composerArea.Children.Remove(_composerBar);
            _composerInner.Children.Add(_composerBar);
        }
        _composerBar.Margin = new Thickness(0);
    }

    private void ApplyPendingChrome()
    {
        foreach (var view in _pendingInstructionViews.Values)
            StylePendingRow(view.Preview, view.Status, view.Actions);
    }

    private void StylePendingRow(TextBlock preview, TextBlock status, StackPanel actions)
    {
        var font = _classicChat ? DeskTheme.UiFont : TuiFont;
        preview.FontFamily = font;
        preview.FontSize = _classicChat ? 13 : 12;
        preview.Opacity = _classicChat ? 1 : .65;
        status.FontFamily = font;
        status.FontSize = DeskTheme.SizeCaption;
        status.Foreground = DeskTheme.Muted;
        status.Opacity = 1;
        foreach (var button in actions.Children.OfType<Button>())
            StylePendingButton(button);
    }

    private void StylePendingButton(Button button)
    {
        var name = AutomationProperties.GetName(button);
        var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var dim = DeskTheme.Muted;
        button.Background = transparent;
        button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(DeskTheme.Space4, 0, DeskTheme.Space4, 0);
        button.MinWidth = 0;
        button.Height = DeskTheme.Space24;
        button.MinHeight = 0;
        button.FontFamily = _classicChat ? DeskTheme.UiFont : TuiFont;
        button.FontSize = DeskTheme.SizeCaption;
        button.Opacity = 1;
        button.Foreground = dim;
        button.Resources["ButtonBackground"] = transparent;
        button.Resources["ButtonBackgroundPointerOver"] = DeskTheme.Hover;
        button.Resources["ButtonBackgroundPressed"] = DeskTheme.Hover;
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Resources["ButtonBorderBrushFocused"] = transparent;
        button.Resources["ButtonForeground"] = dim;
        button.Resources["ButtonForegroundPointerOver"] = DeskTheme.Text;
        button.Resources["ButtonForegroundPressed"] = DeskTheme.Text;
        button.Content = name switch
        {
            "Resume main agent" => "Resume",
            "Edit in composer" => "Edit",
            "Remove pending instruction" => "Remove",
            _ => button.Content
        };
    }

    private void RefreshTuiFooter()
    {
        if (_composerHint is null) return;
        if (_classicChat)
        {
            _composerHint.Text = "Enter to send · Shift+Enter for a new line";
            _composerHint.Opacity = 1;
            _composerHint.FontFamily = DeskTheme.UiFont;
            return;
        }
        _composerHint.Text = "Enter sends · Shift+Enter newline · Esc stops · ↑ recalls";
        _composerHint.Opacity = 1;
        _composerHint.FontFamily = TuiFont;
        if (_tuiContext is null) return;
        long chars = 0;
        foreach (var turn in _saved.Turns) chars += turn.Prompt.Length + turn.Reply.Length;
        if (_activeChatTurn is not null) chars += _activeChatTurn.DisplayTokenEstimate * 4L;
        _tuiContext.Text = GrokTuiPresentation.ContextLabel(chars / 4, GrokTuiPresentation.ContextLimit(SelectedChatModel));
        _tuiContext.FontFamily = TuiFont;
    }
}
