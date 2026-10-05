using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarMediaBar;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Set(object value, string name, object fieldValue) => value.GetType().GetField(name, Private)!.SetValue(value, fieldValue);
    private static object? Invoke(object value, string name) => value.GetType().GetMethod(name, Private)!.Invoke(value, null);
    private static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;
    private static bool Visible(Window window) => NativeMethods.IsWindowVisible(Handle(window));
    private static async Task Settle(Application app) { await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(80); }

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args is ["--helper", _])
        {
            var helper = new Window { Width = 600, Height = 240, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Black, Title = args[1] };
            helper.SourceInitialized += (_, _) =>
            {
                var hwnd = Handle(helper);
                long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr((ex | NativeMethods.WS_EX_NOACTIVATE) & ~NativeMethods.WS_EX_TOOLWINDOW));
            };
            helper.Closed += (_, _) => app.Shutdown();
            // Publish readiness after WPF's initial native placement, avoiding a HWND-created/Loaded race.
            helper.Loaded += (_, _) => helper.Title = args[1] + " Ready";
            app.Run(helper);
            return 0;
        }
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
        Directory.CreateDirectory(args[0]);
        Environment.SetEnvironmentVariable("TMB_DATA_DIR", args[0]);
        var checks = new List<object>();
        int failed = 0;
        void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) failed++; }
        app.Startup += async (_, _) =>
        {
            BarController? controller = null;
            Process? helperProcess = null;
            Process? behindProcess = null;
            IntPtr helperHandle = IntPtr.Zero;
            IntPtr behindHandle = IntPtr.Zero;
            try
            {
                var settings = new AppSettings { Width = 455, Monitor = "cursor", Anchor = "beforeTray", Offset = 137,
                    Glass = false, ShowLyrics = false, ShowLikeButton = false, AvoidOthers = false, IdleHint = false,
                    PreferredApps = new(), FallbackApps = new(), Hotkeys = new() };
                controller = new BarController(settings);
                var media = controller.Media;
                var snapshot = media.Current;
                snapshot.HasSession = true; snapshot.SessionKey = "synthetic-session"; snapshot.SourceAppId = "UI_RENDER_ONLY";
                snapshot.Title = "顶部模式隔离验收"; snapshot.Artist = "模拟媒体"; snapshot.IsPlaying = false;
                snapshot.CanPlayPause = snapshot.CanPrev = snapshot.CanNext = snapshot.CanSeek = true;
                snapshot.DurationSeconds = 200; snapshot.PositionSeconds = 60;
                var identity = snapshot.Identity;
                var selector = Field<MediaSourceSelector>(media, "_selector");
                selector.Select(snapshot.SessionKey);
                Check("media manager never initialized", Field<object?>(media, "_manager") is null);
                Invoke(controller, "EnsureWindow");
                await Settle(app);
                var initial = Field<MainWindow>(controller, "_window");
                var originalHwnd = Handle(initial);
                var originalForeground = NativeMethods.GetForegroundWindow();
                Check("initial taskbar mode remains default", settings.Placement == "taskbar");
                int generation = Field<int>(controller, "_presentationGeneration");
                for (int i = 0; i < 6; i++)
                {
                    string next = i % 2 == 0 ? "top" : "taskbar";
                    var old = Field<MainWindow>(controller, "_window");
                    var oldHwnd = Handle(old);
                    controller.SetPlacement(next);
                    await Settle(app);
                    var current = Field<MainWindow>(controller, "_window");
                    Check($"switch replaces only view {i}", !ReferenceEquals(current, old) && !NativeMethods.IsWindow(oldHwnd)
                        && ReferenceEquals(media, controller.Media) && ReferenceEquals(snapshot, media.Current));
                    Check($"preserves old taskbar settings {i}", settings.Width == 455 && settings.Monitor == "cursor"
                        && settings.Anchor == "beforeTray" && settings.Offset == 137 && settings.Mode == "overlay");
                    Check($"preserves paused source and lock {i}", snapshot.Identity == identity && !snapshot.IsPlaying
                        && selector.CurrentKey == "synthetic-session" && media.SourceLocked);
                    Check($"single presentation and stable foreground {i}", app.Windows.OfType<MainWindow>().Count() == 1
                        && NativeMethods.GetForegroundWindow() == originalForeground);
                }
                Check("switch generation advances", Field<int>(controller, "_presentationGeneration") == generation + 6);
                Check("original taskbar handle retired", !NativeMethods.IsWindow(originalHwnd));
                controller.SetPlacement("top");
                await Settle(app);
                var top = Field<MainWindow>(controller, "_window");
                var topHwnd = Handle(top);
                var owner = NativeMethods.GetWindowLongPtr(topHwnd, NativeMethods.GWLP_HWNDPARENT);
                NativeMethods.GetWindowThreadProcessId(owner, out int ownerPid);
                Check("top window has no external owner", owner == IntPtr.Zero || ownerPid == Environment.ProcessId);
                var beforeNoop = Field<int>(controller, "_presentationGeneration");
                controller.SetPlacement("top"); controller.SetPlacement("INVALID");
                Check("same or invalid placement has no side effects", ReferenceEquals(top, Field<MainWindow>(controller, "_window"))
                    && Field<int>(controller, "_presentationGeneration") == beforeNoop);

                var startup = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    startup.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                startup.ArgumentList.Add("--helper");
                startup.ArgumentList.Add("Qiyin Top Integration Test Surface");
                helperProcess = Process.Start(startup) ?? throw new Exception("Could not start own helper");
                for (int i = 0; i < 50 && helperHandle == IntPtr.Zero; i++)
                {
                    await Task.Delay(60);
                    NativeMethods.EnumWindows((hwnd, _) =>
                    {
                        NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                        if (pid == helperProcess.Id && NativeMethods.IsWindowVisible(hwnd)
                            && NativeMethods.ReadWindowTitle(hwnd) == "Qiyin Top Integration Test Surface Ready") helperHandle = hwnd;
                        return true;
                    }, IntPtr.Zero);
                }
                if (helperHandle == IntPtr.Zero) throw new Exception("Own helper HWND unavailable");
                var screen = TopScreenHost.Pick(settings) ?? throw new Exception("No target monitor");
                var bounds = screen.MonitorRect;
                void MoveHelper(int x, int y, int width, int height) => NativeMethods.SetWindowPos(helperHandle,
                    NativeMethods.HWND_TOPMOST, x, y, width, height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);
                void Refresh() => Invoke(controller, "RefreshFullscreenVisibility");
                bool LongBar() => Visible(top) && Math.Abs(top.Width - settings.Width) < 1 && Math.Abs(top.Height - 40) < 1;
                MoveHelper(bounds.Left + 100, bounds.Top + 250, Math.Min(600, bounds.Width), Math.Min(240, bounds.Height));
                await Settle(app); controller.Reposition(); await Settle(app);
                Check("normal top long bar remains visible", LongBar());
                Check("all available controls and artist row remain visible", ((Button)top.FindName("PrevBtn")).Visibility == Visibility.Visible
                    && ((Button)top.FindName("NextBtn")).Visibility == Visibility.Visible
                    && ((FrameworkElement)top.FindName("ProgressTrack")).Visibility == Visibility.Visible
                    && ((FrameworkElement)top.FindName("LyricViewport")).Visibility == Visibility.Visible);
                Check("mouse activation handler preserves application focus", NativeMethods.SendMessage(topHwnd,
                    (uint)NativeMethods.WM_MOUSEACTIVATE, IntPtr.Zero, IntPtr.Zero) == new IntPtr(3)
                    && NativeMethods.GetForegroundWindow() == originalForeground);

                var compact = TopScreenHost.ComputeLayout(screen, settings, settings.Width, 40);
                MoveHelper(compact.X - 30, bounds.Top, Math.Min(600, bounds.Width), 240);
                await Settle(app); Refresh(); await Settle(app);
                Check("top-aligned app does not shrink the long bar", LongBar()
                    && NativeMethods.GetWindowRect(topHwnd, out var actualBar) && actualBar.Top == compact.Y);
                var root = (FrameworkElement)top.FindName("Root");
                root.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
                await Task.Delay(350);
                Check("mouse enter retains long-bar dimensions", LongBar());
                root.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
                await Task.Delay(750);
                Check("mouse leave retains long-bar dimensions", LongBar());
                long normalHelperStyle = NativeMethods.GetWindowLongPtr(helperHandle, NativeMethods.GWL_STYLE).ToInt64();
                NativeMethods.SetWindowLongPtr(helperHandle, NativeMethods.GWL_STYLE, new IntPtr(normalHelperStyle | 0x01000000L /* WS_MAXIMIZE */));
                MoveHelper(screen.WorkRect.Left, screen.WorkRect.Top, screen.WorkRect.Width, screen.WorkRect.Height);
                await Settle(app); Refresh(); await Settle(app);
                Check("native maximized state confirmed", NativeMethods.IsZoomed(helperHandle));
                Check("maximized app keeps constant long bar", LongBar());
                NativeMethods.SetWindowLongPtr(helperHandle, NativeMethods.GWL_STYLE, new IntPtr(normalHelperStyle));
                MoveHelper(compact.X - 30, bounds.Top, Math.Min(600, bounds.Width), 240);
                await Settle(app); Refresh();
                var openMenu = Field<ContextMenu>(top, "_menu");
                openMenu.PlacementTarget = top;
                openMenu.IsOpen = true;
                var seekTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromSeconds(30) };
                seekTimer.Start();
                Set(top, "_seekPointerTimer", seekTimer);
                Set(top, "_seeking", true);
                Set(top, "_seekSourceHold", media.HoldCurrentSource());
                MoveHelper(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
                await Settle(app); Refresh(); await Settle(app);
                Check("visible fullscreen hides long bar", !Visible(top) && !Field<bool>(top, "_presentationVisible"));
                Check("fullscreen closes independent popup menu", !openMenu.IsOpen);
                Check("fullscreen cancels seek preview and pointer timer", !Field<bool>(top, "_seeking") && !seekTimer.IsEnabled
                    && Field<int>(media, "_holdCount") == 0);
                Check("hidden long bar stops text work", Field<DispatcherTimer?>(top, "_lyricTimer") is null
                    && Field<object?>(top, "_titleMarquee") is null);
                controller.Reposition();
                Check("reposition cannot restore over fullscreen", !Visible(top));
                MoveHelper(bounds.Left + 100, bounds.Top + 250, Math.Min(600, bounds.Width), 240);
                await Settle(app); Refresh(); await Settle(app);
                Check("fullscreen exit restores constant long bar", LongBar());
                Set(controller, "_forceHidden", true); controller.Reposition();
                Check("forceHidden hides immediately", !Visible(top));
                MoveHelper(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
                await Settle(app); Refresh();
                MoveHelper(bounds.Left + 100, bounds.Top + 250, Math.Min(600, bounds.Width), 240);
                await Settle(app); Refresh();
                Check("forceHidden survives fullscreen exit", !Visible(top));
                Set(controller, "_forceHidden", false); controller.Reposition(); await Settle(app);
                Check("forceHidden release restores long bar", LongBar());
                snapshot.HasSession = false; Set(controller, "_everHadSession", true); controller.Reposition();
                Check("media disappearance hides long bar", !Visible(top));
                snapshot.HasSession = true; controller.Reposition(); await Settle(app);
                Check("media return restores long bar", LongBar());
                snapshot.IsPlaying = false;
                await (Task)Invoke(controller, "TickAsync")!;
                Check("synthetic paused tick keeps source", snapshot.Identity == identity && selector.CurrentKey == "synthetic-session" && media.SourceLocked);
                Check("tick uses uninitialized media manager", Field<object?>(media, "_manager") is null);
                Check("all top transitions preserve foreground", NativeMethods.GetForegroundWindow() == originalForeground);

                var behindStartup = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    behindStartup.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                behindStartup.ArgumentList.Add("--helper"); behindStartup.ArgumentList.Add("Qiyin Top Integration Test Backdrop");
                behindProcess = Process.Start(behindStartup)!;
                for (int i = 0; i < 50 && behindHandle == IntPtr.Zero; i++)
                {
                    await Task.Delay(60);
                    NativeMethods.EnumWindows((hwnd, _) =>
                    {
                        NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                        if (pid == behindProcess.Id && NativeMethods.IsWindowVisible(hwnd)
                            && NativeMethods.ReadWindowTitle(hwnd) == "Qiyin Top Integration Test Backdrop Ready") behindHandle = hwnd;
                        return true;
                    }, IntPtr.Zero);
                }
                if (behindHandle == IntPtr.Zero) throw new Exception("Second own helper HWND unavailable");
                NativeMethods.SetWindowPos(behindHandle, NativeMethods.HWND_TOPMOST, compact.X - 30, bounds.Top, Math.Min(600, bounds.Width), 240,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                MoveHelper(bounds.Left + 100, bounds.Top + 300, Math.Min(600, bounds.Width), 200);
                await Settle(app); Refresh(); await Settle(app);
                NativeMethods.GetWindowRect(helperHandle, out var frontDiagnostic);
                NativeMethods.GetWindowRect(behindHandle, out var behindDiagnostic);
                var diagnosticArea = new NativeMethods.RECT { Left = compact.X, Top = compact.Y,
                    Right = compact.X + compact.Width, Bottom = compact.Y + compact.Height };
                File.WriteAllText(Path.Combine(args[0], "double-window-diagnostic.json"), JsonSerializer.Serialize(new
                {
                    width = top.Width, height = top.Height, nativeVisible = Visible(top),
                    front = frontDiagnostic.ToString(), behind = behindDiagnostic.ToString(),
                    frontStyles = NativeMethods.GetWindowLongPtr(helperHandle, NativeMethods.GWL_EXSTYLE).ToInt64(),
                    behindStyles = NativeMethods.GetWindowLongPtr(behindHandle, NativeMethods.GWL_EXSTYLE).ToInt64(),
                    selected = FullscreenDetector.GetVisibleSurface(bounds)?.WindowRect.ToString(),
                    selectedAtTop = FullscreenDetector.GetVisibleSurface(bounds, diagnosticArea)?.WindowRect.ToString()
                }, new JsonSerializerOptions { WriteIndented = true }));
                Check("visible titlebar behind does not shrink long bar", LongBar());
                NativeMethods.SetWindowPos(behindHandle, NativeMethods.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                MoveHelper(bounds.Left + 100, bounds.Top + 300, Math.Min(600, bounds.Width), 200);
                await Settle(app); Refresh(); await Settle(app);
                Check("lower front window does not mask visible fullscreen behind", !Visible(top));
                NativeMethods.ShowWindow(behindHandle, NativeMethods.SW_HIDE);
                Refresh(); await Settle(app);
                Check("hidden backdrop restores constant long bar", LongBar());

                // Exercise the actual context-menu event path back to the retained taskbar presentation.
                var menu = Field<ContextMenu>(top, "_menu");
                var placement = menu.Items.OfType<MenuItem>().First(item => Equals(item.Header, "播控位置"));
                placement.Items.OfType<MenuItem>().First(item => Equals(item.Header, "任务栏")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Settle(app);
                Check("context menu placement event wired to controller", settings.Placement == "taskbar" && !ReferenceEquals(top, Field<MainWindow>(controller, "_window")));
                var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(args[0], "settings.json")))!;
                Check("isolated persisted settings retain original taskbar layout", persisted.Width == 455 && persisted.Monitor == "cursor"
                    && persisted.Anchor == "beforeTray" && persisted.Offset == 137 && persisted.Placement == "taskbar");
                Check("single media service and single final presentation", ReferenceEquals(media, controller.Media) && app.Windows.OfType<MainWindow>().Count() == 1);
            }
            catch (Exception ex)
            {
                Check("no exception", false);
                File.WriteAllText(Path.Combine(args[0], "error.txt"), ex.ToString());
            }
            finally
            {
                if (helperHandle != IntPtr.Zero) NativeMethods.PostMessage(helperHandle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                if (behindHandle != IntPtr.Zero) NativeMethods.PostMessage(behindHandle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                if (helperProcess is not null)
                {
                    if (!helperProcess.WaitForExit(1500)) helperProcess.Kill();
                    helperProcess.Dispose();
                }
                if (behindProcess is not null)
                {
                    if (!behindProcess.WaitForExit(1500)) behindProcess.Kill();
                    behindProcess.Dispose();
                }
                foreach (var window in app.Windows.OfType<MainWindow>().ToArray()) window.Close();
                controller?.Media.Dispose(); controller?.Dispose();
                File.WriteAllText(Path.Combine(args[0], "top-integration-results.json"), JsonSerializer.Serialize(new
                {
                    total = checks.Count, failed, syntheticMediaOnly = true, mediaStartCalled = false,
                    staleAwaitLimit = "Media manager is intentionally null; asynchronous provider-completion generation guard is reviewed statically, not injected.",
                    checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Top integration: {checks.Count - failed}/{checks.Count} passed; failed={failed}");
                app.Shutdown(failed == 0 ? 0 : 1);
            }
        };
        app.Run();
        return failed == 0 ? 0 : 1;
    }
}
