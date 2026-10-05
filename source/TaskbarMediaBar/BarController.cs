using System.Windows.Interop;
using System.Windows.Threading;
using TaskbarMediaBar.Interop;

namespace TaskbarMediaBar.Services;

/// <summary>
/// 播控条的"宿主"：管窗口生命周期、任务栏落位、托盘图标、看门狗。
/// </summary>
public sealed class BarController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly MediaSessionService _media;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _visibilityTimer;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _ticking;
    private bool _forceHidden;
    private bool _everHadSession;
    private bool _fullscreenHidden;
    private TaskbarInfo? _lastTaskbar;
    private TopScreenInfo? _lastTopScreen;
    private bool _placingTop;
    private int _presentationGeneration;
    private bool IsTop => string.Equals(_settings.Placement, "top", StringComparison.OrdinalIgnoreCase);

    public BarController(AppSettings settings)
    {
        _settings = settings;
        _media = new MediaSessionService(
            settings.PreferredApps,
            settings.FallbackApps,
            appId => _settings.FindHotkeyProfile(appId));
        _media.Updated += () => _window?.ApplySnapshot(_media.Current);
    }

    public MediaSessionService Media => _media;

    public AppSettings Settings => _settings;

    public async Task StartAsync()
    {
        HealAutoStartPath();

        await _media.InitAsync();

        CreateTray();

        // 全屏显隐不等待媒体查询完成，也不增加媒体/歌词的轮询频率。
        _visibilityTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _visibilityTimer.Tick += (_, _) => RefreshFullscreenVisibility();
        _visibilityTimer.Start();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000),
        };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        await TickAsync();
        Log.Write("BarController 已启动");
    }

    /// <summary>
    /// 自启项自愈：开过自启但程序被移动到别处时，把注册表里的路径纠正过来。
    /// 否则下次开机它指向一个不存在的文件，会静默失效（用户只会觉得"怎么不自动启动了"）。
    /// </summary>
    private static void HealAutoStartPath()
    {
        // 隔离测试不得把用户已有的自启项指向测试程序。
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMB_DATA_DIR"))) return;
        try
        {
            if (!AutoStart.IsEnabled())
            {
                return;
            }

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            string expected = $"\"{exe}\"";
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

            string? current = key?.GetValue("TaskbarMediaBar") as string;
            if (key is not null && !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue("TaskbarMediaBar", expected);
                Log.Write($"自启路径已纠正: {current} → {expected}");
            }
        }
        catch (Exception ex)
        {
            Log.Write("自启路径自愈失败: " + ex.Message);
        }
    }

    private async Task TickAsync()
    {
        if (_ticking)
        {
            return;
        }

        _ticking = true;
        try
        {
            int generation = _presentationGeneration;
            if (!RefreshHostSelection())
            {
                if (_window is not null) HidePresentation(_window);
                return;
            }

            EnsureWindow();

            var window = _window;
            if (window is null)
            {
                return;
            }

            // Resizing and auto-hide must not wait for a slow music provider.
            ApplyCurrentPlacement(window);
            await _media.RefreshAsync();
            // A mode change may have closed this presentation while a provider was awaited.
            if (generation != _presentationGeneration || !ReferenceEquals(window, _window)) return;
            // The visibility timer can update geometry while the provider is awaited.
            // Never replay the old rectangle over a resize or auto-hide just handled.
            if (IsTop ? _lastTopScreen is null : _lastTaskbar is null || !NativeMethods.IsWindow(_lastTaskbar.Handle))
            {
                HidePresentation(window);
                return;
            }
            var snap = _media.Current;

            if (snap.HasSession)
            {
                _everHadSession = true;
            }

            bool idleHint = false;
            bool shouldShow;

            if (_forceHidden)
            {
                shouldShow = false;
            }
            else if (snap.HasSession)
            {
                shouldShow = true;
            }
            else if (!_everHadSession && _settings.IdleHint)
            {
                // 启动就一直没会话：多半是播放器没开 SMTC，给个明确提示而不是静默消失
                shouldShow = true;
                idleHint = true;
            }
            else
            {
                shouldShow = !_settings.HideWhenNoMedia;
            }

            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return;
            }

            if (!shouldShow)
            {
                HidePresentation(window);
                _tray?.SetTooltip("栖音 · 已隐藏");
                return;
            }

            ApplyCurrentPlacement(window);

            if (idleHint)
            {
                window.ApplyIdleHint();
                _tray?.SetTooltip("栖音 · 未检测到媒体会话");
            }
            else
            {
                window.ApplySnapshot(snap);
                string playing = snap.HasSession
                    ? $"{snap.DisplayTitle} - {snap.DisplayArtist}"
                    : "无播放";
                _tray?.SetTooltip("栖音 · " + playing);
            }
        }
        catch (Exception ex)
        {
            Log.Write("心跳异常: " + ex);
        }
        finally
        {
            _ticking = false;
        }
    }

    /// <summary>按配置的模式落位。</summary>
    private void ApplyPlacement(MainWindow window, TaskbarInfo bar)
    {
        _fullscreenHidden = FullscreenDetector.IsActiveOn(bar.MonitorRect);
        if (_forceHidden || _fullscreenHidden || (!_media.Current.HasSession
            && (_everHadSession || !_settings.IdleHint) && _settings.HideWhenNoMedia))
        {
            HidePresentation(window);
            return;
        }

        if (string.Equals(_settings.Mode, TaskbarHost.ModeDock, StringComparison.OrdinalIgnoreCase))
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            bool docked = hwnd != IntPtr.Zero && NativeMethods.GetParent(hwnd) == bar.Handle;
            if (docked)
            {
                TaskbarHost.PlaceDocked(window, bar, _settings);
            }
            else
            {
                TaskbarHost.DockIntoTaskbar(window, bar, _settings, restyle: true);
            }

            SyncPresentationVisibility(window);
            return;
        }

        TaskbarHost.PlaceOverlay(window, bar, _settings);
        SyncPresentationVisibility(window);
    }

    private bool RefreshHostSelection()
    {
        if (IsTop)
        {
            // Pointer interaction, menus and scrubbing stay on the screen where they began.
            if (_lastTopScreen is null || _window?.IsTopInteractionActive != true)
                _lastTopScreen = TopScreenHost.Pick(_settings);
            _lastTaskbar = null;
            return _lastTopScreen is not null;
        }
        _lastTaskbar = TaskbarHost.Pick(_settings);
        _lastTopScreen = null;
        return _lastTaskbar is not null;
    }

    private void ApplyCurrentPlacement(MainWindow window)
    {
        if (IsTop && _lastTopScreen is not null) ApplyTopPlacement(window, _lastTopScreen);
        else if (!IsTop && _lastTaskbar is not null) ApplyPlacement(window, _lastTaskbar);
        else HidePresentation(window);
    }

    private static void HidePresentation(MainWindow window)
    {
        window.SetPresentationVisible(false);
        TaskbarHost.Hide(window);
    }

    private static void SyncPresentationVisibility(MainWindow window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        window.SetPresentationVisible(hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd));
    }

    private void ApplyTopPlacement(MainWindow window, TopScreenInfo screen)
    {
        if (_placingTop) return;
        _placingTop = true;
        try
        {
            var layout = TopScreenHost.ComputeLayout(screen, _settings, window.Width, window.Height);
            var barRect = new NativeMethods.RECT { Left = layout.X, Top = layout.Y,
                Right = layout.X + layout.Width, Bottom = layout.Y + layout.Height };
            var front = FullscreenDetector.GetVisibleSurface(screen.MonitorRect);
            var surface = front is not null && FullscreenDetector.Intersects(front.WindowRect, barRect)
                ? front : FullscreenDetector.GetVisibleSurface(screen.MonitorRect, barRect);
            _fullscreenHidden = (front is not null && FullscreenDetector.CoversMonitor(front.ContentRect, screen.MonitorRect))
                || (surface is not null && FullscreenDetector.CoversMonitor(surface.ContentRect, screen.MonitorRect));
            if (_forceHidden || _fullscreenHidden || (!_media.Current.HasSession
                && (_everHadSession || !_settings.IdleHint) && _settings.HideWhenNoMedia))
            {
                HidePresentation(window);
                return;
            }
            // Keep the requested taskbar-like strip visible; only fullscreen/media/manual rules hide it.
            TopScreenHost.Place(window, screen, _settings);
            SyncPresentationVisibility(window);
        }
        finally { _placingTop = false; }
    }

    private void RefreshFullscreenVisibility()
    {
        if (_window is null) return;
        if (IsTop)
        {
            if (_lastTopScreen is not null) ApplyTopPlacement(_window, _lastTopScreen);
            return;
        }
        // 200 ms 仅检查前台和已知任务栏的矩形；尺寸改变时才重新枚举。
        var bar = _lastTaskbar;
        if (bar is null) return;
        if (!NativeMethods.IsWindow(bar.Handle)) { HidePresentation(_window); return; }
        if (NativeMethods.GetWindowRect(bar.Handle, out var liveRect)
            && (liveRect.Left != bar.Rect.Left || liveRect.Top != bar.Rect.Top
                || liveRect.Width != bar.Rect.Width || liveRect.Height != bar.Rect.Height))
        {
            Reposition();
            return;
        }
        bool hidden = FullscreenDetector.IsActiveOn(bar.MonitorRect);
        if (hidden)
        {
            _fullscreenHidden = true;
            HidePresentation(_window);
        }
        else if (_fullscreenHidden)
        {
            // 恢复仍经过统一的手动隐藏/无媒体判断，不强制显示。
            Reposition();
        }
    }

    /// <summary>重新计算落位（供菜单/托盘调用）。</summary>
    public void Reposition()
    {
        if (_placingTop) return;
        RefreshHostSelection();
        if (_window is not null) ApplyCurrentPlacement(_window);
    }

    public void SetPlacement(string placement)
    {
        if (placement is not ("taskbar" or "top") || string.Equals(_settings.Placement, placement, StringComparison.OrdinalIgnoreCase)) return;
        _presentationGeneration++;
        _settings.Placement = placement;
        _settings.Save();
        _lastTaskbar = null;
        _lastTopScreen = null;
        var old = _window;
        _window = null;
        old?.Close();
        // Recreate only the view: this also removes an old dock parent/Explorer owner.
        EnsureWindow();
    }

    private void ToggleVisible()
    {
        _forceHidden = !_forceHidden;
        Log.Write($"手动切换显示: {(_forceHidden ? "隐藏" : "显示")}");
        Reposition();
    }

    private void CreateTray()
    {
        var items = new List<TrayItem>
        {
            TrayItem.Header("栖音 · 任务栏播控"),
            TrayItem.Separator,
            new() { Text = "显示位置", Children = () => new List<TrayItem>
            {
                new() { Text = "任务栏", IsChecked = () => !IsTop, Click = () => SetPlacement("taskbar") },
                new() { Text = "顶部长条（试用）", IsChecked = () => IsTop, Click = () => SetPlacement("top") },
            } },
            new() { Text = "播放来源", Children = () =>
            {
                var sources = new List<TrayItem> { new() { Text = "自动切换（暂停保留当前来源）",
                    IsChecked = () => !_media.SourceLocked, Click = () => _media.SelectSource(null) }, TrayItem.Separator };
                sources.AddRange(_media.Sources.Select(s => new TrayItem { Text = s.Label + (s.Playing ? " · 播放中" : "")
                    + (s.Selected && _media.SourceLocked ? " · 已锁定" : ""), IsChecked = () => s.Selected, Click = () => _media.SelectSource(s.Key) }));
                if (_media.Sources.Count == 0) sources.Add(TrayItem.Header("暂无可用来源"));
                return sources;
            } },
            new() { Text = "显示 / 隐藏播控条", Click = ToggleVisible },
            new()
            {
                Text = "重新吸附 / 归位",
                Click = () =>
                {
                    if (IsTop) { _settings.TopOffsetX = 0; _settings.TopOffsetY = 8; }
                    else _settings.Offset = 0;
                    _settings.Save();
                    Reposition();
                },
            },
            new()
            {
                Text = "无播放时自动隐藏",
                Click = () =>
                {
                    _settings.HideWhenNoMedia = !_settings.HideWhenNoMedia;
                    _settings.Save();
                },
                IsChecked = () => _settings.HideWhenNoMedia,
            },
            new()
            {
                Text = "避开任务栏其他组件",
                IsChecked = () => _settings.AvoidOthers,
                Click = () =>
                {
                    _settings.AvoidOthers = !_settings.AvoidOthers;
                    _settings.Save();
                    Reposition();
                },
            },
            new()
            {
                Text = "显示歌词（逐字）",
                IsChecked = () => _settings.ShowLyrics,
                Click = () =>
                {
                    _settings.ShowLyrics = !_settings.ShowLyrics;
                    _settings.Save();
                    _window?.ApplySettingsChanged();
                },
            },
            new()
            {
                Text = "毛玻璃质感",
                IsChecked = () => _settings.Glass,
                Click = () =>
                {
                    _settings.Glass = !_settings.Glass;
                    _settings.Save();
                    _window?.ApplySettingsChanged();
                },
            },
            new()
            {
                Text = "跟随鼠标所在屏",
                IsChecked = () => (IsTop ? _settings.TopMonitor : _settings.Monitor) == "cursor",
                Click = () =>
                {
                    if (IsTop) { _settings.TopMonitor = _settings.TopMonitor == "cursor" ? "primary" : "cursor"; _lastTopScreen = null; }
                    else { _settings.Monitor = _settings.Monitor == "cursor" ? "primary" : "cursor"; _settings.Offset = 0; }
                    _settings.Save();
                    Reposition();
                },
            },
            new()
            {
                Text = "开机自动启动",
                IsChecked = AutoStart.IsEnabled,
                Click = () => AutoStart.Set(!AutoStart.IsEnabled()),
            },
            TrayItem.Separator,
            new()
            {
                Text = "打开配置文件",
                Click = () =>
                {
                    _settings.Save();
                    OpenPath(AppSettings.FilePath);
                },
            },
            new()
            {
                Text = "打开日志文件夹",
                Click = () =>
                {
                    System.IO.Directory.CreateDirectory(Log.Dir);
                    OpenPath(Log.Dir);
                },
            },
            TrayItem.Separator,
            new() { Text = "退出", Click = Shutdown },
        };

        _tray = new TrayIcon();
        _tray.Initialize(items, ToggleVisible, "栖音 · 任务栏播控");
    }

    private static void OpenPath(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            IntPtr hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd))
            {
                return;
            }

            Log.Write("窗口句柄失效，重建窗口");
            try
            {
                _window.Close();
            }
            catch
            {
                // ignore
            }

            _window = null;
        }

        var w = new MainWindow(_settings, _media);
        w.OffsetDeltaRequested += delta =>
        {
            // 夹在合理范围内：别让光标瞬移（或误触）把条甩到屏幕外
            if (IsTop) _settings.TopOffsetX = (int)Math.Clamp(_settings.TopOffsetX + delta, -4000, 4000);
            else _settings.Offset = (int)Math.Clamp(_settings.Offset + delta, -2000, 2600);
            _settings.Save();
            Reposition();
        };
        w.ExitRequested += Shutdown;
        w.RepositionRequested += () => { _lastTopScreen = null; Reposition(); };
        w.TopLayoutChanged += Reposition;
        w.PlacementRequested += SetPlacement;
        w.AnchorChanged += anchor =>
        {
            if (IsTop) _settings.TopAnchor = anchor == "beforeTray" ? "right" : anchor;
            else _settings.Anchor = anchor;
            _settings.Save();
            Reposition();
        };
        w.ShowVisibleToggled += value =>
        {
            _settings.HideWhenNoMedia = value;
            _settings.Save();
        };

        _window = w;
        w.Left = -10000;
        w.Top = -10000;
        w.Show();
        Reposition();
    }

    public void Shutdown()
    {
        Log.Write("退出");
        try
        {
            _timer?.Stop();
            _visibilityTimer?.Stop();
            _tray?.Dispose();
            _media.Dispose();
            _window?.Close();
        }
        catch
        {
            // ignore
        }

        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        _visibilityTimer?.Stop();
        _visibilityTimer = null;
        _tray?.Dispose();
        _tray = null;
    }
}
