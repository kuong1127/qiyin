using System.Windows;
using System.Windows.Interop;
using TaskbarMediaBar.Interop;

namespace TaskbarMediaBar.Services;

public sealed class TaskbarInfo
{
    public IntPtr Handle { get; init; }
    public NativeMethods.RECT Rect { get; init; }
    public NativeMethods.RECT MonitorRect { get; init; }
    public NativeMethods.POINT ClientOrigin { get; init; }
    public double Scale { get; init; } = 1.0;

    /// <summary>任务栏当前是否实际可见（自动隐藏缩回去时为 false）。</summary>
    public bool Visible { get; init; }
}

/// <summary>
/// 播控条在任务栏上的落位。
///
/// 两种模式：
///  - overlay（默认）：顶层窗口浮在任务栏上，和汽水音乐的播控条同一条技术路线。
///    与任务栏建立顶层窗口的 owner 关系，Windows 保证它始终位于任务栏上方。
///    Explorer 重建后由 BarController 的窗口看门狗重新创建并关联。
///  - dock：把自己的窗口 SetParent 进 Shell_TrayWnd 当子窗口。
///    Win11 下实测会被任务栏的 composition 层盖住，保留作为兼容选项。
/// </summary>
public static class TaskbarHost
{
    public const string ModeOverlay = "overlay";
    public const string ModeDock = "dock";

    private static int _lastLoggedX = int.MinValue;
    private static (int, int) _lastLoggedSize = (-1, -1);
    private static string _lastActual = "";
    private sealed class PlacementState
    {
        public (int width, int height, double scale) Region;
        public string Mode = "";
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, PlacementState> Placements = new();

    public static TaskbarInfo? DiscoverPrimary()
    {
        IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        return Describe(taskbar);
    }

