using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using TaskbarMediaBar.Interop;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarMediaBar.Services;

/// <summary>当前媒体状态快照。</summary>
public sealed class MediaSnapshot
{
    public bool HasSession { get; set; }
    public string SourceAppId { get; set; } = "";
    public string SessionKey { get; set; } = "";

    /// <summary>true = 来自"窗口标题兜底通道"（播放器不上报 SMTC，只能读歌名/艺人）。</summary>
    public bool IsFallback { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public bool IsPlaying { get; set; }
    public bool PlaybackStateKnown { get; set; } = true;
    public bool CanPlayPause { get; set; }
    public bool CanNext { get; set; }
    public bool CanPrev { get; set; }
    public bool CanSeek { get; set; }
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public double TimelineStartSeconds { get; set; }
    public double PlaybackRate { get; set; } = 1;
    public long PositionSampleTimestamp { get; set; }
    public double EstimatedPositionSeconds => PlaybackTimeline.Project(PositionSeconds, DurationSeconds,
        IsPlaying && PlaybackStateKnown, PlaybackRate, PlaybackTimeline.Since(PositionSampleTimestamp));
    public BitmapSource? Cover { get; set; }

    public string Identity => (SessionKey.Length > 0 ? SessionKey + "|" : "") + $"{SourceAppId}|{Title}|{Artist}";
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "未知曲目" : Title;
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? (string.IsNullOrWhiteSpace(Album) ? "—" : Album) : Artist;
}

/// <summary>
/// GSMTC(系统媒体传输控件) 读写层。
/// 只依赖 Windows 官方媒体会话接口：不读播放器内存、不注入进程。
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly MediaSourceSelector _selector = new();
    private readonly Dictionary<IntPtr, string> _sessionKeys = new();
    private GlobalSystemMediaTransportControlsSession[] _knownSessions = Array.Empty<GlobalSystemMediaTransportControlsSession>();
    private volatile bool _sessionsDirty = true;
    private static IntPtr SessionIdentity(GlobalSystemMediaTransportControlsSession session)
    {
        // 每次 GetSessions 可产生新的托管包装；用 COM 的规范 IUnknown 身份区分会话。
        var pointer = ((WinRT.IWinRTObject)session).NativeObject.ThisPtr;
        var iid = new Guid("00000000-0000-0000-C000-000000000046");
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(System.Runtime.InteropServices.Marshal.QueryInterface(pointer, ref iid, out var identity));
        System.Runtime.InteropServices.Marshal.Release(identity);
        return identity;
    }
    private List<MediaSourceOption> _sources = new();
    private int _nextSessionKey, _selectionRevision;
    private string? _selectedFallbackApp;
    private string? _heldSource;
    private int _holdCount;
    public bool SourceLocked => _selector.LockedKey is not null;
    public IReadOnlyList<MediaSourceOption> Sources => _sources;
    public void SelectSource(string? key)
    {
        if (key is null) _selector.Automatic();
        else if (_sources.Any(s => s.Key == key)) _selector.Select(key);
        else return;
        _selectionRevision++;
        _ = RefreshAsync();
    }

    public IDisposable HoldCurrentSource()
    {
        if (_holdCount++ == 0) _heldSource = Current.SessionKey;
        return new SourceHold(this);
    }
    private sealed class SourceHold(MediaSessionService owner) : IDisposable
    {
        private bool _released;
        public void Dispose()
        {
            if (_released) return;
            _released = true;
            if (--owner._holdCount == 0) owner._heldSource = null;
        }
    }
    private readonly string[] _preferredApps;
    private readonly string[] _fallbackApps;

    /// <summary>按进程名查全局快捷键档案（播放器不上报 SMTC 时的控制通道）。</summary>
    private readonly Func<string, HotkeyProfile?> _hotkeyResolver;

    private string _coverKey = "";
    private BitmapSource? _coverCache;
    private GlobalSystemMediaTransportControlsSessionMediaProperties? _cachedProperties;
    private GlobalSystemMediaTransportControlsSession? _propertiesSession;
    private long _propertiesReadAt;
    private int _propertiesRevision;
    private int _cachedPropertiesRevision = -1;
    private int _refreshQueued;
    private readonly Dictionary<string, long> _missingFallbackAt = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _coverRetryAt;
    private int _coverFailures;
    private bool _busy;
    private bool _refreshPending;
    private readonly SynchronizationContext? _ownerContext = SynchronizationContext.Current;
    private bool _disposed;
    private readonly TrackOperationGate _seekGate = new();

