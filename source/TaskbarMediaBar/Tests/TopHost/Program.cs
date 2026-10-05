using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
        Directory.CreateDirectory(args[0]);
        Environment.SetEnvironmentVariable("TMB_DATA_DIR", args[0]);
        var results = new List<object>();
        var stability = new List<object>();
        int failed = 0;
        void Check(string name, bool passed) { results.Add(new { name, passed }); if (!passed) failed++; }
        NativeMethods.RECT Rect(int x, int y, int width, int height) => new() { Left = x, Top = y, Right = x + width, Bottom = y + height };
        var legacy = JsonSerializer.Deserialize<AppSettings>("{\"Mode\":\"dock\",\"Anchor\":\"beforeTray\",\"Offset\":321,\"Width\":455}")!;
        Check("legacy settings retain taskbar default and previous values", legacy.Placement == "taskbar" && legacy.Mode == "dock"
            && legacy.Anchor == "beforeTray" && legacy.Offset == 321 && legacy.Width == 455);
        Check("top defaults", legacy.TopAnchor == "center" && legacy.TopMonitor == "primary" && legacy.TopOffsetX == 0 && legacy.TopOffsetY == 8);
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2, 2.5, 3 })
        foreach (var bounds in new[] { Rect(0, 0, 1920, 1080), Rect(-2560, -200, 2560, 1440), Rect(200, -1080, 1920, 1080), Rect(-100, 20, 60, 24) })
        foreach (string anchor in new[] { "left", "center", "right", "UNKNOWN" })
        foreach (int offset in new[] { int.MinValue, -500, 0, 500, int.MaxValue })
        {
            var screen = new TopScreenInfo { MonitorRect = bounds, WorkRect = Rect(bounds.Left, bounds.Top + 30, bounds.Width, bounds.Height - 30), Scale = scale };
            var layout = TopScreenHost.ComputeLayout(screen, new AppSettings { TopAnchor = anchor, TopOffsetX = offset, TopOffsetY = offset }, 455, 40);
            Check($"bounds {scale}/{bounds}/{anchor}/{offset}", layout.Fits && layout.X >= bounds.Left && layout.Y >= bounds.Top
                && (long)layout.X + layout.Width <= bounds.Right && (long)layout.Y + layout.Height <= bounds.Bottom);
        }
        var standard = new TopScreenInfo { MonitorRect = Rect(-1920, -50, 1920, 1080), Scale = 1.5 };
        var center = TopScreenHost.ComputeLayout(standard, new AppSettings(), 200, 40);
        Check("DIP dimensions and top offset convert once", center == new TopScreenLayout(-1110, -38, 300, 60));
        Check("left anchor", TopScreenHost.ComputeLayout(standard, new AppSettings { TopAnchor = "left", TopOffsetX = 10 }, 200, 40).X == -1905);
        Check("right anchor", TopScreenHost.ComputeLayout(standard, new AppSettings { TopAnchor = "right", TopOffsetX = -10 }, 200, 40).X == -315);
        Check("short dimensions still honor top offset", TopScreenHost.ComputeLayout(standard, new AppSettings { TopOffsetY = 400 }, 64, 8)
            == new TopScreenLayout(-1008, 550, 96, 12));
        Check("invalid bounds hide", !TopScreenHost.ComputeLayout(new TopScreenInfo(), new AppSettings(), 200, 40).Fits);
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1 })
            Check($"invalid dimension hides {invalid}", !TopScreenHost.ComputeLayout(standard, new AppSettings(), invalid, 40).Fits);
        var invalidScale = new TopScreenInfo { MonitorRect = standard.MonitorRect, Scale = double.NaN };
        Check("invalid scale uses safe DIP baseline", TopScreenHost.ComputeLayout(invalidScale, new AppSettings(), 200, 40).Width == 200);
        Check("huge dimension clips without overflow", TopScreenHost.ComputeLayout(standard, new AppSettings(), double.MaxValue, double.MaxValue)
            == new TopScreenLayout(-1920, -50, 1920, 1080));
        var roundtrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { Placement = "top", TopAnchor = "right", TopMonitor = "cursor", TopOffsetX = -20, TopOffsetY = 12 }))!;
        Check("top settings roundtrip", roundtrip.Placement == "top" && roundtrip.TopAnchor == "right" && roundtrip.TopMonitor == "cursor" && roundtrip.TopOffsetX == -20 && roundtrip.TopOffsetY == 12);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window { Width = 360, Height = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Title = "Qiyin Top Host Checks" };
        try
        {
            var screen = TopScreenHost.Pick(new AppSettings());
            Check("independent primary monitor enumeration", screen is not null && screen.Primary && screen.Scale > 0 && screen.MonitorRect.Width > 0);
            var cursorScreen = TopScreenHost.Pick(new AppSettings { TopMonitor = "cursor" });
            Check("independent cursor monitor selection", NativeMethods.GetCursorPos(out var point) && cursorScreen?.Handle
                == NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST));
            if (screen is not null)
            {
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                var foreground = NativeMethods.GetForegroundWindow();
                for (int i = 0; i < 12; i++)
                {
                    window.Width = new[] { 220, 360, 455, 720 }[i % 4];
                    window.Height = 40;
                    var settings = new AppSettings { TopAnchor = i % 3 == 0 ? "left" : i % 3 == 1 ? "center" : "right" };
                    TopScreenHost.Place(window, screen, settings);
                    app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    var wanted = TopScreenHost.ComputeLayout(screen, settings, window.Width, window.Height);
                    Check($"native rectangle {i}", NativeMethods.GetWindowRect(hwnd, out var actual) && actual.Left == wanted.X && actual.Top == wanted.Y
                        && actual.Width == wanted.Width && actual.Height == wanted.Height);
                    Check($"no foreground change {i}", foreground == NativeMethods.GetForegroundWindow());
                    TopScreenHost.Hide(window);
                    Check($"native hide {i}", !NativeMethods.IsWindowVisible(hwnd));
                }
                long styles = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                Check("native no-activate and tool-window", (styles & (NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW))
                    == (NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
                // WPF may use its own hidden helper as owner when ShowInTaskbar=false.
                var owner = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT);
                NativeMethods.GetWindowThreadProcessId(owner, out int ownerPid);
                Check("no external taskbar owner", owner == IntPtr.Zero || ownerPid == Environment.ProcessId);
                window.Width = 455;
                window.Height = 40;
                TopScreenHost.Place(window, screen, new AppSettings { TopOffsetY = 24 });
                Check("native long bar honors requested top offset", NativeMethods.GetWindowRect(hwnd, out var bar)
                    && bar.Top == screen.MonitorRect.Top + Math.Round(24 * screen.Scale)
                    && Math.Abs(bar.Width - 455 * screen.Scale) <= 1 && Math.Abs(bar.Height - 40 * screen.Scale) <= 1);
                Console.WriteLine($"Requested long bar=455x40 DIP offset=24; scale={screen.Scale}; native={bar}");
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                int positionEvents = 0;
                HwndSourceHook observer = (IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg == 0x0046 /* WM_WINDOWPOSCHANGING */) positionEvents++;
                    return IntPtr.Zero;
                };
                var source = HwndSource.FromHwnd(hwnd);
                source.AddHook(observer);
                var stableSettings = new AppSettings { TopOffsetY = 24 };
                int stableWidth = 0;
                for (int i = 0; i < 16; i++)
                {
                    var wanted = TopScreenHost.ComputeLayout(screen, stableSettings, window.Width, window.Height);
                    int beforeEvents = positionEvents;
                    TopScreenHost.Place(window, screen, stableSettings);
                    app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    NativeMethods.GetWindowRect(hwnd, out var actual);
                    if (i == 0) stableWidth = actual.Width;
                    stability.Add(new { iteration = i, logicalWidth = window.Width, actual = actual.ToString(),
                        expected = wanted, positionEvents = positionEvents - beforeEvents });
                    Check($"repeated native placement keeps width stable {i}", actual.Width == stableWidth);
                    if (i >= 3) Check($"settled placement avoids redundant native work {i}", positionEvents == beforeEvents);
                }
                source.RemoveHook(observer);
                TopScreenHost.Hide(window);
                var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                Check("work area unchanged", NativeMethods.GetMonitorInfo(screen.Handle, ref info) && info.rcWork.Left == screen.WorkRect.Left
                    && info.rcWork.Top == screen.WorkRect.Top && info.rcWork.Right == screen.WorkRect.Right && info.rcWork.Bottom == screen.WorkRect.Bottom);
                window.MinWidth = 100;
                var tiny = new TopScreenInfo { MonitorRect = Rect(screen.MonitorRect.Left, screen.MonitorRect.Top, 60, 24), Scale = screen.Scale };
                TopScreenHost.Place(window, tiny, new AppSettings());
                Check("too small for declared minimum hides", !NativeMethods.IsWindowVisible(hwnd));
                window.MinWidth = 0;
                window.Width = 455;
                window.Height = 40;
                TopScreenHost.Place(window, tiny, new AppSettings());
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check("native tiny screen remains within bounds", NativeMethods.GetWindowRect(hwnd, out var clipped)
                    && clipped.Left >= tiny.MonitorRect.Left && clipped.Top >= tiny.MonitorRect.Top
                    && clipped.Right <= tiny.MonitorRect.Right && clipped.Bottom <= tiny.MonitorRect.Bottom);
                TopScreenHost.Hide(window);
            }
        }
        catch (Exception ex) { Check("native exception " + ex, false); }
        finally { window.Close(); app.Shutdown(); }
        File.WriteAllText(Path.Combine(args[0], "top-host-results.json"), JsonSerializer.Serialize(new { total = results.Count, failed, results, stability }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Top host: {results.Count - failed}/{results.Count} passed; failed={failed}");
        return failed == 0 ? 0 : 1;
    }
}