    /// <summary>找出所有任务栏（主屏 + 各副屏），用于多显示器。</summary>
    public static List<TaskbarInfo> DiscoverAll()
    {
        var list = new List<TaskbarInfo>();

        var primary = DiscoverPrimary();
        if (primary is not null)
        {
            list.Add(primary);
        }

        // 副屏任务栏类名是 Shell_SecondaryTrayWnd，FindWindow 只能取第一个，要枚举
        try
        {
            NativeMethods.EnumWindows((h, _) =>
            {
                var sb = new System.Text.StringBuilder(64);
                NativeMethods.GetClassName(h, sb, sb.Capacity);
                if (!string.Equals(sb.ToString(), "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var info = Describe(h);
                if (info is not null)
                {
                    list.Add(info);
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Write("枚举副屏任务栏失败: " + ex.Message);
        }

        return list;
    }

    /// <summary>按配置挑任务栏：主屏 or 鼠标所在屏。</summary>
    public static TaskbarInfo? Pick(AppSettings settings)
    {
        var all = DiscoverAll();
        if (all.Count == 0)
        {
            return null;
        }

        if (string.Equals(settings.Monitor, "cursor", StringComparison.OrdinalIgnoreCase) && all.Count > 1)
        {
            if (NativeMethods.GetCursorPos(out var pt))
            {
                IntPtr mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
                foreach (var info in all)
                {
                    if (NativeMethods.MonitorFromWindow(info.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST) == mon)
                    {
                        return info;
                    }
                }
            }
        }

        return all[0];
    }

    private static TaskbarInfo? Describe(IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero || !NativeMethods.IsWindow(taskbar))
        {
            return null;
        }

        if (!NativeMethods.GetWindowRect(taskbar, out var rect))
        {
            return null;
        }

        var monitor = NativeMethods.GetMonitorRect(taskbar);

        double scale = 1.0;
        try
        {
            uint dpi = NativeMethods.GetDpiForWindow(taskbar);
            if (dpi >= 72)
            {
                scale = dpi / 96.0;
            }
        }
        catch
        {
            scale = 1.0;
        }

        // 客户端 (0,0) 对应的屏幕坐标（ClientToScreen 的入参必须是客户端坐标）
        var clientOrigin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(taskbar, ref clientOrigin))
        {
            clientOrigin = new NativeMethods.POINT { X = rect.Left, Y = rect.Top };
        }

        bool visible = rect.Height >= 10;
        if (visible)
        {
            int visibleHeight = Math.Min(rect.Bottom, monitor.Bottom) - Math.Max(rect.Top, monitor.Top);
            int visibleWidth = Math.Min(rect.Right, monitor.Right) - Math.Max(rect.Left, monitor.Left);
            if (visibleHeight < 10 || visibleWidth < 10)
            {
                visible = false;
            }
        }

        return new TaskbarInfo
        {
            Handle = taskbar,
            Rect = rect,
            MonitorRect = monitor,
            ClientOrigin = clientOrigin,
            Scale = scale,
            Visible = visible,
        };
    }

    public static NativeMethods.RECT? GetTrayRect(IntPtr taskbar)
    {
        IntPtr tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !NativeMethods.GetWindowRect(tray, out var rect))
        {
            return null;
        }

        return rect;
    }

    private sealed class Layout
    {
        public int X;
        public int Width;
        public int Height;
        public int Pad;
        public double ContentScale;
    }

    private static Layout ComputeLayout(TaskbarInfo bar, AppSettings settings)
    {
        var occupants = settings.AvoidOthers
            ? TaskbarOccupancy.Discover(bar.Rect, Environment.ProcessId, bar.Handle)
            : new List<OccupantInfo>();
        var result = AdaptiveTaskbarLayout.Compute(bar, settings, GetTrayRect(bar.Handle), occupants);
        return new Layout { X = result.X, Width = result.Width, Height = result.Height, Pad = result.Y,
            ContentScale = result.ContentScale };
    }
    private static void ApplyRoundRegion(IntPtr hwnd, int width, int height, double scale, PlacementState state)
    {
        if (state.Region == (width, height, scale)) return;
        int radius = (int)Math.Round(9 * scale);
        IntPtr rgn = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (rgn != IntPtr.Zero)
        {
            if (NativeMethods.SetWindowRgn(hwnd, rgn, true) != 0) state.Region = (width, height, scale);
            else NativeMethods.DeleteObject(rgn);
        }
    }

    private static bool CoveredByTaskbar(IntPtr hwnd, TaskbarInfo bar)
    {
        if (bar.Handle == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        var point = new NativeMethods.POINT { X = rect.Left + rect.Width / 2, Y = rect.Top + rect.Height / 2 };
        IntPtr above = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(point), 2);
        return above == bar.Handle;
    }

    /// <summary>顶层浮层模式：和汽水音乐一样，坐在任务栏上面。</summary>
    public static void PlaceOverlay(Window window, TaskbarInfo bar, AppSettings settings)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!bar.Visible)
        {
            Hide(window);
            return;
        }

        // owner 不是 SetParent：保持独立顶层窗口，并由系统保证播控始终位于任务栏之上。
        // 只写自己的窗口属性；任务栏句柄变化时重新关联。
        if (bar.Handle != IntPtr.Zero && NativeMethods.IsWindow(bar.Handle)
            && NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT) != bar.Handle)
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT, bar.Handle);

        var layout = ComputeLayout(bar, settings);
        if (layout.Width <= 0 || layout.Height <= 0) { Hide(window); return; }
        int screenX = bar.Rect.Left + layout.X;
        int screenY = bar.Rect.Top + layout.Pad;

