using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarMediaBar;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    private static void Foreground(IntPtr hwnd)
    {
        uint current = GetCurrentThreadId();
        uint active = (uint)NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        bool attached = current != active && active != 0 && AttachThreadInput(current, active, true);
        try { NativeMethods.SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(current, active, false); }
        if (NativeMethods.GetForegroundWindow() != hwnd && NativeMethods.GetWindowRect(hwnd, out var r))
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
            NativeMethods.GetCursorPos(out var saved);
            var point = new NativeMethods.POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 };
            if (NativeMethods.WindowFromPoint(point) == hwnd)
            {
                NativeMethods.SetCursorPos(point.X, point.Y);
                NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                NativeMethods.SetCursorPos(saved.X, saved.Y);
            }
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args is ["--host"])
        {
            var host = new Window { Title = "Qiyin Fullscreen Verification", Width = 640, Height = 360,
                Background = Brushes.Black, ShowInTaskbar = false };
            host.Closed += (_, _) => app.Shutdown();
            app.Run(host);
            return 0;
        }
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
        Directory.CreateDirectory(args[0]);
        Environment.SetEnvironmentVariable("TMB_DATA_DIR", args[0]);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value) => checks.Add(name, value);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = NativeMethods.GetForegroundWindow();
        app.Startup += async (_, _) =>
        {
            Process? helper = null;
            BarController? controller = null;
            MainWindow? barWindow = null;
            try
            {
                var m = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
                Check("full monitor", FullscreenDetector.CoversMonitor(m, m));
                Check("maximized work area remains visible", !FullscreenDetector.CoversMonitor(new() { Right = 1920, Bottom = 1032 }, m));
                Check("other monitor remains visible", !FullscreenDetector.CoversMonitor(new() { Left = 1920, Right = 3840, Bottom = 1080 }, m));
                Check("negative monitor coordinates", FullscreenDetector.CoversMonitor(new() { Left = -2560, Top = -200, Right = 0, Bottom = 1240 }, new() { Left = -2560, Top = -200, Right = 0, Bottom = 1240 }));
                Check("invalid monitor ignored", !FullscreenDetector.CoversMonitor(m, default));
                var bar = TaskbarHost.DiscoverPrimary() ?? throw new Exception("No taskbar");
                controller = new BarController(new AppSettings { Glass = false, IdleHint = false, HideWhenNoMedia = true });
                controller.Media.Current.HasSession = true;
                typeof(BarController).GetMethod("EnsureWindow", flags)!.Invoke(controller, null);
                barWindow = (MainWindow)typeof(BarController).GetField("_window", flags)!.GetValue(controller)!;
                var barHwnd = new WindowInteropHelper(barWindow).Handle;
                void Refresh() => typeof(BarController).GetMethod("RefreshFullscreenVisibility", flags)!.Invoke(controller, null);
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                info.ArgumentList.Add("--host");
                helper = Process.Start(info)!;
                IntPtr h = IntPtr.Zero;
                for (int i = 0; i < 50 && h == IntPtr.Zero; i++)
                {
                    await Task.Delay(100);
                    NativeMethods.EnumWindows((w, _) => { NativeMethods.GetWindowThreadProcessId(w, out int p); if (p == helper.Id && NativeMethods.IsWindowVisible(w) && NativeMethods.ReadWindowTitle(w) == "Qiyin Fullscreen Verification") h = w; return true; }, IntPtr.Zero);
                }
                if (h == IntPtr.Zero) throw new Exception("No helper window");
                NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
                Foreground(h);
                await Task.Delay(150);
                Check("helper foreground confirmed", NativeMethods.GetForegroundWindow() == h);
                NativeMethods.GetClientRect(h, out var debugClient);
                File.WriteAllText(Path.Combine(args[0], "window-state.txt"), $"helper={h}; foreground={NativeMethods.GetForegroundWindow()}; iconic={NativeMethods.IsIconic(h)}; visible={NativeMethods.IsWindowVisible(h)}; client={debugClient}; style={NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_STYLE)}");
                Check("normal native window", !FullscreenDetector.IsActiveOn(bar.MonitorRect));
                controller.Reposition();
                Check("bar visible normally", NativeMethods.IsWindowVisible(barHwnd));
                NativeMethods.ShowWindow(h, 3);
                await Task.Delay(150);
                Check("maximized native window", !FullscreenDetector.IsActiveOn(bar.MonitorRect));
                NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
                long style = NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_STYLE).ToInt64();
                NativeMethods.SetWindowLongPtr(h, NativeMethods.GWL_STYLE, new IntPtr(style & ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME)));
                var screen = bar.MonitorRect;
                NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOP, screen.Left, screen.Top, screen.Width, screen.Height, NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW);
                Foreground(h);
                await Task.Delay(150);
                Check("fullscreen native window", FullscreenDetector.IsActiveOn(screen));
                Refresh();
                Check("fullscreen hides bar", !NativeMethods.IsWindowVisible(barHwnd));
                controller.Reposition();
                Check("reposition cannot raise bar over fullscreen", !NativeMethods.IsWindowVisible(barHwnd));
                for (int i = 0; i < 5; i++) { await Task.Delay(200); Refresh(); }
                Check("bar stays hidden during fullscreen", !NativeMethods.IsWindowVisible(barHwnd));
                NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOP, screen.Left + 100, screen.Top + 100, 640, 360, NativeMethods.SWP_FRAMECHANGED);
                await Task.Delay(100);
                Refresh();
                Check("exit fullscreen restores bar", NativeMethods.IsWindowVisible(barHwnd));
                NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOP, screen.Left, screen.Top, screen.Width, screen.Height, NativeMethods.SWP_FRAMECHANGED);
                await Task.Delay(100); Refresh();
                typeof(BarController).GetField("_forceHidden", flags)!.SetValue(controller, true);
                NativeMethods.ShowWindow(h, NativeMethods.SW_MINIMIZE);
                await Task.Delay(100); Refresh();
                Check("manual hidden survives fullscreen exit", !NativeMethods.IsWindowVisible(barHwnd));
                typeof(BarController).GetField("_forceHidden", flags)!.SetValue(controller, false);
                controller.Media.Current.HasSession = false;
                controller.Reposition();
                Check("no media remains hidden", !NativeMethods.IsWindowVisible(barHwnd));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(args[0], "error.txt"), ex.ToString()); Check("no exception", false); }
            finally
            {
                if (helper is { HasExited: false }) helper.Kill();
                barWindow?.Close(); controller?.Media.Dispose(); controller?.Dispose();
                NativeMethods.SetForegroundWindow(previous);
                File.WriteAllText(Path.Combine(args[0], "checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(checks.Values.All(v => v) ? 0 : 1);
            }
        };
        return app.Run();
    }
}
