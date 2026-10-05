using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TaskbarMediaBar;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[1] == "--lifecycle") return LifecycleChecks.Run(args[0]);
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
        string output = args[0]; Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("TMB_DATA_DIR", output);
        var checks = new List<object>(); int failures = 0;
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) { failures++; Console.Error.WriteLine("FAIL: " + name); }
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                // Service is never initialized. All lyrics/artwork are supplied in memory.
                using var media = new MediaSessionService(Array.Empty<string>());
                var settings = new AppSettings { Placement = "top", Width = 0, Offset = 37, Glass = false, ShowLyrics = false, ShowLikeButton = true };
                window = new MainWindow(settings, media) { Left = -10000, Top = -10000 }; window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                T Find<T>(string name) where T : class => (T)window.FindName(name);
                object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
                void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
                void Call(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                async Task Flush() { await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
                MouseButtonEventArgs Press(UIElement element)
                {
                    var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = element };
                    Find<Grid>("Root").RaiseEvent(press); return press;
                }
                void Release() => window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = Find<Grid>("Root") });
                void Save(string name, double scale = 2)
                {
                    window.UpdateLayout();
                    var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    image.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
                }
                var root = Find<Grid>("Root"); var cover = Find<Border>("Cover"); var play = Find<Button>("PlayBtn"); var track = Find<Grid>("ProgressTrack");
                var snap = media.Current;
                snap.HasSession = true; snap.SourceAppId = "UI_RENDER_ONLY"; snap.Title = "夜空中最亮的星"; snap.Artist = "逃跑计划"; snap.IsPlaying = true;
                snap.CanPlayPause = snap.CanNext = snap.CanPrev = snap.CanSeek = true; snap.DurationSeconds = 252; snap.PositionSeconds = 97;
                window.ApplySnapshot(snap); await Flush(); Keyboard.ClearFocus();
                Check("top defaults to persistent 360x40 bar", window.Width == 360 && window.Height == 40);
                Check("single row retains cover two text lines controls and progress", cover.IsVisible && cover.Width == 28 && Find<ScrollViewer>("LyricViewport").IsVisible
                    && Find<Button>("PrevBtn").IsVisible && Find<Button>("NextBtn").IsVisible && track.IsVisible && Grid.GetColumn(Find<StackPanel>("Controls")) == 2 && Find<Grid>("SurfaceLayout").RowDefinitions.Count == 0);
                Check("top uses rounded light A1 surface", Find<Border>("Surface").CornerRadius == new CornerRadius(8) && ((SolidColorBrush)Find<Border>("Surface").Background).Color.R > 230
                    && ((SolidColorBrush)Find<System.Windows.Shapes.Path>("PauseGlyph").Stroke).Color.G > ((SolidColorBrush)Find<System.Windows.Shapes.Path>("PauseGlyph").Stroke).Color.R);
                Check("all top tooltips disabled", !ToolTipService.GetIsEnabled(root) && !ToolTipService.GetIsEnabled(cover) && new[] { "PrevBtn", "PlayBtn", "NextBtn", "LikeBtn" }.All(n => !ToolTipService.GetIsEnabled(Find<Button>(n))));
                Check("idle bar does not freeze cursor monitor selection", !root.IsMouseOver && !window.IsTopInteractionActive);
                var foreground = NativeMethods.GetForegroundWindow(); Press(play);
                Check("play press does not begin drag", !(bool)Field("_dragWhole")!);
                play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, play)); await Flush();
                Check("play neither resizes nor activates", window.Width == 360 && window.Height == 40 && NativeMethods.GetForegroundWindow() == foreground);
                Press(cover); Check("cover press retains source-only interaction", !(bool)Field("_dragWhole")!);
                var textPress = Press(Find<StackPanel>("TextStack"));
                Check("text pointer press follows existing drag handler without resize", textPress.Handled && window.Width == 360 && window.Height == 40);
                // The real physical mouse stays up in this isolated fixture. Capture can
                // immediately deliver an unpressed movement, correctly ending the gesture.
                // Separately verify the active gesture state and its release handling.
                Set("_dragWhole", true); Check("active horizontal drag freezes screen selection", window.IsTopInteractionActive);
                Release(); await Flush();
                Check("release ends text drag and preserves dimensions", !(bool)Field("_dragWhole")! && !root.IsMouseCaptured && window.Width == 360 && window.Height == 40);
                Check("top text drag preserves foreground and old taskbar offset", NativeMethods.GetForegroundWindow() == foreground && settings.Offset == 37);
                snap.IsPlaying = false; window.ApplySnapshot(snap); await Flush();
                Check("paused bar exposes direct play and supported controls", play.IsVisible && Find<System.Windows.Shapes.Path>("PlayGlyph").Visibility == Visibility.Visible && Find<Button>("PrevBtn").IsVisible && Find<Button>("NextBtn").IsVisible);
                root.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
                root.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
                await Task.Delay(750); await Flush();
                Check("hover and leave preserve persistent bar", window.Width == 360 && window.Height == 40 && track.IsVisible);
                Set("_likeProbeBusy", true); snap.SourceAppId = "QQMusic"; window.ApplySnapshot(snap); await Flush();
                Check("capable source exposes existing favorite button", Find<Button>("LikeBtn").IsVisible && Find<Button>("LikeBtn").IsEnabled && !string.IsNullOrEmpty(AutomationProperties.GetName(Find<Button>("LikeBtn"))));
                foreach (int width in new[] { 220, 360, 720 })
                {
                    settings.Width = width; window.RefreshPresentation(); await Flush(); bool bounded = true;
                    foreach (var name in new[] { "Cover", "TextStack", "LyricViewport", "PrevBtn", "PlayBtn", "NextBtn", "LikeBtn", "ProgressTrack" })
                    {
                        var element = Find<FrameworkElement>(name); var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                        bounded &= bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= window.ActualWidth + 0.5 && bounds.Bottom <= window.ActualHeight + 0.5;
                    }
                    Check($"all content fits {width}x40", bounded && cover.IsVisible && window.Height == 40);
                    Check($"text and buttons do not overlap at {width}", Find<StackPanel>("TextStack").TranslatePoint(new Point(Find<StackPanel>("TextStack").ActualWidth, 0), root).X <= Find<StackPanel>("Controls").TranslatePoint(new Point(), root).X);
                }
                settings.Width = 100; window.RefreshPresentation(); await Flush(); Check("top width lower bound preserves setting", window.Width == 220 && settings.Width == 100);
                settings.Width = 1000; window.RefreshPresentation(); await Flush(); Check("top width upper bound preserves setting", window.Width == 720 && settings.Width == 1000);
                settings.Width = 360; window.RefreshPresentation(); await Flush();
                int layoutEvents = 0; window.TopLayoutChanged += () => layoutEvents++;
                window.RefreshPresentation(); window.SetPresentationVisible(true); window.SetPresentationVisible(true); await Flush();
                Check("same-size host calls do not emit layout changes", layoutEvents == 0);
                int depth = 0, maxDepth = 0; Action reentrant = () => { depth++; maxDepth = Math.Max(maxDepth, depth); window.RefreshPresentation(); depth--; };
                window.TopLayoutChanged += reentrant; settings.Width = 380; window.RefreshPresentation();
                Check("configured top width change immediately notifies host", layoutEvents > 0);
                await Flush(); window.TopLayoutChanged -= reentrant; Check("host layout callback does not recurse", maxDepth == 1);
                settings.Width = 360; window.RefreshPresentation(); await Flush();
                snap.CanPrev = snap.CanNext = snap.CanPlayPause = false; window.ApplySnapshot(snap); await Flush();
                Check("controls follow source capability", !Find<Button>("PrevBtn").IsVisible && !Find<Button>("NextBtn").IsVisible && !play.IsEnabled);
                snap.SourceAppId = "UI_RENDER_ONLY"; snap.IsFallback = true; snap.CanSeek = false; window.ApplySnapshot(snap); await Flush();
                Check("fallback omits seek and unavailable favorite", !track.IsHitTestVisible && !Find<Button>("LikeBtn").IsVisible && ((SolidColorBrush)((System.Windows.Shapes.Rectangle)Field("_progressFill")!).Fill).Color.A == 0);
                snap.IsFallback = false; snap.CanPrev = snap.CanNext = snap.CanPlayPause = snap.CanSeek = true; snap.SourceAppId = "Spotify";
                settings.ShowLyrics = true; snap.DurationSeconds = 20; snap.PositionSeconds = 6; snap.Title = "A long title exercising the existing native marquee in a persistent top player";
                Set("_lyricKey", snap.Identity); Set("_lyricDoc", new LyricsService.LyricDoc("test", "test", new List<LyricLine>
                    { new(0, "Long lyric line flowing through the existing timeline and transform — 顶部长条连续滚动检查", new()), new(15000, "short", new()) }, "synthetic"));
                window.ApplySnapshot(snap); await Flush(); window.ApplySnapshot(snap); await Flush();
                double paused = Find<TranslateTransform>("LyricTranslation").X;
                Check("persistent bar uses existing lyric timeline and scrolling", Find<ScrollViewer>("LyricViewport").ScrollableWidth > 0 && paused < 0);
                await Task.Delay(150); Check("paused lyrics remain stable", Math.Abs(Find<TranslateTransform>("LyricTranslation").X - paused) < 0.5);
                snap.IsPlaying = true; snap.PositionSampleTimestamp = Stopwatch.GetTimestamp(); window.ApplySnapshot(snap);
                Check("visible bar starts existing lyric timer", Field("_lyricTimer") is DispatcherTimer lyric && lyric.IsEnabled);
                Set("_seeking", true); Check("seek freezes screen selection", window.IsTopInteractionActive); Set("_seeking", false);
                Set("_seekCommitting", true); Check("seek commit freezes screen selection", window.IsTopInteractionActive); Set("_seekCommitting", false);
                Call("ShowMenu"); var menu = (ContextMenu)Field("_menu")!;
                Check("open menu freezes screen selection without resizing", menu.IsOpen && window.IsTopInteractionActive && window.Width == 360 && window.Height == 40);
                Check("media source selection menu remains available", menu.Items.OfType<MenuItem>().Any(i => (string?)i.Header == "播放来源"));
                var placements = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "播控位置"); string requested = ""; window.PlacementRequested += p => requested = p;
                placements.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "任务栏").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check("menu requests taskbar without writing settings", requested == "taskbar" && settings.Placement == "top");
                placements.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "屏幕顶部").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Check("menu requests top", requested == "top");
                track.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = track });
                Check("seek preview and popup fixtures active", (bool)Field("_seeking")! && (Field("_seekPointerTimer") as DispatcherTimer)?.IsEnabled == true && menu.IsOpen);
                window.SetPresentationVisible(false); window.ApplySnapshot(snap);
                Check("full hide closes popup and cancels uncommitted seek", !menu.IsOpen && !(bool)Field("_seeking")! && (Field("_seekPointerTimer") as DispatcherTimer)?.IsEnabled != true && Field("_seekSourceHold") is null);
                Check("hidden snapshots keep text clocks stopped", Field("_lyricTimer") is null && Field("_titleMarquee") is null && !Find<TranslateTransform>("LyricTranslation").HasAnimatedProperties);
                window.SetPresentationVisible(false); window.SetPresentationVisible(true); await Flush();
                Check("show resumes visible text work without changing dimensions", Field("_lyricTimer") is DispatcherTimer resumed && resumed.IsEnabled && window.Width == 360 && window.Height == 40);
                Keyboard.ClearFocus(); Check("idle top returns to inactive monitor-selection state", !window.IsTopInteractionActive);
                var hwnd = new WindowInteropHelper(window).Handle;
                Check("native top window remains nonactivating", (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_NOACTIVATE) != 0 && NativeMethods.SendMessage(hwnd, NativeMethods.WM_MOUSEACTIVATE, IntPtr.Zero, IntPtr.Zero).ToInt32() == 3);
                Check("top uses one bounded native window", !window.AllowsTransparency && app.Windows.Count == 1 && window.Width == 360 && window.Height == 40);
                snap.SourceAppId = "QQMusic"; snap.Title = "夜空中最亮的星"; snap.Artist = "逃跑计划"; snap.DurationSeconds = 252; snap.PositionSeconds = 97; snap.IsPlaying = false; snap.PositionSampleTimestamp = 0;
                var art = new DrawingVisual(); using (var drawing = art.RenderOpen())
                {
                    drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(130, 172, 126), Color.FromRgb(36, 91, 70), 90), null, new Rect(0, 0, 96, 96));
                    drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(244, 224, 150)), null, new Point(68, 26), 11, 11);
                    drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(31, 79, 59)), null, Geometry.Parse("M0,76 C25,38 38,44 55,73 C70,48 80,42 96,68 L96,96 L0,96 Z"));
                }
                var image = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32); image.Render(art); image.Freeze(); snap.Cover = image;
                Set("_lyricKey", snap.Identity); Set("_lyricDoc", new LyricsService.LyricDoc("synthetic", "synthetic", new List<LyricLine>
                    { new(90000, "风经过，微光在心间慢慢亮起", new()), new(105000, "下一句模拟歌词", new()) }, "synthetic fixture"));
                window.ApplySnapshot(snap); await Flush(); await Task.Delay(300); await Flush();
                foreach (var scale in new[] { 1.0, 1.5, 2.0 }) Save($"top-persistent-bar-{scale * 100:0}.png", scale);
                Check("example uses memory-only cover and cached lyrics", cover.Background is ImageBrush && Find<Button>("LikeBtn").IsVisible && !(bool)Field("_lyricFetchBusy")! && (string?)Field("_renderedLyricText") == "风经过，微光在心间慢慢亮起" && cover.Opacity > 0.95);
                settings.Placement = "taskbar"; settings.ShowLyrics = false; window.RefreshPresentation(); await Flush();
                Check("leaving top restores taskbar size shape and layout", window.Width == 360 && window.Height == 40 && !window.IsTopInteractionActive && cover.Width == 28 && Find<Border>("Surface").CornerRadius == new CornerRadius(8, 8, 0, 0) && Grid.GetColumn(Find<StackPanel>("Controls")) == 2 && track.IsVisible);
                Save("taskbar-restored.png"); window.Width = 310; window.Height = 32; await Flush(); settings.ShowLyrics = true; window.RefreshPresentation(); await Flush();
                Check("same-mode settings refresh preserves taskbar host dimensions", window.Width == 310 && window.Height == 32 && Math.Abs(((ScaleTransform)root.LayoutTransform).ScaleX - 0.8) < 0.001);
            }
            catch (Exception ex) { failures++; Console.Error.WriteLine(ex); }
            finally
            {
                window?.Close(); File.WriteAllText(Path.Combine(output, "20261003_TopUi长条检查_v01.json"), JsonSerializer.Serialize(new
                {
                    observedAt = DateTimeOffset.Now, passed = checks.Count - failures, total = checks.Count, checks,
                    scope = "Native persistent top WPF bar with synthetic snapshots, cached lyrics and memory-only artwork. Service never started; no network, real player, source focus, favorites, installation or publishing."
                }, new JsonSerializerOptions { WriteIndented = true })); app.Shutdown();
            }
        };
        app.Run(); Console.WriteLine($"Top UI checks: {checks.Count - failures}/{checks.Count}"); return failures == 0 ? 0 : 1;
    }
}
