using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TaskbarMediaBar.Interop;

namespace TaskbarMediaBar.Services;

public sealed class TopScreenInfo
{
    public IntPtr Handle { get; init; }
    public NativeMethods.RECT MonitorRect { get; init; }
    public NativeMethods.RECT WorkRect { get; init; }
    public double Scale { get; init; } = 1;
    public bool Primary { get; init; }
}

public readonly record struct TopScreenLayout(int X, int Y, int Width, int Height)
{
    public bool Fits => Width > 0 && Height > 0;
}

/// <summary>独立顶部窗口：不关联 Explorer、不占用工作区，也不创建屏幕外热区。</summary>
public static class TopScreenHost
{
    private sealed class PlacementState
    {
        public (int Width, int Height, int Radius) Region;
    }

    private static readonly ConditionalWeakTable<Window, PlacementState> Placements = new();
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref NativeMethods.RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    public static TopScreenInfo? Pick(AppSettings settings)
    {
        var screens = new List<TopScreenInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr dc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(monitor, ref info) && info.rcMonitor.Width > 0 && info.rcMonitor.Height > 0)
                    screens.Add(new TopScreenInfo { Handle = monitor, MonitorRect = info.rcMonitor,
                        WorkRect = info.rcWork, Scale = ReadScale(monitor), Primary = (info.dwFlags & 1) != 0 });
                return true;
            }, IntPtr.Zero);
        if (string.Equals(settings.TopMonitor, "cursor", StringComparison.OrdinalIgnoreCase)
            && NativeMethods.GetCursorPos(out var point))
        {
            var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var atCursor = screens.FirstOrDefault(screen => screen.Handle == monitor);
            if (atCursor is not null) return atCursor;
        }
        return screens.FirstOrDefault(screen => screen.Primary) ?? screens.FirstOrDefault();
    }

    private static double ReadScale(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi >= 72 && dpi <= 960)
                return dpi / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1;
    }

    /// <summary>把 DIP 内容尺寸映射至物理屏幕；包括负坐标、极端偏移和过小屏幕。</summary>
    public static TopScreenLayout ComputeLayout(TopScreenInfo screen, AppSettings settings, double widthDip, double heightDip)
    {
        var bounds = screen.MonitorRect;
        long availableWidth = (long)bounds.Right - bounds.Left;
        long availableHeight = (long)bounds.Bottom - bounds.Top;
        if (availableWidth <= 0 || availableHeight <= 0 || availableWidth > int.MaxValue || availableHeight > int.MaxValue
            || !double.IsFinite(widthDip) || !double.IsFinite(heightDip) || widthDip <= 0 || heightDip <= 0)
            return default;
        double scale = double.IsFinite(screen.Scale) && screen.Scale > 0 ? screen.Scale : 1;
        int width = (int)Math.Clamp(Math.Round(widthDip * scale), 1, availableWidth);
        int height = (int)Math.Clamp(Math.Round(heightDip * scale), 1, availableHeight);
        double anchorX = string.Equals(settings.TopAnchor, "left", StringComparison.OrdinalIgnoreCase) ? bounds.Left
            : string.Equals(settings.TopAnchor, "right", StringComparison.OrdinalIgnoreCase) ? (double)bounds.Right - width
            : bounds.Left + (availableWidth - width) / 2.0;
        int x = (int)Math.Clamp(Math.Round(anchorX + settings.TopOffsetX * scale), bounds.Left, (double)bounds.Right - width);
        int y = (int)Math.Clamp(Math.Round(bounds.Top + settings.TopOffsetY * scale), bounds.Top, (double)bounds.Bottom - height);
        return new TopScreenLayout(x, y, width, height);
    }

    public static void Place(Window window, TopScreenInfo screen, AppSettings settings)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
        // Width/Height are the requested DIP dimensions. Auto-sized content uses its measured size.
        double width = double.IsFinite(window.Width) && window.Width > 0 ? window.Width : window.ActualWidth;
        double height = double.IsFinite(window.Height) && window.Height > 0 ? window.Height : window.ActualHeight;
        var layout = ComputeLayout(screen, settings, width, height);
        if (!layout.Fits) { Hide(window); return; }
        double scale = double.IsFinite(screen.Scale) && screen.Scale > 0 ? screen.Scale : 1;
        if (window.MinWidth * scale > layout.Width || window.MinHeight * scale > layout.Height)
        {
            Hide(window);
            return;
        }
        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        long desiredEx = ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        if (ex != desiredEx) NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(desiredEx));
        bool visible = NativeMethods.IsWindowVisible(hwnd);
        bool positioned = NativeMethods.GetWindowRect(hwnd, out var actual) && actual.Left == layout.X && actual.Top == layout.Y
            && actual.Width == layout.Width && actual.Height == layout.Height;
        bool topmost = (ex & NativeMethods.WS_EX_TOPMOST) != 0;
        if (!positioned || !visible || !topmost)
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, layout.X, layout.Y, layout.Width, layout.Height,
                NativeMethods.SWP_NOACTIVATE | (visible ? 0 : NativeMethods.SWP_SHOWWINDOW)
                | (topmost ? NativeMethods.SWP_NOZORDER : 0));
        // Native clipping also bounds the mouse hit area; no transparent screen-sized helper window.
        int radius = (int)Math.Min(Math.Round(8 * scale), Math.Min(layout.Width, layout.Height) / 2);
        var state = Placements.GetOrCreateValue(window);
        if (state.Region == (layout.Width, layout.Height, radius)) return;
        var region = NativeMethods.CreateRoundRectRgn(0, 0, layout.Width + 1, layout.Height + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero) return;
        if (NativeMethods.SetWindowRgn(hwnd, region, true) != 0) state.Region = (layout.Width, layout.Height, radius);
        else NativeMethods.DeleteObject(region);
    }

    public static void Hide(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
    }
}
