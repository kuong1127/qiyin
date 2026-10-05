using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskbarMediaBar.Interop;

/// <summary>
/// Win32 互操作：任务栏吸附所需的全部 API 集合（仅 x64）。
/// </summary>
public static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_HWNDPARENT = -8;

    public const long WS_CHILD = 0x40000000L;
    public const long WS_POPUP = 0x80000000L;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_THICKFRAME = 0x00040000L;
    public const long WS_SYSMENU = 0x00080000L;
    public const long WS_MINIMIZEBOX = 0x00020000L;
    public const long WS_MAXIMIZEBOX = 0x00010000L;
    public const long WS_CLIPSIBLINGS = 0x04000000L;
    public const long WS_CLIPCHILDREN = 0x02000000L;
    public const long WS_DISABLED = 0x08000000L;

    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_TOPMOST = 0x00000008L;

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const int MK_LBUTTON = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    public const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    public const byte VK_MEDIA_PREV_TRACK = 0xB1;
    public const byte VK_MEDIA_STOP = 0xB2;
    public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>发一次媒体键（给不支持 SMTC 的播放器兜底用）。</summary>
    public static void SendMediaKey(byte vk)
    {
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, IntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    public const uint WM_APPCOMMAND = 0x0319;
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    // APPCOMMAND_MEDIA_*
    public const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    public const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    public const int APPCOMMAND_MEDIA_STOP = 13;
    public const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;

    /// <summary>
    /// 向所有顶层窗口广播媒体命令。
    /// 没有 SMTC 会话时（网易云就是），按键注入不会被系统派发给它，
    /// 但直接广播 WM_APPCOMMAND 能被它的窗口过程收到，且不需要抢焦点。
    /// </summary>
    public static void BroadcastMediaCommand(int appCommand)
    {
        IntPtr lParam = new(appCommand << 16);
        SendMessageTimeout(HWND_BROADCAST, WM_APPCOMMAND, IntPtr.Zero, lParam,
            SMTO_ABORTIFHUNG, 1200, out _);
    }

    /// <summary>直接把媒体命令投给某个窗口（不广播、不抢焦点）。</summary>
    public static void SendAppCommand(IntPtr hwnd, int appCommand)
    {
        IntPtr lParam = new(appCommand << 16);
        SendMessageTimeout(hwnd, WM_APPCOMMAND, IntPtr.Zero, lParam,
            SMTO_ABORTIFHUNG, 1200, out _);
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENTPOLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;
    private const int ACCENT_DISABLED = 0;

    /// <summary>毛玻璃（acrylic）。gradientColor 是 0xAABBGGRR。</summary>
    public static bool EnableAcrylic(IntPtr hwnd, byte alpha, byte r, byte g, byte b)
    {
        try
        {
            int gradient = (alpha << 24) | (b << 16) | (g << 8) | r;
            var policy = new ACCENTPOLICY
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = gradient,
                AnimationId = 0,
            };

            int size = Marshal.SizeOf<ACCENTPOLICY>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WINDOWCOMPOSITIONATTRIBDATA
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size,
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            Services.Log.Write("开启毛玻璃失败: " + ex.Message);
            return false;
        }
    }

    public static void DisableAcrylic(IntPtr hwnd)
    {
        try
        {
            var policy = new ACCENTPOLICY { AccentState = ACCENT_DISABLED };
            int size = Marshal.SizeOf<ACCENTPOLICY>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WINDOWCOMPOSITIONATTRIBDATA
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size,
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            // ignore
        }
    }


    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);


    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr hObject);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>这些窗口标题不是主窗口（桌面歌词/迷你播放器/应用名本身=空转）。</summary>
    private static readonly string[] AuxiliaryTitles =
    {
        "桌面歌词", "迷你播放器", "歌词", "托盘", "提示",
    };

    /// <summary>
    /// 找一个进程的"真主窗口"：同一进程往往有多个顶层窗口（隐藏辅助窗口、桌面歌词窗口…），
    /// 选窗策略：标题形如「歌名 - 歌手」的最优先，可见次之，面积再次之。
    /// </summary>
    public static IntPtr FindMainWindow(string processName, bool requireTitle = true)
    {
        var result = IntPtr.Zero;
        long bestScore = long.MinValue;

        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName(processName);
        }
        catch
        {
            return IntPtr.Zero;
        }

        HashSet<int> pids;
        try { pids = procs.Select(p => p.Id).ToHashSet(); }
        finally { foreach (var process in procs) process.Dispose(); }

        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out int pid);
            if (!pids.Contains(pid))
            {
                return true;
            }

            if (!GetWindowRect(h, out var r) || r.Width <= 0 || r.Height <= 0)
            {
                return true;
            }

            string t = ReadWindowTitle(h);
            if (requireTitle && t.Length == 0)
            {
                return true;
            }

            var cls = new System.Text.StringBuilder(128);
            GetClassName(h, cls, cls.Capacity);
            string className = cls.ToString();

            // 辅助窗口排除：桌面歌词 / 迷你播放器 / IME / GDI+ 钩子 …
            foreach (var bad in AuxiliaryTitles)
            {
                if (t.Contains(bad, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            foreach (var badClass in new[] { "IME", "MSCTFIME UI", "GDI+ Hook Window Class", "Base_PowerMessageWindow" })
            {
                if (className.Equals(badClass, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            // 注意：最小化到托盘时窗口 rect 会退化成图标尺寸（例如 237x39），
            // 所以这里**不能按尺寸过滤**，只能靠标题和类名判断。
            bool looksLikeSong = t.Contains(" - ", StringComparison.Ordinal);
            bool knownHost = className is "OrpheusBrowserHost" or "Chrome_WidgetWin_1" or "Chrome_WidgetWin_0";

            long score = (looksLikeSong ? 1_000_000_000L : 0)
                         + (knownHost ? 100_000_000L : 0)
                         + (IsWindowVisible(h) ? 10_000_000L : 0)
                         + (long)r.Width * r.Height;

            if (score > bestScore)
            {
                bestScore = score;
                result = h;
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static string ReadWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "";
        }

        var sb = new System.Text.StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    public const int ATTACH_PARENT_PROCESS = -1;

    public static RECT GetMonitorRect(IntPtr hwnd)
    {
        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
        {
            return mi.rcMonitor;
        }
        return new RECT
        {
            Left = 0,
            Top = 0,
            Right = GetSystemMetrics(SM_CXVIRTUALSCREEN),
            Bottom = GetSystemMetrics(SM_CYVIRTUALSCREEN),
        };
    }
}
