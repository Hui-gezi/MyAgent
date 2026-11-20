using System.Windows;
using System.Windows.Controls;

namespace MyAgent.Desktop;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        var settings = vm.Settings;

        foreach (ComboBoxItem item in ProviderBox.Items)
        {
            if ((string)item.Tag == settings.Provider)
            {
                ProviderBox.SelectedItem = item;
                break;
            }
        }
        ApiKeyBox.Password = settings.ApiKey;
        BaseUrlBox.Text = settings.BaseUrl;
        ThinkingBox.IsChecked = settings.Thinking;
        ContextWindowBox.Text = settings.ContextWindow.ToString();
        foreach (ComboBoxItem item in EffortBox.Items)
        {
            if ((string)item.Content == settings.Effort)
            {
                EffortBox.SelectedItem = item;
                break;
            }
        }
        foreach (ComboBoxItem item in ThemeBox.Items)
        {
            if ((string)item.Tag == settings.Theme)
            {
                ThemeBox.SelectedItem = item;
                break;
            }
        }
        foreach (ComboBoxItem item in PerformanceUsageBox.Items)
        {
            if ((string)item.Tag == settings.PerformanceUsage)
            {
                PerformanceUsageBox.SelectedItem = item;
                break;
            }
        }
        PerformanceUsageBox.SelectedItem ??= PerformanceUsageBox.Items[0];
        foreach (ComboBoxItem item in SendKeyBox.Items)
        {
            if ((string)item.Tag == settings.SendKey)
            {
                SendKeyBox.SelectedItem = item;
                break;
            }
        }
        SendKeyBox.SelectedItem ??= SendKeyBox.Items[0];
        foreach (ComboBoxItem item in PermissionPresetBox.Items)
        {
            if ((string)item.Tag == settings.PermissionPreset)
            {
                PermissionPresetBox.SelectedItem = item;
                break;
            }
        }
        PermissionPresetBox.SelectedItem ??= PermissionPresetBox.Items[0];
        foreach (ComboBoxItem item in SandboxModeBox.Items)
        {
            if ((string)item.Tag == settings.SandboxMode)
            {
                SandboxModeBox.SelectedItem = item;
                break;
            }
        }
        SandboxModeBox.SelectedItem ??= SandboxModeBox.Items[1];
        SandboxRootsBox.Text = string.Join(Environment.NewLine, settings.SandboxWritableRoots);
        RefreshModels(settings.Model);
    }

    private bool _apiKeyVisible;

    /// <summary>当前输入的 API Key（无论显示/隐藏状态）。</summary>
    private string CurrentApiKey => _apiKeyVisible ? ApiKeyPlainBox.Text : ApiKeyBox.Password;

    private void ToggleApiKey_Click(object sender, RoutedEventArgs e)
    {
        _apiKeyVisible = !_apiKeyVisible;
        if (_apiKeyVisible)
        {
            ApiKeyPlainBox.Text = ApiKeyBox.Password;
            ApiKeyPlainBox.Visibility = Visibility.Visible;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ToggleApiKeyBtn.Content = "隐藏";
            ApiKeyPlainBox.Focus();
            ApiKeyPlainBox.CaretIndex = ApiKeyPlainBox.Text.Length;
        }
        else
        {
            ApiKeyBox.Password = ApiKeyPlainBox.Text;
            ApiKeyBox.Visibility = Visibility.Visible;
            ApiKeyPlainBox.Visibility = Visibility.Collapsed;
            ToggleApiKeyBtn.Content = "显示";
            ApiKeyBox.Focus();
        }
    }

    private string SelectedProvider =>
        (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "mock";

    private void RefreshModels(string? selected)
    {
        ModelBox.Items.Clear();
        var settings = _vm.Settings;
        var models = settings.ModelCatalogProvider == SelectedProvider && settings.ModelCatalog.Count > 0
            ? settings.ModelCatalog.Select(id => (Id: id, Name: id)).ToList()
            : DefaultModels(SelectedProvider);
        foreach (var (id, name) in models)
            ModelBox.Items.Add(new ModelOptionVm { Id = id, Name = name });
        ModelBox.SelectedValue = models.FirstOrDefault(m => m.Id == selected).Id ?? models[0].Id;
    }

    private static List<(string Id, string Name)> DefaultModels(string provider) => provider switch
    {
        "openai-compat" => [("deepseek-chat", "Chat"), ("deepseek-reasoner", "Reasoner")],
        "mock" => [("mock", "Mock (keyless demo)")],
        _ => [(MyAgent.Llm.ModelIds.Flash, "Flash"), (MyAgent.Llm.ModelIds.V4Pro, "V4 Pro")],
    };

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ModelBox is not null) RefreshModels(null);
        if (ModelListStatus is not null) SetModelStatus("", false);
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProvider == "mock")
        {
            SetModelStatus("Mock 服务商使用内置模型，无需获取。", false);
            return;
        }
        FetchModelsBtn.IsEnabled = false;
        SetModelStatus("正在获取模型列表…", false);
        try
        {
            var models = await MyAgent.Llm.RemoteModelList.FetchAsync(SelectedProvider, BaseUrlBox.Text, CurrentApiKey);
            var selected = ModelBox.SelectedValue as string;
            ModelBox.Items.Clear();
            foreach (var model in models)
                ModelBox.Items.Add(new ModelOptionVm { Id = model.Id, Name = model.Name });
            ModelBox.SelectedValue = models.Any(m => m.Id == selected) ? selected : models[0].Id;
            SetModelStatus($"已获取 {models.Count} 个模型，保存后生效。", false);
        }
        catch (Exception ex)
        {
            SetModelStatus(ex.Message, true);
        }
        finally
        {
            FetchModelsBtn.IsEnabled = true;
        }
    }

    private void SetModelStatus(string text, bool isError)
    {
        ModelListStatus.Text = text;
        ModelListStatus.SetResourceReference(ForegroundProperty, isError ? "Danger" : "TextTertiary");
    }

    private void NavModel_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(ModelPanel);
        SelectNav(NavModel);
    }

    private void NavAppearance_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(AppearancePanel);
        SelectNav(NavAppearance);
    }

    private void NavPermission_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(PermissionPanel);
        SelectNav(NavPermission);
    }

    private void NavData_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(DataPanel);
        SelectNav(NavData);
        RefreshDataHome();
    }

    private void NavArchive_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(ArchivePanel);
        SelectNav(NavArchive);
        RefreshArchive();
    }

    private void ShowPanel(FrameworkElement visible)
    {
        ModelPanel.Visibility = ReferenceEquals(visible, ModelPanel) ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = ReferenceEquals(visible, AppearancePanel) ? Visibility.Visible : Visibility.Collapsed;
        PermissionPanel.Visibility = ReferenceEquals(visible, PermissionPanel) ? Visibility.Visible : Visibility.Collapsed;
        DataPanel.Visibility = ReferenceEquals(visible, DataPanel) ? Visibility.Visible : Visibility.Collapsed;
        ArchivePanel.Visibility = ReferenceEquals(visible, ArchivePanel) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>切换左侧导航选中态：选中项 Tag="selected"，模板触发器据此更新背景与前景色。</summary>
    private void SelectNav(Button selected)
    {
        NavModel.Tag = ReferenceEquals(selected, NavModel) ? "selected" : null;
        NavAppearance.Tag = ReferenceEquals(selected, NavAppearance) ? "selected" : null;
        NavPermission.Tag = ReferenceEquals(selected, NavPermission) ? "selected" : null;
        NavData.Tag = ReferenceEquals(selected, NavData) ? "selected" : null;
        NavArchive.Tag = ReferenceEquals(selected, NavArchive) ? "selected" : null;
    }

    /* ===== 归档会话页：列表 + 恢复 / 删除 ===== */

    private sealed record ArchiveRow(string Id, string Title, string Cwd, string UpdatedText);

    private void RefreshArchive()
    {
        var rows = _vm.ListArchivedSessions()
            .Select(s => new ArchiveRow(
                s.Id,
                string.IsNullOrEmpty(s.Title) ? "New Session" : s.Title!,
                s.Cwd,
                s.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")))
            .ToList();
        ArchiveList.ItemsSource = rows;
        ArchiveEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArchiveCountText.Text = rows.Count == 0 ? "" : $"共 {rows.Count} 个归档会话";
    }

    private void ArchiveRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ArchiveRow row }) return;
        _vm.UnarchiveSession(row.Id);
        RefreshArchive();
    }

    private void ArchiveDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ArchiveRow row }) return;
        if (ConfirmWindow.Ask(this, "删除会话", $"删除归档会话「{row.Title}」？删除后不可恢复。", "删除", danger: true))
        {
            _vm.DeleteSession(row.Id);
            RefreshArchive();
        }
    }

    /* ===== 数据页：会话/定时任务/日志的数据目录（settings.json 永远住在锚点） ===== */

    /// <summary>当前生效的数据目录：MYAGENT_HOME > settings.dataHome > 锚点（与启动解析一致）。</summary>
    private string EffectiveDataHome
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("MYAGENT_HOME");
            if (!string.IsNullOrEmpty(env)) return env;
            var configured = _vm.Settings.DataHome;
            return string.IsNullOrEmpty(configured) ? MyAgent.Sessions.SessionStore.DefaultHome : configured;
        }
    }

    private void RefreshDataHome()
    {
        var env = Environment.GetEnvironmentVariable("MYAGENT_HOME");
        var configured = _vm.Settings.DataHome;
        DataHomeText.Text = EffectiveDataHome;
        // 来源说明：让"谁赢了"可见，残留环境变量造成的歧义一眼看穿。
        DataHomeSourceText.Text = !string.IsNullOrEmpty(env)
            ? (string.IsNullOrEmpty(configured)
                ? "来自环境变量 MYAGENT_HOME（本次运行强制指定）。"
                : $"来自环境变量 MYAGENT_HOME，覆盖了你在设置中配置的目录：{configured}")
            : (string.IsNullOrEmpty(configured)
                ? "默认位置（与配置目录相同）。"
                : "来自你在设置中配置的目录。");
        DataHomeSourceText.SetResourceReference(ForegroundProperty,
            !string.IsNullOrEmpty(env) ? "Warning" : "TextTertiary");
        ResetDataHomeBtn.Visibility = string.IsNullOrEmpty(configured) ? Visibility.Collapsed : Visibility.Visible;
        // 有对话在运行：按钮直接置灰，并说明原因（点击路径上还有二次拦截兜底）。
        var running = _vm.AnySessionRunning;
        ChangeDataHomeBtn.IsEnabled = !running;
        ResetDataHomeBtn.IsEnabled = !running;
        ChangeDataHomeBtn.ToolTip = running ? "有对话正在运行，等所有对话结束后才能更改数据目录。" : null;
        ResetDataHomeBtn.ToolTip = running ? "有对话正在运行，等所有对话结束后才能更改数据目录。" : null;
        if (running)
            SetDataStatus("有对话正在运行，暂时无法更改数据目录。请等待所有对话结束后再试。", true);
    }

    private void OpenDataHome_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = EffectiveDataHome;
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetDataStatus($"无法打开目录：{ex.Message}", true);
        }
    }

    private void ChangeDataHome_Click(object sender, RoutedEventArgs e)
    {
        if (BlockWhileRunning()) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择数据目录（会话、定时任务与日志）",
            InitialDirectory = EffectiveDataHome,
        };
        if (dialog.ShowDialog(this) != true) return;
        ApplyDataHome(dialog.FolderName);
    }

    private void ResetDataHome_Click(object sender, RoutedEventArgs e)
    {
        if (BlockWhileRunning()) return;
        ApplyDataHome("");
    }

    /// <summary>有对话正在运行时禁止修改数据目录：迁移要挪动会话文件，
    /// 必须等所有对话（含后台/子代理会话）结束后才能改。</summary>
    private bool BlockWhileRunning()
    {
        if (!_vm.AnySessionRunning) return false;
        SetDataStatus("有对话正在运行，暂时无法更改数据目录。请等待所有对话结束（或手动停止）后再试。", true);
        ConfirmWindow.Ask(this, "对话正在运行",
            "当前有对话正在运行，无法更改数据目录。\n\n更改数据目录需要在下次启动时迁移会话数据，正在写入的会话文件不能迁移。\n请等待所有对话结束（或手动停止）后再修改。",
            "知道了", confirmOnly: true);
        return true;
    }

    private void ApplyDataHome(string target)
    {
        // 目录选择/确认期间可能有对话开始运行，落盘前再查一次。
        if (BlockWhileRunning()) return;
        var anchor = MyAgent.Sessions.SessionStore.DefaultHome;
        var normalized = target.Length == 0 ? "" : target.TrimEnd('\\', '/');
        var current = _vm.Settings.DataHome;
        if (string.Equals(normalized, current, StringComparison.OrdinalIgnoreCase))
        {
            SetDataStatus("目录未变化。", false);
            return;
        }
        // 与锚点相同视同恢复默认：存空串，避免把默认路径固化进配置。
        if (string.Equals(normalized, anchor.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            normalized = "";

        // 切换前当场校验可写（建目录 + 试写临时文件），失败立即报错。
        if (normalized.Length > 0 && !MyAgent.App.SettingsService.TryProbeWritable(normalized, out var probeError))
        {
            SetDataStatus($"目标目录不可写：{probeError}", true);
            return;
        }

        // 迁移来源 = 当前实际生效的数据目录（重启后从这里把旧数据合并进新目录）。
        var migrateFrom = EffectiveDataHome;
        var display = normalized.Length == 0 ? anchor : normalized;
        var message = normalized.Length == 0
            ? $"恢复默认数据目录？\n\n{anchor}\n\n下次启动时会把当前目录（{migrateFrom}）中的会话、定时任务与日志合并迁移到默认目录；旧目录文件保留，不会被删除。"
            : $"将会话、定时任务与日志的数据目录改为：\n\n{display}\n\n下次启动时会把当前目录（{migrateFrom}）中的会话、定时任务与日志合并迁移到新目录，历史对话不会丢失；旧目录文件保留，不会被删除。";
        if (!ConfirmWindow.Ask(this, "更改数据目录", message, "更改"))
            return;

        _vm.UpdateSettings(new { dataHome = normalized, dataHomeMigrateFrom = migrateFrom });
        RefreshDataHome();
        var env = Environment.GetEnvironmentVariable("MYAGENT_HOME");
        SetDataStatus(string.IsNullOrEmpty(env)
            ? "已保存。重启应用后生效：启动时会先把现有数据自动迁移到新目录。"
            : "已保存。但当前存在 MYAGENT_HOME 环境变量，本次运行仍使用它指向的目录；清除该变量并重启后，你的设置与数据迁移才会生效。", false);
    }

    private void SetDataStatus(string text, bool isError)
    {
        DataHomeStatus.Text = text;
        DataHomeStatus.SetResourceReference(ForegroundProperty, isError ? "Danger" : "Success");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.UpdateSettings(new
        {
            provider = SelectedProvider,
            apiKey = CurrentApiKey,
            baseUrl = BaseUrlBox.Text,
            model = (ModelBox.SelectedValue as string) ?? _vm.Settings.Model,
            thinking = ThinkingBox.IsChecked == true,
            contextWindow = int.TryParse(ContextWindowBox.Text.Trim(), out var window) && window >= 8192
                ? window
                : _vm.Settings.ContextWindow,
            effort = (EffortBox.SelectedItem as ComboBoxItem)?.Content as string ?? "high",
            theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "system",
            performanceUsage = (PerformanceUsageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "detailed",
            sendKey = (SendKeyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "enter",
            modelCatalog = ModelBox.Items.OfType<ModelOptionVm>().Select(m => m.Id).ToList(),
            modelCatalogProvider = SelectedProvider,
            permissionPreset = (PermissionPresetBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "workspace-write",
            sandboxMode = (SandboxModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "workspace-write",
            sandboxWritableRoots = SandboxRootsBox.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList(),
        });
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}