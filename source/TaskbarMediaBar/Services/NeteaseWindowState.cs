using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using TaskbarMediaBar.Interop;

namespace TaskbarMediaBar.Services;

public sealed record NeteaseVisualState(string Identity, bool? Liked, bool? Playing, bool Fresh)
{
    public bool Confirmed { get; init; }
}

/// <summary>仅识别网易云播放条图标。正常运行不保存截图、不读取账号或网络缓存。</summary>
public static class NeteaseWindowState
{
    private static NeteaseVisualState? _last;
    private static readonly TrackLikeStateCache Likes = new();
    private static readonly object InstanceLock = new();
    private static (IntPtr hwnd, int pid) _instance;
    private static int _reading;
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    private static void EnsureInstance(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
        lock (InstanceLock)
        {
            if (_instance == (hwnd, pid)) return;
            _instance = (hwnd, pid);
            Likes.Clear();
            Volatile.Write(ref _last, null);
        }
    }

    public static NeteaseVisualState Peek()
    {
        IntPtr hwnd = NativeMethods.FindMainWindow("cloudmusic");
        EnsureInstance(hwnd);
        string title = NativeMethods.ReadWindowTitle(hwnd);
        var like = Likes.Get(title);
        var last = Volatile.Read(ref _last);
        return new(title, like.Liked, last?.Identity == title ? last.Playing : null, false) { Confirmed = like.Confirmed };
    }

    public static void RememberIntent(string identity, bool liked)
    {
        IntPtr hwnd = NativeMethods.FindMainWindow("cloudmusic");
        EnsureInstance(hwnd);
        if (NativeMethods.ReadWindowTitle(hwnd) == identity) Likes.Intent(identity, liked, DateTime.UtcNow);
    }

