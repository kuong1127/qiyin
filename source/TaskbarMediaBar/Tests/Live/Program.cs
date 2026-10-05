using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Automation;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

if (args.Length < 3 || !int.TryParse(args[0], out int barPid)) return 2;
string output = args[1];
string mode = args[2];
Directory.CreateDirectory(output);
string report = Path.Combine(output, mode == "cycle" ? "20260914_QQ收藏实机检查_v01.json" : "20260914_A1运行检查_v01.json");
var events = new List<object>();
void Record(object entry)
{
    events.Add(entry);
    File.WriteAllText(report, JsonSerializer.Serialize(new { at = DateTimeOffset.Now, mode, events }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(entry));
}
IntPtr barHwnd = IntPtr.Zero;
NativeMethods.EnumWindows((h, _) => {
    NativeMethods.GetWindowThreadProcessId(h, out int pid);
    if (pid == barPid && NativeMethods.ReadWindowTitle(h).Contains("A1")) barHwnd = h;
    return true;
}, IntPtr.Zero);
if (barHwnd == IntPtr.Zero) { Record(new { error = "A1 窗口不存在" }); return 3; }
var root = AutomationElement.FromHandle(barHwnd);
AutomationElement Find(string id) => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))
    ?? throw new InvalidOperationException("找不到控件 " + id);
string BarTitle() => Find("TitleText").Current.Name;
NativeMethods.GetWindowRect(barHwnd, out var barRect);
var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
    .Cast<AutomationElement>().Select(b => new { id = b.Current.AutomationId, name = b.Current.Name, enabled = b.Current.IsEnabled,
        x = b.Current.BoundingRectangle.X, y = b.Current.BoundingRectangle.Y, width = b.Current.BoundingRectangle.Width, height = b.Current.BoundingRectangle.Height }).ToArray();
Record(new { step = "运行界面", pid = barPid, title = BarTitle(), width = barRect.Width, height = barRect.Height,
    dpi = NativeMethods.GetDpiForWindow(barHwnd), visible = NativeMethods.IsWindowVisible(barHwnd), buttons });
