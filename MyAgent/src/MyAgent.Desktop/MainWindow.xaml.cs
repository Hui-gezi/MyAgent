using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyAgent.App;
using MyAgent.Sessions;

namespace MyAgent.Desktop;

public partial class MainWindow : Window
{
    private readonly SessionStore _store = CreateStore();
    private readonly SettingsService _settings;

    /// <summary>启动时解析数据目录：MYAGENT_HOME（测试隔离等显式指定，无条件生效）
    /// > settings.json 的 dataHome（可写校验失败则回退锚点，设置页仍显示配置值）
    /// > 配置锚点 ~/.myagent。settings.json 永远住在锚点，不随数据目录移动。
    /// 生效目录确定后、SessionStore 打开之前先做数据迁移（旧目录合并进新目录），
    /// 此时没有任何会话在运行/写文件，迁移是安全的。</summary>
    private static SessionStore CreateStore()
    {
        var anchor = SessionStore.DefaultHome;
        var settings = new SettingsService(anchor);
        var env = Environment.GetEnvironmentVariable("MYAGENT_HOME");
        string dataHome;
        if (!string.IsNullOrEmpty(env)) dataHome = env;
        else if (settings.Current.DataHome is { Length: > 0 } configured &&
                 SettingsService.TryProbeWritable(configured, out _))
            dataHome = configured;
        else dataHome = anchor; // 盘符不在/权限丢失：回退默认，别让应用起不来
        DataHomeMigration.Run(anchor, dataHome, settings);
        return new SessionStore(anchor, dataHome);
    }
    private readonly WorkspaceService _workspaces;
    private readonly SessionManager _manager;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _autoScroll;
    private bool _nearBottom = true;
    private bool _programmaticScroll;

    /// <summary>聊天滚动宿主：虚拟化改造后位于 ChatList 模板内，构造时解析一次。
    /// 原有贴底跟随/滚轮转发/ScrollToEnd 逻辑全部继续作用于这个实例。</summary>
    private ScrollViewer ChatScroll = null!;