    // 只读已可见窗口；任何状态读取都不得改变网易云的显隐、位置或焦点。
    public static NeteaseVisualState Read(string? diagnosticImage = null)
    {
        IntPtr dpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        IntPtr hwnd = NativeMethods.FindMainWindow("cloudmusic");
        EnsureInstance(hwnd);
        string title = NativeMethods.ReadWindowTitle(hwnd);
        var cached = Peek();
        var captured = Likes.Get(title);
        if (hwnd == IntPtr.Zero) { SetThreadDpiAwarenessContext(dpiContext); return cached; }
        bool iconic = NativeMethods.IsIconic(hwnd), visible = NativeMethods.IsWindowVisible(hwnd);
        if (iconic || !visible || Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            SetThreadDpiAwarenessContext(dpiContext);
            return cached;
        }
        try
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return cached;
            if (NativeMethods.IsIconic(hwnd)) return cached;
            if (rect.Width < 500 || rect.Height < 200
                || rect.Width > 6000 || rect.Height > 4000) return cached;
            using var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try { if (!PrintWindow(hwnd, dc, 2)) return cached; }
                finally { graphics.ReleaseHdc(dc); }
            }
            double scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
            var result = Analyze(bitmap, scale, title);
            if (NativeMethods.ReadWindowTitle(hwnd) != title) return Peek();
            var like = Likes.Observe(title, result.Liked, captured.Revision, DateTime.UtcNow);
            result = result with { Liked = like.Liked, Confirmed = like.Confirmed,
                Fresh = result.Liked.HasValue && like.Confirmed && result.Liked == like.Liked };
            if (result.Liked.HasValue || result.Playing.HasValue) Volatile.Write(ref _last, result);
            if (diagnosticImage is not null)
            {
                int h = Math.Min(bitmap.Height, (int)(94 * scale));
                using var crop = bitmap.Clone(new Rectangle(0, bitmap.Height - h, bitmap.Width, h), bitmap.PixelFormat);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(diagnosticImage)!);
                crop.Save(diagnosticImage, ImageFormat.Png);
            }
            return result;
        }
        catch (Exception ex) { Log.Write("网易云图标读取失败: " + ex.Message); return cached; }
        finally
        {
            Volatile.Write(ref _reading, 0);
            SetThreadDpiAwarenessContext(dpiContext);
        }
    }

    public static NeteaseVisualState Analyze(Bitmap bitmap, double scale, string identity)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int width = bitmap.Width, height = bitmap.Height, stride = data.Stride;
            var bytes = new byte[stride * height]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            (int r, int g, int b) Pixel(int x, int y)
            {
                x = Math.Clamp(x, 0, width - 1); y = Math.Clamp(y, 0, height - 1);
                int i = y * stride + x * 4; return (bytes[i + 2], bytes[i + 1], bytes[i]);
            }
            int centerY = height - (int)Math.Round(40 * scale);
            int left = (int)(110 * scale), right = Math.Min((int)(380 * scale), width / 2 - (int)(140 * scale));
            int top = centerY - (int)(16 * scale), bottom = centerY + (int)(16 * scale);
            if (right <= left || top < 0 || bottom >= height) return new(identity, null, null, false);
            int sw = right - left, sh = bottom - top;
            var mask = new bool[sw * sh]; var seen = new bool[mask.Length];
            for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
            {
                var p = Pixel(left + x, top + y); var bg = Pixel(left + x, height - (int)(8 * scale));
                mask[y * sw + x] = Math.Max(Math.Abs(p.r - bg.r), Math.Max(Math.Abs(p.g - bg.g), Math.Abs(p.b - bg.b))) > 55;
            }
            bool? liked = null;
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || seen[start]) continue;
                var points = new List<(int x, int y)>(); var queue = new Queue<int>(); queue.Enqueue(start); seen[start] = true;
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue(), x = i % sw, y = i / sw; points.Add((x, y));
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= sw || ny >= sh) continue;
                        int ni = ny * sw + nx; if (mask[ni] && !seen[ni]) { seen[ni] = true; queue.Enqueue(ni); }
                    }
                }
                int x0 = points.Min(p => p.x), x1 = points.Max(p => p.x), y0 = points.Min(p => p.y), y1 = points.Max(p => p.y);
                int w = x1 - x0 + 1, h = y1 - y0 + 1;
                if (w < 12 * scale || h < 12 * scale || w > 26 * scale || h > 27 * scale
                    || (double)w / h is < 0.75 or > 1.35 || Math.Abs(top + (y0 + y1) / 2.0 - centerY) > 4 * scale) continue;
                bool lobeLeft = points.Any(p => p.x < x0 + w * .4 && p.y < y0 + h * .25);
                bool lobeRight = points.Any(p => p.x > x0 + w * .6 && p.y < y0 + h * .25);
                bool bottomTip = points.Any(p => p.x > x0 + w * .3 && p.x < x0 + w * .7 && p.y > y0 + h * .85);
                int bottomCorners = points.Count(p => (p.x < x0 + w * .2 || p.x > x0 + w * .8) && p.y > y0 + h * .8);
                if (!lobeLeft || !lobeRight || !bottomTip || bottomCorners > 3 * scale) continue;
                int red = points.Count(p => { var c = Pixel(left + p.x, top + p.y); return c.r > 170 && c.r > c.g * 1.35 && c.r > c.b * 1.25; });
                if (red > points.Count * .6) { liked = true; break; }
                if (points.Count < w * h * .55) { liked = false; break; }
            }
            // 圆形播放键中间为空隙时是双竖线；白色三角形则填过中心。
            int cx = width / 2;
            var center = Pixel(cx, centerY); var bar = Pixel(cx - (int)Math.Round(4 * scale), centerY);
            var circle = Pixel(cx - (int)Math.Round(14 * scale), centerY);
            int centerL = (center.r + center.g + center.b) / 3, barL = (bar.r + bar.g + bar.b) / 3,
                circleL = (circle.r + circle.g + circle.b) / 3;
            bool? playing = barL > 225 && barL - circleL > 15 ? centerL < barL - 15 : null;
            return new(identity, liked, playing, liked.HasValue || playing.HasValue) { Confirmed = liked.HasValue };
        }
        finally { bitmap.UnlockBits(data); }
    }
}
