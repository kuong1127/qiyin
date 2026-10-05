using System.Reflection;
using System.Runtime.InteropServices;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

[DllImport("user32.dll")]
static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

// 仅用于诊断准备；生产状态读取接口本身不能恢复窗口。
static NeteaseVisualState ReadVisible(string? image = null)
{
    IntPtr h = NativeMethods.FindMainWindow("cloudmusic");
    bool iconic = NativeMethods.IsIconic(h), visible = NativeMethods.IsWindowVisible(h);
    try
    {
        if (iconic || !visible) NativeMethods.ShowWindow(h, NativeMethods.SW_SHOWNOACTIVATE);
        Thread.Sleep(300);
        return NeteaseWindowState.Read(image);
    }
    finally
    {
        if (iconic) NativeMethods.ShowWindow(h, NativeMethods.SW_MINIMIZE);
        else if (!visible) NativeMethods.ShowWindow(h, NativeMethods.SW_HIDE);
    }
}

static NeteaseVisualState WaitVisibleState(bool desired)
{
    NeteaseVisualState state = ReadVisible();
    for (int i = 0; i < 5 && !(state.Fresh && state.Confirmed && state.Liked == desired); i++) state = ReadVisible();
    return state;
}

if (args.Length < 1 || args.Length > 2) return 2;
if (args[0] == "--like-cycle")
{
    var window = NativeMethods.FindMainWindow("cloudmusic");
    var initial = ReadVisible();
    bool SameTrack() => NativeMethods.ReadWindowTitle(window) == initial.Identity;
    var settings = new AppSettings();
    var events = new List<object>();
    events.Add(new { step = "before", initial });
    if (initial.Liked != false) { Console.WriteLine("Skipped: initially liked or unknown."); return 9; }
    bool added = false, restored = false;
    try
    {
        if (!SameTrack()) return 10;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = LikeService.Toggle("cloudmusic.exe", settings, SameTrack, true);
        double dispatchMs = timer.Elapsed.TotalMilliseconds;
        var actual = WaitVisibleState(true);
        added = result.Ok && actual.Fresh && actual.Confirmed && actual.Liked == true;
        events.Add(new { step = "added", result, dispatchMs, actual, passed = added });
    }
    finally
    {
        if (SameTrack())
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = LikeService.Toggle("cloudmusic.exe", settings, SameTrack, false);
            double ms = timer.Elapsed.TotalMilliseconds;
            var final = WaitVisibleState(false);
            restored = final.Fresh && final.Confirmed && final.Liked == false && result.Ok && SameTrack();
            events.Add(new { step = "restored", result, ms, final, restored });
        }
        var json = System.Text.Json.JsonSerializer.Serialize(new { events, passed = added && restored }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (args.Length > 1) System.IO.File.WriteAllText(args[1], json);
    }
    return added && restored ? 0 : 11;
}
if (args[0] is "--visual" or "--visual-min")
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    string? diagnostic = args.Length > 1 ? args[1] : null;
    var state = args[0] == "--visual" ? ReadVisible(diagnostic) : NeteaseWindowState.Read(diagnostic);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { state, ms = sw.Elapsed.TotalMilliseconds }));
    return state.Liked.HasValue ? 0 : 8;
}
if (args[0] == "--analyze")
{
    foreach (var name in new[] { "网易云原生播放条_实际.png", "网易云收藏切换后.png", "网易云测试收藏恢复.png" })
    {
        using var bitmap = new System.Drawing.Bitmap(System.IO.Path.Combine(args[1], name));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { image = name, state = NeteaseWindowState.Analyze(bitmap, 1.5, "fixture") }));
    }
    return 0;
}
SetThreadDpiAwarenessContext(new IntPtr(-4));
var hwnd = NativeMethods.FindMainWindow("cloudmusic");
if (hwnd == IntPtr.Zero) return 3;
var foreground = NativeMethods.GetForegroundWindow();
string initialTitle = NativeMethods.ReadWindowTitle(hwnd);
bool iconic = NativeMethods.IsIconic(hwnd), visible = NativeMethods.IsWindowVisible(hwnd);
try
{
    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
    NativeMethods.SetForegroundWindow(hwnd);
    await Task.Delay(450);
    SetThreadDpiAwarenessContext(new IntPtr(-4));
    if (args.Length == 2 && (args[1] == "--settings" || args[1].StartsWith("--click=")))
    {
        NativeMethods.GetClientRect(hwnd, out var client);
        if (client.Width != 1586) return 12;
        NativeMethods.GetWindowRect(hwnd, out var win);
        NativeMethods.GetCursorPos(out var savedCursor);
        var click = args[1] == "--settings" ? new[] { 1271, 54 } : args[1]["--click=".Length..].Split(',').Select(int.Parse).ToArray();
        if (click.Length != 2 || click[0] < 0 || click[0] >= client.Width || click[1] < 0 || click[1] >= client.Height) return 14;
        int x = win.Left + click[0], y = win.Top + click[1];
        try
        {
            NativeMethods.SetCursorPos(x, y);
            NativeMethods.GetWindowThreadProcessId(NativeMethods.WindowFromPoint(new() { X = x, Y = y }), out int hit);
            NativeMethods.GetWindowThreadProcessId(hwnd, out int owner);
            if (hit != owner) return 13;
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(30);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        }
        finally { NativeMethods.GetCursorPos(out var cursor); if (cursor.X == x && cursor.Y == y) NativeMethods.SetCursorPos(savedCursor.X, savedCursor.Y); }
        await Task.Delay(350);
        SetThreadDpiAwarenessContext(new IntPtr(-4));
    }
    if (args.Length == 2 && args[1].StartsWith("--toggle-like", StringComparison.Ordinal))
    {
        if (args[1].StartsWith("--toggle-like=", StringComparison.Ordinal)
            && initialTitle != args[1]["--toggle-like=".Length..]) return 7;
        if (NativeMethods.ReadWindowTitle(hwnd) != initialTitle) return 5;
        if (!HotkeySender.Send("CTRL+ALT+L", out _)) return 6;
        await Task.Delay(300);
        SetThreadDpiAwarenessContext(new IntPtr(-4));
    }
    NativeMethods.GetWindowRect(hwnd, out var r);
    var capture = typeof(LikeService).Assembly.GetType("TaskbarMediaBar.Services.ScreenShot")!.GetMethod("Capture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
    int cropHeight = args.Length == 2 && (args[1] is "--full" or "--settings" || args[1].StartsWith("--click=")) ? r.Height : Math.Min(140, r.Height);
    bool ok = (bool)capture.Invoke(null, new object[] { args[0], r.Left, r.Bottom - cropHeight, r.Width, cropHeight })!;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { ok, title = NativeMethods.ReadWindowTitle(hwnd),
        x = r.Left, y = r.Bottom - cropHeight, width = r.Width, height = cropHeight, iconic, visible }));
    return ok ? 0 : 4;
}

finally
{
    if (iconic) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
    else if (!visible) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
    NativeMethods.SetForegroundWindow(foreground);
}
