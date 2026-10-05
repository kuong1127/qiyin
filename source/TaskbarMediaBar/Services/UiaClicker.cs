using System.Windows.Automation;
using TaskbarMediaBar.Interop;

namespace TaskbarMediaBar.Services;

/// <summary>解析出来的可点击位置（客户区物理像素 + 屏幕像素）。</summary>
public sealed record ClickTarget(
    IntPtr Window,
    int ClientX,
    int ClientY,
    int ScreenX,
    int ScreenY,
    double Scale,
    bool UiaCoordsSuspect,
    string Note);

/// <summary>
/// 给「UIA 报假坐标」的自绘播放器（QQ音乐 就是）兜底点击。
///
/// 现象：元素的 BoundingRectangle 是垃圾值（顶层窗口被报成 -32000,-32000），
/// InvokePattern 由 MSAA→UIA 桥合成，调用不报错但什么都不发生。
///
/// 办法：用「最顶层祖先元素的 rect ↔ 真实客户区尺寸」自校准出一个比例，
/// 就能把垃圾坐标还原成真实客户区坐标，然后给窗口发真实鼠标消息。
/// </summary>
public static class UiaClicker
{
    public static ClickTarget? Resolve(AutomationElement element, IntPtr window)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
        {
            Log.Write("UiaClicker: 窗口句柄无效");
            return null;
        }

        // 窗口不可见/最小化时，UIA 报的是"最小化图标位置"(-32000,-32000)，坐标不可信。
        // 调用方应当先恢复窗口再来解析。
        bool iconic = NativeMethods.IsIconic(window);
        bool visible = NativeMethods.IsWindowVisible(window);

        if (!NativeMethods.GetClientRect(window, out var client) || client.Width <= 8 || client.Height <= 8)
        {
            Log.Write("UiaClicker: 客户区尺寸异常");
            return null;
        }

        if (!NativeMethods.GetWindowRect(window, out var wRect) || wRect.Width <= 8)
        {
            Log.Write("UiaClicker: 窗口尺寸异常");
            return null;
        }