        var state = Placements.GetOrCreateValue(window);
        bool visible = NativeMethods.IsWindowVisible(hwnd);
        bool topmost = (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_TOPMOST) != 0;
        bool raise = !topmost || CoveredByTaskbar(hwnd, bar);
        bool positioned = NativeMethods.GetWindowRect(hwnd, out var actual) && actual.Left == screenX && actual.Top == screenY
            && actual.Width == layout.Width && actual.Height == layout.Height;
        if (!positioned || !visible || raise || state.Mode != ModeOverlay)
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, screenX, screenY, layout.Width, layout.Height,
                NativeMethods.SWP_NOACTIVATE | (visible ? 0 : NativeMethods.SWP_SHOWWINDOW)
                | (raise ? 0 : NativeMethods.SWP_NOZORDER));
        state.Mode = ModeOverlay;
        ApplyRoundRegion(hwnd, layout.Width, layout.Height, bar.Scale * layout.ContentScale, state);
        LogPlacement(hwnd, screenX, screenY, layout, "overlay");
    }

    /// <summary>子窗口模式：SetParent 进任务栏。</summary>
    public static bool DockIntoTaskbar(Window window, TaskbarInfo bar, AppSettings settings, bool restyle)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (restyle)
        {
            long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
            style |= NativeMethods.WS_CHILD | NativeMethods.WS_CLIPSIBLINGS;
            style &= ~(NativeMethods.WS_POPUP
                       | NativeMethods.WS_CAPTION
                       | NativeMethods.WS_THICKFRAME
                       | NativeMethods.WS_SYSMENU
                       | NativeMethods.WS_MINIMIZEBOX
                       | NativeMethods.WS_MAXIMIZEBOX);
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

            long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

            NativeMethods.SetParent(hwnd, bar.Handle);
        }

        PlaceDocked(window, bar, settings);
        return NativeMethods.GetParent(hwnd) == bar.Handle;
    }

    public static void PlaceDocked(Window window, TaskbarInfo bar, AppSettings settings)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!bar.Visible)
        {
            Hide(window);
            return;
        }

        var layout = ComputeLayout(bar, settings);
        if (layout.Width <= 0 || layout.Height <= 0) { Hide(window); return; }
        int childX = bar.Rect.Left + layout.X - bar.ClientOrigin.X;
        int childY = bar.Rect.Top + layout.Pad - bar.ClientOrigin.Y;

        var state = Placements.GetOrCreateValue(window);
        bool visible = NativeMethods.IsWindowVisible(hwnd);
        bool positioned = NativeMethods.GetWindowRect(hwnd, out var actual)
            && actual.Left == bar.Rect.Left + layout.X && actual.Top == bar.Rect.Top + layout.Pad
            && actual.Width == layout.Width && actual.Height == layout.Height;
        if (!positioned || !visible || state.Mode != ModeDock)
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, childX, childY, layout.Width, layout.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | (visible ? 0 : NativeMethods.SWP_SHOWWINDOW)
                | (state.Mode != ModeDock ? NativeMethods.SWP_FRAMECHANGED : 0));
        state.Mode = ModeDock;
        ApplyRoundRegion(hwnd, layout.Width, layout.Height, bar.Scale * layout.ContentScale, state);
        LogPlacement(hwnd, childX, childY, layout, "dock");
    }

    private static void LogPlacement(IntPtr hwnd, int x, int y, Layout layout, string mode)
    {
        if (mode == "dock")
        {
            NativeMethods.GetWindowRect(hwnd, out var actual);
            long st = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
            long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            Log.Write($"[dock] 期望=({x},{y}) {layout.Width}x{layout.Height} | 实际={actual} | 可见={NativeMethods.IsWindowVisible(hwnd)} | 父={NativeMethods.GetParent(hwnd)} | style=0x{st:X} ex=0x{ex:X}");
            return;
        }

        if (_lastLoggedX == x && _lastLoggedSize == (layout.Width, layout.Height))
        {
            if (NativeMethods.GetWindowRect(hwnd, out var now) && now.ToString() != _lastActual)
            {
                _lastActual = now.ToString();
                Log.Write($"实际矩形被改变: {now}（期望 {layout.Width}x{layout.Height}）");
            }

            return;
        }

        _lastLoggedX = x;
        _lastLoggedSize = (layout.Width, layout.Height);

        NativeMethods.GetWindowRect(hwnd, out var rect);
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();

        // 该点上最顶层窗口是谁？用来判断有没有被别人的窗口盖住
        var probe = new NativeMethods.POINT { X = x + layout.Width / 2, Y = y + layout.Height / 2 };
        IntPtr top = NativeMethods.WindowFromPoint(probe);
        string topDesc = "?";
        if (top != IntPtr.Zero)
        {
            var sb = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(top, sb, sb.Capacity);
            NativeMethods.GetWindowThreadProcessId(top, out int pid);
            topDesc = $"{(top == hwnd ? "本程序" : "其他")} {sb} pid={pid}";
        }

        _lastActual = rect.ToString();
        Log.Write($"[overlay] 期望=({x},{y}) {layout.Width}x{layout.Height} | 实际={rect} | 可见={NativeMethods.IsWindowVisible(hwnd)} | style=0x{style:X} ex=0x{exStyle:X} topmost={(exStyle & NativeMethods.WS_EX_TOPMOST) != 0} | 该点顶层窗口: {topDesc}");
    }

    public static (int Width, int Height) GetPhysicalSize(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r))
        {
            return (r.Width, r.Height);
        }

        return (0, 0);
    }

    public static void Hide(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        }
    }
}
