using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TaskbarMediaBar;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

internal static class LifecycleChecks
{
    private sealed record Observation(string Placement, WeakReference Window, bool TimersStopped,
        bool MenuClosed, bool NativeHandleDestroyed, bool MediaReused, bool MotionRemoved);

    // This has the same reference directions as Controller: live media -> owner -> current view,
    // view events -> owner. It deliberately does not subscribe the window to the media event.
    private sealed class ViewOwner(MediaSessionService media)
    {
        public MainWindow? Current;
        public void Updated() => Current?.ApplySnapshot(media.Current);
        public void Reposition() { }
    }

    public static int Run(string output)
    {
        if (!Path.IsPathFullyQualified(output)) return 2;
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("TMB_DATA_DIR", output);
        var checks = new List<object>(); int failed = 0;
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) { failed++; Console.Error.WriteLine("FAIL: " + name); }
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                using var media = new MediaSessionService(Array.Empty<string>());
                var owner = new ViewOwner(media);
                media.Updated += owner.Updated;
                var observations = new List<Observation>();
                for (int i = 0; i < 6; i++)
                {
                    observations.Add(CreateAndClose(owner, media, i % 2 == 0 ? "top" : "taskbar"));
                    await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Check("all six native handles destroyed after Close", observations.All(o => o.NativeHandleDestroyed));
                Check("closed top and taskbar timers disabled", observations.All(o => o.TimersStopped));
                Check("opened menus closed with their window", observations.All(o => o.MenuClosed));
                Check("title and lyric motion removed on Close", observations.All(o => o.MotionRemoved));
                Check("one unstarted media service reused for every view", observations.All(o => o.MediaReused));
                for (int attempt = 0; attempt < 6 && observations.Any(o => o.Window.IsAlive); attempt++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    await Task.Delay(100);
                    await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Check("all six old window weak references collected", observations.All(o => !o.Window.IsAlive));
                Check("media remains usable after the views close", media.Current.DisplayTitle is not null);
                // Keep media, its event subscription and owner rooted through the collection check.
                GC.KeepAlive(owner); GC.KeepAlive(media);
            }
            catch (Exception ex) { failed++; Console.Error.WriteLine(ex); }
            finally
            {
                File.WriteAllText(Path.Combine(output, "20261003_TopUi窗口回收检查_v01.json"), JsonSerializer.Serialize(new
                {
                    observedAt = DateTimeOffset.Now, passed = checks.Count - failed, total = checks.Count, checks,
                    scope = "Six real WPF windows, alternating top/taskbar, synthetic snapshot, live media event owner retained. Media never started; no network, favorites, player or install actions.",
                    limitation = "Short bounded forced-GC check of completed/idle operations. Does not prove long-term memory behavior or retention while external UIA/provider requests remain pending."
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(failed == 0 ? 0 : 1);
            }
        };
        app.Run();
        Console.WriteLine($"Top window lifecycle: {checks.Count - failed}/{checks.Count}");
        return failed == 0 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Observation CreateAndClose(ViewOwner owner, MediaSessionService media, string placement)
    {
        var settings = new AppSettings { Placement = placement, ShowLyrics = false, ShowLikeButton = false, Glass = false };
        var window = new MainWindow(settings, media) { Left = -10000, Top = -10000 };
        owner.Current = window;
        window.TopLayoutChanged += owner.Reposition;
        object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        void Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        window.Show();
        var snap = media.Current;
        snap.HasSession = true; snap.SourceAppId = "UI_RENDER_ONLY"; snap.IsPlaying = true;
        snap.Title = "窗口回收窄检查 · 这是一段足够长的模拟标题，用来启动既有标题动画";
        snap.Artist = "仅内存模拟"; snap.DurationSeconds = 240; snap.PositionSeconds = 90; snap.CanSeek = true;
        window.ApplySnapshot(snap);
        window.UpdateLayout(); Call("UpdateMarquee"); Call("StartLyricTimer"); Call("ShowStatus", "模拟状态");
        var track = (Grid)window.FindName("ProgressTrack");
        track.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = track });
        Call("ShowMenu");
        var menu = (ContextMenu?)Field("_menu");
        var timers = new[] { "_statusTimer", "_seekPointerTimer", "_lyricTimer" }
            .Select(name => Field(name) as DispatcherTimer).Where(timer => timer is not null).ToArray();
        var handle = new WindowInteropHelper(window).Handle;
        var weak = new WeakReference(window);
        bool reused = ReferenceEquals(Field("_media"), media);
        window.Close(); owner.Current = null;
        return new(placement, weak, timers.All(timer => !timer!.IsEnabled), menu?.IsOpen != true,
            !NativeMethods.IsWindow(handle), reused, Field("_titleMarquee") is null
            && !((System.Windows.Media.TranslateTransform)window.FindName("LyricTranslation")).HasAnimatedProperties);
    }
}
