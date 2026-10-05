using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using TaskbarMediaBar.Services;

var checks = new List<object>();
int failures = 0;
void Check(string name, bool success)
{
    checks.Add(new { name, passed = success });
    if (!success) failures++;
}

var gate = new TrackOperationGate();
Check("没有曲目时拒绝收藏", !gate.TryBegin(out _));
gate.Observe("QQ|song-a|artist");
var beforeProbe = gate.Capture();
Check("同一曲目的探测有效", gate.IsCurrent(beforeProbe));
Check("第一次点击可以开始", gate.TryBegin(out var a));
Check("当前曲目可显示处理标记", gate.CurrentBusy);
Check("进行中的重复点击不会再发送", !gate.TryBegin(out _));
Check("点击后拒绝较早的状态探测", !gate.IsCurrent(beforeProbe));
gate.Observe("QQ|song-b|artist");
Check("切歌后不把旧处理标记显示到新歌", !gate.CurrentBusy);
Check("切歌后拒绝旧收藏结果", !gate.IsCurrent(a));
Check("旧操作未结束时不排队发送新收藏", !gate.TryBegin(out _));
gate.Observe("QQ|song-a|artist");
Check("切回同名歌曲也不接收旧结果", !gate.IsCurrent(a));
gate.End(a);
Check("旧操作结束后可再次点击", gate.TryBegin(out var b));
gate.End(a);
Check("旧回调不会解锁新操作", gate.Busy);
gate.End(b);
Check("当前操作完成后解除忙碌", !gate.Busy);

var tickets = new ConcurrentBag<TrackOperationGate.Ticket>();
Parallel.For(0, 64, _ => { if (gate.TryBegin(out var ticket)) tickets.Add(ticket); });
Check("64 个并发请求只有一个进入", tickets.Count == 1);
foreach (var ticket in tickets) gate.End(ticket);
gate.Observe("");
Check("来源退出后不能继续操作", !gate.TryBegin(out _));

var settings = new AppSettings();
var canceled = LikeService.Toggle("QQMusic.exe", settings, () => false, true);
Check("过期请求在接触播放器前返回", !canceled.Ok && canceled.Message.Contains("取消"));
settings.FallbackApps.Add("UnitTestFallback");
var fallback = LikeService.Probe("UnitTestFallback.exe", settings);
Check("无状态来源的通用快捷键通道不伪造确认", fallback.Liked is null && fallback.Scope.Contains("快捷键"));
using (var media = new MediaSessionService(new[] { "QQMusic" }))
{
    Check("没有可跳转会话时返回失败", !await media.SeekAsync(30));
    Check("无效时间值不发送跳转", !await media.SeekAsync(double.NaN));
    Check("没有会话时播放指令返回失败", !await media.TogglePlayPauseAsync());
}

