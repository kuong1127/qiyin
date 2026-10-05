using System.Diagnostics;
using System.Text;
using TaskbarMediaBar.Interop;

namespace TaskbarMediaBar.Services;

public sealed class OccupantInfo
{
    public IntPtr Handle { get; init; }
    public string ClassName { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public int Pid { get; init; }
    public NativeMethods.RECT Rect { get; init; }
    public bool IsChild { get; init; }

    public override string ToString() =>
        $"{ProcessName} [{ClassName}] pid={Pid} rect={Rect}{(IsChild ? " child" : "")}";
}

/// <summary>
/// 任务栏占位者探测：找出**除任务栏自身以外**、画在任务栏那一条上的窗口。
/// 典型例子：汽水音乐自己画的常驻播控条、其他第三方任务栏小组件。
/// 用途：把我们的播控条自动挪到不重叠的位置。
/// </summary>
public static class TaskbarOccupancy
{
    private static readonly HashSet<string> Ignores = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "ReBarWindow32",
        "MSTaskSwWClass",
        "MSTaskListWClass",
        "TrayNotifyWnd",
        "TrayClockWClass",
        "Windows.UI.Core.CoreWindow",
        "Windows.UI.Composition",
        "Start",
        "TaskListThumbnailWnd",
        "Shell_InputSwitchTopLevelWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow",
        "Taskbar.View",
        "Taskbar.ViewForLaunch",
        "Windows.UI.Composition.DesktopWindowContentBridge",
    };

    public static List<OccupantInfo> Discover(NativeMethods.RECT taskbarRect, int ownPid, IntPtr taskbarHandle = default)
    {
        var result = new List<OccupantInfo>();
        int bandTop = taskbarRect.Top;
        int bandBottom = taskbarRect.Bottom;
        int bandHeight = taskbarRect.Height;

        bool Consider(IntPtr hwnd, bool isChild)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            if (Ignores.Contains(cls))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == ownPid)
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return true;
            }

            if (rect.Width < 40 || rect.Height < 16)
            {
                return true;
            }

            // 全宽的窗口是任务栏自己的背景层，不算占位者
            if (rect.Width >= taskbarRect.Width - 8)
            {
                return true;
            }

            // 必须落在任务栏那一条带子里（放宽 8px 容差）
            bool verticalOverlap = rect.Bottom > bandTop - 8 && rect.Top < bandBottom + 8;
            if (!verticalOverlap)
            {
                return true;
            }

            // 排除整屏 / 大窗口
            if (rect.Height > bandHeight * 2 && !isChild)
            {
                return true;
            }

            string processName = "?";
            try
            {
                using var process = Process.GetProcessById(pid);
                processName = process.ProcessName;
            }
            catch
            {
                // ignore
            }

            result.Add(new OccupantInfo
            {
                Handle = hwnd,
                ClassName = cls,
                ProcessName = processName,
                Pid = pid,
                Rect = rect,
                IsChild = isChild,
            });

            return true;
        }

        NativeMethods.EnumWindows((h, _) => Consider(h, false), IntPtr.Zero);

        IntPtr tray = taskbarHandle != IntPtr.Zero ? taskbarHandle : NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
        {
            NativeMethods.EnumChildWindows(tray, (h, _) => Consider(h, true), IntPtr.Zero);
        }

        return result;
    }

    /// <summary>
    /// 在任务栏可视宽度内，找一段宽度为 width、不被任何占位者压住的横向区间。
    /// 策略：从 preferredX 开始向右找第一个空档（占位者按左边界排序）。
    /// </summary>
    public static int FindFreeX(
        List<OccupantInfo> occupants,
        NativeMethods.RECT taskbarRect,
        int preferredX,
        int width,
        int gap,
        int limitX)
    {
        var blocks = occupants
            .Select(o => (
                Left: o.Rect.Left - taskbarRect.Left,
                Right: o.Rect.Right - taskbarRect.Left))
            .Where(b => b.Right > 0 && b.Left < taskbarRect.Width)
            .OrderBy(b => b.Left)
            .ToList();

        int x = Math.Max(0, preferredX);

        for (int guard = 0; guard < 24; guard++)
        {
            bool moved = false;
            foreach (var b in blocks)
            {
                bool overlap = x < b.Right + gap && x + width > b.Left - gap;
                if (overlap)
                {
                    int next = b.Right + gap;
                    if (next > x)
                    {
                        x = next;
                        moved = true;
                    }
                }
            }

            if (!moved)
            {
                break;
            }
        }

        if (x + width > limitX)
        {
            // 右侧也放不下：退回"通知区左侧"那种贴右对齐的位置
            x = Math.Max(0, limitX - width);
            for (int guard = 0; guard < 24; guard++)
            {
                bool moved = false;
                foreach (var b in blocks)
                {
                    bool overlap = x < b.Right + gap && x + width > b.Left - gap;
                    if (overlap)
                    {
                        int next = b.Left - gap - width;
                        if (next < x)
                        {
                            x = next;
                            moved = true;
                        }
                    }
                }

                if (!moved)
                {
                    break;
                }
            }
        }

        return Math.Max(0, x);
    }
}
