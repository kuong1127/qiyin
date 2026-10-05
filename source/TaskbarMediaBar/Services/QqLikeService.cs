using System.Diagnostics;
using Rect = System.Windows.Rect;
using System.Windows.Automation;
using TaskbarMediaBar.Interop;

namespace TaskbarMediaBar.Services;

/// <summary>按 QQ 播放条实时布局发送一次后台点击；最小化时仍使用播放器内部布局。</summary>
public static class QqLikeService
{
    private static readonly TrackLikeStateCache Likes = new();
    private static readonly object InstanceLock = new();
    private static (IntPtr Window, int Pid) _instance;
    private sealed record Target(IntPtr Window, AutomationElement Element, DateTimeOffset At);
    private static Target? _target;
    private sealed record LayoutElements(IntPtr Window, AutomationElement Heart, AutomationElement Next,
        AutomationElement Previous, AutomationElement Surface, DateTimeOffset At);
    private static LayoutElements? _layout;
    private static int _reading;

    private static void EnsureInstance(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
        lock (InstanceLock)
        {
            if (_instance == (hwnd, pid)) return;
            _instance = (hwnd, pid);
            Likes.Clear();
            Volatile.Write(ref _target, null);
            Volatile.Write(ref _layout, null);
        }
    }

    public static LikeProbe Probe(bool refresh = false)
    {
        var hwnd = NativeMethods.FindMainWindow("QQMusic");
        EnsureInstance(hwnd);
        string title = NativeMethods.ReadWindowTitle(hwnd);
        var captured = Likes.Get(title);
        LikeProbe Last()
        {
            var state = Likes.Get(title);
            return new(state.Liked is true ? "取消喜欢" : state.Liked is false ? "我喜欢" : "", state.Liked,
                null, state.Liked.HasValue ? 1 : 0, "QQ后台收藏状态", state.Confirmed);
        }
        if (hwnd == IntPtr.Zero || Interlocked.CompareExchange(ref _reading, 1, 0) != 0) return Last();
        try
        {
            var target = Volatile.Read(ref _target);
            AutomationElement? element = null;
            if (!refresh && target?.Window == hwnd)
                element = target.Element;
            if (element is null)
            {
                var snapshot = Capture(hwnd, refresh: refresh);
                if (snapshot is null) return Last();
                element = snapshot.Heart;
                Volatile.Write(ref _target, new Target(hwnd, element, DateTimeOffset.UtcNow));
            }
            string name = element.Current.Name;
            bool? liked = name == "取消喜欢" ? true : name == "我喜欢" ? false : null;
            if (!liked.HasValue) { Volatile.Write(ref _target, null); Volatile.Write(ref _layout, null); }
            if (NativeMethods.ReadWindowTitle(hwnd) == title)
                Likes.Observe(title, liked, captured.Revision, DateTime.UtcNow);
            return Last();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _target, null);
            Log.Write("QQ后台收藏状态读取失败: " + ex.Message);
            return Last();
        }
        finally { Volatile.Write(ref _reading, 0); }
    }

    public sealed record LayoutNode(string Name, bool Button, Rect Bounds);

    // 只接受当前播放条唯一的心形按钮；导航、主菜单、重复候选和越界坐标一律拒绝。
    public static Rect? ResolveHeart(IReadOnlyList<LayoutNode> nodes, Rect surface, Rect? playbar = null)
    {
        if (surface.IsEmpty || surface.Width < 400 || surface.Height < 200) return null;
        var hearts = nodes.Where(n => n.Button && (n.Name is "我喜欢" or "取消喜欢") && surface.Contains(n.Bounds)
            && (!playbar.HasValue || playbar.Value.Contains(n.Bounds))).ToArray();
        var next = nodes.Where(n => n.Button && n.Name == "下一曲" && surface.Contains(n.Bounds)).ToArray();
        var previous = nodes.Where(n => n.Button && n.Name == "上一曲" && surface.Contains(n.Bounds)).ToArray();
        if (hearts.Length != 1 || next.Length != 1 || previous.Length != 1) return null;
        var h = hearts[0].Bounds;
        if (h.Width is < 8 or > 180 || h.Height is < 8 or > 180
            || Math.Abs(h.Top - next[0].Bounds.Top) > 100
            || Math.Abs(h.Top - previous[0].Bounds.Top) > 100) return null;
        return h;
    }

    private sealed record Snapshot(AutomationElement Heart, string Name, Rect Bounds, Rect Surface,
        NativeMethods.POINT Origin, bool Ready);

    private static Snapshot? Capture(IntPtr hwnd, bool refresh = false)
    {
        var live = Volatile.Read(ref _layout);
        if (!refresh && live?.Window == hwnd && DateTimeOffset.UtcNow - live.At < TimeSpan.FromMinutes(2))
        {
            try
            {
                var currentOrigin = new NativeMethods.POINT();
                if (!NativeMethods.ClientToScreen(hwnd, ref currentOrigin)) return null;
                LayoutNode Read(AutomationElement e) => new(e.Current.Name, e.Current.ControlType == ControlType.Button, e.Current.BoundingRectangle);
                var liveHeart = Read(live.Heart); var next = Read(live.Next); var previous = Read(live.Previous);
                var full = live.Surface.Current.BoundingRectangle;
                if ((liveHeart.Name is "我喜欢" or "取消喜欢") && next.Name == "下一曲" && previous.Name == "上一曲"
                    && Math.Abs(full.Left - currentOrigin.X) <= 2 && Math.Abs(full.Top - currentOrigin.Y) <= 2)
                    return new(live.Heart, liveHeart.Name, liveHeart.Bounds, full, currentOrigin,
                        ResolveHeart(new[] { liveHeart, next, previous }, full).HasValue);
            }
            catch { Volatile.Write(ref _layout, null); }
        }
        var cache = new CacheRequest();
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        AutomationElement[] found;
        // 只传回播放条定位所需的容器与四个命名按钮，避免缓存整个歌曲列表。
        var needed = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "我喜欢"),
                    new PropertyCondition(AutomationElement.NameProperty, "取消喜欢"),
                    new PropertyCondition(AutomationElement.NameProperty, "下一曲"),
                    new PropertyCondition(AutomationElement.NameProperty, "上一曲"))));
        using (cache.Activate()) found = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, needed)
            .Cast<AutomationElement>().ToArray();
        var nodes = found.Select(n => new LayoutNode(n.Cached.Name, n.Cached.ControlType == ControlType.Button, n.Cached.BoundingRectangle)).ToArray();
        var origin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return null;
        // Windows 的最小化客户区只有图标大小，QQ 的 Pane 仍报告完整播放界面。
        var surfaces = found.Where(n => n.Cached.ControlType == ControlType.Pane).Select(n => n.Cached.BoundingRectangle)
            .Where(r => !r.IsEmpty && Math.Abs(r.Left - origin.X) <= 2 && Math.Abs(r.Top - origin.Y) <= 2
                && r.Width >= 400 && r.Height >= 200).Distinct().ToArray();
        if (surfaces.Length != 1) return null;
        var surface = surfaces[0];
        var likes = found.Where(n => n.Cached.ControlType == ControlType.Button && (n.Cached.Name is "我喜欢" or "取消喜欢")).ToArray();
        var nextElements = found.Where(n => n.Cached.ControlType == ControlType.Button && n.Cached.Name == "下一曲").ToArray();
        var previousElements = found.Where(n => n.Cached.ControlType == ControlType.Button && n.Cached.Name == "上一曲").ToArray();
        if (nextElements.Length != 1 || previousElements.Length != 1) return null;
        // 列表悬停也会出现同名爱心：按包住上一曲、下一曲的最小播放条容器限定。
        var barRects = found.Where(n => n.Cached.ControlType == ControlType.Pane).Select(n => n.Cached.BoundingRectangle)
            .Where(r => !r.IsEmpty && r.Height < surface.Height / 2
                && r.Contains(nextElements[0].Cached.BoundingRectangle) && r.Contains(previousElements[0].Cached.BoundingRectangle))
            .Distinct().OrderBy(r => r.Width * r.Height);
        AutomationElement? heart = null;
        Rect? playbar = null;
        foreach (var bar in barRects)
        {
            var scoped = likes.Where(l => bar.Contains(l.Cached.BoundingRectangle)).ToArray();
            if (scoped.Length == 1) { heart = scoped[0]; playbar = bar; break; }
        }
        if (heart is null) return null;
        var surfaceElement = found.First(n => n.Cached.ControlType == ControlType.Pane && n.Cached.BoundingRectangle == surface);
        // 只缓存控件对象，每次点击重新读取所有位置；绝不保存旧点击坐标。
        Volatile.Write(ref _layout, new LayoutElements(hwnd, heart, nextElements[0], previousElements[0], surfaceElement, DateTimeOffset.UtcNow));
        return new(heart, heart.Cached.Name, heart.Cached.BoundingRectangle, surface, origin, ResolveHeart(nodes, surface, playbar).HasValue);
    }

    public static LikeResult Toggle(Func<bool> current, bool? desired)
    {
        var hwnd = NativeMethods.FindMainWindow("QQMusic");
        EnsureInstance(hwnd);
        if (hwnd == IntPtr.Zero) return new() { Message = "QQ音乐未运行" };
        string title = NativeMethods.ReadWindowTitle(hwnd);
        bool SameTrack() => current() && NativeMethods.IsWindow(hwnd) && NativeMethods.ReadWindowTitle(hwnd) == title;
        if (!SameTrack()) return new() { Message = "歌曲已切换，本次操作已取消" };
        var elapsed = Stopwatch.StartNew();
        try
        {
            var snapshot = Capture(hwnd);
            if (snapshot is not null && !snapshot.Ready && SameTrack())
            {
                // 沉浸歌词页自动隐藏播放条时，先投递悬停，再重新读取动画后的真实位置。
                int hoverX = (int)(snapshot.Surface.Width / 2), hoverY = (int)(snapshot.Surface.Height - 20);
                NativeMethods.PostMessage(hwnd, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, new IntPtr((hoverY << 16) | (hoverX & 0xffff)));
                Thread.Sleep(220);
                snapshot = Capture(hwnd);
            }
            if (snapshot is null || !snapshot.Ready) return new() { Message = "暂时无法确认播放条位置，本次未点击" };
            if (!SameTrack()) return new() { Message = "歌曲已切换，本次操作已取消" };
            bool before = snapshot.Name == "取消喜欢", want = desired ?? !before;
            if (before == want) return new() { Ok = true, Verified = true, LikedAfter = before,
                Message = want ? "已在「我喜欢」中" : "已取消喜欢" };
            var origin = new NativeMethods.POINT(); NativeMethods.ClientToScreen(hwnd, ref origin);
            if (origin.X != snapshot.Origin.X || origin.Y != snapshot.Origin.Y
                || snapshot.Heart.Current.BoundingRectangle != snapshot.Bounds || snapshot.Heart.Current.Name != snapshot.Name || !SameTrack())
                return new() { Message = "播放器界面正在变化，本次未点击" };
            int x = (int)Math.Round(snapshot.Bounds.Left + snapshot.Bounds.Width / 2 - origin.X);
            int y = (int)Math.Round(snapshot.Bounds.Top + snapshot.Bounds.Height / 2 - origin.Y);
            var point = new ClickTarget(hwnd, x, y, origin.X + x, origin.Y + y, 1, false, "QQ实时播放条爱心");
            long locatedMs = elapsed.ElapsedMilliseconds;
            bool sent = UiaClicker.ClickByPostedMessage(point);
            Volatile.Write(ref _target, new Target(hwnd, snapshot.Heart, DateTimeOffset.UtcNow));
            Log.Write($"QQ后台爱心: 定位={locatedMs} ms 投递={elapsed.ElapsedMilliseconds - locatedMs} ms client={x},{y} 最小化={NativeMethods.IsIconic(hwnd)}");
            if (sent) Likes.Intent(title, want, DateTime.UtcNow);
            for (int i = 0; i < 12 && SameTrack(); i++)
            {
                if (i > 0) Thread.Sleep(50);
                string name = snapshot.Heart.Current.Name;
                if (name == (want ? "取消喜欢" : "我喜欢"))
                {
                    var state = Likes.Get(title);
                    Likes.Observe(title, want, state.Revision, DateTime.UtcNow);
                    return new() { Ok = true, Verified = true, LikedAfter = want, Tier = "QQ后台播放条",
                        Message = want ? "已加入「我喜欢」" : "已取消喜欢" };
                }
            }
            return new() { Ok = sent, Message = "已发送，收藏状态待确认" };
        }
        catch (Exception ex)
        {
            Log.Write("QQ后台爱心失败: " + ex.Message);
            return new() { Message = "暂时无法确认收藏状态，请稍后重试" };
        }
    }
}