var samples = new List<object>();
var likeCache = new TrackLikeStateCache();
var at = DateTime.UtcNow;
var empty = likeCache.Get("song-a");
Check("未知收藏不伪造初始值", empty.Liked is null && !empty.Confirmed);
var original = likeCache.Observe("song-a", false, empty.Revision, at);
Check("真实图标建立已确认状态", original.Liked == false && original.Confirmed);
var intent = likeCache.Intent("song-a", true, at.AddMilliseconds(10));
Check("点击可以立即反馈但不冒充确认", intent.Liked == true && !intent.Confirmed);
var stale = likeCache.Observe("song-a", false, original.Revision, at.AddMilliseconds(100));
Check("旧查询不能覆盖刚点击的喜欢", stale.Liked == true && !stale.Confirmed);
var lagging = likeCache.Observe("song-a", false, intent.Revision, at.AddMilliseconds(150));
Check("客户端绘制滞后不会让红心闪回", lagging.Liked == true && !lagging.Confirmed);
var confirmed = likeCache.Observe("song-a", true, intent.Revision, at.AddMilliseconds(180));
Check("匹配的客户端状态立即确认", confirmed.Liked == true && confirmed.Confirmed);
var remove = likeCache.Intent("song-a", false, at.AddMilliseconds(200));
var corrected = likeCache.Observe("song-a", true, remove.Revision, at.AddSeconds(2));
Check("操作未生效时会按真实图标校正", corrected.Liked == true && corrected.Confirmed);
Check("不同歌曲的收藏互不污染", likeCache.Get("song-b").Liked is null);
Check("切回旧歌保留观察记录", likeCache.Get("song-a").Liked == true);
likeCache.Clear();
Check("播放器重启后清除陈旧状态", likeCache.Get("song-a").Liked is null);
Check("只有修饰键的无效快捷键不发送", !HotkeySender.Send("CTRL+ALT", out _));
Check("解析失败不发送部分按键", !HotkeySender.Send("CTRL+ALT+NOT_A_KEY", out _));
Check("目标变更后不投递已排队的快捷键", !HotkeySender.Send("CTRL+ALT+L", out _, () => false));
if (args.Contains("--probe"))
{
    // 只读 UIA；不恢复窗口、不投递点击、不改变收藏。
    for (int i = 0; i < 3; i++)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var probe = await Task.Run(() => LikeService.Probe("QQMusic.exe", settings))
                .WaitAsync(TimeSpan.FromSeconds(4));
            samples.Add(new { sample = i + 1, ms = timer.Elapsed.TotalMilliseconds,
                probe.Scope, probe.ButtonName, probe.Liked, probe.Count });
        }
        catch (TimeoutException)
        {
            samples.Add(new { sample = i + 1, timedOut = true, ms = timer.Elapsed.TotalMilliseconds });
            break;
        }
    }
}
Check("采样之间按播放时间连续前进", Math.Abs(PlaybackTimeline.Project(20, 100, true, 1, 0.24) - 20.24) < 0.001);
Check("暂停时歌词时间冻结", PlaybackTimeline.Project(20, 100, false, 1, 8) == 20);
Check("倍速播放使用实际速率", PlaybackTimeline.Project(20, 100, true, 1.5, 2) == 23);
Check("时间轴超过一分钟仍连续推算", PlaybackTimeline.Project(20, 300, true, 1, 80) == 100);
Check("播放到结尾不越界", PlaybackTimeline.Project(98, 100, true, 1, 8) == 100);
Check("无效时间轴不显示伪进度", PlaybackTimeline.Project(double.NaN, 100, true, 1, 1) == 0);
Check("同歌手同旧版按时长命中", LyricMatch.Score("起风了（旧版）", "买辣椒也用券", 312, "起风了 (旧版)", "买辣椒也用券", 312.913) >= 0);
Check("拒绝同名翻唱", LyricMatch.Score("起风了(旧版)", "等风来", 308, "起风了 (旧版)", "买辣椒也用券", 312.913) < 0);
Check("拒绝新旧版混用", LyricMatch.Score("起风了", "买辣椒也用券", 312, "起风了 (旧版)", "买辣椒也用券", 312.913) < 0);
Check("拒绝相似歌手名", LyricMatch.Score("起风了(旧版)", "买辣椒也用券1", 312, "起风了(旧版)", "买辣椒也用券", 312.913) < 0);
Check("拒绝不同长度录音", LyricMatch.Score("歌", "歌手", 330, "歌", "歌手", 310) < 0);
Check("多歌手次序不影响匹配", LyricMatch.Score("歌", "乙/甲", 100, "歌", "甲/乙", 100) >= 0);
var repeatedLines = LyricsService.ParseLrc("[offset:250]\n[00:10.50][01:10.500]示例甲\n[01:15.00]\n[01:20.00]示例乙");
Check("重复歌词的多个时间标签均解析", repeatedLines.Count == 4 && repeatedLines[0].StartMs == 10250 && repeatedLines[1].StartMs == 70250);
var lyricDoc = new LyricsService.LyricDoc("测试", "测试", repeatedLines, "测试");
Check("到时间前不提前显示下一句", LyricsService.Locate(lyricDoc, 10.24).Line is null);
Check("到精确时间切换歌词", LyricsService.Locate(lyricDoc, 10.25).Line?.Text == "示例甲");
Check("短句间保留上一句", LyricsService.Locate(lyricDoc, 75).Line?.Text == "示例甲");
Check("长空白进入间奏", LyricsService.Locate(lyricDoc, 78).Line is null);
Check("向后跳转按新时间定位", LyricsService.Locate(lyricDoc, 80).Line?.Text == "示例乙" && LyricsService.Locate(lyricDoc, 11).Line?.Text == "示例甲");
Check("无效播放时间不匹配歌词", LyricsService.Locate(lyricDoc, double.NaN).Line is null);
Check("坏时间标签不崩溃", LyricsService.ParseLrc("[999999999999999:23.5]错误\n[00:02.30]正确").Count == 1);
var surface = new System.Windows.Rect(-32000, -32000, 1655, 1082);
var nextButton = new QqLikeService.LayoutNode("下一曲", true, new(-30963, -31031, 63, 53));
var previousButton = new QqLikeService.LayoutNode("上一曲", true, new(-31116, -31031, 63, 53));
var heartButton = new QqLikeService.LayoutNode("我喜欢", true, new(-31556, -31003, 63, 53));
var mainMenu = new QqLikeService.LayoutNode("主菜单", true, new(-31911, -30996, 45, 45));
Check("最小化按QQ完整布局定位爱心", QqLikeService.ResolveHeart(new[] { heartButton, nextButton, previousButton, mainMenu }, surface) == heartButton.Bounds);
var shiftedHeart = heartButton with { Bounds = new(-31900, -31003, 63, 53) };
Check("同窗口切换页面重新读取爱心坐标", QqLikeService.ResolveHeart(new[] { shiftedHeart, nextButton, previousButton, mainMenu }, surface) == shiftedHeart.Bounds);
Check("从不把主菜单当爱心", QqLikeService.ResolveHeart(new[] { mainMenu, nextButton, previousButton }, surface) is null);
Check("同名候选不明确时不点击", QqLikeService.ResolveHeart(new[] { heartButton, shiftedHeart, nextButton, previousButton }, surface) is null);
Check("自动隐藏在窗口外的按钮不点击", QqLikeService.ResolveHeart(new[] { heartButton with { Bounds = new(-31556, -30880, 63, 53) }, nextButton, previousButton }, surface) is null);
Check("缺少播放控制锚点时不点击", QqLikeService.ResolveHeart(new[] { heartButton, nextButton }, surface) is null);
var rowHeart = heartButton with { Bounds = new(-31162, -31112, 49, 39) };
Check("列表悬停爱心不干扰当前歌曲收藏", QqLikeService.ResolveHeart(new[] { heartButton, rowHeart, nextButton, previousButton }, surface, new(-31652, -31052, 1289, 116)) == heartButton.Bounds);
using (var handler = new LyricFallbackHandler())
using (var client = new System.Net.Http.HttpClient(handler))
using (var lyrics = new LyricsService(client))
{
    string song = "模拟取词" + Guid.NewGuid().ToString("N");
    handler.Title = song;
    var doc = await lyrics.FetchAsync(song, "甲/乙", sourceAppId: "QQMusic.exe", durationSeconds: 258);
    Check("QQ主搜索空结果后使用备用搜索", handler.LegacyCalls == 1 && handler.ModernCalls == 1);
    Check("备用搜索仍校验录音后返回歌词", doc?.Lines.Count == 2 && doc.Source == "QQ音乐 LRC");
    var cachedDoc = await lyrics.FetchAsync(song, "甲/乙", sourceAppId: "QQMusic.exe", durationSeconds: 258);
    Check("成功歌词缓存避免重复网络查询", ReferenceEquals(doc, cachedDoc) && handler.LegacyCalls == 1);
}
var selector = new MediaSourceSelector();
using (var handler = new LyricFallbackHandler())
using (var cacheClient = new System.Net.Http.HttpClient(handler))
using (var bounded = new LyricsService(cacheClient))
{
    var prefix = "内存缓存测试" + Guid.NewGuid().ToString("N");
    for (int i = 0; i < 40; i++)
    {
        handler.Title = prefix + i;
        await bounded.FetchAsync(handler.Title, "甲/乙", sourceAppId: "QQMusic.exe", durationSeconds: 258);
    }
    var memory = (ConcurrentDictionary<string, LyricsService.LyricDoc>)typeof(LyricsService)
        .GetField("_memory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(bounded)!;
    Check("连续40首歌词的内存缓存保持上限", memory.Count <= 32);
    int searches = handler.ModernCalls;
    var reloaded = await bounded.FetchAsync(prefix + "0", "甲/乙", sourceAppId: "QQMusic.exe", durationSeconds: 258);
    Check("淘汰的歌词从本地缓存恢复而不重新联网", reloaded is not null && handler.ModernCalls == searches);
}
MediaSourceState Q(bool playing) => new("qq", playing, true);
MediaSourceState B(bool playing) => new("browser", playing, false);
MediaSourceState N(bool playing) => new("netease", playing, true);
Check("启动选择正在播放的视频", selector.Update(new[] { Q(false), B(true) }) == "browser");
Check("暂停视频不切到暂停的QQ", selector.Update(new[] { Q(false), B(false) }) == "browser");
Check("暂停后继续操作同一视频", selector.Update(new[] { Q(false), B(true) }) == "browser");
Check("QQ新开始播放才自动切换", selector.Update(new[] { Q(true), B(false) }) == "qq");
Check("新开始播放的视频可以接管已有QQ", selector.Update(new[] { Q(true), B(true) }) == "browser");
Check("视频暂停时后台正在播放的QQ不抢回", selector.Update(new[] { Q(true), B(false) }) == "browser");
selector.Select("browser");
Check("锁定视频时网易云新播放也不抢走", selector.Update(new[] { Q(true), B(false), N(true) }) == "browser");
Check("锁定期间稳定", selector.Update(new[] { Q(false), B(false), N(true) }) == "browser");
selector.Automatic();
Check("解除锁定仍保留当前暂停来源", selector.Update(new[] { Q(false), B(false), N(true) }) == "browser");
selector.Update(new[] { Q(false), B(false), N(false) });
Check("解除锁定后响应新播放", selector.Update(new[] { Q(false), B(false), N(true) }) == "netease");
selector.Select("browser");
Check("关闭锁定来源后自动恢复其他来源", selector.Update(new[] { Q(false), N(true) }) == "netease" && selector.LockedKey is null);
Check("所有来源关闭后清空", selector.Update(Array.Empty<MediaSourceState>()) is null);
selector.Update(new[] { Q(true), B(false) });
Check("控制操作过程中固定原来源", selector.Update(new[] { Q(true), B(true) }, "qq") == "qq");
Check("操作结束后不因旧播放状态突然跳源", selector.Update(new[] { Q(true), B(true) }) == "qq");
selector.Select("browser:2");
Check("同应用多个会话独立锁定", selector.Update(new[] { new MediaSourceState("browser:1", true, false), new MediaSourceState("browser:2", false, false) }) == "browser:2");
Check("网页来源不视为音乐播放器", !MediaSourceNames.IsMusic("msedge.exe") && !MediaSourceNames.IsMusic("chrome.exe"));

Console.WriteLine(JsonSerializer.Serialize(new {
    observedAt = DateTimeOffset.Now, passed = checks.Count - failures, total = checks.Count,
    checks, readOnlyProbeSamples = samples,
    scope = "单元/服务边界检查及可选只读状态探测；未发送真实播放或收藏指令。探测耗时不等于收藏完成耗时。"
}, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;

sealed class LyricFallbackHandler : System.Net.Http.HttpMessageHandler
{
    public string Title { get; set; } = "";
    public int LegacyCalls { get; private set; }
    public int ModernCalls { get; private set; }
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
    {
        string json;
        if (request.RequestUri!.AbsolutePath.Contains("client_search")) { LegacyCalls++; json = "{\"data\":{\"song\":{\"list\":[]}}}"; }
        else if (request.RequestUri.AbsolutePath.Contains("musicu"))
        {
            ModernCalls++;
            json = JsonSerializer.Serialize(new { req_1 = new { data = new { body = new { song = new { list = new[] {
                new { title = Title, singer = new[] { new { name = "甲" }, new { name = "乙" } }, interval = 258, mid = "test-recording" }
            } } } } } });
        }
        else json = JsonSerializer.Serialize(new { lyric = "[00:01.00]测试第一句\n[00:02.00]测试第二句" });
        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json) });
    }
}