        var rect = SafeRect(element);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            Log.Write("UiaClicker: 目标元素 rect 为空");
            return null;
        }

        // 元素中心（UIA 给的是屏幕坐标，物理像素）
        double screenCx = rect.Left + rect.Width / 2;
        double screenCy = rect.Top + rect.Height / 2;

        // 合理性检查：元素必须落在窗口矩形内
        bool inside = screenCx >= wRect.Left && screenCx <= wRect.Right
                      && screenCy >= wRect.Top && screenCy <= wRect.Bottom;
        bool suspect = !inside || iconic || !visible;

        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(window, ref origin))
        {
            return null;
        }

        int clientX = (int)Math.Round(screenCx - origin.X);
        int clientY = (int)Math.Round(screenCy - origin.Y);

        if (clientX < 0 || clientY < 0 || clientX > client.Width || clientY > client.Height)
        {
            Log.Write($"UiaClicker: 换算后越界 client=({clientX},{clientY}) 客户区 {client.Width}x{client.Height} 最小化={iconic} 可见={visible}");
            return null;
        }

        return new ClickTarget(
            window,
            clientX,
            clientY,
            (int)Math.Round(screenCx),
            (int)Math.Round(screenCy),
            1.0,
            suspect,
            $"元素 rect={rect.Left:F0},{rect.Top:F0} {rect.Width:F0}x{rect.Height:F0} 屏幕中心=({screenCx:F0},{screenCy:F0}) | 窗口={wRect.Left},{wRect.Top} {wRect.Width}x{wRect.Height} 客户区 {client.Width}x{client.Height} | 最小化={iconic} 可见={visible} | 可疑={suspect}");
    }

    public static AutomationElement? GetParentAny(AutomationElement el)
    {
        foreach (var walker in new[] { TreeWalker.ControlViewWalker, TreeWalker.RawViewWalker, TreeWalker.ContentViewWalker })
        {
            try
            {
                var parent = walker.GetParent(el);
                if (parent is not null)
                {
                    return parent;
                }
            }
            catch
            {
                // 换下一个 walker
            }
        }

        return null;
    }

    private static System.Windows.Rect SafeRect(AutomationElement el)
    {
        try
        {
            return el.Current.BoundingRectangle;
        }
        catch
        {
            return System.Windows.Rect.Empty;
        }
    }

    /// <summary>按顺序投递一次点击；QQ 的窗口处理可比确认信号慢，避免同步等它处理完每条消息。</summary>
    public static bool ClickByPostedMessage(ClickTarget target)
    {
        if (!NativeMethods.IsWindow(target.Window) || target.UiaCoordsSuspect) return false;
        var lp = MakeLParam(target.ClientX, target.ClientY);
        bool Post(uint message, IntPtr button) => NativeMethods.PostMessage(target.Window, message, button, lp);
        if (!Post(NativeMethods.WM_MOUSEMOVE, IntPtr.Zero)) return false;
        Thread.Sleep(20);
        bool down = false, up = false;
        try
        {
            down = Post(NativeMethods.WM_LBUTTONDOWN, new IntPtr(NativeMethods.MK_LBUTTON));
            if (down) Thread.Sleep(40);
        }
        finally { up = Post(NativeMethods.WM_LBUTTONUP, IntPtr.Zero); }
        Log.Write($"UiaClicker: 点击已排队 down={down} up={up} client=({target.ClientX},{target.ClientY})");
        return down && up;
    }

    /// <summary>给窗口直接发鼠标消息（不抢焦点，窗口被遮挡也能生效）。</summary>
    public static bool ClickByMessage(ClickTarget target)
    {
        try
        {
            IntPtr lp = MakeLParam(target.ClientX, target.ClientY);
            const uint timeoutMs = 250;
            bool Send(uint message, IntPtr button) => NativeMethods.SendMessageTimeout(
                target.Window, message, button, lp, NativeMethods.SMTO_ABORTIFHUNG,
                timeoutMs, out _) != IntPtr.Zero;
            if (!Send(NativeMethods.WM_MOUSEMOVE, IntPtr.Zero)) return false;
            Thread.Sleep(20);
            bool down = false;
            bool up = false;
            try
            {
                down = Send(NativeMethods.WM_LBUTTONDOWN, new IntPtr(NativeMethods.MK_LBUTTON));
                if (down) Thread.Sleep(40);
            }
            finally { up = Send(NativeMethods.WM_LBUTTONUP, IntPtr.Zero); }
            if (!down || !up) return false;
            Log.Write($"UiaClicker: 已发送鼠标消息 client=({target.ClientX},{target.ClientY}) {target.Note}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("UiaClicker: 鼠标消息投递失败 " + ex.Message);
            return false;
        }
    }

    /// <summary>真实鼠标点击（会短暂把目标窗口切到前台，随后尽量还原）。</summary>
    public static bool ClickByMouse(ClickTarget target)
    {
        IntPtr previousForeground = NativeMethods.GetForegroundWindow();
        bool movedCursor = false;
        int savedX = 0;
        int savedY = 0;

        try
        {
            if (NativeMethods.GetCursorPos(out var saved))
            {
                savedX = saved.X;
                savedY = saved.Y;
            }

            // 该点当前是不是我们的目标窗口？不是就先把窗口切上来
            var probe = new NativeMethods.POINT { X = target.ScreenX, Y = target.ScreenY };
            IntPtr hit = NativeMethods.WindowFromPoint(probe);
            NativeMethods.GetWindowThreadProcessId(target.Window, out int targetPid);
            NativeMethods.GetWindowThreadProcessId(hit, out int hitPid);

            if (hit == IntPtr.Zero || hitPid != targetPid)
            {
                NativeMethods.SwitchToThisWindow(target.Window, false);
                Thread.Sleep(220);
            }

            if (!NativeMethods.SetCursorPos(target.ScreenX, target.ScreenY))
            {
                return false;
            }

            movedCursor = true;
            Thread.Sleep(40);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(50);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            Log.Write($"UiaClicker: 已真实点击 screen=({target.ScreenX},{target.ScreenY})");

            return true;
        }
        catch (Exception ex)
        {
            Log.Write("UiaClicker: 真实点击失败 " + ex.Message);
            return false;
        }
        finally
        {
            if (movedCursor)
            {
                Thread.Sleep(80);
                NativeMethods.SetCursorPos(savedX, savedY);
            }

            if (previousForeground != IntPtr.Zero
                && previousForeground != target.Window
                && previousForeground != NativeMethods.GetForegroundWindow())
            {
                NativeMethods.SetForegroundWindow(previousForeground);
            }
        }
    }

    private static IntPtr MakeLParam(int x, int y) =>
        new((y << 16) | (x & 0xFFFF));
}
