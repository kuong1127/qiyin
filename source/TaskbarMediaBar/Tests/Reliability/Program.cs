using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarMediaBar.Services;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
        Directory.CreateDirectory(args[0]);
        Environment.SetEnvironmentVariable("TMB_DATA_DIR", args[0]);
        var checks = new Dictionary<string, bool>();
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) =>
        {
            int result = 0;
            void Check(string name, bool passed)
            {
                checks[name] = passed;
                if (!passed) throw new InvalidOperationException(name);
            }
            try
            {
                BitmapSource? Cover(MediaSessionService media, string title) => (BitmapSource?)typeof(MediaSessionService)
                    .GetMethod("GetFallbackCover", Private)!.Invoke(media, new object[] { title, "测试艺人" });
                async Task Idle(MediaSessionService media)
                {
                    for (int i = 0; i < 100; i++)
                    {
                        if (!(bool)typeof(MediaSessionService).GetField("_fallbackCoverBusy", Private)!.GetValue(media)!) return;
                        await Task.Delay(10);
                    }
                    throw new TimeoutException("封面请求未完成");
                }
                using (var handler = new FakeHandler { FailNextSearch = true })
                using (var client = new HttpClient(handler))
                using (var media = new MediaSessionService(Array.Empty<string>(), lyrics: new LyricsService(client)))
                {
                    string song = "失败恢复" + Guid.NewGuid().ToString("N");
                    Check("首次封面失败立即返回占位，不阻塞", Cover(media, song) is null);
                    await Idle(media);
                    for (int i = 0; i < 10; i++) Cover(media, song);
                    Check("失败退避期间重复轮询不联网", handler.SearchCalls == 1);
                    typeof(MediaSessionService).GetField("_fallbackCoverRetryAt", Private)!.SetValue(media, DateTimeOffset.UtcNow.AddSeconds(-1));
                    Cover(media, song); await Idle(media);
                    Check("同歌短暂失败后可恢复封面", handler.SearchCalls == 2 && Cover(media, song) is not null);
                    int calls = handler.SearchCalls;
                    for (int i = 0; i < 10; i++) Cover(media, song);
                    Check("成功封面保持缓存", handler.SearchCalls == calls);
                }
                using (var handler = new FakeHandler { HoldNextSearch = true })
                using (var client = new HttpClient(handler))
                using (var media = new MediaSessionService(Array.Empty<string>(), lyrics: new LyricsService(client)))
                {
                    string first = "旧曲" + Guid.NewGuid().ToString("N"), next = "新曲" + Guid.NewGuid().ToString("N");
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    Cover(media, first);
                    for (int i = 0; i < 15; i++) Cover(media, first);
                    Check("慢封面请求立即返回，不阻塞UI", watch.ElapsedMilliseconds < 200);
                    Cover(media, next);
                    Check("切歌取消旧请求", handler.HeldToken.IsCancellationRequested);
                    Check("切歌与反复轮询只有单个在途请求", handler.SearchCalls == 1 && handler.MaximumActive == 1);
                    handler.Release.TrySetResult();
                    await Idle(media);
                    Check("过期结果不会写入新曲封面", typeof(MediaSessionService).GetField("_fallbackCover", Private)!.GetValue(media) is null);
                    Check("取消的封面请求不计为失败", (int)typeof(MediaSessionService).GetField("_fallbackCoverFailures", Private)!.GetValue(media)! == 0);
                    Cover(media, next); await Idle(media);
                    Check("新曲在旧请求退出后成功加载", Cover(media, next) is not null && handler.LastSearchTitle == next && handler.MaximumActive == 1);
                }
                using (var handler = new FakeHandler())
                using (var client = new HttpClient(handler))
                using (var media = new MediaSessionService(Array.Empty<string>(), lyrics: new LyricsService(client)))
                {
                    // 注入请求启动阶段的异常，验证外层 catch 也退避，不能立即重发。
                    string song = "启动异常" + Guid.NewGuid().ToString("N"), key = song + "|测试艺人";
                    var cancellation = new CancellationTokenSource(); cancellation.Dispose();
                    typeof(MediaSessionService).GetField("_fallbackKey", Private)!.SetValue(media, key);
                    typeof(MediaSessionService).GetField("_fallbackCoverRevision", Private)!.SetValue(media, 1);
                    typeof(MediaSessionService).GetField("_fallbackCoverBusy", Private)!.SetValue(media, true);
                    var fetch = (Task)typeof(MediaSessionService).GetMethod("FetchFallbackCoverAsync", Private)!
                        .Invoke(media, new object[] { key, song, "测试艺人", 1, cancellation })!;
                    await fetch;
                    var retryAt = (DateTimeOffset)typeof(MediaSessionService).GetField("_fallbackCoverRetryAt", Private)!.GetValue(media)!;
                    Check("封面启动异常同样安排退避", retryAt > DateTimeOffset.UtcNow
                        && (int)typeof(MediaSessionService).GetField("_fallbackCoverFailures", Private)!.GetValue(media)! == 1);
                    Cover(media, song);
                    Check("封面异常后轮询不立即重发", handler.SearchCalls == 0);
                }
                using (var handler = new FakeHandler { HoldNextSearch = true })
                using (var client = new HttpClient(handler))
                {
                    var media = new MediaSessionService(Array.Empty<string>(), lyrics: new LyricsService(client));
                    Cover(media, "退出取消" + Guid.NewGuid().ToString("N"));
                    media.Dispose();
                    Check("释放媒体服务取消在途封面请求", handler.HeldToken.IsCancellationRequested);
                    handler.Release.TrySetResult(); await Idle(media);
                    Check("释放后不重新开始封面请求", Cover(media, "其他曲目") is null && handler.SearchCalls == 1);
                }
                using (var handler = new FakeHandler())
                using (var client = new HttpClient(handler))
                using (var lyrics = new LyricsService(client))
                {
                    string song = "空白标签" + Guid.NewGuid().ToString("N");
                    var doc = await lyrics.FetchAsync(song, "测试艺人", sourceAppId: "cloudmusic.exe", durationSeconds: 30);
                    Check("网易云请求路径保留空时间标签", doc?.Lines.Count == 3 && doc.Lines[1].Text == "");
                    Check("网易云长空白按标签进入间奏", LyricsService.Locate(doc!, 6).Line is null);
                    Check("网易云短空白继续显示上一句", LyricsService.Locate(doc!, 4).Line?.Text == "第一句");
                }
                var lines = LyricsService.ParseLrc("[00:01]鼓起勇气向前走\n[00:02]词不达意的温柔\n[00:03]曲终人散以后\n"
                    + "[00:04]作词：甲\n[00:05]作曲:乙\n[00:06]编曲 丙\n[00:07]混音 : 丁");
                Check("歌词不因鼓词曲等字头被误删", lines.Count == 3 && lines[0].Text == "鼓起勇气向前走"
                    && lines[1].Text == "词不达意的温柔" && lines[2].Text == "曲终人散以后");
                Check("作词作曲编曲混音标签仍被过滤", lines.All(l => l.StartMs < 4000));
                var dialogue = LyricsService.ParseLrc("[00:01]你说：明天见\n[00:02]You say : see you tomorrow\n[00:03]时钟显示:十二点");
                Check("普通含中文或英文冒号歌词保留", dialogue.Count == 3 && dialogue[0].Text == "你说：明天见"
                    && dialogue[1].Text == "You say : see you tomorrow" && dialogue[2].Text == "时钟显示:十二点");
            }
            catch (Exception ex) { result = 1; File.WriteAllText(Path.Combine(args[0], "error.txt"), ex.ToString()); }
            finally
            {
                var report = JsonSerializer.Serialize(new { passed = checks.Count(c => c.Value), total = checks.Count, checks,
                    scope = "注入HTTP响应与纯逻辑检查；未联网、未初始化媒体会话、未操作用户播放器" }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(args[0], "checks.json"), report);
                Console.WriteLine(report);
                application.Shutdown(result);
            }
        };
        return application.Run();
    }
}

