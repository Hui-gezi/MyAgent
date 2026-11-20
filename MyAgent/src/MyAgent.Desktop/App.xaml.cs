using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MyAgent.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI-thread exceptions: log and KEEP RUNNING instead of the system
        // "application error" crash dialog. A transient binding/refresh failure
        // must not take the whole session host down.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("dispatcher", args.Exception);
            args.Handled = true;
            try
            {
                ConfirmWindow.Ask(Current?.MainWindow, "界面错误",
                    $"界面发生了一个错误，已记录日志并继续运行：\n{args.Exception.Message}\n\n日志：{LogDirectory}",
                    "知道了", confirmOnly: true);
            }
            catch { /* never let the reporter itself crash */ }
        };

        // Background-thread exceptions are still fatal (CLR policy), but leave a
        // diagnosable log next to the session data before the process dies.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("appdomain", args.ExceptionObject as Exception);

        // Fire-and-forget task failures: observe and log, never crash.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("unobserved-task", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>日志跟随数据目录（与 MainWindow.CreateStore 同一优先级）：
    /// MYAGENT_HOME > settings.json 的 dataHome > 配置锚点 ~/.myagent。
    /// 这里不做可写探测——LogCrash 自身兜底异常，探测失败就留在锚点。</summary>
    private static string LogDirectory
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("MYAGENT_HOME");
            if (!string.IsNullOrEmpty(env)) return Path.Combine(env, "logs");
            var anchor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".myagent");
            try
            {
                var settingsPath = Path.Combine(anchor, "settings.json");
                if (File.Exists(settingsPath))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                    if (doc.RootElement.TryGetProperty("dataHome", out var dh) &&
                        dh.GetString() is { Length: > 0 } dataHome && Directory.Exists(dataHome))
                        return Path.Combine(dataHome, "logs");
                }
            }
            catch { /* 配置损坏时日志留在锚点 */ }
            return Path.Combine(anchor, "logs");
        }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var file = Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }

    public static void ApplyTheme(string theme)
    {
        var dark = theme switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemUsesDarkTheme(),
        };
        var dict = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
        };
        Current.Resources.MergedDictionaries[0] = dict;
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}