    public MainWindow()
    {
        InitializeComponent();
        ChatList.ApplyTemplate();
        ChatScroll = (ScrollViewer)ChatList.Template.FindName("ChatScroll", ChatList);
        _settings = new SettingsService(_store.Home);
        _workspaces = new WorkspaceService(_store.Home);
        _manager = new SessionManager(_store, _settings);
        _vm = new MainViewModel(_manager, _store, _settings, _workspaces);
        DataContext = _vm;
        App.ApplyTheme(_settings.Current.Theme);

        // sendKey 设置变更（设置窗口保存 / 其他 Update 路径）即时刷新发送按钮提示。
        _settings.Changed += UpdateSendKeyHint;
        UpdateSendKeyHint();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsSidebarCollapsed) or nameof(MainViewModel.IsRightbarOpen))
                UpdateColumns();
            if (e.PropertyName is nameof(MainViewModel.CurrentChat))
            {
                HookChat();
                UpdateRows();
                // 切换会话：恢复贴底跟随，并在布局完成后定位到最新消息。
                _nearBottom = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollToEndSticky);
            }
        };

        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _autoScroll.Tick += (_, _) =>
        {
            if (_vm.CurrentChat?.IsRunning == true && _nearBottom) ScrollToEndSticky();
        };

        ChatScroll.ScrollChanged += (_, e) =>
        {
            // 只有“用户驱动的偏移变化”才重新判定是否贴底。
            // 内容增长（ExtentHeightChange）或视口变化（如首次对话从空态布局切换到聊天布局）
            // 也会触发 ScrollChanged，此时 offset 不动而 ScrollableHeight 暴涨；
            // 若在这里重算 _nearBottom，会被误判为“用户离开了底部”，导致首次对话自动跟随永远失效。
            if (e.VerticalChange != 0 && !_programmaticScroll)
                _nearBottom = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 120;

            // 贴底状态下内容一增长立即跟到底部（不等 200ms 定时器，避免滚动条停留在顶部不动）。
            // 只在 extent 增长时吸附：虚拟化会随容器实现/回收重估 extent（可能瞬时缩水），
            // 若对缩水也吸附，用户跳往顶部的途中会被拉回底部。
            if ((e.ExtentHeightChange > 0 || e.ViewportHeightChange != 0) && _nearBottom)
                ScrollToEndSticky();
        };
        ChatScroll.PreviewMouseWheel += ChatScroll_PreviewMouseWheel;

        Loaded += OnLoaded;
        Closed += (_, _) => _manager.Dispose();
        // 无边框窗口最大化按显示器工作区铺满（WM_GETMINMAXINFO），
        // 系统任务栏不再压住底部界面；旧的 RootGrid.Margin=7 补偿 hack 一并移除。
        MaximizeInterop.Attach(this);
        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            CenterBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(16, 0, 0, 0);
        };
    }

    private void CaptionMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CaptionMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateColumns()
    {
        SidebarColumn.Width = new GridLength(_vm.IsSidebarCollapsed ? 56 : 280);
        RightColumn.Width = new GridLength(_vm.IsRightbarOpen ? 320 : 0);
    }

    private void ScrollToEndSticky()
    {
        // 程序驱动的滚动不应被 ScrollChanged 误判为用户操作。
        _programmaticScroll = true;
        try { ChatScroll.ScrollToEnd(); }
        finally { _programmaticScroll = false; }
    }

    private ChatViewModel? _hookedChat;

    private void HookChat()
    {
        if (ReferenceEquals(_hookedChat, _vm.CurrentChat)) return;
        _hookedChat = _vm.CurrentChat;
        if (_hookedChat is not null)
            _hookedChat.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ChatViewModel.IsEmpty)) UpdateRows();
            };
    }

    private void UpdateRows()
    {
        var empty = _vm.CurrentChat?.IsEmpty ?? true;
        var star = new GridLength(1, GridUnitType.Star);
        EmptyTopRow.Height = empty ? star : new GridLength(0);
        EmptyBottomRow.Height = empty ? star : new GridLength(0);
        ChatRow.Height = empty ? GridLength.Auto : star;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _autoScroll.Start();
        var first = _vm.Sessions.SelectMany(g => g.Children).FirstOrDefault();
        if (first is not null) _vm.SelectSession(first.Id);
        else _vm.NewSession();
        HookChat();
        UpdateRows();

        var screenshot = Environment.GetEnvironmentVariable("MYAGENT_SCREENSHOT");
        if (screenshot is not null)
        {
            var delay = int.TryParse(Environment.GetEnvironmentVariable("MYAGENT_SCREENSHOT_DELAY"), out var d) ? d : 1500;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (Environment.GetEnvironmentVariable("MYAGENT_SCROLL_END") is not null) ChatScroll.ScrollToEnd();
                UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)FrameGrid.ActualWidth, (int)RootGrid.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(RootGrid);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = System.IO.File.Create(screenshot);
                encoder.Save(stream);
                if (Environment.GetEnvironmentVariable("MYAGENT_EXIT_AFTER_SHOT") is not null)
                    Application.Current.Shutdown();
            };
            timer.Start();
        }
    }

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_vm) { Owner = this };
        dialog.ShowDialog();
    }

    private void ChatScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        var step = SystemParameters.WheelScrollLines * 48;
        var direction = Math.Sign(e.Delta);

        ScrollViewer? inner = null;
        var node = e.OriginalSource as DependencyObject;
        while (node is not null && node != ChatScroll)
        {
            if (node is ScrollViewer sv && sv != ChatScroll) { inner = sv; break; }
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        if (inner is not null &&
            inner.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled &&
            inner.ScrollableHeight > 0)
        {
            var atTop = inner.VerticalOffset <= 0.5;
            var atBottom = inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
            if ((direction > 0 && !atTop) || (direction < 0 && !atBottom))
            {
                inner.ScrollToVerticalOffset(inner.VerticalOffset - direction * step);
                e.Handled = true;
                return;
            }
        }

        if (ChatScroll.ScrollableHeight > 0)
        {
            ChatScroll.ScrollToVerticalOffset(ChatScroll.VerticalOffset - direction * step);
            e.Handled = true;
        }
    }

    private void SessionRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionRowVm row })
            _vm.SelectSession(row.Id);
    }

    private void RenameRow_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: SessionRowVm row }) return;
        var dialog = new RenameWindow(row.Title) { Owner = this };
        if (dialog.ShowDialog() == true)
            _vm.RenameSession(row.Id, dialog.NewTitle);
    }

    private void ArchiveRow_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: SessionRowVm row }) return;
        if (ConfirmWindow.Ask(this, "归档会话", $"归档会话「{row.Title}」？归档后可在归档列表中恢复。", "归档"))
            _vm.ArchiveSession(row.Id);
    }

    private void ProcessHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProcessGroupItem group }) return;
        if (group.IsCollapsed || group.IsUserCollapsed)
        {
            group.IsCollapsed = false;
            group.IsUserCollapsed = false;
        }
        else
        {
            group.IsCollapsed = true;
            group.IsUserCollapsed = true;
        }
    }

    private void ReasoningToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReasoningItem item })
            item.IsOpen = !item.IsOpen;
    }

    private void ToolToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToolItem item })
            item.IsOpen = !item.IsOpen;
    }

    private void NewSession_Click(object sender, RoutedEventArgs e) => _vm.NewSession();

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: SessionRowVm row }) return;
        if (ConfirmWindow.Ask(this, "删除会话", $"删除会话「{row.Title}」？删除后不可恢复。", "删除", danger: true))
            _vm.DeleteSession(row.Id);
    }

    private void DeleteWorkspace_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: WorkspaceGroupVm { Path: { } path } group }) return;
        var entry = _vm.Workspaces.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        if (ConfirmWindow.Ask(this, "删除工作区", $"删除工作区「{group.Name}」？其中的会话会移入未分组，不会被删除。", "删除", danger: true))
            _vm.DeleteWorkspace(entry.Id);
    }

    private void AddWorkspace_Click(object sender, RoutedEventArgs e)
    {
        WorkspacePicker.IsOpen = false;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择工作区目录" };
        if (dialog.ShowDialog(this) == true) _vm.AddWorkspace(dialog.FolderName);
    }

    private void NewSessionInWorkspace_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: WorkspaceGroupVm { Path: { } path } })
            _vm.NewSession(path);
    }

    private void WorkspacePickerChip_Click(object sender, RoutedEventArgs e) => WorkspacePicker.IsOpen = true;

    private void WorkspacePick_Click(object sender, RoutedEventArgs e)
    {
        WorkspacePicker.IsOpen = false;
        if (sender is FrameworkElement { DataContext: WorkspacePickVm pick })
            _vm.PickWorkspace(pick.Entry);
    }

    private void SearchToggle_Click(object sender, RoutedEventArgs e) => _vm.IsSearchOpen = !_vm.IsSearchOpen;

    private void GroupRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorkspaceGroupVm group } fe) return;
        DependencyObject? parent = fe;
        while (parent is not null and not TreeViewItem) parent = VisualTreeHelper.GetParent(parent);
        if (parent is TreeViewItem item)
            item.SetCurrentValue(TreeViewItem.IsExpandedProperty, group.IsExpanded);
    }

    private void CollapseSidebar_Click(object sender, RoutedEventArgs e) =>
        _vm.IsSidebarCollapsed = !_vm.IsSidebarCollapsed;

    private void RightbarToggle_Click(object sender, RoutedEventArgs e) =>
        _vm.IsRightbarOpen = !_vm.IsRightbarOpen;

    /* ===== todo summary（对话区一行摘要，点击展开/收起） ===== */
    private void TodoSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentChat is { } chat) chat.IsTodoExpanded = !chat.IsTodoExpanded;
    }

    private void TogglePlanMode_Click(object sender, RoutedEventArgs e) => _vm.TogglePlanMode();
    private void ExitPlanMode_Click(object sender, RoutedEventArgs e) => _vm.ExitPlanMode();

    private void GoalEdit_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RenameWindow(_vm.Goal.Objective) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewTitle))
            _vm.GoalEdit(dialog.NewTitle);
    }

    private void ToggleTeamMode_Click(object sender, RoutedEventArgs e) => _vm.ToggleTeamMode();

    private void GoalPauseResume_Click(object sender, RoutedEventArgs e) => _vm.GoalPauseResume();
    private void GoalClear_Click(object sender, RoutedEventArgs e) => _vm.GoalClear();
    private void ApprovePlan_Click(object sender, RoutedEventArgs e) => _vm.ApprovePlan();
    private void DiscussPlan_Click(object sender, RoutedEventArgs e) => _vm.DiscussPlan();
    private void ApproveEscalation_Click(object sender, RoutedEventArgs e) => _vm.ApproveEscalation();
    private void RejectEscalation_Click(object sender, RoutedEventArgs e) => _vm.RejectEscalation();
    private void ActivityPill_Click(object sender, RoutedEventArgs e) => _vm.ComposerStats.ToggleActivity();
    private void UsagePill_Click(object sender, RoutedEventArgs e) => _vm.ComposerStats.ToggleUsage();
    private void ContextMeter_Click(object sender, RoutedEventArgs e) => _vm.ComposerStats.ToggleContext();

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void Send_Click(object sender, RoutedEventArgs e) => SendOrCancel();

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt 组合键在 WPF 中作为系统键到达：Key == Key.System，真实键在 SystemKey。
        // IME 组词中的回车是 Key.ImeProcessed，不等于 Enter，天然不会误发送。
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Enter) return;

        var mods = Keyboard.Modifiers;
        var send = _settings.Current.SendKey switch
        {
            "ctrl-enter" => mods == ModifierKeys.Control,
            "alt-enter" => mods == ModifierKeys.Alt,
            // "enter"（默认）：保持历史行为——Shift+Enter 换行，其余回车发送。
            _ => mods != ModifierKeys.Shift,
        };
        if (!send) return; // 不拦截：TextBox AcceptsReturn=True 自己插入换行
        e.Handled = true;
        SendOrCancel();
    }

    /// <summary>发送按钮提示随 sendKey 设置更新（保存设置后立即生效）。</summary>
    private void UpdateSendKeyHint()
    {
        SendBtn.ToolTip = _settings.Current.SendKey switch
        {
            "ctrl-enter" => "发送（Ctrl+Enter）· Enter 换行",
            "alt-enter" => "发送（Alt+Enter）· Enter 换行",
            _ => "发送（Enter）· Shift+Enter 换行",
        };
    }

    private void SendOrCancel()
    {
        if (_vm.CurrentChat?.IsRunning == true)
        {
            _vm.Cancel();
            return;
        }
        var text = InputBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        // 用户主动发送消息：无论之前滚到哪里，都恢复贴底跟随，立即看到自己的消息与回复。
        _nearBottom = true;
        _vm.Send(text);
        InputBox.Clear();
        InputBox.Focus();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollToEndSticky);
    }
}