internal sealed class FakeHandler : HttpMessageHandler
{
    public bool FailNextSearch, HoldNextSearch;
    public int SearchCalls, MaximumActive;
    private int _active;
    public string LastSearchTitle = "";
    public CancellationToken HeldToken;
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        MaximumActive = Math.Max(MaximumActive, Interlocked.Increment(ref _active));
        try
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("search"))
            {
                SearchCalls++;
                string query = Uri.UnescapeDataString(request.RequestUri.Query.Split("&s=")[1]);
                LastSearchTitle = query[..query.LastIndexOf(" 测试艺人", StringComparison.Ordinal)];
                if (HoldNextSearch) { HoldNextSearch = false; HeldToken = token; await Release.Task; }
                if (FailNextSearch) { FailNextSearch = false; return new(HttpStatusCode.ServiceUnavailable); }
                return Json(new { result = new { songs = new[] { new { id = 12, name = LastSearchTitle,
                    artists = new[] { new { name = "测试艺人" } }, duration = 30000,
                    album = new { picUrl = "https://test.invalid/cover.png" } } } } });
            }
            if (path.Contains("lyric")) return Json(new { lrc = new { lyric = "[00:01]第一句\n[00:02]\n[00:20]第二句" } });
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = new MemoryStream(); encoder.Save(image);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(image.ToArray()) };
        }
        finally { Interlocked.Decrement(ref _active); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value)) };
}
