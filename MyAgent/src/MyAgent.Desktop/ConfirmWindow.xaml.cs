using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MyAgent.Desktop;

/// <summary>
/// 页内风格确认弹窗（mirrors harness ui-primitives Modal / RiskConfirmation）。
/// 替代系统 MessageBox：圆角卡片 + 标题 + 描述 + 描边取消/主色确认。
/// danger=true 时确认按钮走 Danger 红色（删除类操作）；
/// confirmOnly=true 时为单按钮提示（如错误通知），Esc/✕ 等同确认关闭。
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string confirmLabel = "确定",
        bool danger = false, bool confirmOnly = false)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmBtn.Content = confirmLabel;
        if (danger)
        {
            ConfirmBtn.SetResourceReference(BackgroundProperty, "Danger");
            ConfirmBtn.Foreground = Brushes.White;
        }
        else
        {
            ConfirmBtn.SetResourceReference(BackgroundProperty, "BrandPrimary");
            ConfirmBtn.SetResourceReference(ForegroundProperty, "BrandPrimaryText");
        }
        if (confirmOnly) CancelBtn.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => ConfirmBtn.Focus();
    }

    /// <summary>单按钮提示模式：✕ / Esc 也等同确认关闭。</summary>
    private bool IsConfirmOnly => CancelBtn.Visibility != Visibility.Visible;

    /// <summary>便捷入口：模态弹出并返回用户是否确认。</summary>
    public static bool Ask(Window? owner, string title, string message, string confirmLabel = "确定",
        bool danger = false, bool confirmOnly = false)
    {
        var dialog = new ConfirmWindow(title, message, confirmLabel, danger, confirmOnly);
        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        else if (Application.Current?.MainWindow is { IsLoaded: true } main) dialog.Owner = main;
        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = IsConfirmOnly;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        // Esc：双按钮视为取消；单按钮提示等同确认关闭。
        DialogResult = IsConfirmOnly;
        Close();
    }
}
