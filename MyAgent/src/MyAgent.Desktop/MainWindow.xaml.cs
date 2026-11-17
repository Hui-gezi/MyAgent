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
    private readonly SessionStore _store = new();
    private readonly SettingsService _settings;
    private readonly WorkspaceService _workspaces;
    private readonly SessionManager _manager;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _autoScroll;
    private bool _nearBottom = true;

    public MainWindow()
    {
        InitializeComponent();
        _settings = new SettingsService(_store.Home);
        _workspaces = new WorkspaceService(_store.Home);
        _manager = new SessionManager(_store, _settings);
        _vm = new MainViewModel(_manager, _store, _settings, _workspaces);
        DataContext = _vm;
        App.ApplyTheme(_settings.Current.Theme);

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsSidebarCollapsed) or nameof(MainViewModel.IsRightbarOpen))
                UpdateColumns();
            if (e.PropertyName is nameof(MainViewModel.CurrentChat))
            {
                HookChat();
                UpdateRows();
            }
        };

        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _autoScroll.Tick += (_, _) =>
        {
            if (_vm.CurrentChat?.IsRunning == true && _nearBottom) ChatScroll.ScrollToEnd();
        };

        ChatScroll.ScrollChanged += (_, _) =>
        {
            _nearBottom = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 120;
        };
        ChatScroll.PreviewMouseWheel += ChatScroll_PreviewMouseWheel;

        Loaded += OnLoaded;
        Closed += (_, _) => _manager.Dispose();
        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            RootGrid.Margin = maximized ? new Thickness(7) : new Thickness(0);
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
        var result = MessageBox.Show($"归档会话「{row.Title}」？", "MyAgent",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.OK) _vm.ArchiveSession(row.Id);
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
        var result = MessageBox.Show($"删除会话「{row.Title}」？删除后不可恢复。", "MyAgent",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.OK) _vm.DeleteSession(row.Id);
    }

    private void DeleteWorkspace_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: WorkspaceGroupVm { Path: { } path } group }) return;
        var entry = _vm.Workspaces.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        var result = MessageBox.Show($"删除工作区「{group.Name}」？其中的会话会移入未分组，不会被删除。", "MyAgent",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.OK) _vm.DeleteWorkspace(entry.Id);
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

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void Send_Click(object sender, RoutedEventArgs e) => SendOrCancel();

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            SendOrCancel();
        }
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
        _vm.Send(text);
        InputBox.Clear();
        InputBox.Focus();
    }
}