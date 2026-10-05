using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

namespace TaskbarMediaBar.Services;

/// <summary>一个字/词的起止时间（毫秒）。</summary>
public sealed record LyricWord(int StartMs, int EndMs, string Text);

/// <summary>一行歌词（含逐字时间轴，可能为空 = 只有整行时间）。</summary>
public sealed record LyricLine(int StartMs, string Text, List<LyricWord> Words)
{
    public int EndMs => Words.Count > 0 ? Words[^1].EndMs : StartMs + 4000;
}

/// <summary>
/// 逐字歌词。
///
/// 数据来源：优先使用正在播放的平台公开搜索与歌词接口（不需要登录）。
/// 网易云返回 yrc 字段时就是逐字（每字带起止时间），否则退回 lrc（行级）。
/// 取不到就什么都不显示，不影响播放控制。
///
/// 按来源、录音时长、歌名和艺人缓存，短暂失败会自动重试。
/// </summary>
public sealed class LyricsService : IDisposable
{
    private static readonly HttpClient SharedHttp = CreateClient();
    private readonly HttpClient Http;

    public LyricsService(HttpClient? http = null) => Http = http ?? SharedHttp;

    private readonly ConcurrentDictionary<string, LyricDoc> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _memoryOrder = new();
    private void Remember(string key, LyricDoc doc)
    {
        if (_memory.TryAdd(key, doc)) _memoryOrder.Enqueue(key);
        while (_memory.Count > 32 && _memoryOrder.TryDequeue(out var old)) _memory.TryRemove(old, out _);
    }
    private void RememberFailure(string key)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _failed)
            if (entry.Value <= now) _failed.TryRemove(entry.Key, out _);
        if (_failed.Count >= 128)
            foreach (var old in _failed.OrderBy(p => p.Value).Take(_failed.Count - 127)) _failed.TryRemove(old.Key, out _);
        _failed[key] = now.AddSeconds(12);
    }
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly CancellationTokenSource _lifetime = new();

    public sealed record LyricDoc(string Title, string Artist, List<LyricLine> Lines, string Source)
    {
        public bool IsEmpty => !Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text));
        public double DurationSeconds { get; init; }
        public string TrackId { get; init; } = "";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return client;
    }

    public bool TryGet(string title, string artist, out LyricDoc doc)
    {
        doc = null!;
        string key = LyricCacheKey(title, artist, "", 0);
        if (RecentlyFailed(key))
        {
            return false;
        }

        if (_memory.TryGetValue(key, out var cached))
        {
            doc = cached;
            return !cached.IsEmpty;
        }

        return false;
    }

    /// <summary>取歌词（带磁盘缓存）。失败返回 null，并记住别再重复试。</summary>
    public async Task<LyricDoc?> FetchAsync(string title, string artist, CancellationToken token = default,
        string sourceAppId = "", double durationSeconds = 0)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = linked.Token;
        string key = LyricCacheKey(title, artist, sourceAppId, durationSeconds);

        if (_memory.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (RecentlyFailed(key))
        {
            return null;
        }

        bool entered = false;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            if (_memory.TryGetValue(key, out cached))
            {
                return cached;
            }

            var fromDisk = LoadFromDisk(key);
            if (fromDisk is not null)
            {
                Remember(key, fromDisk);
                return fromDisk;
            }

            LyricDoc? doc = null;
            if (QqProgressSeek.Supports(sourceAppId))
            {
                try { doc = await FetchFromQqAsync(title, artist, durationSeconds, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Write("QQ歌词获取失败: " + ex.Message); }
            }
            doc ??= await FetchFromNetEaseAsync(title, artist, durationSeconds, token).ConfigureAwait(false);
            if (doc is null || doc.IsEmpty)
            {
                RememberFailure(key);
                Log.Write($"歌词: 取不到 『{title} - {artist}』");
                return null;
            }

            Remember(key, doc);
            SaveToDisk(key, doc);
            Log.Write($"歌词: 命中 『{doc.Title} - {doc.Artist}』 {doc.Lines.Count} 行（{doc.Source}）");
            return doc;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            RememberFailure(key);
            Log.Write("歌词获取失败: " + ex.Message);
            return null;
        }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    private bool RecentlyFailed(string key) => _failed.TryGetValue(key, out var retryAt)
        && DateTimeOffset.UtcNow < retryAt;

    private static string LyricCacheKey(string title, string artist, string source, double duration) =>
        MakeKey("lyrics-v2|" + source + "|" + Math.Round(duration).ToString(CultureInfo.InvariantCulture) + "|" + title, artist);

    private async Task<LyricDoc?> FetchFromQqAsync(string title, string artist, double duration, CancellationToken token)
    {
        var candidates = new List<(string Title, string Artist, string Mid, double Duration, double Score)>();
        string query = title + " " + Regex.Replace(artist, @"\s*[/&、,;；]\s*", " ");
        string alternateQuery = title + " " + Regex.Split(artist, @"[/&、,;；]")[0].Trim();
        string modernData = JsonSerializer.Serialize(new { comm = new { ct = 24, cv = 0 }, req_1 = new {
            method = "DoSearchForQQMusicDesktop", module = "music.search.SearchCgiService", param = new {
                grp = 1, num_per_page = 10, page_num = 1, query = alternateQuery, search_type = 0 } } });
        // 旧接口偶尔返回空列表；直接换 QQ 桌面搜索接口，仍按完整艺人和录音时长校验。
        foreach (string url in new[] {
            "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&new_json=1&p=1&n=10&w=" + Uri.EscapeDataString(query),
            "https://u.y.qq.com/cgi-bin/musicu.fcg?data=" + Uri.EscapeDataString(modernData) })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = new Uri("https://y.qq.com/");
                using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var search = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                JsonElement list;
                if (search.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("song", out var songs)
                    && songs.TryGetProperty("list", out list)) { }
                else if (search.RootElement.TryGetProperty("req_1", out var req) && req.TryGetProperty("data", out var modern)
                    && modern.TryGetProperty("body", out var body) && body.TryGetProperty("song", out songs)
                    && songs.TryGetProperty("list", out list)) { }
                else continue;
                foreach (var song in list.EnumerateArray())
                {
                    string name = song.GetProperty("title").GetString() ?? "";
                    string singers = string.Join("/", song.GetProperty("singer").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
                    double length = song.GetProperty("interval").GetDouble();
                    double score = LyricMatch.Score(name, singers, length, title, artist, duration);
                    if (score >= 0) candidates.Add((name, singers, song.GetProperty("mid").GetString() ?? "", length, score));
                }
                if (candidates.Count > 0) break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Write("QQ歌词搜索重试: " + ex.Message); }
        }
        foreach (var candidate in candidates.OrderByDescending(c => c.Score).Take(2))
        {
            using var lyricRequest = new HttpRequestMessage(HttpMethod.Get,
                "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?format=json&nobase64=1&songmid=" + Uri.EscapeDataString(candidate.Mid));
            lyricRequest.Headers.Referrer = new Uri("https://y.qq.com/");
            using var lyricResponse = await Http.SendAsync(lyricRequest, token).ConfigureAwait(false);
            lyricResponse.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await lyricResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            if (!result.RootElement.TryGetProperty("lyric", out var value)) continue;
            string lrc = WebUtility.HtmlDecode(value.GetString() ?? "");
            if (!lrc.Contains('['))
            {
                try { lrc = Encoding.UTF8.GetString(Convert.FromBase64String(lrc)); } catch (FormatException) { continue; }
            }
            var lines = ParseLrc(lrc);
            if (lines.Count >= 2) return new LyricDoc(candidate.Title, candidate.Artist, lines, "QQ音乐 LRC")
                { DurationSeconds = candidate.Duration, TrackId = candidate.Mid };
        }
        return null;
    }

    /// <summary>网络诊断：直接打一次搜索接口，返回状态码与响应片段。</summary>
    public static string ProbeNetwork(bool useProxy, string title, string artist)
    {
        try
        {
            var handler = new HttpClientHandler { UseProxy = useProxy };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");

            string query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            string url = "https://music.163.com/api/search/get/web?type=1&offset=0&limit=3&s=" + Uri.EscapeDataString(query);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = client.GetAsync(url).GetAwaiter().GetResult();
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            sw.Stop();

            string head = body.Length > 240 ? body[..240] : body;
            var sb = new StringBuilder();
            sb.AppendLine($"搜索: HTTP {(int)resp.StatusCode} 耗时 {sw.ElapsedMilliseconds}ms 长度 {body.Length}");
            sb.AppendLine("    " + head.Replace("\n", " "));

            // 再打一次歌词接口，看它到底返回什么
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("result", out var r)
                    && r.TryGetProperty("songs", out var songs)
                    && songs.GetArrayLength() > 0)
                {
                    var first = songs[0];
                    long id = first.GetProperty("id").GetInt64();
                    string name = first.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    sb.AppendLine($"    第一首: id={id} name={name}");

                    foreach (string lyricUrl in new[]
                             {
                                 $"https://music.163.com/api/song/lyric?id={id}&lv=1&tv=-1&yv=1&kv=1",
                                 $"https://music.163.com/api/song/lyric?os=pc&id={id}&lv=-1&kv=-1&tv=-1",
                             })
                    {
                        var lr = client.GetAsync(lyricUrl).GetAwaiter().GetResult();
                        string lb = lr.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        string lh = lb.Length > 300 ? lb[..300] : lb;
                        sb.AppendLine($"  歌词: HTTP {(int)lr.StatusCode} 长度 {lb.Length}");
                        sb.AppendLine("    " + lh.Replace("\n", " "));
                    }
                }
            }
            catch (Exception ex2)
            {
                sb.AppendLine("    歌词探测失败: " + ex2.Message);
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "异常: " + ex.GetType().Name + " / " + ex.Message;
        }
    }

    // ---------- 封面（给不上报 SMTC 的播放器兜底用） ----------

    /// <summary>按「歌名 + 艺人」搜封面。用于窗口标题兜底通道。</summary>
    public async Task<System.Windows.Media.Imaging.BitmapSource?> FetchCoverAsync(
        string title, string artist, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = linked.Token;
        string key = MakeKey("cover:" + title, artist);

        try
        {
            string cachePath = Path.Combine(CacheDir, key + ".img");
            if (File.Exists(cachePath))
            {
                byte[] cachedBytes = await File.ReadAllBytesAsync(cachePath, token).ConfigureAwait(false);
                var cached = Decode(cachedBytes);
                if (cached is not null)
                {
                    return cached;
                }
            }

            string query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            string searchUrl =
                "https://music.163.com/api/search/get/web?type=1&offset=0&limit=3&s=" + Uri.EscapeDataString(query);

            using var resp = await Http.GetAsync(searchUrl, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("songs", out var songs)
                || songs.GetArrayLength() == 0)
            {
                return null;
            }

            string? picUrl = null;
            long bestId = 0;
            double bestScore = -1;
            foreach (var song in songs.EnumerateArray())
            {
                string name = song.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                if (!song.TryGetProperty("id", out var songId))
                {
                    continue;
                }

                string candidateArtist = song.TryGetProperty("artists", out var artists)
                    ? string.Join("/", artists.EnumerateArray().Select(a => a.TryGetProperty("name", out var an) ? an.GetString() : "")) : "";
                double score = LyricMatch.Score(title, artist, 0, name, candidateArtist, 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = songId.GetInt64();
                    picUrl = song.TryGetProperty("album", out var selectedAlbum)
                        && selectedAlbum.TryGetProperty("picUrl", out var selectedPic) ? selectedPic.GetString() : null;
                }

                // 有些返回里直接带 picUrl
                if (songId.GetInt64() == bestId && song.TryGetProperty("album", out var album)
                    && album.TryGetProperty("picUrl", out var p)
                    && !string.IsNullOrEmpty(p.GetString()))
                {
                    picUrl = p.GetString();
                }
            }

            // 搜索接口通常不含 picUrl，要用歌曲详情接口拿
            if (picUrl is null && bestId != 0)
            {
                string detailUrl = $"https://music.163.com/api/song/detail/?id={bestId}&ids=%5B{bestId}%5D";
                using var detailResp = await Http.GetAsync(detailUrl, token).ConfigureAwait(false);
                if (detailResp.IsSuccessStatusCode)
                {
                    string detailJson = await detailResp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    using var detail = JsonDocument.Parse(detailJson);
                    if (detail.RootElement.TryGetProperty("songs", out var ds)
                        && ds.GetArrayLength() > 0
                        && ds[0].TryGetProperty("album", out var dAlbum)
                        && dAlbum.TryGetProperty("picUrl", out var dp))
                    {
                        picUrl = dp.GetString();
                    }
                }
            }

            if (string.IsNullOrEmpty(picUrl))
            {
                Log.Write($"封面: 『{title} - {artist}』没找到图片地址（bestScore={bestScore}）");
                return null;
            }

            byte[] bytes = await Http.GetByteArrayAsync(picUrl, token).ConfigureAwait(false);
            var bmp = Decode(bytes);
            if (bmp is null)
            {
                return null;
            }

            Directory.CreateDirectory(CacheDir);
            await File.WriteAllBytesAsync(cachePath, bytes, token).ConfigureAwait(false);
            Log.Write($"封面: 『{title} - {artist}』已获取");
            return bmp;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            Log.Write("封面获取失败: " + ex.Message);
            return null;
        }
    }

    private static System.Windows.Media.Imaging.BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            return CoverImage.Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    // ---------- 网络 ----------

    private async Task<LyricDoc?> FetchFromNetEaseAsync(string title, string artist, double duration, CancellationToken token)
    {
        string query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        string searchUrl =
            "https://music.163.com/api/search/get/web?type=1&offset=0&limit=5&s=" + Uri.EscapeDataString(query);

        using var searchResp = await Http.GetAsync(searchUrl, token).ConfigureAwait(false);
        if (!searchResp.IsSuccessStatusCode)
        {
            return null;
        }

        string searchJson = await searchResp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var search = JsonDocument.Parse(searchJson);

        if (!search.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("songs", out var songs)
            || songs.GetArrayLength() == 0)
        {
            return null;
        }

        // 按匹配度排序，前 3 名逐个试：有些候选是纯音乐/无歌词，试下一个
        var ranked = new List<(double Score, long Id, string Name, string Artist, double Duration)>();
        foreach (var song in songs.EnumerateArray())
        {
            string name = song.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var artists = new StringBuilder();
            if (song.TryGetProperty("artists", out var arr))
            {
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.TryGetProperty("name", out var an))
                    {
                        if (artists.Length > 0) artists.Append('/');
                        artists.Append(an.GetString());
                    }
                }
            }

            if (!song.TryGetProperty("id", out var idEl))
            {
                continue;
            }

            double length = song.TryGetProperty("duration", out var d) ? d.GetDouble() / 1000 : 0;
            double score = LyricMatch.Score(name, artists.ToString(), length, title, artist, duration);
            if (score >= 0) ranked.Add((score, idEl.GetInt64(), name, artists.ToString(), length));
        }

        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));

        foreach (var candidate in ranked.Take(3))
        {
            var lines = await TryFetchLinesAsync(candidate.Id, token).ConfigureAwait(false);
            if (lines.Count > 0)
            {
                string source = lines.Any(l => l.Words.Count > 0) ? "yrc(逐字)" : "lrc";
                Log.Write($"歌词: 命中 id={candidate.Id} 『{candidate.Name}』 {lines.Count} 行（{source}）");
                return new LyricDoc(candidate.Name, candidate.Artist, lines, source)
                    { DurationSeconds = candidate.Duration, TrackId = candidate.Id.ToString(CultureInfo.InvariantCulture) };
            }
        }

        return null;
    }

    /// <summary>两种接口形态都试一遍，取到像样的歌词就返回。</summary>
    private async Task<List<LyricLine>> TryFetchLinesAsync(long id, CancellationToken token)
    {
        foreach (string url in new[]
                 {
                     $"https://music.163.com/api/song/lyric?id={id}&lv=1&tv=-1&yv=1&kv=1",
                     $"https://music.163.com/api/song/lyric?os=pc&id={id}&lv=-1&kv=-1&tv=-1",
                 })
        {
            try
            {
                using var resp = await Http.GetAsync(url, token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                string yrc = GetNested(doc.RootElement, "yrc", "lyric") ?? "";
                string lrc = GetNested(doc.RootElement, "lrc", "lyric") ?? "";

                var lines = !string.IsNullOrWhiteSpace(yrc) ? ParseYrc(yrc) : new List<LyricLine>();
                if (lines.Count == 0)
                {
                    lines = ParseLrc(lrc);
                }

                // 空时间标签标记间奏，Locate 必须保留它们；仅过滤非空占位文本。
                lines = lines.Where(l => string.IsNullOrWhiteSpace(l.Text) || !IsPlaceholder(l.Text)).ToList();
                if (lines.Count(l => !string.IsNullOrWhiteSpace(l.Text)) >= 2)
                {
                    return lines;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"歌词请求失败({url}): {ex.Message}");
            }
        }

        return new List<LyricLine>();
    }

    /// <summary>过滤"纯音乐，请欣赏"这类占位行。</summary>
    private static bool IsPlaceholder(string text)
    {
        string t = text.Trim();
        return t.Length == 0
               || t.Contains("纯音乐", StringComparison.Ordinal)
               || t.Contains("请欣赏", StringComparison.Ordinal)
               || t.Contains("该歌曲暂无歌词", StringComparison.Ordinal);
    }

    /// <summary>
    /// 过滤制作信息行（作词/作曲/吉他/录音室/版权方…）。
    /// 只匹配明确字段及分隔符，普通对话歌词中的冒号必须保留。
    /// </summary>
    private static bool IsMetaLine(string text)
    {
        string t = text.Trim();
        if (t.Length == 0)
        {
            return false;
        }

        string[] prefixes =
        {
            "作词", "作曲", "编曲", "制作", "混音", "母带", "录音", "吉他", "贝斯", "鼓",
            "键盘", "和声", "弦乐", "监制", "出品", "发行", "统筹", "企划", "版权", "设计",
            "插画", "封面", "人声", "配唱", "音乐总监", "项目", "OP", "SP", "词", "曲",
        };

        foreach (var p in prefixes)
        {
            if (t.StartsWith(p, StringComparison.Ordinal) && t.Length > p.Length
                && (t[p.Length] is ':' or '：' || char.IsWhiteSpace(t[p.Length])))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetNested(JsonElement root, string section, string field)
    {
        if (root.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object
            && s.TryGetProperty(field, out var f))
        {
            return f.GetString();
        }

        return null;
    }

    /// <summary>行级 LRC： [mm:ss.xx]文本</summary>
    public static List<LyricLine> ParseLrc(string lrc)
    {
        var list = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return list;
        }

        var offsetMatch = Regex.Match(lrc, @"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase);
        int.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset);
        foreach (var raw in lrc.Split('\n'))
        {
            string line = raw.Trim();
            var head = Regex.Match(line, @"^(?:\[\d+:\d+(?:[.:]\d+)?\])+");
            if (!head.Success) continue;
            string text = line[head.Length..].Trim();
            if (IsMetaLine(text) || (text.Length > 0 && IsPlaceholder(text))) continue;
            foreach (Match m in Regex.Matches(head.Value, @"\[(\d+):(\d+)(?:[.:](\d+))?\]"))
            {
                if (!int.TryParse(m.Groups[1].Value, out int min) || min > 10000
                    || !int.TryParse(m.Groups[2].Value, out int sec) || sec >= 60) continue;
                string frac = m.Groups[3].Value;
                int ms = frac.Length == 0 ? 0 : int.Parse(frac.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
                long time = (long)min * 60000 + sec * 1000 + ms - offset;
                if (time <= int.MaxValue) list.Add(new LyricLine((int)Math.Max(0, time), text, new List<LyricWord>()));
            }
        }

        return list.OrderBy(l => l.StartMs).ToList();
    }

    /// <summary>
    /// 逐字 YRC 格式（网易云）：
    /// [行起始ms,行时长ms](字起始ms,字时长ms,0)字(charStart,charDur,0)字...
    /// </summary>
    private static List<LyricLine> ParseYrc(string yrc)
    {
        var list = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(yrc))
        {
            return list;
        }

        foreach (var raw in yrc.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var head = Regex.Match(line, @"^\[(\d+),(\d+)\]");
            if (!head.Success)
            {
                continue;
            }

            int lineStart = int.Parse(head.Groups[1].Value);

            // 拆出每个 "(start,dur,0)文字"
            var words = new List<LyricWord>();
            var text = new StringBuilder();
            foreach (Match m in Regex.Matches(line, @"\((\d+),(\d+),\d+\)([^\(]*)", RegexOptions.None))
            {
                int ws = int.Parse(m.Groups[1].Value);
                int wd = int.Parse(m.Groups[2].Value);
                string chunk = m.Groups[3].Value;
                if (chunk.Length == 0)
                {
                    continue;
                }

                words.Add(new LyricWord(ws, ws + wd, chunk));
                text.Append(chunk);
            }

            string full = text.ToString().Trim();
            if (full.Length == 0 || IsMetaLine(full) || IsPlaceholder(full))
            {
                continue;
            }

            list.Add(new LyricLine(lineStart, full, words));
        }

        return list.OrderBy(l => l.StartMs).ToList();
    }

    /// <summary>找当前时间对应的行；返回该行 + 已唱字符数（用于逐字高亮）。</summary>
    public static (LyricLine? Line, int SungChars) Locate(LyricDoc doc, double positionSeconds)
    {
        if (doc is null || doc.Lines.Count == 0 || !double.IsFinite(positionSeconds))
        {
            return (null, 0);
        }

        int ms = (int)(positionSeconds * 1000);
        LyricLine? current = null;
        int? blankStart = null;
        foreach (var line in doc.Lines)
        {
            if (line.StartMs <= ms)
            {
                if (string.IsNullOrWhiteSpace(line.Text)) blankStart ??= line.StartMs;
                else { current = line; blankStart = null; }
            }
            else
            {
                break;
            }
        }

        if (current is null || string.IsNullOrWhiteSpace(current.Text))
        {
            return (null, 0);
        }

        // Brief gaps keep the completed lyric; longer blank sections are instrumental.
        if (blankStart is { } gap && ms - gap > 3000) return (null, 0);

        // 超过 15 秒还没下一句认为是间奏
        if (ms - current.EndMs > 15000)
        {
            return (null, 0);
        }

        if (current.Words.Count == 0)
        {
            return (current, -1); // -1 = 无逐字信息，整行高亮
        }

        int sung = 0;
        foreach (var w in current.Words)
        {
            if (w.StartMs <= ms)
            {
                sung += w.Text.Length;
            }
            else
            {
                break;
            }
        }

        return (current, Math.Min(sung, current.Text.Length));
    }

    // ---------- 缓存 ----------

    private static string MakeKey(string title, string artist)
    {
        string raw = $"{title.Trim()}|{artist.Trim()}".ToLowerInvariant();
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant() + ".json";
    }

    private static string CacheDir => Path.Combine(Log.Dir, "lyrics");

    private static LyricDoc? LoadFromDisk(string key)
    {
        try
        {
            string path = Path.Combine(CacheDir, key);
            if (!File.Exists(path))
            {
                return null;
            }

            var doc = JsonSerializer.Deserialize<LyricDoc>(File.ReadAllText(path, Encoding.UTF8));
            if (doc is not null && doc.Lines.Count > 0)
            {
                return doc;
            }
        }
        catch
        {
            // 缓存坏了就当没有
        }

        return null;
    }

    private static void SaveToDisk(string key, LyricDoc doc)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(
                Path.Combine(CacheDir, key),
                JsonSerializer.Serialize(doc, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }

    private static int Similarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0;
        }

        a = a.ToLowerInvariant().Trim();
        b = b.ToLowerInvariant().Trim();
        if (a == b)
        {
            return 10;
        }

        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            return 6;
        }

        return 0;
    }

    public void Dispose() => _lifetime.Cancel();
}
