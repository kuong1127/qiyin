using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

namespace TaskbarMediaBar;

public partial class MainWindow : Window
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private bool _compactCover;
    private readonly ScaleTransform _adaptiveScale = new(1, 1);
    private bool _isTopPresentation;
    private bool _presentationVisible = true;
    private bool _notifyingTopLayout;

    public bool IsTopInteractionActive => _isTopPresentation && (Root.IsMouseOver || _seeking
        || _seekCommitting || _dragWhole || _menu?.IsOpen == true || IsKeyboardFocusWithin);
    public event Action<string>? PlacementRequested;
    public event Action? TopLayoutChanged;

    private readonly AppSettings _settings;
    private readonly MediaSessionService _media;
    private readonly Rectangle _progressBg = new();
    private readonly Rectangle _progressFill = new();

    private bool _dragWhole;
    private int _dragLastCursorX;
    private bool _seeking;
    private bool _mouseCaptured;
    private bool _seekCommitting;
    private string _seekIdentity = "";
    private double _seekDuration;
    private DispatcherTimer? _seekPointerTimer;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    private string _status = "";
    private DispatcherTimer? _statusTimer;

    private Storyboard? _titleMarquee;
    private string _marqueeKey = "";
    private string _lastProbeIdentity = "";
    private string _renderedIdentity = "";
    private DateTime _lastProbeAt;
    private bool _likeProbeBusy;
    private bool _liked;
    private bool _likeKnown;
    private bool _likeConfirmed;
    private bool _closed;
    private bool _transportBusy;
    private readonly TrackOperationGate _likeGate = new();
    private bool _idleHint;
    private bool _light = true;
    private Color _accent = Color.FromRgb(0x3D, 0x84, 0xE0);
    private Color _iconFg = Color.FromRgb(0x30, 0x30, 0x30);
    private Color _subFg = Color.FromRgb(0x63, 0x63, 0x63);
    private ContextMenu? _menu;
    private IDisposable? _seekSourceHold;

    private readonly LyricsService _lyrics = new();
    private LyricsService.LyricDoc? _lyricDoc;
    private string _lyricKey = "";
    private DispatcherTimer? _lyricTimer;
    private CancellationTokenSource? _lyricFetchCancellation;
    private bool _lyricFetchBusy;
    private DateTimeOffset _lyricRetryAt;
    private string? _renderedLyricText;
    private int _renderedSung = -1;

    public event Action<double>? OffsetDeltaRequested;

    public event Action? ExitRequested;

    public event Action<string>? AnchorChanged;

    public event Action<bool>? ShowVisibleToggled;

    public event Action? RepositionRequested;

    public MainWindow(AppSettings settings, MediaSessionService media)
    {
        _settings = settings;
        _media = media;

        InitializeComponent();
        Root.LayoutTransform = _adaptiveScale;
        SizeChanged += (_, _) =>
        {
            UpdateAdaptiveContent();
            if (_isTopPresentation) NotifyTopLayoutChanged();
        };
        IsVisibleChanged += (_, _) => SetPresentationVisible(IsVisible);

        _progressBg.HorizontalAlignment = HorizontalAlignment.Stretch;
        _progressBg.VerticalAlignment = VerticalAlignment.Bottom;
        _progressBg.Height = 2;
        _progressBg.RadiusX = _progressBg.RadiusY = 2;
        _progressFill.HorizontalAlignment = HorizontalAlignment.Left;
        _progressFill.VerticalAlignment = VerticalAlignment.Bottom;
        _progressFill.Height = 2;
        _progressFill.RadiusX = _progressFill.RadiusY = 2;
        _progressFill.Width = 0;

        ProgressTrack.Children.Add(_progressBg);
        ProgressTrack.Children.Add(_progressFill);
        ProgressTrack.MouseEnter += (_, _) =>
        {
            _progressBg.Height = 4;
            _progressFill.Height = 4;
        };
        ProgressTrack.MouseLeave += (_, _) =>
        {
            _progressBg.Height = 2;
            _progressFill.Height = 2;
            if (!_seeking && _status.Length > 0)
            {
                _status = "";
                ApplySnapshot(_media.Current);
            }
        };

        Cover.Cursor = Cursors.Hand;
        Cover.ToolTip = null;
        Cover.MouseLeftButtonUp += (_, e) => { _media.FocusSourceApp(); e.Handled = true; };
        Cover.MouseEnter += (_, _) => Cover.Opacity = 0.82;
        Cover.MouseLeave += (_, _) => Cover.Opacity = 1.0;

        PrevBtn.Click += async (_, _) => await RunTransportAsync("上一首", _media.PreviousAsync);
        NextBtn.Click += async (_, _) => await RunTransportAsync("下一首", _media.NextAsync);
        PlayBtn.Click += async (_, _) => await RunTransportAsync("播放 / 暂停", _media.TogglePlayPauseAsync);
        LikeBtn.Click += OnLikeClicked;

        ProgressTrack.MouseLeftButtonDown += ProgressTrack_MouseLeftButtonDown;
        ProgressTrack.MouseMove += ProgressTrack_MouseMove;
        ProgressTrack.MouseLeftButtonUp += ProgressTrack_MouseLeftButtonUp;
        ProgressTrack.LostMouseCapture += (_, _) => CancelSeekPreview();
        ProgressTrack.SizeChanged += (_, _) => UpdateProgress(_media.Current);

        MouseRightButtonUp += (_, _) => ShowMenu();

        TextStack.SizeChanged += (_, _) => UpdateMarquee();

        Loaded += (_, _) =>
        {
            RefreshPresentation();
            ApplyTheme();
            BuildMenu();
            ApplySnapshot(_media.Current);
        };

        Closed += (_, _) =>
        {
            _seekSourceHold?.Dispose();
            _seekSourceHold = null;
            _closed = true;
            _likeGate.Observe("");
            _statusTimer?.Stop();
            _seekPointerTimer?.Stop();
            StopMarquee();
            StopLyricMotion();
            StopLyricTimer();
            _lyricFetchCancellation?.Cancel();
            _lyrics.Dispose();
        };
        RefreshPresentation();
    }

    /// <summary>Applies the requested location without replacing media or business event handlers.</summary>
    public void RefreshPresentation()
    {
        bool top = string.Equals(_settings.Placement, "top", StringComparison.OrdinalIgnoreCase);
        bool changed = top != _isTopPresentation;
        _isTopPresentation = top;
        double width = _settings.Width > 0 ? _settings.Width : 360;
        double oldWidth = Width, oldHeight = Height;
        // TaskbarHost owns adaptive taskbar dimensions; settings refresh must preserve them.
        if (top || changed)
        {
            Width = top ? Math.Clamp(width, 220, 720) : width;
            Height = 40;
        }
        Surface.CornerRadius = top ? new CornerRadius(8) : new CornerRadius(8, 8, 0, 0);
        _marqueeKey = "";
        UpdateAdaptiveContent();
        ApplyTheme();
        ApplySnapshot(_media.Current);
        if (changed || (top && (Width != oldWidth || Height != oldHeight))) NotifyTopLayoutChanged();
    }

    private void NotifyTopLayoutChanged()
    {
        if (_notifyingTopLayout) return;
        _notifyingTopLayout = true;
        try { TopLayoutChanged?.Invoke(); }
        finally { _notifyingTopLayout = false; }
    }

    /// <summary>Stops invisible text work; repeated host placement calls are harmless.</summary>
    public void SetPresentationVisible(bool visible)
    {
        if (_presentationVisible == visible) return;
        _presentationVisible = visible;
        if (!visible)
        {
            if (_menu?.IsOpen == true) _menu.IsOpen = false;
            CancelSeekPreview();
            _seekPointerTimer?.Stop();
            if (_dragWhole) { _dragWhole = false; Root.ReleaseMouseCapture(); }
            StopMarquee();
            StopLyricTimer();
            StopLyricMotion();
            Cover.BeginAnimation(OpacityProperty, null);
            Cover.Opacity = 1;
            return;
        }
        _marqueeKey = "";
        ApplySnapshot(_media.Current);
        UpdateMarquee();
    }

    private bool TextMotionVisible => _presentationVisible && IsVisible;

    private void StopLyricMotion()
    {
        if (!LyricTranslation.HasAnimatedProperties) return;
        double position = LyricTranslation.X;
        LyricTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        LyricTranslation.X = position;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(WndProc);
        if (source is not null)
        {
            long ex = NativeMethods.GetWindowLongPtr(source.Handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(source.Handle, NativeMethods.GWL_EXSTYLE,
                new IntPtr(ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE：点击可用，但不抢走当前应用的焦点。
        }
        if (msg is WM_SETTINGCHANGE or WM_THEMECHANGED or WM_DPICHANGED)
        {
            ApplyTheme();
        }
        if (msg is WM_SETTINGCHANGE or WM_DPICHANGED or WM_DISPLAYCHANGE)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_closed) return;
                UpdateAdaptiveContent();
                RepositionRequested?.Invoke();
            }));

        return IntPtr.Zero;
    }

    private void UpdateAdaptiveContent()
    {
        if (ActualHeight <= 0 || ActualWidth <= 0) return;
        double scale = _isTopPresentation ? 1 : Math.Clamp(ActualHeight / 40, 0.5, 1.35);
        if (Math.Abs(_adaptiveScale.ScaleX - scale) > 0.001)
            _adaptiveScale.ScaleX = _adaptiveScale.ScaleY = scale;
        _compactCover = !_isTopPresentation && ActualWidth / scale < 280;
        Cover.Visibility = _settings.ShowCover && !_compactCover ? Visibility.Visible : Visibility.Collapsed;
    }

    private double Scale
    {
        get
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                uint dpi = NativeMethods.GetDpiForWindow(hwnd);
                if (dpi >= 72)
                {
                    return dpi / 96.0;
                }
            }
            catch
            {
                // ignore
            }

            return 1.0;
        }
    }

    private static readonly bool DebugSkin =
        Environment.GetEnvironmentVariable("TMB_DEBUG") == "1";

    private void ApplyTheme()
    {
        if (DebugSkin)
        {
            Surface.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
            TitleText.Foreground = new SolidColorBrush(Colors.White);
            TitleText.Text = "RENDER OK";
            ArtistText.Foreground = new SolidColorBrush(Colors.White);
            ArtistText.Text = "debug skin";
            _progressBg.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x00));
            _progressFill.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00));
            _progressFill.Width = 120;
            _liked = true;
            _likeKnown = true;
            _iconFg = Colors.White;
            _accent = Colors.Yellow;
            _subFg = Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);
            UpdateHeartBrush();

            // 顺便验证逐字高亮渲染路径
            RenderLyricLine(new LyricLine(0, "这是一句逐字高亮测试歌词", new List<LyricWord>()), 5);

            Log.Write("调试皮肤已应用（矢量爱心）");
            return;
        }

        try
        {
            _light = true;
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("SystemUsesLightTheme") is int v)
            {
                _light = v != 0;
            }
        }
        catch
        {
            _light = true;
        }

        if (_isTopPresentation) _light = true;

        // A1：用克制的绿色标出播放和喜欢，其余按钮保持中性色。
        _accent = _light ? Color.FromRgb(0x28, 0x65, 0x4C) : Color.FromRgb(0xBD, 0xD7, 0xA9);

        Color barBg = _light ? Color.FromRgb(0xFA, 0xFC, 0xFA) : Color.FromRgb(0x2B, 0x37, 0x2D);
        Color titleFg = _light ? Color.FromRgb(0x25, 0x39, 0x2F) : Color.FromRgb(0xEC, 0xF0, 0xE7);
        _subFg = _light ? Color.FromRgb(0x71, 0x80, 0x75) : Color.FromRgb(0xA2, 0xAF, 0xA0);
        _iconFg = titleFg;
        _trackBg = _light ? Color.FromRgb(0xDF, 0xE7, 0xDD) : Color.FromRgb(0x49, 0x5A, 0x43);
        Resources["ButtonHoverBrush"] = new SolidColorBrush(_light
            ? Color.FromRgb(0xEA, 0xF0, 0xEA) : Color.FromRgb(0x3B, 0x4A, 0x39));
        Resources["ButtonPressedBrush"] = new SolidColorBrush(_light
            ? Color.FromRgb(0xDF, 0xE9, 0xDE) : Color.FromRgb(0x43, 0x5C, 0x3B));

        // 毛玻璃：窗口本身交给 acrylic 做背景模糊，WPF 这层只铺半透明底色
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            hwnd = new WindowInteropHelper(this).Handle;
        }
        catch
        {
            hwnd = IntPtr.Zero;
        }

        if (_settings.Glass && hwnd != IntPtr.Zero)
        {
            bool ok = NativeMethods.EnableAcrylic(hwnd, 0xC0, barBg.R, barBg.G, barBg.B);
            Surface.Background = new SolidColorBrush(
                ok ? Color.FromArgb(0x66, barBg.R, barBg.G, barBg.B) : barBg);
            Log.Write($"毛玻璃={(ok ? "已开启" : "失败，退回实色")}");
        }
        else
        {
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.DisableAcrylic(hwnd);
            }

            Surface.Background = new SolidColorBrush(barBg);
        }

        Surface.BorderBrush = new SolidColorBrush(
            _light ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

        TitleText.Foreground = new SolidColorBrush(titleFg);
        ArtistText.Foreground = new SolidColorBrush(_subFg);
        PrevBtn.Foreground = new SolidColorBrush(_iconFg);
        NextBtn.Foreground = new SolidColorBrush(_iconFg);
        PlayBtn.Foreground = new SolidColorBrush(_accent);
        PlayBtn.Background = Brushes.Transparent;
        PrevGlyph.Stroke = new SolidColorBrush(_iconFg);
        NextGlyph.Stroke = new SolidColorBrush(_iconFg);
        PlayGlyph.Stroke = new SolidColorBrush(_accent);
        PauseGlyph.Stroke = new SolidColorBrush(_accent);
        _progressVisible = true;
        _progressBg.Fill = new SolidColorBrush(_trackBg);
        _progressFill.Fill = new SolidColorBrush(_accent);

        UpdateHeartBrush();
        _marqueeKey = "";
        UpdateMarquee();
    }

    /// <summary>从注册表读系统强调色（AccentColor 是 0xAABBGGRR）。</summary>
    private Color ReadAccentColor()
    {
        Color fallback = _light ? Color.FromRgb(0x3D, 0x84, 0xE0) : Color.FromRgb(0x5A, 0xA0, 0xFF);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            object? raw = key?.GetValue("AccentColor");
            if (raw is null)
            {
                return fallback;
            }

            long v = raw switch
            {
                int i => i,
                uint u => u,
                _ => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
            };

            var color = Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));

            int luma = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
            if (luma is > 225 or < 40)
            {
                return fallback;
            }

            return color;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>字体里有没有实心爱心字形；没有就退回空心 + 强调色，避免显示成方块。</summary>
    private static readonly bool HasHeartFill = CheckGlyph('\uEB52');

    private static bool CheckGlyph(char c)
    {
        try
        {
            var typeface = new Typeface(new FontFamily("Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyphTypeface))
            {
                return glyphTypeface.CharacterToGlyphMap.ContainsKey(c);
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>进度条显隐（兜底通道没有时间轴）。用颜色切换，避免改布局。</summary>
    private void UpdateProgressVisibility(bool show)
    {
        if (_progressVisible == show)
        {
            return;
        }

        _progressVisible = show;
        _progressBg.Fill = show ? new SolidColorBrush(_trackBg) : Brushes.Transparent;
        _progressFill.Fill = show ? new SolidColorBrush(_accent) : Brushes.Transparent;
        if (!show)
        {
            _progressFill.Width = 0;
        }
    }

    private bool _progressVisible = true;
    private Color _trackBg = Color.FromRgb(0xE4, 0xE4, 0xE4);

    /// <summary>切换播放/暂停图标（两个 Path 换可见性）。</summary>
    private void SetPlayGlyph(bool playing, bool known = true)
    {
        PlayGlyph.Visibility = !playing ? Visibility.Visible : Visibility.Collapsed;
        PauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        string label = !known ? "播放 / 暂停（状态待确认）" : playing ? "暂停" : "播放";
        PlayBtn.ToolTip = label;
        AutomationProperties.SetName(PlayBtn, label);
    }

    private void UpdateHeartBrush()
    {
        bool busy = _likeGate.CurrentBusy;
        var heartColor = _light ? Color.FromRgb(0xE4, 0x46, 0x60) : Color.FromRgb(0xFF, 0x78, 0x8F);
        var brush = new SolidColorBrush((_likeKnown && _liked) || busy ? heartColor : _iconFg);
        LikeBtn.Foreground = brush;
        HeartGlyph.Stroke = brush;
        HeartGlyph.Fill = _likeKnown && _liked && !busy ? brush : Brushes.Transparent;
        LikePendingDot.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LikeBtn.Background = busy
            ? new SolidColorBrush(Color.FromArgb(30, _accent.R, _accent.G, _accent.B))
            : Brushes.Transparent;
        LikeBtn.ToolTip = busy ? "正在处理喜欢…"
            : !_likeKnown ? "切换喜欢（状态待同步）"
            : !_likeConfirmed ? (_liked ? "喜欢（已发送，待同步）" : "取消喜欢（已发送，待同步）")
            : _liked ? "已喜欢（点击取消）" : "加入「我喜欢」";
        AutomationProperties.SetName(LikeBtn, (string)LikeBtn.ToolTip);
    }

    public void ApplySnapshot(MediaSnapshot snap)
    {
        _likeGate.Observe(snap.HasSession ? snap.Identity : "");
        string identity = snap.HasSession ? snap.Identity : "";
        if (_renderedIdentity != identity)
        {
            CancelSeekPreview();
            _renderedIdentity = identity;
            _likeKnown = false;
            _likeConfirmed = false;
            _liked = false;
            _lastProbeIdentity = "";
            _statusTimer?.Stop();
            _status = "";
            UpdateHeartBrush();
        }
        if (DebugSkin)
        {
            return;
        }

        if (!snap.HasSession && _idleHint)
        {
            return;
        }

        _idleHint = false;

        string title = snap.DisplayTitle;
        if (TitleText.Text != title)
        {
            TitleText.Text = title;
            UpdateMarquee();
        }

        UpdateSecondaryText(snap);

        // 图标现在是 Path，切换播放/暂停是切两个 Path 的可见性，不能再改 Content
        SetPlayGlyph(snap.IsPlaying, snap.PlaybackStateKnown);

        PrevBtn.IsEnabled = snap.CanPrev;
        NextBtn.IsEnabled = snap.CanNext;
        PrevBtn.Visibility = snap.CanPrev ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.Visibility = snap.CanNext ? Visibility.Visible : Visibility.Collapsed;
        PlayBtn.IsEnabled = snap.CanPlayPause;

        // 爱心：兜底源只有在配置了"收藏快捷键"时才显示（否则点了也没通道，宁可别给假按钮）
        bool likeAvailable = CanLike(snap);
        LikeBtn.Visibility = _settings.ShowLikeButton && likeAvailable ? Visibility.Visible : Visibility.Collapsed;
        LikeBtn.IsEnabled = snap.HasSession && likeAvailable;
        Cover.Visibility = _settings.ShowCover && !_compactCover ? Visibility.Visible : Visibility.Collapsed;

        // 兜底通道（网易云这类不上报 SMTC 的）：控制走全局快捷键
        if (snap.IsFallback)
        {
            var profile = _settings.FindHotkeyProfile(snap.SourceAppId);
            Root.ToolTip = profile is null
                ? "该播放器没有向 Windows 上报媒体信息，只能显示歌名/艺人/封面。"
                : $"该播放器不上报媒体信息，控制通过它的全局快捷键完成（{profile.PlayPause} 播放/暂停、{profile.Like} 收藏）。\n"
                  + "如果按键无效，请到播放器「设置 → 快捷键」里勾选「启用全局快捷键」。";
        }
        else
        {
            Root.ToolTip = null;
        }

        UpdateProgressVisibility(!snap.IsFallback && double.IsFinite(snap.DurationSeconds) && snap.DurationSeconds > 0);
        ProgressTrack.IsHitTestVisible = !snap.IsFallback && snap.CanSeek && double.IsFinite(snap.DurationSeconds) && snap.DurationSeconds > 0;
        ProgressTrack.Cursor = snap.CanSeek ? Cursors.Hand : Cursors.Arrow;
        if (!snap.IsFallback && snap.HasSession && snap.DurationSeconds > 0) StartLyricTimer();
        else StopLyricTimer();

        if (_settings.ShowCover && snap.Cover is not null)
        {
            if (Cover.Tag as string != snap.Identity)
            {
                Cover.Background = new ImageBrush(snap.Cover) { Stretch = Stretch.UniformToFill };
                Cover.Tag = snap.Identity;
                CoverGlyph.Visibility = Visibility.Collapsed;
                Cover.Opacity = 1;
                if (_presentationVisible && IsVisible)
                    Cover.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { FillBehavior = FillBehavior.Stop });
            }
        }
        else if (Cover.Tag as string != "placeholder")
        {
            Cover.Background = new SolidColorBrush(_light ? Color.FromRgb(0xD9, 0xD9, 0xD9) : Color.FromRgb(0x3F, 0x3F, 0x3F));
            CoverGlyph.Foreground = new SolidColorBrush(_light ? Color.FromRgb(0x8C, 0x8C, 0x8C) : Color.FromRgb(0xA0, 0xA0, 0xA0));
            CoverGlyph.Visibility = Visibility.Visible;
            Cover.Tag = "placeholder";
        }

        UpdateProgress(snap);
        ProbeLikeStateIfNeeded(snap);
    }

    // ---------- 副标题：状态 > 歌词（逐字高亮） > 艺人 ----------

    private void UpdateSecondaryText(MediaSnapshot snap)
    {
        if (!_presentationVisible)
        {
            StopLyricMotion();
            return;
        }
        if (_status.Length > 0)
        {
            SetArtistPlain(_status);
            return;
        }

        if (_idleHint)
        {
            SetArtistPlain("QQ音乐：设置 → 常规设置 → 通知 → 开启 SMTC");
            return;
        }

        if (!MediaSourceNames.IsMusic(snap.SourceAppId))
        {
            if (_lyricKey != snap.Identity)
            {
                _lyricKey = snap.Identity;
                _lyricDoc = null;
                _lyricFetchCancellation?.Cancel();
                _lyricFetchBusy = false;
            }
            SetArtistPlain(MediaSourceNames.Display(snap.SourceAppId)
                + (snap.DisplayArtist == "—" ? "" : " · " + snap.DisplayArtist));
            return;
        }

        if (!_settings.ShowLyrics || !snap.HasSession || snap.IsFallback || snap.DurationSeconds <= 0)
        {
            SetArtistPlain(snap.DisplayArtist);
            return;
        }

        if (snap.Identity != _lyricKey)
        {
            _lyricKey = snap.Identity;
            _lyricDoc = null;
            StartLyricFetch(snap);
            SetArtistPlain(snap.DisplayArtist);
            return;
        }

        if (_lyricDoc is null || _lyricDoc.IsEmpty)
        {
            if (!_lyricFetchBusy && DateTimeOffset.UtcNow >= _lyricRetryAt) StartLyricFetch(snap);
            SetArtistPlain(snap.DisplayArtist);
            return;
        }

        var (line, sung) = LyricsService.Locate(_lyricDoc, snap.EstimatedPositionSeconds);
        if (line is null)
        {
            SetArtistPlain("间奏");
            return;
        }

        RenderLyricLine(line, sung < 0 ? line.Text.Length : sung);
        UpdateLyricScroll(snap, line);
    }

    private void UpdateLyricScroll(MediaSnapshot snap, LyricLine line)
    {
        // Follow the media timeline: pausing freezes the text and seeking resets it immediately.
        double end = snap.DurationSeconds;
        foreach (var candidate in _lyricDoc!.Lines)
            if (candidate.StartMs > line.StartMs) { end = candidate.StartMs / 1000.0; break; }
        double duration = Math.Max(0.1, end - line.StartMs / 1000.0);
        double lead = Math.Min(0.8, duration * 0.15);
        double tail = Math.Min(0.6, duration * 0.15);
        double fraction = Math.Clamp((snap.EstimatedPositionSeconds - line.StartMs / 1000.0 - lead)
            / Math.Max(0.1, duration - lead - tail), 0, 1);
        double overflow = LyricViewport.ScrollableWidth;
        double target = -overflow * fraction;
        double current = LyricTranslation.X;
        double speed = overflow / Math.Max(0.1, duration - lead - tail);
        // Animate only the rendered text, not the ScrollViewer layout. Native WPF
        // animation fills the frames between the existing 120 ms timeline samples.
        bool jump = Math.Abs(current - target) > Math.Max(8, speed * 0.4);
        LyricTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        LyricTranslation.X = target;
        if (snap.IsPlaying && TextMotionVisible && overflow > 0 && fraction > 0 && fraction < 1)
        {
            LyricTranslation.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(jump ? target : current, Math.Max(-overflow, target - speed * 0.12),
                    TimeSpan.FromMilliseconds(120)));
        }
    }

    private void StartLyricFetch(MediaSnapshot snap)
    {
        string title = snap.DisplayTitle;
        string artist = snap.Artist;
        string identity = snap.Identity;
        string source = snap.SourceAppId;
        double duration = snap.DurationSeconds;
        if (string.IsNullOrWhiteSpace(title) || title == "未知曲目")
        {
            return;
        }

        _lyricFetchCancellation?.Cancel();
        _lyricFetchCancellation?.Dispose();
        _lyricFetchCancellation = new CancellationTokenSource();
        var token = _lyricFetchCancellation.Token;
        _lyricFetchBusy = true;
        _lyricRetryAt = DateTimeOffset.UtcNow.AddSeconds(15);
        _ = Fetch();
        async Task Fetch()
        {
            try
            {
                var doc = await _lyrics.FetchAsync(title, artist, token, source, duration).ConfigureAwait(false);
                if (token.IsCancellationRequested || _closed) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_closed || _lyricKey != identity || token.IsCancellationRequested) return;
                    _lyricFetchBusy = false;
                    _lyricDoc = doc;
                    if (doc is null) _lyricRetryAt = DateTimeOffset.UtcNow.AddSeconds(15);
                    UpdateSecondaryText(_media.Current);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write("歌词刷新失败: " + ex.Message);
                if (!_closed && _lyricKey == identity) _lyricFetchBusy = false;
            }
        }
    }

    private void StartLyricTimer()
    {
        if (!TextMotionVisible) { StopLyricTimer(); return; }
        if (_lyricTimer is not null)
        {
            return;
        }

        // 逐字高亮要跟得上，1 秒一跳太糙
        _lyricTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(120) };
        _lyricTimer.Tick += (_, _) =>
        {
            if (!_closed && TextMotionVisible)
            {
                if (_status.Length == 0) UpdateSecondaryText(_media.Current);
                UpdateProgress(_media.Current);
            }
        };
        _lyricTimer.Start();
    }

    private void StopLyricTimer()
    {
        _lyricTimer?.Stop();
        _lyricTimer = null;
    }

    private void SetArtistPlain(string text)
    {
        if (_renderedLyricText is not null)
        {
            LyricTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            LyricTranslation.X = 0;
            LyricViewport.ScrollToHorizontalOffset(0);
            LyricViewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ArtistText.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        _renderedLyricText = null;
        string plain = text ?? "";
        if (ArtistText.Inlines.Count == 1
            && ArtistText.Inlines.FirstInline is Run only
            && only.Text == plain
            && only.Foreground is SolidColorBrush b
            && b.Color == _subFg)
        {
            return;
        }

        ArtistText.Inlines.Clear();
        ArtistText.Inlines.Add(new Run(plain) { Foreground = new SolidColorBrush(_subFg) });
    }

    private void RenderLyricLine(LyricLine line, int sung)
    {
        if (_renderedLyricText == line.Text && _renderedSung == sung) return;
        if (_renderedLyricText != line.Text)
        {
            LyricTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            LyricTranslation.X = 0;
            LyricViewport.ScrollToHorizontalOffset(0);
        }
        LyricViewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        ArtistText.TextTrimming = TextTrimming.None;
        _renderedLyricText = line.Text;
        _renderedSung = sung;
        ArtistText.Inlines.Clear();

        int cut = Math.Clamp(sung, 0, line.Text.Length);
        if (cut > 0)
        {
            ArtistText.Inlines.Add(new Run(line.Text[..cut])
            {
                Foreground = new SolidColorBrush(_accent),
                FontWeight = FontWeights.SemiBold,
            });
        }

        if (cut < line.Text.Length)
        {
            ArtistText.Inlines.Add(new Run(line.Text[cut..]) { Foreground = new SolidColorBrush(_subFg) });
        }
        LyricViewport.UpdateLayout();
    }

    /// <summary>外部（托盘）改了配置后，让窗口重新套用。</summary>
    public void ApplySettingsChanged()
    {
        RefreshPresentation();
        _lyricKey = "";
        StopLyricTimer();
        ApplySnapshot(_media.Current);
    }

    /// <summary>无媒体会话时的引导态（比静默消失有用）。</summary>
    public void ApplyIdleHint()
    {
        _idleHint = true;
        StopMarquee();
        TitleText.Text = "未检测到媒体会话";
        ArtistText.Text = "QQ音乐：设置 → 常规设置 → 通知 → 开启 SMTC";
        PrevBtn.IsEnabled = false;
        NextBtn.IsEnabled = false;
        PlayBtn.IsEnabled = false;
        LikeBtn.Visibility = Visibility.Collapsed;
        SetPlayGlyph(false);
        _progressFill.Width = 0;
        Cover.Background = new SolidColorBrush(_light ? Color.FromRgb(0xD9, 0xD9, 0xD9) : Color.FromRgb(0x3F, 0x3F, 0x3F));
        CoverGlyph.Foreground = new SolidColorBrush(_light ? Color.FromRgb(0x8C, 0x8C, 0x8C) : Color.FromRgb(0xA0, 0xA0, 0xA0));
        CoverGlyph.Visibility = Visibility.Visible;
        Cover.Tag = "placeholder";
    }

    private void UpdateProgress(MediaSnapshot snap)
    {
        if (_seeking || _seekCommitting)
        {
            return;
        }

        double total = ProgressTrack.ActualWidth;
        if (total <= 0 || snap.DurationSeconds <= 0)
        {
            _progressFill.Width = 0;
            return;
        }

        double ratio = Math.Clamp(snap.EstimatedPositionSeconds / snap.DurationSeconds, 0, 1);
        if (Math.Abs(_progressFill.Width - total * ratio) > 0.05) _progressFill.Width = total * ratio;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return "--:--";
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    private void UpdateMarquee()
    {
        if (!TextMotionVisible) { StopMarquee(); _marqueeKey = ""; return; }
        try
        {
            double available = TextStack.ActualWidth;
            string key = $"{TitleText.Text}|{(int)available}|{_idleHint}";
            if (key == _marqueeKey)
            {
                return;
            }

            _marqueeKey = key;
            StopMarquee();

            if (available <= 8 || _idleHint || string.IsNullOrEmpty(TitleText.Text))
            {
                return;
            }

            var typeface = new Typeface(
                TitleText.FontFamily, TitleText.FontStyle, TitleText.FontWeight, TitleText.FontStretch);

            var formatted = new FormattedText(
                TitleText.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                TitleText.FontSize,
                TitleText.Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            if (formatted.Width <= available - 4)
            {
                return;
            }

            TitleText.TextTrimming = TextTrimming.None;

            double overflow = formatted.Width - available + 18;
            var transform = new TranslateTransform();
            TitleText.RenderTransform = transform;

            double travel = Math.Max(0.8, overflow / 26.0);
            var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.0))));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.0 + travel))));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.2 + travel))));

            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, new PropertyPath(TranslateTransform.XProperty));

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin(this, HandoffBehavior.SnapshotAndReplace, true);
            _titleMarquee = storyboard;
        }
        catch (Exception ex)
        {
            Log.Write("走马灯失败: " + ex.Message);
        }
    }

    private void StopMarquee()
    {
        try
        {
            _titleMarquee?.Remove(this);
        }
        catch
        {
            // ignore
        }

        _titleMarquee = null;
        TitleText.RenderTransform = Transform.Identity;
        TitleText.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private bool CanLike(MediaSnapshot snap) => MediaSourceNames.IsQq(snap.SourceAppId)
        || (MediaSourceNames.IsCloudMusic(snap.SourceAppId)
            && !string.IsNullOrWhiteSpace(_settings.FindHotkeyProfile(snap.SourceAppId)?.Like));

    private async void ProbeLikeStateIfNeeded(MediaSnapshot snap)
    {
        if (!_settings.ShowLikeButton || !snap.HasSession || !CanLike(snap))
        {
            return;
        }

        string identity = snap.Identity;
        bool identityChanged = identity != _lastProbeIdentity;
        if (_likeGate.Busy || _likeProbeBusy || (identity == _lastProbeIdentity
            && (DateTime.UtcNow - _lastProbeAt).TotalSeconds < (snap.IsFallback ? 1.5 : 3)))
        {
            return;
        }

        _lastProbeIdentity = identity;
        _lastProbeAt = DateTime.UtcNow;
        _likeProbeBusy = true;
        string appId = snap.SourceAppId;
        var ticket = _likeGate.Capture();
        if (identityChanged) _likeKnown = false;

        // 换了歌就先回到"未收藏"，等探测结果回来再点亮（宁可先空心，也不谎报）
        if (identityChanged)
        {
            _liked = false;
            UpdateHeartBrush();
        }

        var pendingProbe = Task.Run(() => LikeService.Probe(appId, _settings));
        try
        {
            var probe = await pendingProbe.WaitAsync(TimeSpan.FromSeconds(2));
            if (_closed || !_likeGate.IsCurrent(ticket) || _likeGate.Busy) return;
            _likeKnown = probe.Liked.HasValue;
            _likeConfirmed = probe.Confirmed && _likeKnown;
            _liked = probe.Liked ?? false;
            UpdateHeartBrush();
        }
        catch (Exception ex) { Log.Write("喜欢状态探测未完成: " + ex.Message); }
        finally
        {
            // 超时只结束等待，不会终止 UIA。后台调用结束前不启动新探测。
            try { if (!pendingProbe.IsCompleted) await pendingProbe; } catch { }
            _likeProbeBusy = false;
        }
    }

    private async void OnLikeClicked(object sender, RoutedEventArgs e)
    {
        if (!CanLike(_media.Current)) return;
        using var sourceHold = _media.HoldCurrentSource();
        string appId = _media.Current.SourceAppId;
        if (string.IsNullOrWhiteSpace(appId))
        {
            ShowStatus("✗ 还没检测到播放器");
            return;
        }

        _likeGate.Observe(_media.Current.HasSession ? _media.Current.Identity : "");
        if (!_likeGate.TryBegin(out var ticket)) return;
        var elapsed = Stopwatch.StartNew();
        bool desired = !(_likeKnown && _liked);
        UpdateHeartBrush();
        ShowStatus(_media.Current.IsFallback ? "正在发送喜欢指令…"
            : desired ? "正在加入「我喜欢」…" : "正在取消喜欢…");
        Log.Write($"收藏反馈已提交 UI: {elapsed.Elapsed.TotalMilliseconds:F1} ms");
        int expired = 0;
        try
        {
            var result = await Task.Run(() => LikeService.Toggle(appId, _settings,
                () => Volatile.Read(ref expired) == 0 && _likeGate.IsCurrent(ticket), desired))
                .WaitAsync(TimeSpan.FromSeconds(4));
            if (_closed || !_likeGate.IsCurrent(ticket)) return;
            if ((result.Verified || result.Optimistic) && result.LikedAfter.HasValue)
            {
                _likeKnown = true;
                _likeConfirmed = result.Verified;
                _liked = result.LikedAfter.Value;
            }
            else if (result.Ok) _likeKnown = false;
            Log.Write($"收藏完成: {elapsed.Elapsed.TotalMilliseconds:F1} ms verified={result.Verified} liked={result.LikedAfter}");
            ShowStatus((result.Verified ? "✓ " : result.Ok ? "· " : "✗ ") + result.Message);
        }
        catch (TimeoutException)
        {
            Volatile.Write(ref expired, 1);
            if (!_closed && _likeGate.IsCurrent(ticket))
            {
                _likeKnown = false;
                ShowStatus("播放器响应较慢，收藏状态待确认");
            }
        }
        catch (Exception ex)
        {
            Log.Write("收藏处理异常: " + ex.Message);
            if (!_closed && _likeGate.IsCurrent(ticket)) ShowStatus("暂时无法确认收藏，请稍后重试");
        }
        finally
        {
            _likeGate.End(ticket);
            if (!_closed) UpdateHeartBrush();
        }
    }

    private async Task RunTransportAsync(string label, Func<Task<bool>> action)
    {
        if (_transportBusy) return;
        _transportBusy = true;
        ShowStatus("正在" + label + "…");
        try
        {
            bool sent = await action();
            if (!_closed) ShowStatus(sent ? "已发送" + label : "播放器未及时响应，请稍后重试");
        }
        finally { _transportBusy = false; }
    }

    private void ShowStatus(string message)
    {
        _idleHint = false;
        _status = message;
        ArtistText.Text = message;

        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer?.Stop();
            _status = "";
            ApplySnapshot(_media.Current);
        };
        _statusTimer.Start();
    }

    private void ProgressTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_seekCommitting || !_media.Current.CanSeek || _media.Current.DurationSeconds <= 0)
        {
            return;
        }

        _seeking = true;
        _seekSourceHold?.Dispose();
        _seekSourceHold = _media.HoldCurrentSource();
        _seekIdentity = _media.Current.Identity;
        _seekDuration = _media.Current.DurationSeconds;
        // 非激活窗口的 WPF 捕获会不稳定地成功或丢失。仅在本次按下期间读取鼠标位置和释放状态。
        _mouseCaptured = false;
        _seekPointerTimer ??= CreateSeekPointerTimer();
        _seekPointerTimer.Start();
        PreviewSeek(e.GetPosition(ProgressTrack).X);
        e.Handled = true;
    }

    private void ProgressTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (_seeking && e.LeftButton == MouseButtonState.Pressed)
        {
            PreviewSeek(e.GetPosition(ProgressTrack).X);
            e.Handled = true;
            return;
        }

    }

    private DispatcherTimer CreateSeekPointerTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += async (_, _) =>
        {
            if (!_seeking || _closed) { timer.Stop(); return; }
            if (_seekIdentity != _media.Current.Identity || !NativeMethods.GetCursorPos(out var point)) { CancelSeekPreview(); return; }
            double x = ProgressTrack.PointFromScreen(new Point(point.X, point.Y)).X;
            if ((GetAsyncKeyState(1) & 0x8000) != 0) PreviewSeek(x);
            else await CommitSeekAsync(x);
        };
        return timer;
    }

    private void PreviewSeek(double x)
    {
        double total = ProgressTrack.ActualWidth;
        if (total <= 0)
        {
            return;
        }

        double ratio = Math.Clamp(x / total, 0, 1);
        _progressFill.Width = total * ratio;
        double dur = _seekDuration;
        _status = $"{FormatTime(ratio * dur)} / {FormatTime(dur)}";
        ArtistText.Text = _status;
    }

    private async void ProgressTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        e.Handled = true;
        await CommitSeekAsync(e.GetPosition(ProgressTrack).X);
    }

    private async Task CommitSeekAsync(double x)
    {
        if (!_seeking)
        {
            return;
        }

        _seeking = false;
        _seekPointerTimer?.Stop();
        _seekCommitting = true;
        double total = ProgressTrack.ActualWidth;
        string identity = _seekIdentity;
        double duration = _seekDuration;
        if (_mouseCaptured)
        {
            ProgressTrack.ReleaseMouseCapture();
            _mouseCaptured = false;
        }

        try
        {
            if (total <= 0 || identity != _media.Current.Identity) return;
            double target = Math.Clamp(x / total, 0, 1) * duration;
            bool completed = await _media.SeekAsync(target, identity);
            if (_closed || identity != _media.Current.Identity) return;
            if (completed) { _status = ""; _statusTimer?.Stop(); UpdateSecondaryText(_media.Current); }
            else ShowStatus("播放器未完成跳转，请重试");
        }
        finally
        {
            _seekSourceHold?.Dispose();
            _seekSourceHold = null;
            _seekCommitting = false;
            if (!_closed) UpdateProgress(_media.Current);
        }
    }

    private void CancelSeekPreview()
    {
        if (!_seeking) return;
        _seeking = false;
        _seekSourceHold?.Dispose();
        _seekSourceHold = null;
        _seekPointerTimer?.Stop();
        _mouseCaptured = false;
        if (ProgressTrack.IsMouseCaptured) ProgressTrack.ReleaseMouseCapture();
        _status = "";
        if (!_closed) { UpdateSecondaryText(_media.Current); UpdateProgress(_media.Current); }
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _dragWhole = true;
        NativeMethods.GetCursorPos(out var pt);
        _dragLastCursorX = pt.X;
        _dragStartAt = DateTime.Now;
        Root.CaptureMouse();
        e.Handled = true;
    }

    private DateTime _dragStartAt = DateTime.Now;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragWhole)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || NativeMethods.GetCursorPos(out var pt) == false)
        {
            _dragWhole = false;
            Root.ReleaseMouseCapture();
            return;
        }

        int delta = pt.X - _dragLastCursorX;

        // 光标瞬移（例如被程序移动、切换显示器）会被误判成拖拽，
        // 单帧位移超过 300px 直接当成异常丢弃
        if (Math.Abs(delta) > 300)
        {
            _dragLastCursorX = pt.X;
            e.Handled = true;
            return;
        }

        if (delta != 0)
        {
            _dragLastCursorX = pt.X;
            OffsetDeltaRequested?.Invoke(delta / Scale);
        }

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_dragWhole)
        {
            _dragWhole = false;
            Root.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private static bool IsOnInteractive(DependencyObject? source)
    {
        // 注意：歌词用的是 Run（ContentElement，不是 Visual），
        // 直接 VisualTreeHelper.GetParent 会抛 InvalidOperationException 把进程干掉。
        // 这里必须区分 Visual / ContentElement，并且整体兜底。
        try
        {
            int guard = 0;
            while (source is not null && guard++ < 64)
            {
                if (source is Button)
                {
                    return true;
                }

                if (source is FrameworkElement fe && (fe.Name == "ProgressTrack" || fe.Name == "Cover"))
                {
                    return true;
                }

                source = source switch
                {
                    System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                        => VisualTreeHelper.GetParent(source),
                    ContentElement ce
                        => ContentOperations.GetParent(ce) ?? (ce as FrameworkContentElement)?.Parent,
                    _ => LogicalTreeHelper.GetParent(source),
                };
            }
        }
        catch (Exception ex)
        {
            Log.Write("IsOnInteractive 异常(已忽略): " + ex.Message);
        }

        return false;
    }

    private void BuildMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem { Header = _isTopPresentation ? "栖音 · 顶部播控" : "任务栏播控", IsEnabled = false });
        var placement = new MenuItem { Header = "播控位置" };
        var taskbarPlacement = new MenuItem { Header = "任务栏", IsCheckable = true, IsChecked = !_isTopPresentation };
        var topPlacement = new MenuItem { Header = "屏幕顶部", IsCheckable = true, IsChecked = _isTopPresentation };
        taskbarPlacement.Click += (_, _) => PlacementRequested?.Invoke("taskbar");
        topPlacement.Click += (_, _) => PlacementRequested?.Invoke("top");
        placement.Items.Add(taskbarPlacement); placement.Items.Add(topPlacement);
        placement.SubmenuOpened += (_, _) =>
        {
            taskbarPlacement.IsChecked = !_isTopPresentation;
            topPlacement.IsChecked = _isTopPresentation;
        };
        menu.Items.Add(placement);
        menu.Items.Add(new Separator());
        var sources = new MenuItem { Header = "播放来源" };
        void FillSources()
        {
            sources.Items.Clear();
            var automatic = new MenuItem { Header = "自动切换（暂停保留当前来源）", IsCheckable = true, IsChecked = !_media.SourceLocked };
            automatic.Click += (_, _) => _media.SelectSource(null);
            sources.Items.Add(automatic);
            sources.Items.Add(new Separator());
            foreach (var option in _media.Sources)
            {
                var item = new MenuItem { Header = option.Label + (option.Playing ? " · 播放中" : "")
                    + (option.Selected && _media.SourceLocked ? " · 已锁定" : ""),
                    IsCheckable = true, IsChecked = option.Selected };
                item.Click += (_, _) => _media.SelectSource(option.Key);
                sources.Items.Add(item);
            }
            if (_media.Sources.Count == 0) sources.Items.Add(new MenuItem { Header = "暂无可用来源", IsEnabled = false });
        }
        FillSources();
        sources.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, sources)) FillSources(); };
        menu.Items.Add(sources);
        menu.Items.Add(new Separator());

        var miLeft = new MenuItem { Header = "位置：任务栏左端", IsCheckable = true, IsEnabled = !_isTopPresentation };
        var miTray = new MenuItem { Header = "位置：通知区左侧", IsCheckable = true, IsEnabled = !_isTopPresentation };
        var miCenter = new MenuItem { Header = "位置：居中", IsCheckable = true, IsEnabled = !_isTopPresentation };

        void Sync()
        {
            miLeft.IsChecked = _settings.Anchor == "left";
            miTray.IsChecked = _settings.Anchor == "beforeTray";
            miCenter.IsChecked = _settings.Anchor == "center";
        }

        void SetAnchor(string anchor)
        {
            _settings.Anchor = anchor;
            _settings.Offset = 0;
            AnchorChanged?.Invoke(anchor);
            Sync();
        }

        miLeft.Click += (_, _) => SetAnchor("left");
        miTray.Click += (_, _) => SetAnchor("beforeTray");
        miCenter.Click += (_, _) => SetAnchor("center");
        Sync();

        menu.Items.Add(miLeft);
        menu.Items.Add(miTray);
        menu.Items.Add(miCenter);

        var miReset = new MenuItem { Header = "重新吸附 / 归位" };
        miReset.Click += (_, _) =>
        {
            if (_isTopPresentation) { _settings.TopOffsetX = 0; _settings.TopOffsetY = 8; }
            else _settings.Offset = 0;
            _settings.Save();
            RepositionRequested?.Invoke();
        };
        menu.Items.Add(miReset);

        menu.Items.Add(new Separator());

        var miHide = new MenuItem { Header = "无播放时自动隐藏", IsCheckable = true, IsChecked = _settings.HideWhenNoMedia };
        miHide.Click += (_, _) =>
        {
            _settings.HideWhenNoMedia = miHide.IsChecked;
            ShowVisibleToggled?.Invoke(miHide.IsChecked);
        };
        menu.Items.Add(miHide);

        var miLike = new MenuItem { Header = "显示爱心按钮", IsCheckable = true, IsChecked = _settings.ShowLikeButton };
        miLike.Click += (_, _) =>
        {
            _settings.ShowLikeButton = miLike.IsChecked;
            _settings.Save();
            ApplySnapshot(_media.Current);
        };
        menu.Items.Add(miLike);

        var miCover = new MenuItem { Header = "显示封面", IsCheckable = true, IsChecked = _settings.ShowCover };
        miCover.Click += (_, _) =>
        {
            _settings.ShowCover = miCover.IsChecked;
            _settings.Save();
            Cover.Tag = null;
            ApplySnapshot(_media.Current);
        };
        menu.Items.Add(miCover);

        var miAvoid = new MenuItem { Header = "避开任务栏上的其他组件", IsCheckable = true, IsChecked = _settings.AvoidOthers, IsEnabled = !_isTopPresentation };
        miAvoid.Click += (_, _) =>
        {
            _settings.AvoidOthers = miAvoid.IsChecked;
            _settings.Save();
        };
        menu.Items.Add(miAvoid);

        var miLyrics = new MenuItem { Header = "显示歌词（逐字）", IsCheckable = true, IsChecked = _settings.ShowLyrics };
        miLyrics.Click += (_, _) =>
        {
            _settings.ShowLyrics = miLyrics.IsChecked;
            _settings.Save();
            _lyricKey = "";
            StopLyricTimer();
            ApplySnapshot(_media.Current);
        };
        menu.Items.Add(miLyrics);

        var miGlass = new MenuItem { Header = "毛玻璃质感", IsCheckable = true, IsChecked = _settings.Glass };
        miGlass.Click += (_, _) =>
        {
            _settings.Glass = miGlass.IsChecked;
            _settings.Save();
            ApplyTheme();
        };
        menu.Items.Add(miGlass);

        string monitor = _isTopPresentation ? _settings.TopMonitor : _settings.Monitor;
        var miMonPrimary = new MenuItem { Header = _isTopPresentation ? "显示在：主屏顶部" : "显示在：主屏任务栏", IsCheckable = true, IsChecked = monitor != "cursor" };
        var miMonCursor = new MenuItem { Header = "显示在：鼠标所在屏", IsCheckable = true, IsChecked = monitor == "cursor" };
        void SetMonitor(string mode)
        {
            if (_isTopPresentation) { _settings.TopMonitor = mode; _settings.TopOffsetX = 0; }
            else { _settings.Monitor = mode; _settings.Offset = 0; }
            _settings.Save();
            miMonPrimary.IsChecked = mode != "cursor";
            miMonCursor.IsChecked = mode == "cursor";
            RepositionRequested?.Invoke();
        }

        miMonPrimary.Click += (_, _) => SetMonitor("primary");
        miMonCursor.Click += (_, _) => SetMonitor("cursor");
        menu.Items.Add(miMonPrimary);
        menu.Items.Add(miMonCursor);

        var miAuto = new MenuItem { Header = "开机自动启动", IsCheckable = true, IsChecked = AutoStart.IsEnabled() };
        miAuto.Click += (_, _) =>
        {
            AutoStart.Set(miAuto.IsChecked);
            miAuto.IsChecked = AutoStart.IsEnabled();
            ShowStatus(AutoStart.IsEnabled() ? "✓ 已设置开机自启" : "已取消开机自启");
        };
        menu.Items.Add(miAuto);

        menu.Items.Add(new Separator());

        var miLog = new MenuItem { Header = "打开日志文件夹" };
        miLog.Click += (_, _) => OpenFolder(Log.Dir);
        menu.Items.Add(miLog);

        var miConfig = new MenuItem { Header = "打开配置文件" };
        miConfig.Click += (_, _) =>
        {
            _settings.Save();
            OpenPath(AppSettings.FilePath);
        };
        menu.Items.Add(miConfig);

        var miExit = new MenuItem { Header = "退出" };
        miExit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(miExit);

        _menu = menu;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private void ShowMenu()
    {
        if (_menu is null)
        {
            return;
        }

        _menu.PlacementTarget = this;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }
}
