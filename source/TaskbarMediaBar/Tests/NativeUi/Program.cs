using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar;
using TaskbarMediaBar.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !System.IO.Path.IsPathFullyQualified(args[0])) return 2;
        string output = args[0];
        Directory.CreateDirectory(output);
        var results = new List<object>();
        int failures = 0;
        void Check(string name, bool passed)
        {
            results.Add(new { name, passed });
            if (!passed) failures++;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                foreach (var size in new[] { (Width: 4000, Height: 2000), (Width: 2000, Height: 4000) })
                {
                    var large = BitmapSource.Create(size.Width, size.Height, 96, 96, PixelFormats.Bgra32, null,
                        new byte[size.Width * size.Height * 4], size.Width * 4);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(large));
                    using var compressed = new MemoryStream(); encoder.Save(compressed);
                    var small = CoverImage.Decode(compressed.ToArray());
                    Check($"大封面长边限制192且保持比例 {size.Width}x{size.Height}",
                        Math.Max(small.PixelWidth, small.PixelHeight) == 192 && Math.Min(small.PixelWidth, small.PixelHeight) == 96 && small.IsFrozen);
                }
                var settings = new AppSettings { Glass = false, ShowLyrics = false, ShowLikeButton = false };
                using var media = new MediaSessionService(Array.Empty<string>());
                var loadCover = typeof(MediaSessionService).GetMethod("LoadCoverAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                using var imageStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var reference = Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(imageStream);
                var missing = await (Task<BitmapSource?>)loadCover.Invoke(media, new object?[] { reference, "cover-test" })!;
                Check("空封面可安全失败", missing is null);
                var coverEncoder = new PngBitmapEncoder();
                coverEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8)));
                using var coverBytes = new MemoryStream(); coverEncoder.Save(coverBytes);
                using (var writer = new Windows.Storage.Streams.DataWriter(imageStream.GetOutputStreamAt(0)))
                { writer.WriteBytes(coverBytes.ToArray()); await writer.StoreAsync(); await writer.FlushAsync(); }
                typeof(MediaSessionService).GetField("_coverRetryAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(media, DateTimeOffset.MinValue);
                var recovered = await (Task<BitmapSource?>)loadCover.Invoke(media, new object?[] { reference, "cover-test" })!;
                Check("首次为空后同首歌曲封面可重试恢复", recovered is not null);
                var retained = await (Task<BitmapSource?>)loadCover.Invoke(media, new object?[] { null, "cover-test" })!;
                Check("同首歌封面暂缺时保留已加载图片", recovered is not null && ReferenceEquals(recovered, retained));
                var window = new MainWindow(settings, media) { Left = -10000, Top = -10000 };
                window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                T Find<T>(string name) where T : class => (T)window.FindName(name);
                void Set(string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
                void Heart() => typeof(MainWindow).GetMethod("UpdateHeartBrush", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                var root = Find<Grid>("Root");
                var buttons = new[] { "PrevBtn", "PlayBtn", "NextBtn", "LikeBtn" }.Select(Find<Button>).ToArray();
                var snapshot = media.Current;
                snapshot.HasSession = true; snapshot.SourceAppId = "UI_RENDER_ONLY"; snapshot.Title = "夜空中最亮的星"; snapshot.Artist = "逃跑计划";
                snapshot.IsPlaying = true; snapshot.CanPlayPause = true; snapshot.CanNext = true; snapshot.CanPrev = true;
                snapshot.CanSeek = true; snapshot.DurationSeconds = 252; snapshot.PositionSeconds = 97;
                void Apply()
                {
                    window.ApplySnapshot(snapshot);
                    Find<Button>("LikeBtn").Visibility = Visibility.Visible;
                    window.UpdateLayout();
                }
                Apply();
                Check("四个按钮都有辅助功能名称", buttons.All(b => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))));
                Check("三个点按钮已移除", window.FindName("MoreBtn") is null);
                Check("所有按钮和封面均关闭悬停提示框", buttons.All(b => !ToolTipService.GetIsEnabled(b))
                    && !ToolTipService.GetIsEnabled(Find<Border>("Cover")) && !ToolTipService.GetIsEnabled(root));
                Check("四个按钮均在 360x40 范围内", buttons.All(b => { var p = b.TranslatePoint(new Point(), root); return p.X >= 0 && p.Y >= 0 && p.X + b.ActualWidth <= root.ActualWidth && p.Y + b.ActualHeight <= root.ActualHeight; }));
                var text = Find<StackPanel>("TextStack");
                var controls = Find<StackPanel>("Controls");
                Check("文字和按钮区域不重叠", text.TranslatePoint(new Point(text.ActualWidth, 0), root).X <= controls.TranslatePoint(new Point(), root).X);
                var progress = Find<Grid>("ProgressTrack");
                Check("细进度条具有至少8像素点击高度", progress.ActualHeight >= 8);
                var hit = root.InputHitTest(new Point(root.ActualWidth / 2, root.ActualHeight - 6)) as DependencyObject;
                while (hit is not null && !ReferenceEquals(hit, progress)) hit = VisualTreeHelper.GetParent(hit);
                Check("细线以上区域也能命中进度条", ReferenceEquals(hit, progress));
                var fill = (Rectangle)typeof(MainWindow).GetField("_progressFill", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                snapshot.PositionSampleTimestamp = Stopwatch.GetTimestamp();
                double widthBefore = fill.Width;
                await Task.Delay(280);
                Check("未收到新采样时进度仍连续推进", fill.Width > widthBefore + 0.15);
                snapshot.IsPlaying = false; Apply();
                double pauseWidth = fill.Width;
                await Task.Delay(240);
                Check("暂停期间进度不继续走动", Math.Abs(fill.Width - pauseWidth) < 0.01);
                snapshot.PositionSampleTimestamp = 0; snapshot.IsPlaying = true; Apply();
                settings.ShowLyrics = true;
                // 只测试歌词类型，不触发真实音乐应用的收藏探测。
                snapshot.SourceAppId = "Spotify.exe";
                Set("_lyricKey", snapshot.Identity);
                Set("_lyricDoc", new LyricsService.LyricDoc("测试", "测试", new List<LyricLine>
                    { new(1000, "同步甲", new()), new(2000, "同步乙", new()) }, "模拟时间轴"));
                snapshot.PositionSeconds = 1.8; snapshot.PositionSampleTimestamp = Stopwatch.GetTimestamp(); Apply();
                await Task.Delay(350);
                string secondary = new System.Windows.Documents.TextRange(Find<TextBlock>("ArtistText").ContentStart, Find<TextBlock>("ArtistText").ContentEnd).Text;
                Check("歌词在采样间隔内按播放时间换行", secondary == "同步乙");
                snapshot.PositionSeconds = 1.2; snapshot.PositionSampleTimestamp = 0; Apply();
                secondary = new System.Windows.Documents.TextRange(Find<TextBlock>("ArtistText").ContentStart, Find<TextBlock>("ArtistText").ContentEnd).Text;
                Check("向前跳转后立即回到对应歌词", secondary == "同步甲");
                snapshot.IsPlaying = false;
                Set("_lyricDoc", new LyricsService.LyricDoc("测试", "测试", new List<LyricLine>
                    { new(1000, "It's enough to make kings and vagabonds believe the very best — 长歌词完整显示测试", new()),
                      new(11000, "短歌词", new()) }, "模拟时间轴"));
                var viewport = Find<ScrollViewer>("LyricViewport");
                snapshot.PositionSeconds = 1; Apply();
                Check("长歌词开头可见且存在可滚动区域", viewport.ScrollableWidth > 0 && (-Find<TranslateTransform>("LyricTranslation").X) < 1);
                Save("长歌词_开头.png", 1.5);
                snapshot.PositionSeconds = 6; Apply();
                double middleOffset = (-Find<TranslateTransform>("LyricTranslation").X);
                Check("长歌词随播放时间展示后半句", middleOffset > 0 && middleOffset < viewport.ScrollableWidth);
                Save("长歌词_中段.png", 1.5);
                await Task.Delay(240);
                Check("暂停时长歌词位置保持不变", Math.Abs((-Find<TranslateTransform>("LyricTranslation").X) - middleOffset) < 1);
                snapshot.IsPlaying = true; snapshot.PositionSampleTimestamp = Stopwatch.GetTimestamp(); Apply();
                var offsets = new List<double>();
                EventHandler sampleFrame = (_, _) => offsets.Add(Find<TranslateTransform>("LyricTranslation").X);
                CompositionTarget.Rendering += sampleFrame;
                try { await Task.Delay(250); }
                finally { CompositionTarget.Rendering -= sampleFrame; }
                Check("滚动在时间轴采样间隔内连续产生中间帧", offsets.Select(x => Math.Round(x, 3)).Distinct().Count() >= 4);
                Check("滚动不改变视口布局偏移", viewport.HorizontalOffset == 0);
                snapshot.IsPlaying = false; snapshot.PositionSampleTimestamp = 0; Apply();
                snapshot.PositionSeconds = 10.8; Apply();
                Check("换行前能看到歌词末尾", Math.Abs((-Find<TranslateTransform>("LyricTranslation").X) - viewport.ScrollableWidth) < 1);
                Save("长歌词_末尾.png", 1.5);
                snapshot.PositionSeconds = 1; Apply();
                Check("回跳后长歌词恢复开头", (-Find<TranslateTransform>("LyricTranslation").X) < 1);
                snapshot.PositionSeconds = 11; Apply();
                Check("短歌词不滚动且不保留上句偏移", viewport.ScrollableWidth < 1 && (-Find<TranslateTransform>("LyricTranslation").X) < 1);
                settings.ShowLyrics = false; snapshot.SourceAppId = "UI_RENDER_ONLY"; snapshot.IsPlaying = true; snapshot.PositionSeconds = 97; Apply();
                var press = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = progress };
                progress.RaiseEvent(press);
                Check("进度区域按下不会开始拖动整个播控", press.Handled && !(bool)typeof(MainWindow).GetField("_dragWhole", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
                progress.ReleaseMouseCapture();
                typeof(MainWindow).GetMethod("CancelSeekPreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                Check("播放按钮无常驻底色", ((SolidColorBrush)Find<Button>("PlayBtn").Background).Color.A == 0);
                Check("播放时显示暂停图标", Find<System.Windows.Shapes.Path>("PauseGlyph").Visibility == Visibility.Visible);
                snapshot.IsPlaying = false; Apply();
                Check("暂停时显示播放图标", Find<System.Windows.Shapes.Path>("PlayGlyph").Visibility == Visibility.Visible);
                snapshot.IsFallback = true; Apply();
                Check("无时间轴的来源不显示可点击进度", !progress.IsHitTestVisible);
                Check("网易云仅显示一个播放图标", Find<System.Windows.Shapes.Path>("PlayGlyph").Visibility == Visibility.Visible && Find<System.Windows.Shapes.Path>("PauseGlyph").Visibility == Visibility.Collapsed && window.FindName("ToggleGlyph") is null);
                snapshot.IsFallback = false; snapshot.IsPlaying = true; Apply();
                Set("_likeKnown", true); Set("_liked", false); Heart();
                void Save(string name, double scale)
                {
                    root.UpdateLayout();
                    var bmp = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bmp.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
                    using var file = File.Create(System.IO.Path.Combine(output, name)); encoder.Save(file);
                }
                foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                    Save($"A1_原生渲染_{scale * 100:0}pct.png", scale);
                Check("三个缩放比例完成原生渲染", new[] { 100, 150, 200 }
                    .All(scale => File.Exists(System.IO.Path.Combine(output, $"A1_原生渲染_{scale}pct.png"))));
                snapshot.IsPlaying = false;
                foreach (var ratio in new[] { 0.0, 0.01, 0.5, 1.0 })
                {
                    snapshot.PositionSeconds = snapshot.DurationSeconds * ratio; Apply();
                    typeof(MainWindow).GetMethod("UpdateProgress", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object[] { snapshot });
                    foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                        Save($"进度条_{ratio * 100:0}pct_缩放{scale * 100:0}.png", scale);
                }
                snapshot.PositionSeconds = 97; snapshot.IsPlaying = true; Apply();
                var gate = (TrackOperationGate)typeof(MainWindow).GetField("_likeGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var watch = Stopwatch.StartNew();
                gate.TryBegin(out var ticket); Heart();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                double pendingMs = watch.Elapsed.TotalMilliseconds;
                Check("收藏进行中立即显示标记", Find<Ellipse>("LikePendingDot").Visibility == Visibility.Visible && pendingMs < 100);
                Check("等待确认时不显示实心爱心", ((SolidColorBrush)Find<System.Windows.Shapes.Path>("HeartGlyph").Fill).Color.A == 0);
                Save("A1_原生渲染_处理中.png", 1.5);
                gate.End(ticket); Set("_liked", true); Heart();
                Check("确认喜欢后显示实心爱心", ((SolidColorBrush)Find<System.Windows.Shapes.Path>("HeartGlyph").Fill).Color.A > 0);
                var heartColor = ((SolidColorBrush)Find<System.Windows.Shapes.Path>("HeartGlyph").Fill).Color;
                Check("确认喜欢后爱心为红色", heartColor.R > heartColor.G * 1.5 && heartColor.R > heartColor.B * 1.3);
                Check("完成后清除等待标记", Find<Ellipse>("LikePendingDot").Visibility == Visibility.Collapsed);
                Save("A1_原生渲染_已喜欢.png", 1.5);
                snapshot.Title = "这是一首很长很长的歌曲名称用于检查标题不会挤压右边四个按钮"; Apply();
                Check("长歌名保留按钮完整空间", Find<Button>("LikeBtn").TranslatePoint(new Point(28, 0), root).X <= 360);
                settings.ShowCover = false; Apply();
                Check("关闭封面后让出布局空间", Find<Border>("Cover").Visibility == Visibility.Collapsed);
                typeof(MainWindow).GetMethod("ShowMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                var menu = (ContextMenu)typeof(MainWindow).GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                Check("右键设置菜单继续可用", menu.IsOpen && ReferenceEquals(menu.PlacementTarget, window));
                menu.IsOpen = false;
                snapshot.SourceAppId = "msedge.exe"; snapshot.CanNext = false; snapshot.CanPrev = false;
                snapshot.CanSeek = false; settings.ShowLikeButton = true; settings.ShowLyrics = true;
                window.ApplySnapshot(snapshot);
                Check("网页不显示音乐收藏按钮", Find<Button>("LikeBtn").Visibility == Visibility.Collapsed);
                Check("不支持上下曲时隐藏对应按钮", Find<Button>("PrevBtn").Visibility == Visibility.Collapsed && Find<Button>("NextBtn").Visibility == Visibility.Collapsed);
                Check("不支持跳转时进度条不可交互", !progress.IsHitTestVisible);
                Check("网页不请求歌词", !(bool)typeof(MainWindow).GetField("_lyricFetchBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
                Check("网页副标题标明浏览器来源", Find<TextBlock>("ArtistText").Text.StartsWith("Edge"));
                var sourceMenu = ((ContextMenu)typeof(MainWindow).GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "播放来源");
                Check("右键菜单提供来源选择入口", sourceMenu is not null && sourceMenu.Items.Count > 0);
                snapshot.CanSeek = true; window.ApplySnapshot(snapshot);
                Check("来源支持跳转时可操作进度", progress.IsHitTestVisible);
                settings.ShowLikeButton = false;
                snapshot.HasSession = false; snapshot.CanPlayPause = false; snapshot.CanNext = false; snapshot.CanPrev = false;
                window.ApplySnapshot(snapshot);
                Check("无播放器时收藏和播放不能发送", !Find<Button>("LikeBtn").IsEnabled && !Find<Button>("PlayBtn").IsEnabled);
                var hwnd = new WindowInteropHelper(window).Handle;
                long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                Check("播控声明不激活窗口", (exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0);
                Check("鼠标点击不会激活播控", NativeMethods.SendMessage(hwnd, NativeMethods.WM_MOUSEACTIVATE, IntPtr.Zero, IntPtr.Zero).ToInt32() == 3);
                settings.AvoidOthers = false;
                var bar = new TaskbarInfo { Visible = true, Scale = 1,
                    Rect = new NativeMethods.RECT { Left = -10000, Top = -10000, Right = -8000, Bottom = -9952 } };
                TaskbarHost.PlaceOverlay(window, bar, settings);
                int positionMessages = 0;
                HwndSourceHook observer = (IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                { if (msg == 0x47) positionMessages++; return IntPtr.Zero; };
                var source = HwndSource.FromHwnd(hwnd);
                source.AddHook(observer);
                for (int i = 0; i < 20; i++) TaskbarHost.PlaceOverlay(window, bar, settings);
                Check("20 次位置巡检不触发重复窗口重绘", positionMessages == 0);
                settings.Offset += 20;
                TaskbarHost.PlaceOverlay(window, bar, settings);
                NativeMethods.GetWindowRect(hwnd, out var moved);
                Check("位置变化时仍正确移动", moved.Left == -9976 && positionMessages > 0);
                int afterMove = positionMessages;
                TaskbarHost.PlaceOverlay(window, bar, settings);
                Check("移动结束后恢复静止", positionMessages == afterMove);
                source.RemoveHook(observer);
                // 本测试的媒体服务没有会话，因此不会发送真实播放命令。
                IntPtr originalForeground = NativeMethods.GetForegroundWindow();
                var playButton = Find<Button>("PlayBtn");
                playButton.IsEnabled = true;
                var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(playButton);
                ((System.Windows.Automation.Provider.IInvokeProvider)peer).Invoke();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("按钮调用也不抢走当前应用焦点", NativeMethods.GetForegroundWindow() == originalForeground && !playButton.IsKeyboardFocused);
                var simulatedTaskbar = new Window { Left = -10000, Top = -10000, Width = 1200, Height = 48,
                    WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
                simulatedTaskbar.Show();
                IntPtr owner = new WindowInteropHelper(simulatedTaskbar).Handle;
                var ownedBar = new TaskbarInfo { Handle = owner, Visible = true, Scale = 1, Rect = bar.Rect };
                TaskbarHost.PlaceOverlay(window, ownedBar, settings);
                Check("播控从属于任务栏顶层窗口", NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT) == owner);
                bool keptAbove = true;
                for (int i = 0; i < 20; i++)
                {
                    NativeMethods.SetWindowPos(owner, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
                    int index = 0, barIndex = -1, ownerIndex = -1;
                    NativeMethods.EnumWindows((h, _) => { if (h == hwnd) barIndex = index; if (h == owner) ownerIndex = index; index++; return true; }, IntPtr.Zero);
                    keptAbove &= barIndex >= 0 && ownerIndex >= 0 && barIndex < ownerIndex;
                }
                Check("任务栏连续置顶 20 次也不能遮住播控", keptAbove);
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT, IntPtr.Zero);
                simulatedTaskbar.Close();
                window.Close();
                File.WriteAllText(System.IO.Path.Combine(output, "20260914_A1原生UI检查_v01.json"), JsonSerializer.Serialize(new { observedAt = DateTimeOffset.Now,
                    passed = results.Count - failures, total = results.Count, checks = results, pendingFeedbackMs = pendingMs,
                    scope = "真实 WPF 控件、模拟媒体状态；未发送音乐播放、切歌或收藏命令。" }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { failures++; Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        };
        app.Run();
        Console.WriteLine($"Native UI checks: {results.Count - failures}/{results.Count}");
        return failures == 0 ? 0 : 1;
    }
}
