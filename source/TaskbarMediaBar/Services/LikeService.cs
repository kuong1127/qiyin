using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using TaskbarMediaBar.Interop;

namespace TaskbarMediaBar.Services;

public sealed class LikeResult
{
    public bool Ok { get; init; }
    public bool Verified { get; init; }
    public bool Optimistic { get; init; }
    public string Message { get; init; } = "";
    public string ButtonName { get; init; } = "";
    public bool? LikedAfter { get; init; }
    public string Tier { get; init; } = "";
    public int CandidateCount { get; init; }
}

/// <summary>只读探测结果。Liked 为 null 表示读不到状态（不猜）。</summary>
public sealed record LikeProbe(string ButtonName, bool? Liked, int? FavoriteCount, int Count, string Scope, bool Confirmed = true);

/// <summary>
/// 点赞（爱心）。系统 SMTC 没有"点赞"命令，只能操作播放器自己的 UI。
///
/// QQ音乐单独走后台添加喜欢；以下 UI 点击只用于其他兼容播放器。
///  2. 播放条按钮的标签会随状态变化：未收藏='我喜欢'，已收藏='取消喜欢'
///     （所以「取消*」不能一律排除——那是同一个按钮的另一种状态）。
///  3. 全窗口按名字找会撞上「收藏歌单」这类干扰项 —— 改成以「下一曲」为锚点，
///     只在播放条容器内部挑按钮。
///
/// 状态信号（用于校验"到底生效没有"）：
///  · 播放条按钮自己的标签（我喜欢 ↔ 取消喜欢）
///  · 左侧导航「喜欢·199」里的歌曲数（点赞成功 ±1）
/// </summary>
public static class LikeService
{
    private static readonly string[] ExcludeWords =
    {
        "歌单", "列表", "批量", "更多", "分享", "下载", "MV", "专辑", "歌手",
        "评论", "队列", "本地", "文件夹", "电台", "排行榜", "购物",
    };

    private static readonly string[] TransportAnchors = { "下一曲", "下一首", "上一曲", "上一首" };

    /// <summary>记住这个应用的 InvokePattern 是不是摆设（QQ音乐 就是），下次直接跳过省 ~1.5 秒。</summary>
    private static bool _invokeUseless;

    /// <summary>
    /// 点击位置缓存：同一首歌反复点爱心是最常见操作，窗口没动就直接复用，
    /// 省掉"全量扫描选靶 + 解析坐标"（冷启动各要 2~4 秒）。
    /// </summary>
    private sealed record ClickCache(IntPtr Window, NativeMethods.RECT WindowRect,
        AutomationElement Target, ClickTarget Click, DateTime At);
    private static ClickCache? _cache;
    private sealed record TargetCache(IntPtr Window, AutomationElement Target, DateTime At);
    private static TargetCache? _targetCache;
    private static int _toggleBusy;

    private static bool TryReuseTarget(IntPtr hwnd, out AutomationElement target, out string name)
    {
        target = null!;
        name = "";
        var cache = Volatile.Read(ref _targetCache);
        if (cache is null || cache.Window != hwnd || (DateTime.Now - cache.At).TotalMinutes > 10)
            return false;
        name = SafeName(cache.Target);
        if (name is not ("我喜欢" or "喜欢" or "取消喜欢" or "取消收藏" or "收藏" or "红心"))
            return false;
        target = cache.Target;
        return true;
    }