    public MediaSnapshot Current { get; } = new();

    public string? LastError { get; private set; }

    public MediaSessionService(
        IEnumerable<string> preferredApps,
        IEnumerable<string>? fallbackApps = null,
        Func<string, HotkeyProfile?>? hotkeyResolver = null,
        LyricsService? lyrics = null)
    {
        _hotkeyResolver = hotkeyResolver ?? (_ => null);
        _lyrics = lyrics ?? new LyricsService();

        _preferredApps = preferredApps
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToArray();

        _fallbackApps = (fallbackApps ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToArray();
    }

    public event Action? Updated;

    public async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
        }
        catch (Exception ex)
        {
            LastError = "初始化媒体会话管理器失败: " + ex.Message;
            Log.Write(LastError);
        }
    }

    private void QueueRefresh()
    {
        if (_disposed) return;
        if (_ownerContext is not null && Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            _ownerContext.Post(async _ =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                if (!_disposed) await RefreshAfterCommandAsync();
            }, null);
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => QueueRefresh();
    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        _sessionsDirty = true;
        QueueRefresh();
    }
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => QueueRefresh();
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        Interlocked.Increment(ref _propertiesRevision);
        QueueRefresh();
    }
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => QueueRefresh();

    private void SetSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _session) || (session is not null && _session is not null && SessionIdentity(session) == SessionIdentity(_session))) return;
        if (_session is not null)
        {
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        _session = session;
        if (session is not null)
        {
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            return s.GetPlaybackInfo().PlaybackStatus
                   == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private bool IsPreferred(GlobalSystemMediaTransportControlsSession s)
    {
        var id = s.SourceAppUserModelId ?? "";
        foreach (var pref in _preferredApps)
        {
            if (id.Contains(pref, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null)
        {
            return null;
        }

        try
        {
            if (_sessionsDirty)
            {
                _sessionsDirty = false;
                GlobalSystemMediaTransportControlsSession[] fresh;
                try { fresh = _manager.GetSessions().ToArray(); }
                catch { _sessionsDirty = true; throw; }
                var nextKeys = new Dictionary<IntPtr, string>();
                foreach (var s in fresh)
                {
                    IntPtr id = SessionIdentity(s);
                    if (!_sessionKeys.TryGetValue(id, out var key))
                    {
                        var sameApp = _knownSessions.Where(old => old.SourceAppUserModelId == s.SourceAppUserModelId).ToArray();
                        // 系统可能连原生对象也重新创建。单一应用来源可安全续接；
                        // 同应用多个会话无法明确对应时分配新键、解除旧锁，绝不猜测目标。
                        if (sameApp.Length == 1 && fresh.Count(f => f.SourceAppUserModelId == s.SourceAppUserModelId) == 1)
                            key = _sessionKeys.GetValueOrDefault(SessionIdentity(sameApp[0]));
                        key ??= "session:" + ++_nextSessionKey;
                    }
                    nextKeys[id] = key;
                }
                _sessionKeys.Clear();
                foreach (var entry in nextKeys) _sessionKeys.Add(entry.Key, entry.Value);
                _knownSessions = fresh;
            }
            var sessions = _knownSessions;
            var ids = sessions.ToDictionary(s => s, SessionIdentity);
            foreach (var old in _sessionKeys.Keys.Where(id => !ids.Values.Contains(id)).ToArray()) _sessionKeys.Remove(old);
            foreach (var s in sessions)
                if (!_sessionKeys.ContainsKey(ids[s])) _sessionKeys[ids[s]] = "session:" + ++_nextSessionKey;
            var states = sessions.Select(s => new MediaSourceState(_sessionKeys[ids[s]], IsPlaying(s), IsPreferred(s))).ToList();
            var choices = sessions.Select(s => new MediaSourceOption(_sessionKeys[ids[s]], MediaSourceNames.Display(s.SourceAppUserModelId), IsPlaying(s), false)).ToList();
            var fallback = new Dictionary<string, string>();
            foreach (var app in _fallbackApps)
            {
                string proc = app.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
                if (sessions.Any(s => s.SourceAppUserModelId.Contains(proc, StringComparison.OrdinalIgnoreCase))) continue;
                if (_missingFallbackAt.TryGetValue(proc, out long missedAt)
                    && Stopwatch.GetElapsedTime(missedAt).TotalSeconds < 3) continue;
                var h = NativeMethods.FindMainWindow(proc);
                if (h == IntPtr.Zero) { _missingFallbackAt[proc] = Stopwatch.GetTimestamp(); continue; }
                _missingFallbackAt.Remove(proc);
                if (h == IntPtr.Zero || !TryParseSongTitle(NativeMethods.ReadWindowTitle(h), out var title, out _)) continue;
                string key = "fallback:" + proc;
                if (!fallback.TryAdd(key, proc)) continue;
                states.Add(new(key, false, false));
                choices.Add(new(key, MediaSourceNames.Display(proc) + " · " + title, false, false));
            }
            string? selected = _selector.Update(states, _heldSource);
            _selectedFallbackApp = selected is not null && fallback.TryGetValue(selected, out var fa) ? fa : null;
            _sources = choices.Select(s => s with { Selected = s.Key == selected,
                Label = choices.Count(c => c.Label == s.Label) > 1 ? s.Label + "（" + s.Key.Replace("session:", "来源 ") + "）" : s.Label }).ToList();
            return sessions.FirstOrDefault(s => _sessionKeys[ids[s]] == selected);
        }
        catch (Exception ex)
        {
            LastError = "枚举媒体会话失败: " + ex.Message;
        }

        // 暂时枚举失败时保留现有目标，不能因此切到另一个应用。
        return _session;
    }

    // ---------- 窗口标题兜底通道 ----------
    //
    // 网易云音乐 Win32 原版实测（3.1.37）：
    //   · 不上报 SMTC（会话数 0）
    //   · 没有 UIA 树（主窗口只暴露 1 个元素）
    //   · 不响应 WM_APPCOMMAND（直接投递 / 广播 / 媒体键注入 三种都试过，全部无效）
    // 唯一可读的是主窗口标题（"歌名 - 歌手"），而且最小化到托盘时标题依然有效。
    // 所以这里只能做"只读显示"：歌名、艺人、（联网补）封面；控制类一律置灰。

    private string _fallbackKey = "";
    private System.Windows.Media.Imaging.BitmapSource? _fallbackCover;
    private bool _fallbackCoverBusy;
    private int _fallbackCoverRevision, _fallbackCoverFailures;
    private DateTimeOffset _fallbackCoverRetryAt;
    private CancellationTokenSource? _fallbackCoverCancellation;

    private void TryBuildFallback(MediaSnapshot snap, List<string> apps)
    {
        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app))
            {
                continue;
            }

            string proc = app.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? app.Trim()[..^4]
                : app.Trim();

            IntPtr hwnd = NativeMethods.FindMainWindow(proc);
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            string title = NativeMethods.ReadWindowTitle(hwnd);
            if (!TryParseSongTitle(title, out string song, out string artist))
            {
                continue;
            }

            snap.HasSession = true;
            snap.IsFallback = true;
            snap.SourceAppId = proc + ".exe";
            snap.Title = song;
            snap.Artist = artist;
            snap.IsPlaying = false;
            snap.PlaybackStateKnown = false;

            // 没有 SMTC 就没有时间轴/seek；但播放器若配了全局快捷键，
            // 播放暂停/上下曲/收藏是能用的，所以按钮要亮起来
            var profile = _hotkeyResolver(proc);
            snap.CanPlayPause = !string.IsNullOrWhiteSpace(profile?.PlayPause);
            snap.CanNext = !string.IsNullOrWhiteSpace(profile?.Next);
            snap.CanPrev = !string.IsNullOrWhiteSpace(profile?.Prev);
            snap.CanSeek = false;
            snap.DurationSeconds = 0;
            snap.PositionSeconds = 0;
            snap.Cover = GetFallbackCover(song, artist);
            if (_fallbackLogged != snap.SourceAppId + song)
            {
                _fallbackLogged = snap.SourceAppId + song;
                Log.Write($"兜底通道: {proc} → 『{song} - {artist}』");
            }

            return;
        }
    }

    private string _fallbackLogged = "";

    /// <summary>把「歌名 - 歌手」拆开；拆不出来（例如标题是应用名）就当没在播放。</summary>
    private static bool TryParseSongTitle(string title, out string song, out string artist)
    {
        song = "";
        artist = "";

        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        string t = title.Trim().TrimEnd('-', ' ', '　');

        // 标题只有应用名 → 空转
        foreach (var idle in new[] { "网易云音乐", "CloudMusic", "QQ音乐", "汽水音乐", "Spotify", "桌面歌词", "迷你播放器" })
        {
            if (t.Equals(idle, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // 排除内部/桥接窗口的标题：
        // InfLink-rs 会创建名为「MediaPlayer SMTC window - {GUID}」的隐藏窗口，
        // 它长得像「歌名 - 歌手」，但根本不是歌。
        if (t.Contains("SMTC", StringComparison.OrdinalIgnoreCase)
            || t.Contains("MediaPlayer", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(t, @"\{[0-9A-Fa-f\-]{30,}\}"))
        {
            return false;
        }

        int idx = t.LastIndexOf(" - ", StringComparison.Ordinal);
        if (idx <= 0 || idx >= t.Length - 3)
        {
            return false;
        }

        song = t[..idx].Trim();
        artist = t[(idx + 3)..].Trim();
        return song.Length > 0 && artist.Length > 0;
    }

    private System.Windows.Media.Imaging.BitmapSource? GetFallbackCover(string song, string artist)
    {
        if (_disposed) return null;
        string key = $"{song}|{artist}";
        if (key != _fallbackKey)
        {
            _fallbackCoverCancellation?.Cancel();
            _fallbackCoverRevision++;
            _fallbackKey = key;
            _fallbackCover = null;
            _fallbackCoverFailures = 0;
            _fallbackCoverRetryAt = default;
        }
        if (_fallbackCover is not null || _fallbackCoverBusy || DateTimeOffset.UtcNow < _fallbackCoverRetryAt)
            return _fallbackCover;

        // 所有曲目共用一个请求槽；切歌取消旧请求，完成后才启动新歌。
        _fallbackCoverBusy = true;
        var cancellation = new CancellationTokenSource();
        _fallbackCoverCancellation = cancellation;
        _ = FetchFallbackCoverAsync(key, song, artist, _fallbackCoverRevision, cancellation);
        return _fallbackCover;
    }

    private async Task FetchFallbackCoverAsync(string key, string song, string artist, int revision,
        CancellationTokenSource cancellation)
    {
        try
        {
            var cover = await _lyrics.FetchCoverAsync(song, artist, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || revision != _fallbackCoverRevision || key != _fallbackKey) return;
            _fallbackCover = cover;
            if (cover is null) ScheduleFallbackCoverRetry();
            else { _fallbackCoverFailures = 0; _fallbackCoverRetryAt = default; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && !cancellation.IsCancellationRequested && revision == _fallbackCoverRevision && key == _fallbackKey)
            {
                ScheduleFallbackCoverRetry();
                Log.Write("兜底封面获取失败: " + ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_fallbackCoverCancellation, cancellation)) _fallbackCoverCancellation = null;
            cancellation.Dispose();
            _fallbackCoverBusy = false;
            QueueRefresh();
        }
    }

    private void ScheduleFallbackCoverRetry()
    {
        _fallbackCoverFailures++;
        _fallbackCoverRetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30,
            2 * Math.Pow(2, Math.Min(_fallbackCoverFailures - 1, 4))));
    }

    private readonly LyricsService _lyrics;

    /// <summary>拉取一次最新状态；状态有变化时触发 Updated。</summary>
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        if (_busy)
        {
            _refreshPending = true;
            return;
        }

        _busy = true;
        try
        {
            if (_manager is null)
            {
                return;
            }

            int selectionRevision = _selectionRevision;
            var session = PickSession();

            var snap = new MediaSnapshot();
            IRandomAccessStreamReference? thumbRef = null;

            if (session is not null)
            {
                snap.HasSession = true;
                snap.SourceAppId = session.SourceAppUserModelId ?? "";
                snap.SessionKey = _sessionKeys.GetValueOrDefault(SessionIdentity(session), "");

                try
                {
                    int revision = Volatile.Read(ref _propertiesRevision);
                    var props = _cachedProperties;
                    // Metadata includes native thumbnail resources. Reuse it between changes;
                    // the slow refresh remains a fallback for players that miss notifications.
                    if (!ReferenceEquals(session, _propertiesSession) || props is null
                        || revision != _cachedPropertiesRevision
                        || Stopwatch.GetElapsedTime(_propertiesReadAt).TotalSeconds >= 5)
                    {
                        props = await session.TryGetMediaPropertiesAsync();
                        _cachedProperties = props;
                        _propertiesSession = session;
                        _cachedPropertiesRevision = revision;
                        _propertiesReadAt = Stopwatch.GetTimestamp();
                    }
                    if (props is not null)
                    {
                        snap.Title = props.Title ?? "";
                        snap.Artist = props.Artist ?? "";
                        snap.Album = props.AlbumTitle ?? "";
                        thumbRef = props.Thumbnail;
                    }
                }
                catch (Exception ex)
                {
                    LastError = "读取曲目信息失败: " + ex.Message;
                }

                try
                {
                    var info = session.GetPlaybackInfo();
                    snap.IsPlaying = info.PlaybackStatus
                                    == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    var ctrl = info.Controls;
                    snap.CanPlayPause = ctrl?.IsPauseEnabled == true || ctrl?.IsPlayEnabled == true;
                    snap.CanNext = ctrl?.IsNextEnabled == true;
                    snap.CanPrev = ctrl?.IsPreviousEnabled == true;
                    snap.CanSeek = ctrl?.IsPlaybackPositionEnabled == true;
                    snap.PlaybackRate = info.PlaybackRate ?? 1;
                }
                catch
                {
                    snap.PlaybackStateKnown = false;
                }

                try
                {
                    var tl = session.GetTimelineProperties();
                    snap.TimelineStartSeconds = tl.StartTime.TotalSeconds;
                    snap.DurationSeconds = Math.Max(0, (tl.EndTime - tl.StartTime).TotalSeconds);
                    double since = tl.LastUpdatedTime > DateTimeOffset.UnixEpoch
                        ? Math.Max(0, (DateTimeOffset.UtcNow - tl.LastUpdatedTime).TotalSeconds) : 0;
                    snap.PositionSeconds = PlaybackTimeline.Project((tl.Position - tl.StartTime).TotalSeconds,
                        snap.DurationSeconds, snap.IsPlaying, snap.PlaybackRate, since);
                    snap.PositionSampleTimestamp = Stopwatch.GetTimestamp();
                    if (snap.DurationSeconds > 0 && QqProgressSeek.Supports(snap.SourceAppId)) snap.CanSeek = true;
                }
                catch
                {
                    snap.DurationSeconds = 0;
                    snap.PositionSeconds = 0;
                }
            }

            string identity = snap.Identity;

            // SMTC 没会话 → 走"窗口标题"兜底通道（网易云这类不上报的播放器）
            if (!snap.HasSession && _fallbackApps.Length > 0)
            {
                TryBuildFallback(snap, _selectedFallbackApp is { } fallbackApp ? new List<string> { fallbackApp } : new List<string>());
                if (snap.HasSession) snap.SessionKey = _selector.CurrentKey ?? "";
                identity = snap.Identity;
            }

            bool identityChanged = identity != _lastIdentity;
            if (identityChanged)
            {
                _lastIdentity = identity;
                _coverCache = null;
                _coverKey = "";
                _coverRetryAt = default;
                _coverFailures = 0;
            }

            if (snap.HasSession && !snap.IsFallback)
            {
                _coverCache = await LoadCoverAsync(thumbRef, identity);
                snap.Cover = _coverCache;
                if (snap.Cover is null && MediaSourceNames.IsMusic(snap.SourceAppId))
                    snap.Cover = GetFallbackCover(snap.Title, snap.Artist);
            }
            else if (snap.IsFallback)
            {
                // 兜底通道的封面由 TryBuildFallback 直接给，别被 SMTC 分支覆盖掉
                _coverCache = snap.Cover;
            }

            bool changed = identityChanged
                           || snap.HasSession != Current.HasSession
                           || snap.IsFallback != Current.IsFallback
                           || snap.IsPlaying != Current.IsPlaying
                           || snap.PlaybackStateKnown != Current.PlaybackStateKnown
                           || Math.Abs(snap.PositionSeconds - Current.PositionSeconds) > 0.4
                           || Math.Abs(snap.DurationSeconds - Current.DurationSeconds) > 0.4
                           || snap.CanNext != Current.CanNext
                           || snap.CanPrev != Current.CanPrev
                           || snap.CanPlayPause != Current.CanPlayPause
                           || snap.CanSeek != Current.CanSeek
                           || !ReferenceEquals(snap.Cover, Current.Cover);

            // 来源选择发生在异步读取期间时，丢弃旧读取，避免旧来源覆盖用户选择。
            if (selectionRevision != _selectionRevision) { _refreshPending = true; return; }
            SetSession(session);
            Copy(snap, Current);
            _seekGate.Observe(snap.HasSession ? snap.Identity : "");

            if (changed)
            {
                Updated?.Invoke();
            }
        }
        finally
        {
            _busy = false;
            if (_refreshPending)
            {
                _refreshPending = false;
                await RefreshAsync();
            }
        }
    }

    private string _lastIdentity = "";

    private static void Copy(MediaSnapshot from, MediaSnapshot to)
    {
        to.HasSession = from.HasSession;
        to.IsFallback = from.IsFallback;
        to.SourceAppId = from.SourceAppId;
        to.SessionKey = from.SessionKey;
        to.Title = from.Title;
        to.Artist = from.Artist;
        to.Album = from.Album;
        to.IsPlaying = from.IsPlaying;
        to.PlaybackStateKnown = from.PlaybackStateKnown;
        to.CanPlayPause = from.CanPlayPause;
        to.CanNext = from.CanNext;
        to.CanPrev = from.CanPrev;
        to.CanSeek = from.CanSeek;
        to.PositionSeconds = from.PositionSeconds;
        to.DurationSeconds = from.DurationSeconds;
        to.TimelineStartSeconds = from.TimelineStartSeconds;
        to.PlaybackRate = from.PlaybackRate;
        to.PositionSampleTimestamp = from.PositionSampleTimestamp;
        to.Cover = from.Cover;
    }

    private async Task<BitmapSource?> LoadCoverAsync(IRandomAccessStreamReference? thumb, string key)
    {
        if (key == _coverKey && _coverCache is not null)
        {
            return _coverCache;
        }
        if (DateTimeOffset.UtcNow < _coverRetryAt) return null;
        if (thumb is null) return null; // A later media update may supply the missing thumbnail.

        try
        {
            using var ras = await thumb.OpenReadAsync();
            uint size = (uint)ras.Size;
            if (size == 0 || size > 16 * 1024 * 1024)
            {
                throw new InvalidOperationException($"封面数据长度无效: {size}");
            }

            using var reader = new DataReader(ras.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);

            var bmp = CoverImage.Decode(bytes);

            _coverKey = key;
            _coverCache = bmp;
            _coverRetryAt = default;
            _coverFailures = 0;
            return bmp;
        }
        catch (Exception ex)
        {
            LastError = "封面解码失败: " + ex.Message;
            _coverFailures++;
            _coverRetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, 2 * Math.Pow(2, Math.Min(_coverFailures - 1, 4))));
            if (_coverFailures == 1) Log.Write(LastError + "；将延迟重试");
            return null;
        }
    }

    /// <summary>
    /// 兜底源没有 SMTC 会话，用播放器的全局快捷键当控制通道（网易云走这条）。
    /// </summary>
    private bool TryHotkey(Func<HotkeyProfile, string> pick)
    {
        if (!Current.IsFallback)
        {
            return false;
        }

        var profile = _hotkeyResolver(Current.SourceAppId);
        if (profile is null)
        {
            return false;
        }

        string combo = pick(profile) ?? "";
        if (string.IsNullOrWhiteSpace(combo))
        {
            return false;
        }

        if (HotkeySender.Send(combo, out string err))
        {
            return true;
        }

        Log.Write($"快捷键发送失败({combo}): {err}");
        return false;
    }

    public Task<bool> TogglePlayPauseAsync() => SendTransportAsync(
        p => p.PlayPause, s => s.TryTogglePlayPauseAsync().AsTask());

    public Task<bool> NextAsync() => SendTransportAsync(
        p => p.Next, s => s.TrySkipNextAsync().AsTask());

    public Task<bool> PreviousAsync() => SendTransportAsync(
        p => p.Prev, s => s.TrySkipPreviousAsync().AsTask());

    private async Task<bool> SendTransportAsync(Func<HotkeyProfile, string> pick,
        Func<GlobalSystemMediaTransportControlsSession, Task<bool>> send)
    {
        // 在 UI 线程读取并绑定目标；键盘发送中的等待全部移到后台。
        using var sourceHold = HoldCurrentSource();
        bool fallback = Current.IsFallback;
        string appId = Current.SourceAppId;
        var session = _session;
        string combo = fallback && _hotkeyResolver(appId) is { } profile ? pick(profile) : "";
        IntPtr target = fallback ? NativeMethods.FindMainWindow(appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? appId[..^4] : appId) : IntPtr.Zero;
        string targetTitle = fallback ? NativeMethods.ReadWindowTitle(target) : "";
        try
        {
            bool ok;
            if (fallback)
                ok = !string.IsNullOrWhiteSpace(combo)
                     && await Task.Run(() => HotkeySender.Send(combo, out _,
                         () => !_disposed && NativeMethods.IsWindow(target) && NativeMethods.ReadWindowTitle(target) == targetTitle));
            else
                ok = session is not null && await send(session).WaitAsync(TimeSpan.FromSeconds(2));

            // 立即发起状态刷新，不等下一次一秒轮询；刷新忙时合并为下一次读取。
            if (ok)
            {
                Interlocked.Increment(ref _propertiesRevision);
                _ = RefreshAfterCommandAsync(fallback);
            }
            return ok;
        }
        catch (Exception ex)
        {
            Log.Write("媒体指令失败: " + ex.Message);
            return false;
        }
    }

    private async Task RefreshAfterCommandAsync(bool followUp = false)
    {
        try
        {
            await RefreshAsync();
            if (followUp)
            {
                await Task.Delay(120);
                await RefreshAsync();
            }
        }
        catch (Exception ex) { Log.Write("指令后状态刷新失败: " + ex.Message); }
    }

    public async Task<bool> SeekAsync(double seconds, string? expectedIdentity = null)
    {
        var session = _session;
        if (!Current.HasSession || !Current.CanSeek || session is null || !double.IsFinite(seconds)
            || Current.DurationSeconds <= 0 || (expectedIdentity is not null && expectedIdentity != Current.Identity)) return false;
        using var sourceHold = HoldCurrentSource();
        _seekGate.Observe(Current.Identity);
        if (!_seekGate.TryBegin(out var ticket)) return false;
        string title = Current.Title, artist = Current.Artist, appId = Current.SourceAppId;
        double duration = Current.DurationSeconds, start = Current.TimelineStartSeconds;
        double target = Math.Clamp(seconds, 0, duration);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        QqProgressSeek.WindowState? qqWindow = null;
        bool CurrentTrack() => !_disposed && !deadline.IsCancellationRequested && _seekGate.IsCurrent(ticket);
        try
        {
            var props = await session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(deadline.Token);
            if (!CurrentTrack() || props.Title != title || props.Artist != artist) return false;
            bool sent;
            if (QqProgressSeek.Supports(appId))
            {
                // 准备完成后继续保持布局，等真实时间轴确认再恢复最小化。
                var preparation = Task.Run(() => QqProgressSeek.Send(target, duration, CurrentTrack, deadline.Token));
                try { qqWindow = await preparation.WaitAsync(deadline.Token); }
                catch { _ = preparation.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose(); }, TaskScheduler.Default); throw; }
                sent = qqWindow is not null;
            }
            else sent = await session.TryChangePlaybackPositionAsync((long)((target + start) * 10_000_000d)).AsTask().WaitAsync(deadline.Token);
            if (!sent || !CurrentTrack()) return false;
            var watch = Stopwatch.StartNew();
            while (CurrentTrack() && watch.ElapsedMilliseconds < 1500)
            {
                var timeline = session.GetTimelineProperties();
                double actual = timeline.Position.TotalSeconds - start;
                if (Math.Abs(actual - target) <= 1.6 + (Current.IsPlaying ? watch.Elapsed.TotalSeconds * Current.PlaybackRate : 0))
                {
                    var latest = await session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(deadline.Token);
                    if (!CurrentTrack() || latest.Title != title || latest.Artist != artist) return false;
                    await RefreshAsync();
                    return true;
                }
                await Task.Delay(80, deadline.Token);
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Write("TryChangePlaybackPosition 失败: " + ex);
            return false;
        }
        finally { deadline.Cancel(); qqWindow?.Dispose(); _seekGate.End(ticket); }
    }

    /// <summary>切换当前媒体来源窗口：前台最小化，否则显示到前台。</summary>
    public bool FocusSourceApp()
    {
        var id = Current.SourceAppId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        Log.Write("FocusSourceApp, SourceAppUserModelId=" + id);

        var candidates = new List<string>();
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(id[..^4]);
        }
        else
        {
            candidates.Add(id);
            int bang = id.IndexOf('!');
            if (bang > 0)
            {
                candidates.Add(id[..bang]);
            }
        }

        foreach (var name in candidates)
        {
            // 优先用"真主窗口"判定（收在托盘时 MainWindowHandle 会是 0）
            IntPtr hwnd = NativeMethods.FindMainWindow(name);
            if (hwnd != IntPtr.Zero)
            {
                return ToggleSourceWindow(hwnd);
            }

            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        return ToggleSourceWindow(p.MainWindowHandle);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }

    private static bool ToggleSourceWindow(IntPtr hwnd)
    {
        // 播控使用 MA_NOACTIVATE，点击封面不会先夺走播放器的前台状态。
        // 按真实窗口状态判断，避免手动切换应用或最小化后与点击次数脱节。
        var foreground = NativeMethods.GetForegroundWindow();
        bool isForeground = foreground == hwnd
            || (foreground != IntPtr.Zero && NativeMethods.GetAncestor(foreground, 3 /* GA_ROOTOWNER */) == hwnd);
        if (NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd) && isForeground)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
            return true;
        }

        // 已显示但被遮挡时只激活，避免 SW_RESTORE 把最大化窗口改成普通窗口。
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        return NativeMethods.SetForegroundWindow(hwnd);
    }

    /// <summary>诊断用：列出当前所有媒体会话。</summary>
    public string DescribeSessions()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            if (_manager is null)
            {
                return "媒体会话管理器未初始化";
            }

            var sessions = _manager.GetSessions();
            sb.AppendLine($"会话数量: {sessions.Count}");
            foreach (var s in sessions)
            {
                string status = "?";
                string title = "?";
                try
                {
                    status = s.GetPlaybackInfo().PlaybackStatus.ToString();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    var p = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                    title = $"{p?.Title} - {p?.Artist}";
                }
                catch
                {
                    // ignore
                }

                sb.AppendLine($"  · app={s.SourceAppUserModelId} | {status} | {title}");
            }

            var cur = _manager.GetCurrentSession();
            sb.AppendLine($"当前会话: {cur?.SourceAppUserModelId ?? "(无)"}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("枚举失败: " + ex.Message);
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SetSession(null);
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }
        _manager = null;
        _cachedProperties = null;
        _propertiesSession = null;
        _knownSessions = Array.Empty<GlobalSystemMediaTransportControlsSession>();
        _sessionKeys.Clear();
        _coverCache = null;
        _fallbackCover = null;
        _fallbackCoverCancellation?.Cancel();
        _lyrics.Dispose();
    }
}
