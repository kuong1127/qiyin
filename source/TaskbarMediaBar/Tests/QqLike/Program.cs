using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int result = 1;
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => { try { result = await Run(args); } catch (Exception ex) { Console.WriteLine(ex); } finally { app.Shutdown(); } };
        app.Run(); return result;
    }
    private static async Task<int> Run(string[] args)
    {
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[1])) return 2;
        var hwnd = NativeMethods.FindMainWindow("QQMusic");
        if (hwnd == IntPtr.Zero) return 3;
        var records = new List<object>();
        void Record(object data) { records.Add(data); Console.WriteLine(JsonSerializer.Serialize(data)); }
        var watch = Stopwatch.StartNew();
        Record(new { window = hwnd.ToInt64(), title = NativeMethods.ReadWindowTitle(hwnd), visible = NativeMethods.IsWindowVisible(hwnd), minimized = NativeMethods.IsIconic(hwnd) });
        var root = AutomationElement.FromHandle(hwnd);
        if (args[0] == "bar-cycle")
        {
            var process = Process.GetProcessesByName("TaskbarMediaBar").Single(p => p.MainModule!.FileName.Contains("20260914_A1播控_v01"));
            if (process.MainModule!.FileVersionInfo.FileVersion != "0.2.8.0") return 17;
            IntPtr bar = IntPtr.Zero;
            NativeMethods.EnumWindows((h, _) => { NativeMethods.GetWindowThreadProcessId(h, out int p);
                if (p == process.Id && NativeMethods.ReadWindowTitle(h).Contains("A1")) bar = h; return true; }, IntPtr.Zero);
            if (bar == IntPtr.Zero) return 18;
            var barRoot = AutomationElement.FromHandle(bar);
            var button = barRoot.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "LikeBtn"));
            if (button is null) return 19;
            var initial = QqLikeService.Probe(true);
            if (!initial.Liked.HasValue || !initial.Confirmed) return 20;
            string title = NativeMethods.ReadWindowTitle(hwnd);
            bool iconic = NativeMethods.IsIconic(hwnd), visible = NativeMethods.IsWindowVisible(hwnd);
            var foreground = NativeMethods.GetForegroundWindow();
            bool changed = false, restored = false, preserved = true;
            bool qqStatePreserved = true;
            var focusChanges = new List<long>();
            using var monitoring = new CancellationTokenSource();
            var monitor = Task.Run(async () => { while (!monitoring.IsCancellationRequested) {
                preserved &= NativeMethods.IsIconic(hwnd) == iconic && NativeMethods.IsWindowVisible(hwnd) == visible && NativeMethods.GetForegroundWindow() == foreground;
                qqStatePreserved &= NativeMethods.IsIconic(hwnd) == iconic && NativeMethods.IsWindowVisible(hwnd) == visible;
                var focused = NativeMethods.GetForegroundWindow();
                if (focused != foreground && !focusChanges.Contains(focused.ToInt64())) focusChanges.Add(focused.ToInt64());
                await Task.Delay(15);
            } });
            void Click()
            {
                var r = button.Current.BoundingRectangle;
                int x = (int)(r.Left + r.Width / 2), y = (int)(r.Top + r.Height / 2);
                NativeMethods.GetCursorPos(out var saved);
                try
                {
                    NativeMethods.SetCursorPos(x, y);
                    NativeMethods.GetWindowThreadProcessId(NativeMethods.WindowFromPoint(new() { X = x, Y = y }), out int hit);
                    if (hit != process.Id) throw new InvalidOperationException("组件按钮被遮挡，未点击");
                    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                    Thread.Sleep(35);
                    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                }
                finally
                {
                    NativeMethods.GetCursorPos(out var now);
                    if (now.X == x && now.Y == y) NativeMethods.SetCursorPos(saved.X, saved.Y);
                }
            }
            void Capture(string name)
            {
                NativeMethods.GetWindowRect(bar, out var r);
                typeof(LikeService).Assembly.GetType("TaskbarMediaBar.Services.ScreenShot")!.GetMethod("Capture")!
                    .Invoke(null, new object[] { Path.Combine(Path.GetDirectoryName(args[1])!, name + ".png"), r.Left, r.Top, r.Width, r.Height });
            }
            try
            {
                Capture("组件初始状态"); watch.Restart(); Click();
                for (int i = 0; i < 50 && NativeMethods.ReadWindowTitle(hwnd) == title; i++)
                {
                    var state = QqLikeService.Probe();
                    if (state.Confirmed && state.Liked == !initial.Liked.Value) { changed = true; break; }
                    await Task.Delay(50);
                }
                await Task.Delay(120); Capture("组件点击后状态");
                Record(new { mode = args[0], initial = initial.Liked, changed, elapsedMs = watch.ElapsedMilliseconds });
            }
            finally
            {
                if (changed && NativeMethods.ReadWindowTitle(hwnd) == title)
                {
                    Click();
                    for (int i = 0; i < 50 && NativeMethods.ReadWindowTitle(hwnd) == title; i++)
                    {
                        var state = QqLikeService.Probe();
                        if (state.Confirmed && state.Liked == initial.Liked) { restored = true; break; }
                        await Task.Delay(50);
                    }
                }
                monitoring.Cancel(); await monitor;
                Record(new { restored, windowAndFocusPreservedThroughout = preserved, qqStatePreserved,
                    foregroundBefore = foreground.ToInt64(), focusChanges });
                File.WriteAllText(args[1], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            }
            return changed && restored && preserved ? 0 : 21;
        }
        if (args[0] == "lyrics-live")
        {
            using var lyrics = new LyricsService();
            int successes = 0;
            foreach (var song in new[] { ("飘向北方 (Live)", "尤长靖/那吾克热-NW", 258.0), ("起风了 (旧版)", "买辣椒也用券", 312.0) })
            {
                watch.Restart();
                var doc = await lyrics.FetchAsync(song.Item1, song.Item2, sourceAppId: "QQMusic.exe", durationSeconds: song.Item3);
                Record(new { title = song.Item1, found = doc is not null, doc?.Source, lines = doc?.Lines.Count, doc?.TrackId,
                    elapsedMs = watch.ElapsedMilliseconds, hasTimedLine = doc is not null && doc.Lines.Any(l => l.StartMs > 10000 && l.Text.Length > 0) });
                if (doc is not null) successes++;
            }
            File.WriteAllText(args[1], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            return successes == 2 ? 0 : 14;
        }
        if (args[0] == "service-cycle")
        {
            string title = NativeMethods.ReadWindowTitle(hwnd);
            bool iconic = NativeMethods.IsIconic(hwnd), visible = NativeMethods.IsWindowVisible(hwnd);
            var foreground = NativeMethods.GetForegroundWindow();
            var before = QqLikeService.Probe(true);
            if (!before.Liked.HasValue || !before.Confirmed || NativeMethods.ReadWindowTitle(hwnd) != title) return 15;
            bool changed = false, restored = false, preserved = true;
            using var monitoring = new CancellationTokenSource();
            var monitor = Task.Run(async () => {
                while (!monitoring.IsCancellationRequested)
                {
                    preserved &= NativeMethods.IsIconic(hwnd) == iconic && NativeMethods.IsWindowVisible(hwnd) == visible
                        && NativeMethods.GetForegroundWindow() == foreground;
                    await Task.Delay(15);
                }
            });
            bool Current() => NativeMethods.ReadWindowTitle(hwnd) == title;
            try
            {
                watch.Restart();
                var result = await Task.Run(() => LikeService.Toggle("QQMusic.exe", new AppSettings(), Current, !before.Liked.Value));
                changed = result.Verified && result.LikedAfter == !before.Liked.Value;
                Record(new { mode = args[0], before = before.Liked, changed, elapsedMs = watch.ElapsedMilliseconds, result.Message, result.Tier });
            }
            finally
            {
                if (changed && Current())
                {
                    var result = await Task.Run(() => LikeService.Toggle("QQMusic.exe", new AppSettings(), Current, before.Liked.Value));
                    restored = result.Verified && result.LikedAfter == before.Liked;
                }
                monitoring.Cancel(); await monitor;
                Record(new { restored, windowAndFocusPreservedThroughout = preserved, minimized = NativeMethods.IsIconic(hwnd) });
                File.WriteAllText(args[1], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            }
            return changed && restored && preserved ? 0 : 16;
        }
        var cache = new CacheRequest();
        foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.AutomationIdProperty, AutomationElement.BoundingRectangleProperty,
            AutomationElement.ControlTypeProperty, AutomationElement.AcceleratorKeyProperty, AutomationElement.AccessKeyProperty }) cache.Add(property);
        AutomationElement[] nodes;
        using (cache.Activate()) nodes = root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
        Record(new { nodes = nodes.Length, elapsedMs = watch.ElapsedMilliseconds });
        foreach (var n in nodes)
            if (n.Cached.Name.Length > 0 || n.Cached.AutomationId.Length > 0)
                Record(new { name = n.Cached.Name, id = n.Cached.AutomationId, rect = n.Cached.BoundingRectangle.ToString(), type = n.Cached.ControlType.ProgrammaticName,
                    accelerator = n.Cached.AcceleratorKey, access = n.Cached.AccessKey });
        File.WriteAllText(args[1], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        await Task.CompletedTask;
        return 0;
    }
}