    private static bool TryReuseCache(IntPtr hwnd, out AutomationElement target, out ClickTarget click, out string name)
    {
        target = null!;
        click = null!;
        name = "";

        var cache = Volatile.Read(ref _cache);
        if (cache is null || (DateTime.Now - cache.At).TotalMinutes > 10)
        {
            return false;
        }

        if (cache.Window != hwnd)
        {
            return false;
        }

        // 窗口位置/尺寸变了就作废（拖过窗口、改过大小）
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)
            || rect.Left != cache.WindowRect.Left || rect.Top != cache.WindowRect.Top
            || rect.Width != cache.WindowRect.Width || rect.Height != cache.WindowRect.Height)
        {
            return false;
        }

        // 元素可能已失效，读一次名字验活
        string verify = SafeName(cache.Target);
        if (verify is not ("我喜欢" or "喜欢" or "取消喜欢" or "取消收藏" or "收藏" or "红心"))
        {
            return false;
        }

        target = cache.Target;
        click = cache.Click;
        name = verify;
        Log.Write($"爱心: 复用上次点击位置（{verify}）");
        return true;
    }

    private static void SaveCache(IntPtr hwnd, AutomationElement target, ClickTarget click, string name)
    {
        try
        {
            if (NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                Volatile.Write(ref _cache, new ClickCache(hwnd, rect, target, click, DateTime.Now));
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>把窗口状态还原（原来最小化就再最小化回去）。</summary>
    private static void RestoreWindowState(IntPtr hwnd, bool wasIconic, bool wasVisible)
    {
        try
        {
            if (wasIconic && !NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
            }
            else if (!wasVisible && NativeMethods.IsWindowVisible(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            }
        }
        catch
        {
            // ignore
        }
    }

    private sealed record Signals(bool? LikedSelf, int? FavoriteCount)
    {
        public bool DiffersFrom(Signals other)
        {
            if (LikedSelf.HasValue && other.LikedSelf.HasValue && LikedSelf != other.LikedSelf)
            {
                return true;
            }

            return FavoriteCount.HasValue && other.FavoriteCount.HasValue && FavoriteCount != other.FavoriteCount;
        }
    }

    // ---------- 只读探测 ----------

    public static LikeProbe Probe(string sourceAppId, AppSettings settings, bool verbose = false)
    {
        if (MatchesApp("QQMusic", sourceAppId)) return QqLikeService.Probe(verbose);
        if (MatchesApp("cloudmusic", sourceAppId) || MatchesApp("网易云音乐", sourceAppId))
        {
            var state = NeteaseWindowState.Read();
            return new LikeProbe(state.Liked is true ? "取消喜欢" : state.Liked is false ? "我喜欢" : "",
                state.Liked, null, state.Liked.HasValue ? 1 : 0,
                state.Fresh ? "网易云播放条图标" : state.Confirmed ? "网易云上次观察值" : "网易云本地点击反馈", state.Confirmed);
        }
        if (settings.FallbackApps.Any(a => MatchesApp(a, sourceAppId)))
            return new LikeProbe("", null, null, 0, "快捷键通道不提供收藏状态");

        IntPtr hwnd = FindTargetWindow(sourceAppId, out _, allowRestore: false);
        if (hwnd == IntPtr.Zero)
        {
            return new LikeProbe("", null, null, 0, "无窗口");
        }

        // 最小化时不能缓存屏幕坐标，但播放条的 UIA 对象仍可读取和复用。
        // 把对象缓存与坐标缓存分离，避免隐藏窗口每次都重新全树定位。
        if (!verbose && TryReuseTarget(hwnd, out var warmTarget, out string warmName))
            return new LikeProbe(warmName, warmName.StartsWith("取消", StringComparison.Ordinal),
                null, 1, "按钮缓存");

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null)
            {
                return new LikeProbe("", null, null, 0, "无根元素");
            }

            AutomationElement? pick;
            string scope;
            int candidates;
            if (!verbose && TryReuseCache(hwnd, out var cached, out _, out _))
            {
                pick = cached;
                scope = "缓存";
                candidates = 1;
            }
            else pick = PickBestScoped(root, settings, out scope, out candidates);
            var signals = ReadSignals(root, pick, includeCount: verbose);
            if (pick is not null)
                Volatile.Write(ref _targetCache, new TargetCache(hwnd, pick, DateTime.Now));

            // 顺手预热点击位置缓存：这样用户第一次点爱心也是亚秒级
            if (pick is not null
                && NativeMethods.IsWindowVisible(hwnd)
                && !NativeMethods.IsIconic(hwnd))
            {
                try
                {
                    var click = UiaClicker.Resolve(pick, hwnd);
                    if (click is not null && !click.UiaCoordsSuspect)
                    {
                        SaveCache(hwnd, pick, click, SafeName(pick));
                    }
                }
                catch
                {
                    // 预热失败不影响主流程
                }
            }

            return new LikeProbe(
                pick is null ? "" : SafeName(pick),
                signals.LikedSelf,
                signals.FavoriteCount,
                candidates,
                scope);
        }
        catch (Exception ex)
        {
            Log.Write("Probe 失败: " + ex.Message);
            return new LikeProbe("", null, null, 0, "异常");
        }
    }

    // ---------- 点击 ----------

    public static LikeResult Toggle(string sourceAppId, AppSettings settings,
        Func<bool>? isCurrent = null, bool? desiredState = null)
    {
        if (Interlocked.CompareExchange(ref _toggleBusy, 1, 0) != 0)
            return new LikeResult { Message = "上一条收藏指令仍在处理中" };
        var elapsed = Stopwatch.StartNew();
        try { return ToggleCore(sourceAppId, settings, isCurrent, desiredState); }
        finally
        {
            Volatile.Write(ref _toggleBusy, 0);
            Log.Write($"收藏总耗时: {elapsed.ElapsedMilliseconds} ms");
        }
    }

    private static LikeResult ToggleCore(string sourceAppId, AppSettings settings,
        Func<bool>? isCurrent, bool? desiredState)
    {
        bool Current() => isCurrent?.Invoke() != false;
        if (!Current()) return new LikeResult { Message = "歌曲已切换，本次操作已取消" };
        if (MatchesApp("QQMusic", sourceAppId)) return QqLikeService.Toggle(Current, desiredState);
        // 播放器没有 SMTC、也没有 UIA 树时（网易云），走它的全局快捷键收藏。
        // 网易云默认「喜欢歌曲」= Ctrl+Alt+L（需在设置里勾选"启用全局快捷键"）。
        var profile = settings.FindHotkeyProfile(sourceAppId);
        if ((MatchesApp("cloudmusic", sourceAppId) || MatchesApp("网易云音乐", sourceAppId))
            && !string.IsNullOrWhiteSpace(profile?.Like))
            return ToggleNetease(profile.Like, Current, desiredState);
        if (!string.IsNullOrWhiteSpace(profile?.Like) && settings.FallbackApps.Any(a => MatchesApp(a, sourceAppId)))
        {
            if (!NativeMethods.IsWindow(NativeMethods.FindMainWindow(AppName(sourceAppId))))
            {
                return new LikeResult { Ok = false, Message = "播放器没在运行" };
            }

            if (!Current()) return new LikeResult { Message = "歌曲已切换，本次操作已取消" };
            if (HotkeySender.Send(profile.Like, out string hkErr))
            {
                Log.Write($"爱心: 走快捷键 {profile.Like}（{sourceAppId}）");
                return new LikeResult
                {
                    Ok = true,
                    Verified = false,
                    Tier = "全局快捷键 " + profile.Like,
                    Message = "已发送喜欢切换指令，状态待确认",
                };
            }

            return new LikeResult { Ok = false, Message = "快捷键发送失败: " + hkErr };
        }

        IntPtr hwnd = FindTargetWindow(sourceAppId, out string how, allowRestore: true);
        if (hwnd == IntPtr.Zero)
        {
            return new LikeResult { Ok = false, Message = "播放器窗口没找到" };
        }

        bool wasIconic = NativeMethods.IsIconic(hwnd);
        bool wasVisible = NativeMethods.IsWindowVisible(hwnd);
        string originalTitle = NativeMethods.ReadWindowTitle(hwnd);
        bool SameTrack() => Current() && NativeMethods.ReadWindowTitle(hwnd) == originalTitle;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null)
            {
                return new LikeResult { Ok = false, Message = "无法访问播放器界面" };
            }

            // 一、先处理窗口状态：最小化/隐藏时 UIA 只报"最小化图标"坐标，而且 UI 操作会被忽略。
            //     先恢复窗口，后面只需要选一次靶（少一次全量 UIA 扫描）。
            if (wasIconic || !wasVisible)
            {
                Log.Write("爱心: 窗口不可见/最小化 → 先恢复窗口");
                if (RestoreWindow(sourceAppId, out string restoreHow))
                {
                    IntPtr fresh = FindTargetWindow(sourceAppId, out _, allowRestore: false);
                    if (fresh != IntPtr.Zero)
                    {
                        hwnd = fresh;
                    }

                    var root2 = AutomationElement.FromHandle(hwnd);
                    if (root2 is not null)
                    {
                        root = root2;
                    }

                    Log.Write($"爱心: 窗口已恢复（{restoreHow}）");
                }
            }

            // 二、选靶：优先复用上次的位置（同歌曲反复点击时几乎瞬时）
            string scope;
            int candidates;
            AutomationElement? target;
            string buttonName;
            var clickTarget = default(ClickTarget);

            if (TryReuseCache(hwnd, out var cachedTarget, out var cachedClick, out string cachedName))
            {
                target = cachedTarget;
                clickTarget = cachedClick;
                buttonName = cachedName;
                scope = "缓存";
                candidates = 1;
            }
            else
            {
                if (TryReuseTarget(hwnd, out var warm, out string warmName))
                {
                    target = warm;
                    scope = "按钮缓存";
                    candidates = 1;
                }
                else target = PickBestScoped(root, settings, out scope, out candidates);
                if (target is null)
                {
                    Log.Write($"爱心: 没命中候选（范围={scope}）");
                    RestoreWindowState(hwnd, wasIconic, wasVisible);
                    return new LikeResult
                    {
                        Ok = false,
                        CandidateCount = candidates,
                        Message = candidates == 0
                            ? "播放器里找不到「我喜欢」按钮（试试把 QQ音乐 窗口打开）"
                            : $"候选 {candidates} 个但都没匹配（范围={scope}）",
                    };
                }

                buttonName = SafeName(target);
                clickTarget = UiaClicker.Resolve(target, hwnd);
                if (clickTarget is not null)
                {
                    SaveCache(hwnd, target, clickTarget, buttonName);
                }
            }

            var before = ReadSignals(root, target, includeCount: false);
            Log.Write($"爱心: 候选={candidates} 范围={scope} 选中='{buttonName}' | 窗口恢复={wasIconic || !wasVisible} | 前={Describe(before)}");

            if (!SameTrack()) return new LikeResult { Message = "歌曲已切换，本次操作已取消" };
            if (desiredState.HasValue && before.LikedSelf == desiredState)
                return new LikeResult { Ok = true, Verified = true, LikedAfter = desiredState,
                    Message = desiredState.Value ? "已在「我喜欢」中" : "已取消喜欢" };

            // 本项目已确认 QQ 的 Invoke 是空实现。首次点击即用消息通道，
            // 一次请求只发送一次点击；回读超时不再追加点击，避免延迟确认时反向取消。
            if (MatchesApp("QQMusic", sourceAppId))
            {
                if (clickTarget is null || clickTarget.UiaCoordsSuspect)
                    return new LikeResult { Message = "暂时无法定位喜欢按钮，请打开播放器后重试" };
                var stage = Stopwatch.StartNew();
                bool sent = UiaClicker.ClickByPostedMessage(clickTarget);
                long dispatchMs = stage.ElapsedMilliseconds;
                // 消息返回失败也可能已经处理部分消息；始终读实际状态，不把投递结果当作收藏结果。
                var confirmed = WaitForChange(hwnd, settings, before, target, SameTrack);
                Log.Write($"收藏消息通道: 投递={dispatchMs} ms 确认={stage.ElapsedMilliseconds - dispatchMs} ms");
                if (!SameTrack()) return new LikeResult { Ok = sent, Message = "歌曲已切换，收藏结果未应用到新歌" };
                bool changed = before.DiffersFrom(confirmed);
                bool? liked = changed ? ResolveLiked(before, confirmed) : null;
                return new LikeResult { Ok = sent || changed, Verified = changed, LikedAfter = liked,
                    Tier = "QQ 异步鼠标消息", ButtonName = buttonName, CandidateCount = candidates,
                    Message = changed ? liked == true ? "已加入「我喜欢」" : "已取消喜欢"
                        : sent ? "已发送，收藏状态待确认" : "播放器未及时响应，请稍后重试" };
            }

            string tier;
            Signals after;

            // 三、第 1 手：UIA Invoke（坐标可疑时必然是空实现；已确认过无效的应用直接跳过）
            if (_invokeUseless || clickTarget is null || clickTarget.UiaCoordsSuspect)
            {
                tier = _invokeUseless ? "(已知 Invoke 无效，跳过)" : "(跳过 Invoke)";
                after = before;
            }
            else
            {
                Invoke(target, out string invokeHow);
                tier = "UIA-" + invokeHow;
                after = WaitForChange(hwnd, settings, before, target);
            }

            // 四、第 2 手：鼠标消息（不抢焦点）
            if (SignalsUsable(before, after) && !before.DiffersFrom(after) && clickTarget is not null)
            {
                if (UiaClicker.ClickByMessage(clickTarget))
                {
                    tier = "鼠标消息";
                    after = WaitForChange(hwnd, settings, before, target);
                    if (before.DiffersFrom(after))
                    {
                        // 记下来：这个应用的 Invoke 是摆设，下次别再白试
                        _invokeUseless = true;
                    }
                }
            }

            // 五、第 3 手：真实鼠标点击（会短暂切前台）
            if (SignalsUsable(before, after) && !before.DiffersFrom(after) && clickTarget is not null)
            {
                Log.Write("爱心: 升级为真实鼠标点击");
                if (UiaClicker.ClickByMouse(clickTarget))
                {
                    tier = "真实鼠标";
                    after = WaitForChange(hwnd, settings, before, target);
                }
            }

            // 六、还原窗口状态，别打扰用户
            RestoreWindowState(hwnd, wasIconic, wasVisible);

            bool verified = before.DiffersFrom(after);
            bool? likedAfter = verified ? ResolveLiked(before, after) : null;

            string message = verified
                ? likedAfter switch
                {
                    true => $"已收藏到「我喜欢」（{tier}）",
                    false => $"已取消收藏（{tier}）",
                    _ => $"已完成（{tier}）",
                }
                : $"已点击「{buttonName}」（{tier}，状态未能确认）";

            Log.Write($"爱心: 生效={verified} → {message} | 前={Describe(before)} 后={Describe(after)}");

            return new LikeResult
            {
                Ok = true,
                Verified = verified,
                ButtonName = buttonName,
                LikedAfter = likedAfter,
                Tier = tier,
                CandidateCount = candidates,
                Message = message,
            };
        }
        catch (Exception ex)
        {
            Log.Write("爱心异常: " + ex);
            return new LikeResult { Ok = false, Message = "点赞失败，详见日志" };
        }
        finally { RestoreWindowState(hwnd, wasIconic, wasVisible); }
    }

    /// <summary>两个信号里至少有一个前后都能读到，才谈得上"确认没生效"。</summary>
    private static bool SignalsUsable(Signals before, Signals after) =>
        (before.LikedSelf.HasValue && after.LikedSelf.HasValue)
        || (before.FavoriteCount.HasValue && after.FavoriteCount.HasValue);

    private static string Describe(Signals s) =>
        $"按钮状态={(s.LikedSelf switch { true => "已收藏", false => "未收藏", null => "?" })} 我喜欢数量={s.FavoriteCount?.ToString() ?? "?"}";

    /// <summary>网易云只发送全局快捷键；不恢复窗口，不为读取图标阻塞点击。</summary>
    private static LikeResult ToggleNetease(string hotkey, Func<bool> current, bool? desired)
    {
        var hwnd = NativeMethods.FindMainWindow("cloudmusic");
        if (hwnd == IntPtr.Zero) return new LikeResult { Message = "网易云音乐未运行" };
        string title = NativeMethods.ReadWindowTitle(hwnd);
        bool SameTrack() => current() && NativeMethods.ReadWindowTitle(hwnd) == title;
        if (!SameTrack()) return new LikeResult { Message = "歌曲已切换，本次操作已取消" };
        var before = NeteaseWindowState.Peek();
        bool want = desired ?? !(before.Liked ?? false);
        if (before.Identity == title && before.Liked == want)
            return new LikeResult { Ok = true, Verified = before.Confirmed, Optimistic = !before.Confirmed,
                LikedAfter = want, Tier = "网易云本地状态", Message = before.Confirmed
                    ? want ? "已在「我喜欢」中" : "已取消喜欢" : "已发送喜欢指令" };
        if (!SameTrack()) return new LikeResult { Message = "歌曲已切换，本次操作已取消" };
        if (!HotkeySender.Send(hotkey, out _, SameTrack)) return new LikeResult { Message = "网易云喜欢指令未能发送" };
        if (!SameTrack()) return new LikeResult { Ok = true, Message = "歌曲已切换，旧收藏结果已忽略" };
        NeteaseWindowState.RememberIntent(title, want);
        return new LikeResult { Ok = true, Optimistic = true, LikedAfter = want,
            Tier = "网易云后台快捷键", Message = want ? "已发送喜欢" : "已发送取消喜欢" };
    }

    private static Signals WaitForChange(IntPtr hwnd, AppSettings settings, Signals before,
        AutomationElement? target, Func<bool>? isCurrent = null)
    {
        Signals last = before;
        int[] delays = { 0, 25, 35, 50, 80, 120, 180, 250, 300, 400 };
        for (int i = 0; i < delays.Length; i++)
        {
            if (isCurrent?.Invoke() == false) return before;
            if (delays[i] > 0) Thread.Sleep(delays[i]);

            // 快路径：按钮标签翻转（一次属性读取，毫秒级）
            if (target is not null && before.LikedSelf.HasValue)
            {
                string name = SafeName(target);
                if (name.Length > 0)
                {
                    bool liked = name.StartsWith("取消", StringComparison.Ordinal);
                    if (liked != before.LikedSelf.Value)
                    {
                        return new Signals(liked, before.FavoriteCount);
                    }
                }
            }

            // 慢查询（全树扫描，秒级）只在最后一轮兜底一次，别每轮都做
            if (i < delays.Length - 1 || !before.FavoriteCount.HasValue)
            {
                continue;
            }

            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root is null)
                {
                    continue;
                }

                int? count = ReadFavoriteCount(root);
                last = new Signals(before.LikedSelf, count);
                if (before.DiffersFrom(last))
                {
                    return last;
                }
            }
            catch
            {
                // 元素可能失效
            }
        }

        return last;
    }

    private static bool? ResolveLiked(Signals before, Signals after)
    {
        if (before.LikedSelf.HasValue && after.LikedSelf.HasValue && before.LikedSelf != after.LikedSelf)
        {
            return after.LikedSelf;
        }

        if (before.FavoriteCount.HasValue && after.FavoriteCount.HasValue
            && before.FavoriteCount != after.FavoriteCount)
        {
            return after.FavoriteCount > before.FavoriteCount;
        }

        return null;
    }

    private static bool Invoke(AutomationElement el, out string how)
    {
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object ip))
            {
                ((InvokePattern)ip).Invoke();
                how = "Invoke";
                return true;
            }

            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sp))
            {
                ((SelectionItemPattern)sp).Select();
                how = "Select";
                return true;
            }

            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out object tp))
            {
                ((TogglePattern)tp).Toggle();
                how = "Toggle";
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Invoke 失败: " + ex.Message);
        }

        how = "无可用 pattern";
        return false;
    }

    /// <summary>
    /// 状态信号。热路径只读「播放条按钮自己的标签」（一次属性读取，毫秒级）；
    /// 「我喜欢数量」是慢查询，只在诊断模式（verbose）下才读。
    /// </summary>
    private static Signals ReadSignals(AutomationElement root, AutomationElement? target, bool includeCount)
    {
        bool? likedSelf = null;
        if (target is not null)
        {
            string name = SafeName(target);
            if (name.Length > 0)
            {
                likedSelf = name.StartsWith("取消", StringComparison.Ordinal);
            }
        }

        return new Signals(likedSelf, includeCount ? ReadFavoriteCount(root) : null);
    }

    /// <summary>读左侧导航「喜欢·199」里的数字（点赞成功 ±1）。这是慢查询，只在校验需要时才调。</summary>
    private static int? ReadFavoriteCount(AutomationElement root)
    {
        try
        {
            var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            int guard = 0;
            foreach (AutomationElement el in all)
            {
                if (++guard > 3000)
                {
                    break;
                }

                string name = SafeName(el);
                if (name.Length == 0)
                {
                    continue;
                }

                var m = Regex.Match(name, @"^我?喜欢\s*[·・:：]?\s*(\d+)$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
                {
                    return n;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("读我喜欢数量失败: " + ex.Message);
        }

        return null;
    }

    private static List<AutomationElement> CollectButtons(AutomationElement root, out int count)
    {
        var list = new List<AutomationElement>();
        count = 0;
        try
        {
            var buttons = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

            foreach (AutomationElement b in buttons)
            {
                count++;
                list.Add(b);
            }
        }
        catch (Exception ex)
        {
            Log.Write("枚举按钮失败: " + ex.Message);
        }

        return list;
    }

    /// <summary>
    /// 选定要点的按钮。性能关键：**不做全量按钮扫描**（60+ 次跨进程读名字会花 20 秒），
    /// 而是用 UIA 的精确名字条件一次筛出候选（1 次调用），再按锚点（下一曲）限定到播放条。
    /// </summary>
    private static AutomationElement? PickBestScoped(
        AutomationElement root,
        AppSettings settings,
        out string scope,
        out int candidateCount)
    {
        candidateCount = 0;
        scope = "无候选";

        // 一次 FindAll 同时捞出「锚点 + 候选」：跨进程 UIA 调用很贵，能少一次是一次
        AutomationElementCollection found;
        try
        {
            found = root.FindAll(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    BuildNameCondition(settings)));
        }
        catch (Exception ex)
        {
            Log.Write("查找喜欢按钮失败: " + ex.Message);
            return null;
        }

        AutomationElement? anchor = null;
        var likes = new List<AutomationElement>();

        foreach (AutomationElement el in found)
        {
            string name = SafeName(el);
            if (name.Length == 0)
            {
                continue;
            }

            if (TransportAnchors.Contains(name))
            {
                anchor ??= el;
                continue;
            }

            if (Score(name, settings) > 0)
            {
                likes.Add(el);
            }
        }

        candidateCount = likes.Count;
        if (likes.Count == 0)
        {
            return null;
        }

        // 有锚点就优先取"跟锚点同一个容器"的候选（播放条里那个），
        // 避免命中详情页/导航里的同名按钮
        if (anchor is not null && likes.Count > 1)
        {
            var barParent = UiaClicker.GetParentAny(anchor);
            if (barParent is not null)
            {
                var inBar = likes.Where(l => IsWithin(l, barParent)).ToList();
                if (inBar.Count > 0)
                {
                    scope = "播放条";
                    return Best(inBar, settings);
                }
            }
        }

        scope = likes.Count == 1 ? "唯一候选" : "全窗口";
        return Best(likes, settings);
    }

    private static OrCondition BuildNameCondition(AppSettings settings)
    {
        var names = new List<string> { "我喜欢", "喜欢", "取消喜欢", "取消收藏", "收藏", "红心" };
        names.AddRange(TransportAnchors);
        foreach (var custom in settings.LikeButtonNames)
        {
            if (!string.IsNullOrWhiteSpace(custom))
            {
                names.Add(custom.Trim());
            }
        }

        var conditions = names
            .Distinct(StringComparer.Ordinal)
            .Select(n => (Condition)new PropertyCondition(AutomationElement.NameProperty, n))
            .ToArray();

        return new OrCondition(conditions);
    }

    private static AutomationElement? Best(System.Collections.IEnumerable elements, AppSettings settings)
    {
        AutomationElement? best = null;
        int bestScore = 0;
        foreach (object o in elements)
        {
            if (o is not AutomationElement el)
            {
                continue;
            }

            int score = Score(SafeName(el), settings);
            if (score > bestScore)
            {
                bestScore = score;
                best = el;
            }
        }

        return best;
    }

    /// <summary>candidate 是不是 ancestor 的后代（往上找 ≤6 层）。</summary>
    private static bool IsWithin(AutomationElement candidate, AutomationElement ancestor)
    {
        try
        {
            AutomationElement? cur = candidate;
            for (int i = 0; i < 6 && cur is not null; i++)
            {
                if (cur.Equals(ancestor))
                {
                    return true;
                }

                cur = UiaClicker.GetParentAny(cur);
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static int Score(string rawName, AppSettings settings)
    {
        string name = rawName.Trim();

        foreach (var custom in settings.LikeButtonNames)
        {
            if (name.Equals(custom.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return 200;
            }
        }

        foreach (var bad in ExcludeWords)
        {
            if (name.Contains(bad, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
        }

        if (Regex.IsMatch(name, @"[·・]\s*\d+"))
        {
            return 0;
        }

        // 已收藏状态下的同一个按钮（标签翻转成「取消喜欢」）
        if (name.StartsWith("取消", StringComparison.Ordinal))
        {
            return name.Contains("喜欢", StringComparison.Ordinal)
                   || name.Contains("收藏", StringComparison.Ordinal)
                ? 95
                : 0;
        }

        if (name is "我喜欢" or "喜欢")
        {
            return 100;
        }

        if (name.Contains("我喜欢", StringComparison.OrdinalIgnoreCase))
        {
            return 92;
        }

        if (name.Contains("喜欢", StringComparison.OrdinalIgnoreCase))
        {
            return 72;
        }

        foreach (var w in new[] { "红心", "heart", "like", "favorite" })
        {
            if (name.Contains(w, StringComparison.OrdinalIgnoreCase))
            {
                return 70;
            }
        }

        if (name == "收藏")
        {
            return 60;
        }

        if (name.Contains("收藏", StringComparison.OrdinalIgnoreCase))
        {
            return 35;
        }

        return 0;
    }

    private static string AppName(string appId)
    {
        string n = (appId ?? "").Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    private static bool MatchesApp(string fallbackEntry, string appId) =>
        AppName(fallbackEntry).Equals(AppName(appId), StringComparison.OrdinalIgnoreCase);

    private static string SafeName(AutomationElement el)
    {
        // 注意：不要用 el.Cached.Name —— 缓存请求返回的元素会失去实时连接，
        // 后续 pattern 调用和 Current 读取都会失败（实测踩过）。
        try
        {
            return (el.Current.Name ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>播放器窗口状态，用于判断"这条 UI 现在能不能被操作"。</summary>
    public static string DescribeWindow(string sourceAppId)
    {
        IntPtr hwnd = FindTargetWindow(sourceAppId, out string how, allowRestore: false);
        if (hwnd == IntPtr.Zero)
        {
            return "没找到窗口（进程未运行或已完全收进托盘）";
        }

        NativeMethods.GetWindowRect(hwnd, out var r);
        NativeMethods.GetClientRect(hwnd, out var cr);
        return $"可见={NativeMethods.IsWindowVisible(hwnd)} 最小化={NativeMethods.IsIconic(hwnd)} "
               + $"矩形={r.Left},{r.Top} {r.Width}x{r.Height} 客户区={cr.Width}x{cr.Height} 来源={how}";
    }

    /// <summary>把最小化/隐藏的播放器窗口恢复出来（不抢焦点）。</summary>
    public static bool RestoreWindow(string sourceAppId, out string note)
    {
        IntPtr hwnd = FindTargetWindow(sourceAppId, out string how, allowRestore: true);
        note = how;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            Thread.Sleep(240);
            return !NativeMethods.IsIconic(hwnd);
        }
        catch (Exception ex)
        {
            Log.Write("恢复播放器窗口失败: " + ex.Message);
            return false;
        }
    }

    private static IntPtr FindTargetWindow(string sourceAppId, out string how, bool allowRestore)
    {
        how = "";
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return IntPtr.Zero;
        }

        var names = new List<string>();
        if (sourceAppId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            names.Add(sourceAppId[..^4]);
        }
        else
        {
            names.Add(sourceAppId);
            int bang = sourceAppId.IndexOf('!');
            if (bang > 0)
            {
                names.Add(sourceAppId[..bang]);
            }
        }

        foreach (var name in names)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    how = $"{name}:MainWindow";
                    return p.MainWindowHandle;
                }

                IntPtr candidate = IntPtr.Zero;
                int bestArea = 0;
                int pid = p.Id;
                NativeMethods.EnumWindows((h, _) =>
                {
                    NativeMethods.GetWindowThreadProcessId(h, out int wpid);
                    if (wpid != pid)
                    {
                        return true;
                    }

                    if (!NativeMethods.GetWindowRect(h, out var r) || r.Width < 200 || r.Height < 100)
                    {
                        return true;
                    }

                    int area = r.Width * r.Height;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        candidate = h;
                    }

                    return true;
                }, IntPtr.Zero);

                if (candidate != IntPtr.Zero)
                {
                    // 查找本身无副作用；由调用者记录原状态后再决定是否恢复。
                    how = $"{name}:枚举窗口";
                    return candidate;
                }
            }
        }

        return IntPtr.Zero;
    }
}
