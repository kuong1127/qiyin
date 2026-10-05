using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;
using Windows.Media.Control;

internal static class Program
{
[STAThread]
private static int Main(string[] args)
{
    int result = 1;
    var application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
    application.Startup += async (_, _) =>
    {
        try { result = await Run(args); }
        catch (Exception ex) { Console.WriteLine(ex); }
        finally { application.Shutdown(); }
    };
    application.Run();
    return result;
}

private static async Task<int> Run(string[] args)
{
if (args.Length != 2 || !Path.IsPathFullyQualified(args[1])) return 2;
var records = new List<object>();
void Record(object value) { records.Add(value); Console.WriteLine(JsonSerializer.Serialize(value)); }
if (args[0] == "netease-play-cycle")
{
    var neteaseManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    string? testPath = Environment.GetEnvironmentVariable("TMB_TEST_APP_PATH");
    var session = testPath is null
        ? neteaseManager.GetSessions().Single(s => s.SourceAppUserModelId == "cloudmusic.exe")
        : neteaseManager.GetSessions().FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? neteaseManager.GetCurrentSession() ?? throw new InvalidOperationException("没有可验证的媒体会话");
    var original = await session.TryGetMediaPropertiesAsync();
    bool initial = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    string? expectedPath = Environment.GetEnvironmentVariable("TMB_TEST_APP_PATH");
    var process = Process.GetProcessesByName("TaskbarMediaBar").Single(p => expectedPath is null
        ? p.MainModule!.FileName.Contains("20260914_A1播控_v01")
        : string.Equals(p.MainModule!.FileName, expectedPath, StringComparison.OrdinalIgnoreCase));
    IntPtr bar = IntPtr.Zero;
    NativeMethods.EnumWindows((h, _) => { NativeMethods.GetWindowThreadProcessId(h, out int pid);
        if (pid == process.Id && NativeMethods.ReadWindowTitle(h).Contains("A1")) bar = h; return true; }, IntPtr.Zero);
    if (bar == IntPtr.Zero) return 20;
    var root = AutomationElement.FromHandle(bar);
    AutomationElement Find(string id) => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))!;
    var button = Find("PlayBtn");
    if (Find("TitleText").Current.Name != original.Title || !button.Current.IsEnabled) return 21;
    if (session.SourceAppUserModelId is not ("cloudmusic.exe" or "QQMusic.exe")) return 23;
    var native = NativeMethods.FindMainWindow(Path.GetFileNameWithoutExtension(session.SourceAppUserModelId));
    var foreground = NativeMethods.GetForegroundWindow();
    bool iconic = NativeMethods.IsIconic(native), visible = NativeMethods.IsWindowVisible(native);
    bool preserved = true, changed = false, restored = false;
    using var monitoring = new CancellationTokenSource();
    var monitor = Task.Run(async () => { while (!monitoring.IsCancellationRequested) {
        preserved &= NativeMethods.IsIconic(native) == iconic && NativeMethods.IsWindowVisible(native) == visible
            && NativeMethods.GetForegroundWindow() == foreground;
        await Task.Delay(15);
    } });
    async Task<bool> SameTrack() { var p = await session.TryGetMediaPropertiesAsync(); return p.Title == original.Title && p.Artist == original.Artist; }
    void Capture(string name)
    {
        NativeMethods.GetWindowRect(bar, out var r);
        typeof(LikeService).Assembly.GetType("TaskbarMediaBar.Services.ScreenShot")!.GetMethod("Capture")!
            .Invoke(null, new object[] { Path.Combine(Path.GetDirectoryName(args[1])!, name + ".png"), r.Left, r.Top, r.Width, r.Height });
    }
    void Click()
    {
        var r = button.Current.BoundingRectangle;
        int x = (int)(r.Left + r.Width / 2), y = (int)(r.Top + r.Height / 2);
        NativeMethods.GetCursorPos(out var saved);
        try {
            NativeMethods.SetCursorPos(x, y);
            NativeMethods.GetWindowThreadProcessId(NativeMethods.WindowFromPoint(new() { X = x, Y = y }), out int hit);
            if (hit != process.Id) throw new InvalidOperationException("组件被遮挡，未点击");
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(35);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        }
        finally { NativeMethods.GetCursorPos(out var now); if (now.X == x && now.Y == y) NativeMethods.SetCursorPos(saved.X, saved.Y); }
    }
    async Task<bool> WaitState(bool desired)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 4000 && await SameTrack()) {
            bool playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            string label = button.Current.Name;
            if (playing == desired && label == (desired ? "暂停" : "播放")) {
                Record(new { desired, playing, label, responseMs = watch.Elapsed.TotalMilliseconds }); return true;
            }
            await Task.Delay(25);
        }
        return false;
    }
    try {
        Capture("网易云组件_初始");
        Record(new { original.Title, original.Artist, initial, label = button.Current.Name, iconic, visible });
        Click(); changed = await WaitState(!initial);
        if (expectedPath is not null) await Task.Delay(3800);
        Capture("网易云组件_切换后");
    }
    finally {
        if (await SameTrack()) {
            bool now = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (now != initial) Click();
            restored = await WaitState(initial);
            if (expectedPath is not null) await Task.Delay(3800);
            Capture("网易云组件_恢复后");
        }
        monitoring.Cancel(); await monitor;
        Record(new { changed, restored, windowAndFocusPreservedThroughout = preserved });
        File.WriteAllText(args[1], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }
    return changed && restored && preserved ? 0 : 22;
}
if (args[0] is "bar-click" or "bar-drag")
{
    using var media = new MediaSessionService(new[] { "QQMusic" });
    await media.InitAsync(); await media.RefreshAsync();
    if (media.Current.SourceAppId != "QQMusic.exe") return 4;
    double original = media.Current.EstimatedPositionSeconds, duration = media.Current.DurationSeconds;
    string identity = media.Current.Identity;
    bool playing = media.Current.IsPlaying;
    if (duration < 50 || duration - original < 15) return 7;
    var barProcess = Process.GetProcessesByName("TaskbarMediaBar").Single(p => p.MainModule!.FileName.Contains("20260914_A1播控_v01"));
    if (barProcess.MainModule!.FileVersionInfo.FileVersion != "0.2.7.0") return 8;
    IntPtr bar = IntPtr.Zero;
    NativeMethods.EnumWindows((h, _) => { NativeMethods.GetWindowThreadProcessId(h, out int pid);
        if (pid == barProcess.Id && NativeMethods.ReadWindowTitle(h).Contains("A1")) bar = h; return true; }, IntPtr.Zero);
    if (bar == IntPtr.Zero) return 9;
    var root = AutomationElement.FromHandle(bar);
    AutomationElement Find(string id) => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))!;
    if (Find("TitleText").Current.Name != media.Current.Title) return 10;
    NativeMethods.GetWindowRect(bar, out var rect);
    double scale = NativeMethods.GetDpiForWindow(bar) / 96.0;
    double left = rect.Left + 4 * scale, width = rect.Width - 8 * scale;
    double target = original < duration / 2 ? Math.Min(duration - 10, original + 20) : Math.Max(10, original - 20);
    int x = (int)Math.Round(left + width * target / duration), y = rect.Bottom - (int)Math.Round(5 * scale);
    int startX = args[0] == "bar-drag" ? (int)Math.Round(left + width * original / duration) : x;
    var foreground = NativeMethods.GetForegroundWindow();
    var qqWindowHandle = NativeMethods.FindMainWindow("QQMusic");
    bool minimized = NativeMethods.IsIconic(qqWindowHandle);
    NativeMethods.GetCursorPos(out var saved);
    var watch = Stopwatch.StartNew();
    bool pressed = false, changed = false, restored = false;
    string feedback = "";
    try
    {
        NativeMethods.SetCursorPos(startX, y);
        await Task.Delay(70);
        NativeMethods.GetWindowThreadProcessId(NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = startX, Y = y }), out int hitPid);
        if (hitPid != barProcess.Id) return 11;
        var hitControl = AutomationElement.FromPoint(new System.Windows.Point(startX, y));
        Record(new { hitControl = hitControl.Current.Name, hitId = hitControl.Current.AutomationId, startX, x, y,
            progressRect = Find("ProgressTrack")?.Current.BoundingRectangle.ToString() });
        watch.Restart();
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero); pressed = true;
        await Task.Delay(65);
        if (args[0] == "bar-drag") { NativeMethods.SetCursorPos(x, y); await Task.Delay(75); }
        feedback = Find("ArtistText").Current.Name;
        double feedbackMs = watch.Elapsed.TotalMilliseconds;
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero); pressed = false;
        while (watch.ElapsedMilliseconds < 4500)
        {
            await media.RefreshAsync();
            if (media.Current.Identity != identity) break;
            if (Math.Abs(media.Current.EstimatedPositionSeconds - target) < 3) { changed = true; break; }
            await Task.Delay(80);
        }
        NativeMethods.GetWindowRect(bar, out var after);
        Record(new { mode = args[0], changed, original, target, actual = media.Current.EstimatedPositionSeconds,
            feedback, feedbackMs, totalMs = watch.Elapsed.TotalMilliseconds,
            barDidNotMove = after.Left == rect.Left && after.Top == rect.Top,
            foregroundPreserved = NativeMethods.GetForegroundWindow() == foreground,
            minimizedPreserved = NativeMethods.IsIconic(qqWindowHandle) == minimized, finalText = Find("ArtistText").Current.Name });
    }
    finally
    {
        if (pressed) NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        NativeMethods.GetCursorPos(out var now);
        if (now.Y == y && (now.X == x || now.X == startX)) NativeMethods.SetCursorPos(saved.X, saved.Y);
        if (changed && media.Current.Identity == identity)
        {
            double restoreTarget = original + (playing ? watch.Elapsed.TotalSeconds : 0);
            restored = await media.SeekAsync(restoreTarget, identity);
            await media.RefreshAsync();
            Record(new { restored, restoreTarget, actual = media.Current.EstimatedPositionSeconds });
        }
        File.WriteAllText(args[1], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }
    return changed && restored ? 0 : 5;
}
if (args[0] == "qq-capture")
{
    var hwnd = NativeMethods.FindMainWindow("QQMusic");
    if (hwnd == IntPtr.Zero) return 4;
    bool iconic = NativeMethods.IsIconic(hwnd), visible = NativeMethods.IsWindowVisible(hwnd);
    var foreground = NativeMethods.GetForegroundWindow();
    try
    {
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hwnd);
        await Task.Delay(500);
        NativeMethods.GetWindowRect(hwnd, out var r);
        var monitor = NativeMethods.GetMonitorRect(hwnd);
        Record(new { monitor = new { monitor.Left, monitor.Top, monitor.Right, monitor.Bottom }, dpi = NativeMethods.GetDpiForWindow(hwnd) });
        NativeMethods.EnumChildWindows(hwnd, (h, _) => {
            NativeMethods.GetWindowRect(h, out var child); var cls = new System.Text.StringBuilder(128); NativeMethods.GetClassName(h, cls, 128);
            Record(new { child = h.ToInt64(), cls = cls.ToString(), rect = new { child.Left, child.Top, child.Right, child.Bottom }, visible = NativeMethods.IsWindowVisible(h) });
            return true;
        }, IntPtr.Zero);
        var capture = typeof(LikeService).Assembly.GetType("TaskbarMediaBar.Services.ScreenShot")!.GetMethod("Capture")!;
        bool captured = (bool)capture.Invoke(null, new object[] { args[1], r.Left, r.Bottom - 150, r.Width, Math.Min(320, monitor.Bottom - r.Bottom + 150) })!;
        Record(new { captured, r.Left, r.Top, r.Bottom, r.Width });
        return captured ? 0 : 5;
    }
    finally
    {
        if (NativeMethods.GetForegroundWindow() == hwnd)
        {
            if (iconic) NativeMethods.ShowWindow(hwnd, 7);
            else if (!visible) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            NativeMethods.SetForegroundWindow(foreground);
        }
    }
}
if (args[0] == "lyrics-fetch")
{
    using var lyrics = new LyricsService();
    var timer = Stopwatch.StartNew();
    var doc = await lyrics.FetchAsync("起风了 (旧版)", "买辣椒也用券", sourceAppId: "QQMusic.exe", durationSeconds: 312.913);
    Record(new { success = doc is not null, doc?.Title, doc?.Artist, doc?.Source, doc?.TrackId, doc?.DurationSeconds,
        lines = doc?.Lines.Count, first = doc?.Lines.FirstOrDefault()?.StartMs, last = doc?.Lines.LastOrDefault()?.StartMs,
        elapsedMs = timer.Elapsed.TotalMilliseconds });
    File.WriteAllText(args[1], JsonSerializer.Serialize(records));
    return doc?.TrackId == "0004jPDk2eB2dt" && doc.Lines.Count > 20 ? 0 : 5;
}
if (args[0] == "seek-service")
{
    using var media = new MediaSessionService(new[] { "QQMusic" });
    await media.InitAsync(); await media.RefreshAsync();
    if (!media.Current.HasSession || media.Current.SourceAppId != "QQMusic.exe") return 4;
    string identity = media.Current.Identity;
    double original = media.Current.EstimatedPositionSeconds, duration = media.Current.DurationSeconds;
    bool playing = media.Current.IsPlaying;
    if (duration < 40 || duration - original < 12) { Record(new { skipped = "歌曲接近结尾，避免恢复时跨歌" }); return 7; }
    var hwnd = NativeMethods.FindMainWindow("QQMusic");
    bool iconic = NativeMethods.IsIconic(hwnd);
    var foreground = NativeMethods.GetForegroundWindow();
    double target = original < duration / 2 ? Math.Min(duration - 10, original + 20) : Math.Max(10, original - 20);
    var watch = Stopwatch.StartNew();
    bool ok = await media.SeekAsync(target, identity);
    double elapsed = watch.Elapsed.TotalMilliseconds;
    await media.RefreshAsync();
    bool changed = media.Current.Identity == identity && Math.Abs(media.Current.EstimatedPositionSeconds - target) < 3;
    Record(new { seek = ok, changed, original, target, actual = media.Current.EstimatedPositionSeconds, elapsedMs = elapsed,
        foregroundPreserved = NativeMethods.GetForegroundWindow() == foreground, minimizedPreserved = NativeMethods.IsIconic(hwnd) == iconic });
    bool restoredOk = false;
    if (changed)
    {
        double restoreTarget = original + (playing ? watch.Elapsed.TotalSeconds : 0);
        bool restored = await media.SeekAsync(restoreTarget, identity);
        await media.RefreshAsync();
        Record(new { restored, restoreTarget, actual = media.Current.EstimatedPositionSeconds });
        restoredOk = restored && media.Current.Identity == identity && Math.Abs(media.Current.EstimatedPositionSeconds - restoreTarget) < 3;
    }
    File.WriteAllText(args[1], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    return ok && changed && restoredOk ? 0 : 5;
}
if (args[0] == "qq-search")
{
    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    http.DefaultRequestHeaders.Referrer = new Uri("https://y.qq.com/");
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
    string query = Uri.EscapeDataString("起风了 (旧版) 买辣椒也用券");
    string data = JsonSerializer.Serialize(new { comm = new { ct = 24, cv = 0 }, req_1 = new {
        method = "DoSearchForQQMusicDesktop", module = "music.search.SearchCgiService", param = new {
            grp = 1, num_per_page = 10, page_num = 1, query = "起风了 (旧版) 买辣椒也用券", search_type = 0 } } });
    foreach (string url in new[] { "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&new_json=1&p=1&n=10&w=" + query,
        "https://u.y.qq.com/cgi-bin/musicu.fcg?data=" + Uri.EscapeDataString(data) })
    {
        try
        {
            string json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement list;
            if (root.TryGetProperty("data", out var legacy)) list = legacy.GetProperty("song").GetProperty("list");
            else if (root.TryGetProperty("req_1", out var modern)) list = modern.GetProperty("data").GetProperty("body").GetProperty("song").GetProperty("list");
            else { Record(new { response = json[..Math.Min(json.Length, 150)] }); continue; }
            foreach (var song in list.EnumerateArray())
                Record(new { title = song.GetProperty("title").GetString(), mid = song.GetProperty("mid").GetString(),
                    duration = song.GetProperty("interval").GetInt32(), artist = string.Join(",", song.GetProperty("singer").EnumerateArray().Select(a => a.GetProperty("name").GetString())) });
        }
        catch (Exception ex) { Record(new { error = ex.Message }); }
    }
    File.WriteAllText(args[1], JsonSerializer.Serialize(records));
    return 0;
}
if (args[0] == "lyrics-search")
{
    using var http = new System.Net.Http.HttpClient();
    http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
    string query = Uri.EscapeDataString("起风了 (旧版) 买辣椒也用券");
    var json = await http.GetStringAsync("https://music.163.com/api/search/get/web?type=1&offset=0&limit=10&s=" + query);
    using var doc = JsonDocument.Parse(json);
    foreach (var song in doc.RootElement.GetProperty("result").GetProperty("songs").EnumerateArray())
        Record(new { id = song.GetProperty("id").GetInt64(), title = song.GetProperty("name").GetString(),
            artist = string.Join(",", song.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString())),
            duration = song.GetProperty("duration").GetDouble() / 1000 });
    File.WriteAllText(args[1], JsonSerializer.Serialize(records));
    return 0;
}
var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
var sessions = manager.GetSessions();
Record(new { count = sessions.Count });
foreach (var session in sessions)
{
    var props = await session.TryGetMediaPropertiesAsync();
    Record(new { app = session.SourceAppUserModelId, props.Title, props.Artist, props.AlbumTitle });
    if (args[0] == "seek-native" && session.SourceAppUserModelId == "QQMusic.exe")
    {
        var before = session.GetTimelineProperties();
        var watch = Stopwatch.StartNew();
        double initial = before.Position.TotalSeconds;
        double target = Math.Clamp(initial + 15, 0, before.EndTime.TotalSeconds - 3);
        bool ok = await session.TryChangePlaybackPositionAsync((long)(target * 10_000_000));
        await Task.Delay(700);
        var after = session.GetTimelineProperties();
        var nowProps = await session.TryGetMediaPropertiesAsync();
        bool same = nowProps.Title == props.Title && nowProps.Artist == props.Artist;
        bool changed = same && Math.Abs(after.Position.TotalSeconds - target) < 3;
        Record(new { nativeSeek = ok, changed, initial, target, actual = after.Position.TotalSeconds, elapsed = watch.Elapsed.TotalMilliseconds });
        if (changed)
        {
            bool restored = await session.TryChangePlaybackPositionAsync((long)((initial + watch.Elapsed.TotalSeconds) * 10_000_000));
            Record(new { restoreSent = restored });
        }
    }
    for (int i = 0; i < 8; i++)
    {
        var info = session.GetPlaybackInfo();
        var t = session.GetTimelineProperties();
        Record(new { sample = i, status = info.PlaybackStatus.ToString(), rate = info.PlaybackRate,
            canSeek = info.Controls.IsPlaybackPositionEnabled, now = DateTimeOffset.UtcNow,
            updated = t.LastUpdatedTime, pos = t.Position.TotalSeconds, start = t.StartTime.TotalSeconds,
            end = t.EndTime.TotalSeconds, min = t.MinSeekTime.TotalSeconds, max = t.MaxSeekTime.TotalSeconds });
        await Task.Delay(250);
    }
}
var qq = NativeMethods.FindMainWindow("QQMusic");
if (args[0] == "seek-qq-hidden" && qq != IntPtr.Zero)
{
    var session = sessions.First(s => s.SourceAppUserModelId == "QQMusic.exe");
    var props = await session.TryGetMediaPropertiesAsync();
    var initial = session.GetTimelineProperties();
    var watch = Stopwatch.StartNew();
    var root = AutomationElement.FromHandle(qq);
    var nodes = root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
    var times = nodes.Where(n => System.Text.RegularExpressions.Regex.IsMatch(n.Current.Name.Trim(), "^\\d{1,3}:\\d{2}$")).OrderBy(n => n.Current.BoundingRectangle.Left).ToArray();
    if (times.Length != 2) { Record(new { error = "时间锚点不唯一", count = times.Length }); return 4; }
    var left = times[0].Current.BoundingRectangle; var right = times[1].Current.BoundingRectangle;
    var track = nodes.Select(n => n.Current.BoundingRectangle).Where(r => !r.IsEmpty && r.Width > 100 && r.Height < 50
        && r.Left >= left.Right && r.Right <= right.Left && r.Top < left.Bottom && r.Bottom > left.Top).OrderByDescending(r => r.Width).First();
    NativeMethods.GetWindowRect(qq, out var wr);
    double duration = initial.EndTime.TotalSeconds, original = initial.Position.TotalSeconds;
    double target = original < duration / 2 ? Math.Min(duration - 10, original + 20) : Math.Max(10, original - 20);
    bool Click(double seconds)
    {
        int x = (int)Math.Round(track.Left - wr.Left + (track.Width - 1) * seconds / duration);
        int y = (int)Math.Round(track.Top - wr.Top + track.Height / 2);
        Record(new { x, y, target = seconds, track = track.ToString(), minimized = NativeMethods.IsIconic(qq) });
        return UiaClicker.ClickByPostedMessage(new ClickTarget(qq, x, y, 0, 0, 1, false, "诊断进度"));
    }
    bool sent = Click(target);
    await Task.Delay(700);
    var after = session.GetTimelineProperties();
    var current = await session.TryGetMediaPropertiesAsync();
    bool changed = current.Title == props.Title && current.Artist == props.Artist && Math.Abs(after.Position.TotalSeconds - target) < 3;
    Record(new { sent, changed, original, target, actual = after.Position.TotalSeconds });
    if (changed)
    {
        bool restored = Click(original + watch.Elapsed.TotalSeconds);
        await Task.Delay(600);
        Record(new { restoreSent = restored, actual = session.GetTimelineProperties().Position.TotalSeconds });
    }
    File.WriteAllText(args[1], JsonSerializer.Serialize(records));
    return changed ? 0 : 5;
}
if (qq != IntPtr.Zero)
{
    bool minimized = NativeMethods.IsIconic(qq), visible = NativeMethods.IsWindowVisible(qq);
    bool inspectVisible = args[0] == "inspect-visible";
    try
    {
    if (inspectVisible && (minimized || !visible))
    {
        NativeMethods.ShowWindow(qq, NativeMethods.SW_SHOWNOACTIVATE);
        await Task.Delay(400);
    }
    Record(new { window = qq.ToInt64(), visible = NativeMethods.IsWindowVisible(qq), iconic = NativeMethods.IsIconic(qq) });
    NativeMethods.GetWindowRect(qq, out var wr); NativeMethods.GetClientRect(qq, out var cr);
    Record(new { windowRect = new { wr.Left, wr.Top, wr.Right, wr.Bottom }, client = new { cr.Width, cr.Height } });
    NativeMethods.GetWindowThreadProcessId(qq, out int qqPid);
    NativeMethods.EnumWindows((h, _) => {
        NativeMethods.GetWindowThreadProcessId(h, out int pid);
        if (pid != qqPid) return true;
        NativeMethods.GetWindowRect(h, out var r);
        if (r.Width < 100 || r.Height < 10) return true;
        var cls = new System.Text.StringBuilder(128); NativeMethods.GetClassName(h, cls, cls.Capacity);
        Record(new { hwnd = h.ToInt64(), cls = cls.ToString(), visible = NativeMethods.IsWindowVisible(h), iconic = NativeMethods.IsIconic(h),
            title = NativeMethods.ReadWindowTitle(h), rect = new { r.Left, r.Top, r.Right, r.Bottom } });
        return true;
    }, IntPtr.Zero);
    var root = AutomationElement.FromHandle(qq);
    var nodes = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
    Record(new { nodes = nodes.Count });
    foreach (AutomationElement node in nodes)
    {
        if (!inspectVisible && node.Current.ControlType != ControlType.Slider && node.Current.ControlType != ControlType.ProgressBar
            && !System.Text.RegularExpressions.Regex.IsMatch(node.Current.Name, "进度|播放|暂停|歌词|[0-9]:[0-9]")) continue;
        Record(new { slider = node.Current.Name, type = node.Current.ControlType.ProgrammaticName, id = node.Current.AutomationId, enabled = node.Current.IsEnabled,
            rect = node.Current.BoundingRectangle.ToString(), patterns = node.GetSupportedPatterns().Select(p => p.ProgrammaticName).ToArray() });
    }
    if (inspectVisible)
    {
        var capture = typeof(LikeService).Assembly.GetType("TaskbarMediaBar.Services.ScreenShot")!.GetMethod("Capture")!;
        capture.Invoke(null, new object[] { Path.Combine(Path.GetDirectoryName(args[1])!, "QQ播放条.png"), wr.Left, wr.Bottom - 180, wr.Width, 180 });
    }
    }
    finally
    {
        if (inspectVisible && minimized) NativeMethods.ShowWindow(qq, NativeMethods.SW_MINIMIZE);
        else if (inspectVisible && !visible) NativeMethods.ShowWindow(qq, NativeMethods.SW_HIDE);
    }
}
File.WriteAllText(args[1], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
return 0;
}
}
