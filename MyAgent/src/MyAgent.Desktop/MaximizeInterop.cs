using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MyAgent.Desktop;

/// <summary>
/// 无边框窗口（WindowStyle=None）最大化修正：WPF 默认按整个显示器铺满，
/// 底部会被系统任务栏压住。这里接管 WM_GETMINMAXINFO，把最大化位置/尺寸
/// 约束到窗口所在显示器的工作区（MonitorFromWindow + GetMonitorInfo），
/// 多显示器、任务栏停靠方位、不同 DPI 均正确。
/// </summary>
internal static class MaximizeInterop
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    public static void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        // EnsureHandle：在 SourceInitialized 之前也可安全取句柄（构造期即可挂钩）。
        var source = HwndSource.FromHwnd(helper.EnsureHandle());
        source?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var work = info.rcWork;
        var screen = info.rcMonitor;
        // 位置相对所在显示器原点（副屏可能非零/负值），尺寸取工作区大小。
        mmi.ptMaxPosition.x = work.left - screen.left;
        mmi.ptMaxPosition.y = work.top - screen.top;
        mmi.ptMaxSize.x = work.right - work.left;
        mmi.ptMaxSize.y = work.bottom - work.top;
        // 最小跟踪尺寸沿用窗口 MinWidth/MinHeight 的默认值，不覆写。
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }
}
