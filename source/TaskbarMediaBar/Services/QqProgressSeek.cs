using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using TaskbarMediaBar.Interop;

namespace TaskbarMediaBar.Services;

/// <summary>QQ 不处理系统 Seek。通过经过时间标签校验的原生播放条发送一次点击。</summary>
public static class QqProgressSeek
{
    public sealed class WindowState : IDisposable
    {
        private IntPtr _window;
        private readonly bool _iconic, _visible;
        public WindowState(IntPtr window, bool iconic, bool visible) { _window = window; _iconic = iconic; _visible = visible; }
        public void Dispose()
        {
            var hwnd = Interlocked.Exchange(ref _window, IntPtr.Zero);
            if (NativeMethods.IsWindow(hwnd) && NativeMethods.GetForegroundWindow() != hwnd)
            {
                if (_iconic) NativeMethods.ShowWindow(hwnd, 7);
                else if (!_visible) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            }
        }
    }
    public static bool Supports(string appId) => appId.Equals("QQMusic.exe", StringComparison.OrdinalIgnoreCase);

    public static WindowState? Send(double seconds, double duration, Func<bool> current, CancellationToken token)
    {
        if (token.IsCancellationRequested || !current() || !double.IsFinite(seconds) || duration <= 0) return null;
        var hwnd = NativeMethods.FindMainWindow("QQMusic");
        if (hwnd == IntPtr.Zero) return null;
        bool iconic = NativeMethods.IsIconic(hwnd), visible = NativeMethods.IsWindowVisible(hwnd);
        var restoration = new WindowState(hwnd, iconic, visible);
        bool handedOff = false;
        string title = NativeMethods.ReadWindowTitle(hwnd);
        bool Valid() => !token.IsCancellationRequested && current() && NativeMethods.IsWindow(hwnd)
            && NativeMethods.ReadWindowTitle(hwnd) == title;
        try
        {
            var elapsed = Stopwatch.StartNew();
            // QQ 在最小化时不处理播放条点击。仅后台恢复布局，绝不切前台或移动鼠标。
            if (iconic || !visible) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            if (NativeMethods.GetClientRect(hwnd, out var hoverArea))
            {
                var point = new IntPtr((Math.Max(1, hoverArea.Height - 20) << 16) | (hoverArea.Width / 2 & 0xffff));
                NativeMethods.PostMessage(hwnd, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, point);
                Thread.Sleep(220); // QQ 沉浸歌词页需要先展开自动隐藏的播放条。
            }
            var timer = Stopwatch.StartNew();
            string diagnostic = "";
            while (Valid() && timer.ElapsedMilliseconds < 1200)
            {
                var root = AutomationElement.FromHandle(hwnd);
                var cache = new CacheRequest();
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.BoundingRectangleProperty);
                AutomationElement[] nodes;
                using (cache.Activate()) nodes = root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
                var times = nodes.Where(n => Regex.IsMatch(n.Cached.Name.Trim(), @"^\d{1,3}:\d{2}$"))
                    .OrderBy(n => n.Cached.BoundingRectangle.Left).ToArray();
                diagnostic = $"nodes={nodes.Length} timeLabels={string.Join('|', times.Select(t => t.Cached.Name))} duration={duration:F3}";
                if (times.Length == 2 && TrySeconds(times[1].Cached.Name, out var total) && Math.Abs(total - duration) < 2)
                {
                    var left = times[0].Cached.BoundingRectangle; var right = times[1].Cached.BoundingRectangle;
                    var candidates = nodes.Select(n => n.Cached.BoundingRectangle).Where(r => !r.IsEmpty
                        && r.Width > 100 && r.Height is > 0 and < 50 && r.Left >= left.Right && r.Right <= right.Left
                        && r.Top < left.Bottom && r.Bottom > left.Top).Distinct().ToArray();
                    diagnostic += $" candidates={candidates.Length}";
                    if (candidates.Length == 1 && NativeMethods.GetWindowRect(hwnd, out var window)
                        && NativeMethods.GetClientRect(hwnd, out var client))
                    {
                        var track = candidates[0];
                        var origin = new NativeMethods.POINT();
                        NativeMethods.ClientToScreen(hwnd, ref origin);
                        // QQ 滑轨两端各留 4 DIP 的滑块边距；用实际行程计算，避免越靠两端偏差越大。
                        double inset = 4 * NativeMethods.GetDpiForWindow(hwnd) / 96.0;
                        int x = (int)Math.Round(track.Left - origin.X + inset + (track.Width - 2 * inset) * Math.Clamp(seconds / duration, 0, 1));
                        int y = (int)Math.Round(track.Top - origin.Y + track.Height / 2);
                        diagnostic += $" client={x},{y} bounds={client.Width}x{client.Height}";
                        // 恢复后的 UIA 可能短暂保留旧布局；等坐标进入真实客户区再发送。
                        if (x >= 0 && x < client.Width && y >= 0 && y < client.Height && Valid())
                        {
                            var point = new ClickTarget(hwnd, x, y, origin.X + x, origin.Y + y, 1, false, "QQ播放进度");
                            Log.Write($"QQ进度定位: {elapsed.ElapsedMilliseconds} ms client={x},{y} ratio={seconds / duration:F4} rect={track}");
                            bool sent = UiaClicker.ClickByPostedMessage(point);
                            if (!sent || !Valid()) return null;
                            handedOff = true;
                            return restoration;
                        }
                    }
                }
                Thread.Sleep(100);
            }
            Log.Write("QQ进度定位未完成: " + diagnostic);
            return null;
        }
        catch (Exception ex) { Log.Write("QQ 进度定位失败: " + ex.Message); return null; }
        finally
        {
            if (!handedOff) restoration.Dispose();
        }
    }

    private static bool TrySeconds(string text, out double value)
    {
        value = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds >= 60) return false;
        value = minutes * 60 + seconds;
        return true;
    }
}
