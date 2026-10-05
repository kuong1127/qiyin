using TaskbarMediaBar.Interop;
using System.Runtime.InteropServices;

namespace TaskbarMediaBar.Services;

/// <summary>只检查前台窗口的客户区，避免把最大化窗口的边框当成全屏内容。</summary>
public static class FullscreenDetector
{
    public sealed record WindowSurface(NativeMethods.RECT WindowRect, NativeMethods.RECT ContentRect, bool Maximized);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>在目标显示器按可见窗口层级检查，忽略本程序、桌面和工具窗口。</summary>
    public static WindowSurface? GetVisibleSurface(NativeMethods.RECT monitor, NativeMethods.RECT? area = null)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0) return null;
        WindowSurface? result = null;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == Environment.ProcessId) return true;
            if ((NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64()
                & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            var name = new System.Text.StringBuilder(128);
            NativeMethods.GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect) || !Intersects(rect, monitor)
                || (area is { } relevant && !Intersects(rect, relevant))) return true;
            if (!NativeMethods.GetClientRect(hwnd, out var client) || client.Width <= 0 || client.Height <= 0) return true;
            var origin = new NativeMethods.POINT { X = client.Left, Y = client.Top };
            if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return true;
            result = new WindowSurface(rect, new NativeMethods.RECT
            {
                Left = origin.X, Top = origin.Y,
                Right = origin.X + client.Width, Bottom = origin.Y + client.Height,
            }, NativeMethods.IsZoomed(hwnd));
            return false;
        }, IntPtr.Zero);
        return result;
    }

    public static bool Intersects(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0 &&
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    public static bool IsActiveOn(NativeMethods.RECT monitor)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == Environment.ProcessId) return false;
        var name = new System.Text.StringBuilder(128);
        NativeMethods.GetClassName(hwnd, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        if (!NativeMethods.GetClientRect(hwnd, out var client)) return false;
        var origin = new NativeMethods.POINT { X = client.Left, Y = client.Top };
        if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return false;
        return CoversMonitor(new NativeMethods.RECT
        {
            Left = origin.X, Top = origin.Y,
            Right = origin.X + client.Width, Bottom = origin.Y + client.Height,
        }, monitor);
    }

    public static bool CoversMonitor(NativeMethods.RECT content, NativeMethods.RECT monitor)
    {
        // 使用同一坐标系下的物理像素；允许窗口边缘有 1 像素舍入误差。
        return monitor.Width > 0 && monitor.Height > 0 && content.Width > 0 && content.Height > 0
            && content.Left <= monitor.Left + 1 && content.Top <= monitor.Top + 1
            && content.Right >= monitor.Right - 1 && content.Bottom >= monitor.Bottom - 1;
    }
}