if (mode == "inspect")
{
    bool idle = BarTitle() == "未检测到媒体会话";
    // Media sources can omit previous/next; inspect validates only controls actually shown.
    bool shape = buttons.Length is >= 1 and <= 4 && buttons.Any(b => b.id == "PlayBtn")
        && (!idle || buttons.All(b => !b.enabled));
    bool contained = buttons.All(b => b.x >= barRect.Left - 1 && b.y >= barRect.Top - 1
        && b.x + b.width <= barRect.Right + 1 && b.y + b.height <= barRect.Bottom + 1);
    bool ok = shape && contained && buttons.All(b => b.id is "PrevBtn" or "PlayBtn" or "NextBtn" or "LikeBtn");
    Record(new { step = "按实际媒体状态检查按钮及边界", idle, passed = ok });
    if (!ok) return 4;
}
else if (buttons.Length != 4 || buttons.Any(b => b.id == "MoreBtn")) return 4;
if (mode == "cover-toggle")
{
    string playerName = args.Length > 3 ? args[3] : "cloudmusic";
    var player = NativeMethods.FindMainWindow(playerName);
    if (player == IntPtr.Zero) return 30;
    bool visible = NativeMethods.IsWindowVisible(player), minimized = NativeMethods.IsIconic(player), maximized = NativeMethods.IsZoomed(player);
    var foreground = NativeMethods.GetForegroundWindow();
    bool passed = true;
    async Task Check(string step, Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 2500) await Task.Delay(20);
        bool ok = condition(); passed &= ok;
        Record(new { step, passed = ok, responseMs = timer.Elapsed.TotalMilliseconds,
            target = player.ToInt64(), resolved = NativeMethods.FindMainWindow(playerName).ToInt64(),
            foreground = NativeMethods.GetForegroundWindow().ToInt64(),
            visible = NativeMethods.IsWindowVisible(player), minimized = NativeMethods.IsIconic(player), maximized = NativeMethods.IsZoomed(player) });
        // 状态标志先于窗口动画完成，等动画稳定后再进行下一次真实点击。
        await Task.Delay(350);
    }
    void ClickCover()
    {
        NativeMethods.GetWindowRect(barHwnd, out var rect);
        double scale = NativeMethods.GetDpiForWindow(barHwnd) / 96.0;
        int x = rect.Left + (int)(21 * scale), y = rect.Top + rect.Height / 2;
        NativeMethods.GetCursorPos(out var saved);
        try
        {
            NativeMethods.SetCursorPos(x, y);
            // 光标跨窗口后，让 WPF 完成鼠标进入和命中目标更新再按下。
            Thread.Sleep(180);
            NativeMethods.GetWindowThreadProcessId(NativeMethods.WindowFromPoint(new() { X = x, Y = y }), out int hit);
            if (hit != barPid) throw new InvalidOperationException("封面被遮挡，未点击");
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(35);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(80);
        }
        finally { NativeMethods.SetCursorPos(saved.X, saved.Y); }
    }
    try
    {
        NativeMethods.ShowWindow(player, NativeMethods.SW_MINIMIZE);
        await Task.Delay(350);
        ClickCover();
        await Check("最小化后点击封面恢复", () => !NativeMethods.IsIconic(player) && NativeMethods.GetForegroundWindow() == player);
        ClickCover();
        await Check("再次点击封面最小化", () => NativeMethods.IsIconic(player));
        ClickCover();
        await Check("第三次点击重新显示", () => !NativeMethods.IsIconic(player) && NativeMethods.GetForegroundWindow() == player);
        NativeMethods.ShowWindow(player, NativeMethods.SW_HIDE);
        await Task.Delay(250);
        ClickCover();
        await Check("隐藏到托盘后点击显示", () => NativeMethods.IsWindowVisible(player) && !NativeMethods.IsIconic(player) && NativeMethods.GetForegroundWindow() == player);
        if (foreground != IntPtr.Zero && foreground != player && foreground != barHwnd)
        {
            NativeMethods.ShowWindow(player, 3 /* SW_MAXIMIZE */);
            NativeMethods.SetForegroundWindow(foreground);
            await Task.Delay(350);
            await Check("被遮挡场景准备", () => NativeMethods.GetForegroundWindow() != player && NativeMethods.IsZoomed(player));
            ClickCover();
            await Check("被遮挡时激活并保留最大化", () => NativeMethods.GetForegroundWindow() == player && NativeMethods.IsZoomed(player));
            ClickCover();
            await Check("最大化窗口再次点击最小化", () => NativeMethods.IsIconic(player));
            ClickCover();
            await Check("恢复后仍为最大化", () => NativeMethods.GetForegroundWindow() == player && NativeMethods.IsZoomed(player));
        }
        return passed ? 0 : 31;
    }
    finally
    {
        NativeMethods.ShowWindow(player, maximized ? 3 : NativeMethods.SW_RESTORE);
        if (minimized) NativeMethods.ShowWindow(player, NativeMethods.SW_MINIMIZE);
        else if (!visible) NativeMethods.ShowWindow(player, NativeMethods.SW_HIDE);
        NativeMethods.SetForegroundWindow(foreground);
    }
}
if (mode == "hover")
{
    NativeMethods.GetCursorPos(out var saved);
    var b = buttons.First(x => x.id == "PlayBtn");
    int x = (int)(b.x + b.width / 2), y = (int)(b.y + b.height / 2);
    NativeMethods.SetCursorPos(x, y);
    try
    {
        await Task.Delay(1400);
        var tips = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, barPid),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip)));
        NativeMethods.GetCursorPos(out var now);
        bool stayed = now.X == x && now.Y == y;
        Record(new { step = "实际悬停播放按钮", durationMs = 1400, cursorStayed = stayed, visibleTooltipCount = tips.Count,
            passed = stayed && tips.Count == 0 });
        return stayed && tips.Count == 0 ? 0 : 10;
    }
    finally
    {
        NativeMethods.GetCursorPos(out var now);
        if (now.X == x && now.Y == y) NativeMethods.SetCursorPos(saved.X, saved.Y);
    }
}
if (mode == "inspect")
{
    var capture = typeof(LikeService).Assembly.GetType("TaskbarMediaBar.Services.ScreenShot")!.GetMethod("Capture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
    string image = Path.Combine(output, "A1_任务栏实机.png");
    bool ok = (bool)capture.Invoke(null, new object[] { image, barRect.Left, barRect.Top, barRect.Width, barRect.Height })!;
    Record(new { step = "截图", ok, image });
    return ok ? 0 : 5;
}
if (mode == "netease")
{
    // 旧版检查显式打开播放器核对图标；后台不拉起的完整检查位于 Tests/Stability。
    NeteaseVisualState ReadVisible()
    {
        var h = NativeMethods.FindMainWindow("cloudmusic");
        bool wasMinimized = NativeMethods.IsIconic(h), wasVisible = NativeMethods.IsWindowVisible(h);
        try
        {
            if (wasMinimized || !wasVisible) NativeMethods.ShowWindow(h, NativeMethods.SW_SHOWNOACTIVATE);
            Thread.Sleep(300);
            return NeteaseWindowState.Read();
        }
        finally
        {
            if (wasMinimized) NativeMethods.ShowWindow(h, NativeMethods.SW_MINIMIZE);
            else if (!wasVisible) NativeMethods.ShowWindow(h, NativeMethods.SW_HIDE);
        }
    }
    var native = NativeMethods.FindMainWindow("cloudmusic");
    var original = ReadVisible();
    string originalBar = BarTitle();
    bool Same() => NativeMethods.ReadWindowTitle(native) == original.Identity && BarTitle() == originalBar;
    Record(new { step = "网易云初始状态", original });
    if (original.Liked != false || !original.Identity.StartsWith(originalBar + " - ", StringComparison.Ordinal)) return 7;
    var heart = Find("LikeBtn");
    var config = new AppSettings();
    bool neteaseAdded = false, neteaseRestored = false;
    try
    {
        var watch = Stopwatch.StartNew();
        ((InvokePattern)heart.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        double? feedbackMs = null;
        while (Same() && watch.ElapsedMilliseconds < 5000)
        {
            string label = heart.Current.Name;
            if (feedbackMs is null && label.Contains("正在")) feedbackMs = watch.Elapsed.TotalMilliseconds;
            if (label.Contains("已喜欢") || label == "喜欢（已发送，待同步）")
            {
                double confirmedUiMs = watch.Elapsed.TotalMilliseconds;
                var actual = ReadVisible();
                neteaseAdded = actual.Fresh && actual.Confirmed && actual.Liked == true && Same();
                Record(new { step = "任务栏红心确认", added = neteaseAdded, confirmedUiMs, feedbackMs, label, actual });
                var capture = typeof(LikeService).Assembly.GetType("TaskbarMediaBar.Services.ScreenShot")!.GetMethod("Capture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
                string image = Path.Combine(output, "A1_网易云红心实机.png");
                bool captured = (bool)capture.Invoke(null, new object[] { image, barRect.Left, barRect.Top, barRect.Width, barRect.Height })!;
                Record(new { step = "红心截图", captured, image });
                break;
            }
            await Task.Delay(20);
        }
        if (neteaseAdded && Same())
        {
            ((InvokePattern)heart.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            watch.Restart();
            while (Same() && watch.ElapsedMilliseconds < 5000)
            {
                if (heart.Current.Name.Contains("加入") || heart.Current.Name == "取消喜欢（已发送，待同步）")
                { var actual = ReadVisible(); neteaseRestored = actual.Fresh && actual.Confirmed && actual.Liked == false; break; }
                await Task.Delay(30);
            }
        }
    }
    finally
    {
        if (Same())
        {
            var actual = ReadVisible();
            if (actual.Liked == true)
            {
                var restore = LikeService.Toggle("cloudmusic.exe", config, Same, false);
                var final = ReadVisible();
                neteaseRestored = restore.Ok && final.Fresh && final.Confirmed && final.Liked == false;
            }
            else neteaseRestored = actual.Liked == false;
        }
        Record(new { step = "最终状态", added = neteaseAdded, restored = neteaseRestored, sameTrack = Same(), label = heart.Current.Name });
    }
    return neteaseAdded && neteaseRestored ? 0 : 9;
}
if (mode != "cycle") return 2;

using var media = new MediaSessionService(new[] { "QQMusic" });
await media.InitAsync(); await media.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(4));
string originalSong = BarTitle();
if (!media.Current.SourceAppId.Contains("QQMusic", StringComparison.OrdinalIgnoreCase) || media.Current.Title != originalSong)
{ Record(new { skipped = "当前播控和 QQ 曲目未能一致确认" }); return 6; }
var settings = new AppSettings();
IntPtr qq = NativeMethods.FindMainWindow("QQMusic");
string qqTitle = NativeMethods.ReadWindowTitle(qq);
string windowBefore = LikeService.DescribeWindow("QQMusic.exe");
bool Current() => NativeMethods.ReadWindowTitle(qq) == qqTitle && BarTitle() == originalSong;
var before = LikeService.Probe("QQMusic.exe", settings);
Record(new { step = "收藏初始状态", before.Liked, before.ButtonName, windowBefore });
// 只测试“未喜欢 -> 加入 -> 恢复未喜欢”，不取消用户原来就喜欢的歌曲。
if (before.Liked != false) { Record(new { skipped = "初始状态不是明确未喜欢" }); return 7; }
var like = Find("LikeBtn");
if (!like.Current.Name.Contains("加入")) { Record(new { skipped = "任务栏喜欢状态尚未同步" }); return 8; }
async Task<bool> ClickAndConfirm(bool desired)
{
    if (!Current()) return false;
    var watch = Stopwatch.StartNew();
    ((InvokePattern)like.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    double? feedback = null;
    while (watch.ElapsedMilliseconds < 5500 && Current())
    {
        string label = like.Current.Name;
        if (feedback is null && label.Contains("正在")) feedback = watch.Elapsed.TotalMilliseconds;
        if (desired ? label.Contains("已喜欢") : label.Contains("加入"))
        {
            var probe = LikeService.Probe("QQMusic.exe", settings);
            if (probe.Liked == desired)
            {
                Record(new { step = desired ? "加入已确认" : "恢复已确认", elapsedMs = watch.Elapsed.TotalMilliseconds,
                    feedbackObservedMs = feedback, probe.Liked, probe.ButtonName });
                return true;
            }
        }
        await Task.Delay(35);
    }
    Record(new { step = "确认超时或曲目变化", desired, elapsedMs = watch.Elapsed.TotalMilliseconds, label = like.Current.Name });
    return false;
}
bool added = false, restored = false;
try
{
    added = await ClickAndConfirm(true);
    if (added && Current()) restored = await ClickAndConfirm(false);
}
finally
{
    if (Current())
    {
        var after = LikeService.Probe("QQMusic.exe", settings);
        if (after.Liked == true)
        {
            // 状态明确时才恢复目标值；不盲目再次切换。
            var rollback = LikeService.Toggle("QQMusic.exe", settings, Current, false);
            restored = rollback.Verified && rollback.LikedAfter == false;
            Record(new { step = "目标状态恢复", rollback.Verified, rollback.Message });
        }
        else restored = after.Liked == false;
        Record(new { step = "最终状态", added, restored, liked = LikeService.Probe("QQMusic.exe", settings).Liked,
            windowAfter = LikeService.DescribeWindow("QQMusic.exe"), sameTrack = Current() });
    }
    else Record(new { warning = "测试过程中歌曲变化，停止发送后续操作；原歌曲收藏状态需要人工确认。", added, restored });
}
return added && restored ? 0 : 9;
