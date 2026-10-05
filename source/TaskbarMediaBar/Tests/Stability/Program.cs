using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

internal static class Program
{
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    private delegate void WinEvent(IntPtr hook, uint evt, IntPtr hwnd, int objectId, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out int pid)) return 2;
        string mode = args[0], output = args[2];
        System.IO.Directory.CreateDirectory(output);
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        IntPtr bar = IntPtr.Zero;
        NativeMethods.EnumWindows((h, _) => { NativeMethods.GetWindowThreadProcessId(h, out int owner);
            if (owner == pid && NativeMethods.ReadWindowTitle(h).Contains("A1")) bar = h; return true; }, IntPtr.Zero);
        IntPtr player = NativeMethods.FindMainWindow("cloudmusic");
        if (bar == IntPtr.Zero || player == IntPtr.Zero) return 3;
        bool initialIconic = NativeMethods.IsIconic(player), initialVisible = NativeMethods.IsWindowVisible(player);
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        var root = AutomationElement.FromHandle(bar);
        AutomationElement Find(string id) => root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id)) ?? throw new InvalidOperationException(id);
        string originalTitle = NativeMethods.ReadWindowTitle(player);
        bool SameTrack() => NativeMethods.ReadWindowTitle(player) == originalTitle;
        NativeMethods.GetWindowRect(bar, out var bounds);
        var events = new List<object>();
        var samples = new List<object>();
        var clock = Stopwatch.StartNew();
        int shown = 0, hidden = 0, moved = 0, barActivated = 0, restoredDuringClick = 0, blankFrames = 0;
        string phase = "setup";
        WinEvent callback = (_, evt, h, obj, child, thread, time) =>
        {
            if (h != player && h != bar) return;
            if (obj != 0 || child != 0) return;
            events.Add(new { ms = clock.ElapsedMilliseconds, phase, target = h == bar ? "bar" : "player", evt });
            if (h == bar && evt == 0x8002) shown++;
            if (h == bar && evt == 0x8003) hidden++;
            if (h == bar && evt == 0x800B) moved++;
            if (h == bar && evt == 3) barActivated++;
            if (h == player && phase.StartsWith("click") && evt == 0x17) restoredDuringClick++;
        };
        IntPtr hook = SetWinEventHook(3, 0x800B, IntPtr.Zero, callback, 0, 0, 0);
        void Pump(int ms) { var t = Stopwatch.StartNew(); do { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); } while (t.ElapsedMilliseconds < ms); }
        Bitmap Capture()
        {
            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }
        void Sample()
        {
            using var bmp = Capture();
            var first = Find("PrevBtn").Current.BoundingRectangle;
            var next = Find("NextBtn").Current.BoundingRectangle;
            int left = Math.Clamp((int)first.Left - bounds.Left, 0, bmp.Width - 1);
            int right = Math.Clamp((int)next.Right - bounds.Left, left + 1, bmp.Width);
            int ink = 0, area = 0;
            for (int y = 9; y < bmp.Height - 9; y++) for (int x = left; x < right; x++)
            { var c = bmp.GetPixel(x, y); if (Math.Min(c.R, Math.Min(c.G, c.B)) < 160) ink++; area++; }
            bool blank = ink < 10 || ink > area * .9;
            if (blank) { blankFrames++; bmp.Save(System.IO.Path.Combine(output, $"异常帧_{blankFrames}.png"), ImageFormat.Png); }
            NativeMethods.GetWindowRect(bar, out var current);
            bool iconic = NativeMethods.IsIconic(player);
            if (phase.StartsWith("click") && !iconic) restoredDuringClick++;
            samples.Add(new { ms = clock.ElapsedMilliseconds, phase, barVisible = NativeMethods.IsWindowVisible(bar),
                rect = current.ToString(), iconic, ink, blank, foregroundIsBar = NativeMethods.GetForegroundWindow() == bar });
        }
        bool added = false, restoredLike = false, passed = false;
        bool? originalLike = null;
        try
        {
            if (mode is "open" or "cover" or "taskbar")
            {
                NativeMethods.ShowWindow(player, NativeMethods.SW_MINIMIZE); Pump(350);
                phase = "before-open";
                for (int i = 0; i < 8; i++) { Sample(); Pump(25); }
                phase = "open-player";
                if (mode is "cover" or "taskbar")
                {
                    if ((GetAsyncKeyState(1) & 0x8000) != 0) return 10;
                    NativeMethods.GetCursorPos(out var cursor);
                    int x = bounds.Left + (int)Math.Round(21 * NativeMethods.GetDpiForWindow(bar) / 96.0);
                    int y = bounds.Top + bounds.Height / 2;
                    if (mode == "taskbar")
                    {
                        var tray = AutomationElement.FromHandle(NativeMethods.FindWindow("Shell_TrayWnd", null));
                        var playerButton = tray.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                            .Cast<AutomationElement>().FirstOrDefault(b => b.Current.Name.Contains("网易云"));
                        if (playerButton is null) return 11;
                        var r = playerButton.Current.BoundingRectangle;
                        x = (int)(r.Left + r.Width / 2); y = (int)(r.Top + r.Height / 2);
                    }
                    NativeMethods.SetCursorPos(x, y);
                    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                    Thread.Sleep(15);
                    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                    Pump(50);
                    NativeMethods.GetCursorPos(out var currentCursor);
                    if (currentCursor.X == x && currentCursor.Y == y) NativeMethods.SetCursorPos(cursor.X, cursor.Y);
                }
                else
                {
                    NativeMethods.ShowWindow(player, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(player);
                }
                for (int i = 0; i < 50; i++) { Sample(); Pump(25); }
                using var image = Capture(); image.Save(System.IO.Path.Combine(output, "打开网易云后的播控.png"), ImageFormat.Png);
                passed = hidden == 0 && shown == 0 && blankFrames == 0 && !NativeMethods.IsIconic(player)
                    && (mode == "open" || barActivated == 0);
            }
            else if (mode == "like")
            {
                // 显式准备真实初始状态；下面 click 阶段禁止任何窗口恢复。
                NativeMethods.ShowWindow(player, NativeMethods.SW_SHOWNOACTIVATE); Pump(2300);
                originalLike = NeteaseWindowState.Read().Liked;
                if (originalLike != false || !SameTrack()) return 7;
                var heart = Find("LikeBtn");
                if (!heart.Current.Name.Contains("加入")) return 8;
                foreach (bool desired in new[] { true, false })
                {
                    NativeMethods.ShowWindow(player, NativeMethods.SW_MINIMIZE); Pump(250);
                    phase = desired ? "click-add" : "click-remove";
                    var elapsed = Stopwatch.StartNew();
                    ((InvokePattern)heart.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    while (heart.Current.Name.Contains("正在") && elapsed.ElapsedMilliseconds < 2000) Pump(10);
                    double feedbackMs = elapsed.Elapsed.TotalMilliseconds;
                    for (int i = 0; i < 18; i++) { Sample(); Pump(20); }
                    string label = heart.Current.Name;
                    if (desired) { using var red = Capture(); red.Save(System.IO.Path.Combine(output, "后台点击红心.png"), ImageFormat.Png); }
                    phase = "manual-verify";
                    NativeMethods.ShowWindow(player, NativeMethods.SW_SHOWNOACTIVATE); Pump(250);
                    var actual = NeteaseWindowState.Read();
                    bool matches = SameTrack() && actual.Fresh && actual.Liked == desired;
                    if (desired) added = matches; else restoredLike = matches;
                    events.Add(new { phase = desired ? "add-result" : "remove-result", feedbackMs, label, actual, matches });
                    if (!matches) break;
                    Pump(1500);
                }
                passed = added && restoredLike && restoredDuringClick == 0 && hidden == 0 && blankFrames == 0;
            }
            else return 2;
        }
        finally
        {
            phase = "cleanup";
            if (originalLike == false && SameTrack())
            {
                NativeMethods.ShowWindow(player, NativeMethods.SW_SHOWNOACTIVATE); Pump(180);
                var current = NeteaseWindowState.Read();
                if (current.Liked == true)
                {
                    HotkeySender.Send("CTRL+ALT+L", out _); Pump(350);
                    restoredLike = NeteaseWindowState.Read().Liked == false;
                }
            }
            if (initialIconic) NativeMethods.ShowWindow(player, NativeMethods.SW_MINIMIZE);
            else if (!initialVisible) NativeMethods.ShowWindow(player, NativeMethods.SW_HIDE);
            NativeMethods.SetForegroundWindow(foreground);
            Pump(100);
            UnhookWinEvent(hook);
            var report = new { at = DateTimeOffset.Now, mode, pid, passed, added, restoredLike, originalLike,
                shown, hidden, moved, barActivated, restoredDuringClick, blankFrames, frames = samples.Count, events, samples };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(System.IO.Path.Combine(output, "实机检查.json"), json);
            Console.WriteLine(JsonSerializer.Serialize(new { mode, passed, added, restoredLike, shown, hidden, moved, barActivated, restoredDuringClick, blankFrames, frames = samples.Count }));
            GC.KeepAlive(callback);
        }
        return passed ? 0 : 9;
    }
}
