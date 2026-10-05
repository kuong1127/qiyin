using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

namespace TaskbarMediaBar;

public partial class App : Application
{
    private Mutex? _mutex;
    private BarController? _controller;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 任何未处理异常都要留下来，别让播控条"莫名其妙就不见了"
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("UI 未处理异常(已吞掉保命): " + args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Write("域未处理异常: " + args.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("任务未观察异常: " + args.Exception);
            args.SetObserved();
        };

        if (e.Args.Any(a => string.Equals(a, "--list", StringComparison.OrdinalIgnoreCase)))
        {
            await RunDiagnosticsAsync();
            Shutdown();
            return;
        }

        int shotIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--shot", StringComparison.OrdinalIgnoreCase));
        if (shotIndex >= 0)
        {
            RunShot(e.Args, shotIndex);
            Shutdown();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--windows", StringComparison.OrdinalIgnoreCase)))
        {
            RunWindowList();
            Shutdown();
            return;
        }

        int uiaIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--uia", StringComparison.OrdinalIgnoreCase));
        if (uiaIndex >= 0)
        {
            RunUiaDump(e.Args, uiaIndex);
            Shutdown();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--uiaprops", StringComparison.OrdinalIgnoreCase)))
        {
            RunUiaProps(e.Args);
            Shutdown();
            return;
        }

        int likeIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--liketest", StringComparison.OrdinalIgnoreCase));
        if (likeIndex >= 0)
        {
            RunLikeTest(e.Args, likeIndex);
            Shutdown();
            return;
        }

        int toggleIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--liketoggle", StringComparison.OrdinalIgnoreCase));
        if (toggleIndex >= 0)
        {
            RunLikeToggle(e.Args, toggleIndex);
            Shutdown();
            return;
        }

        int clickIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--likeclick", StringComparison.OrdinalIgnoreCase));
        if (clickIndex >= 0)
        {
            RunLikeClick(e.Args, clickIndex);
            Shutdown();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--lyrictest", StringComparison.OrdinalIgnoreCase)))
        {
            RunLyricTest(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--clicktest", StringComparison.OrdinalIgnoreCase)))
        {
            RunClickTest(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--mediakeytest", StringComparison.OrdinalIgnoreCase)))
        {
            RunMediaKeyTest(e.Args);
            Shutdown();
            return;
        }

        int autoIdx = Array.FindIndex(e.Args, a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));
        if (autoIdx >= 0)
        {
            string want = autoIdx + 1 < e.Args.Length ? e.Args[autoIdx + 1].Trim().ToLowerInvariant() : "on";
            bool enable = want is "on" or "1" or "true" or "yes";
            bool ok = AutoStart.Set(enable);

            NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== 开机自启 ===");
            sb.AppendLine($"目标: {(enable ? "开启" : "关闭")}");
            sb.AppendLine($"结果: {(ok ? "成功" : "失败")}");
            sb.AppendLine($"当前状态: {(AutoStart.IsEnabled() ? "已开启" : "未开启")}");

            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                sb.AppendLine($"注册表值: {key?.GetValue("TaskbarMediaBar") ?? "(无)"}");
            }

            WriteDiag("autostart.txt", sb.ToString());
            Shutdown();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--play", StringComparison.OrdinalIgnoreCase)))
        {
            RunTransport("play");
            Shutdown();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--next", StringComparison.OrdinalIgnoreCase)))
        {
            RunTransport("next");
            Shutdown();
            return;
        }

        bool createdNew;
        _mutex = new Mutex(true, @"Global\TaskbarMediaBar.SingleInstance", out createdNew);
        if (!createdNew)
        {
            Log.Write("已有实例在运行，本次启动退出");
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();
        Log.Write($"启动，配置锚点={settings.Anchor} 偏移={settings.Offset} 宽度={settings.Width}");

        _controller = new BarController(settings);
        await _controller.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>诊断：列出画在任务栏上的窗口（含别人的常驻组件）。</summary>
    private static void RunWindowList()
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 任务栏占位者清单 ===");

        var bar = TaskbarHost.DiscoverPrimary();
        if (bar is null)
        {
            sb.AppendLine("没找到任务栏");
        }
        else
        {
            sb.AppendLine($"任务栏: {bar.Rect} scale={bar.Scale:F2} 可见={bar.Visible}");
            int ownPid = Environment.ProcessId;
            var list = TaskbarOccupancy.Discover(bar.Rect, ownPid);
            if (list.Count == 0)
            {
                sb.AppendLine("（没有探测到别人的常驻窗口）");
            }
            else
            {
                foreach (var o in list)
                {
                    sb.AppendLine("  · " + o);
                }
            }

            var tray = TaskbarHost.GetTrayRect(bar.Handle);
            sb.AppendLine($"通知区: {tray?.ToString() ?? "(未找到)"}");
        }

        string text = sb.ToString();

        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(Path.Combine(Log.Dir, "windows.txt"), text, System.Text.Encoding.UTF8);
        }
        catch
        {
            // ignore
        }

        try
        {
            Console.WriteLine(text);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>深挖「喜欢」按钮的全部属性 / 模式 / 父链 / 坐标。</summary>
    private static void RunUiaProps(string[] args)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        string target = "QQMusic";
        string filter = "喜欢";

        // 注意：WPF 的 StartupEventArgs.Args 不含 exe 路径，第一个位置参数就是目标进程名
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (positional.Length > 0)
        {
            target = positional[0];
        }

        if (positional.Length > 1)
        {
            filter = positional[1];
        }

        string text = UiaDump.DumpProps(target, filter);

        string path = Path.Combine(Log.Dir, "uia-props.txt");
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(path, text, System.Text.Encoding.UTF8);
        }
        catch
        {
            // ignore
        }

        try
        {
            Console.WriteLine($"UIA props -> {path}");
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>在指定屏幕坐标注入一次真实点击（用于回归测试"点歌词不再崩"）。</summary>
    private static void RunClickTest(string[] args)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        int x = 700;
        int y = 2124;
        if (positional.Length > 0)
        {
            var parts = positional[0].Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int px) && int.TryParse(parts[1], out int py))
            {
                x = px;
                y = py;
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 注入点击测试 ===");
        sb.AppendLine($"目标点: ({x},{y})");

        NativeMethods.GetCursorPos(out var saved);
        NativeMethods.SetCursorPos(x, y);
        Thread.Sleep(120);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(60);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(200);
        NativeMethods.SetCursorPos(saved.X, saved.Y);

        sb.AppendLine("已注入点击（若主程序仍在运行说明没崩）");
        WriteDiag("clicktest.txt", sb.ToString());
    }

    /// <summary>发一个 SMTC 传输命令（验证用；也让用户能脚本化控制）。</summary>
    private static void RunTransport(string action)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        var settings = AppSettings.Load();
        using var media = new MediaSessionService(
            settings.PreferredApps,
            settings.FallbackApps,
            appId => settings.FindHotkeyProfile(appId));
        media.RefreshAsync().GetAwaiter().GetResult();
        Thread.Sleep(300);

        var before = media.Current;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== 传输命令: {action} ===");
        sb.AppendLine($"当前: {(before.HasSession ? $"{before.SourceAppId} | {before.DisplayTitle} - {before.Artist}" : "(无 SMTC 会话)")}");

        bool can = action == "next" ? before.CanNext : before.CanPlayPause;

        // 兜底源（网易云这类）没有 SMTC 会话：直接用它的全局快捷键，不依赖快照
        if (!before.HasSession)
        {
            var profile = settings.FindHotkeyProfile(settings.FallbackApps.FirstOrDefault() ?? "");
            string combo = profile is null ? "" : (action == "next" ? profile.Next : profile.PlayPause);
            string hkErr = "";
            if (!string.IsNullOrWhiteSpace(combo) && HotkeySender.Send(combo, out hkErr))
            {
                sb.AppendLine($"执行结果: 已发送全局快捷键 {combo}（兜底通道）");
                WriteDiag("transport.txt", sb.ToString());
                return;
            }

            sb.AppendLine($"执行结果: 无会话，且没有可用快捷键{(string.IsNullOrEmpty(hkErr) ? "" : "（" + hkErr + "）")}");
            WriteDiag("transport.txt", sb.ToString());
            return;
        }

        if (can)
        {
            if (action == "next")
            {
                media.NextAsync().GetAwaiter().GetResult();
            }
            else
            {
                media.TogglePlayPauseAsync().GetAwaiter().GetResult();
            }
        }

        Thread.Sleep(1200);
        media.RefreshAsync().GetAwaiter().GetResult();
        var after = media.Current;
        sb.AppendLine($"执行结果: {(can ? "已发送" : "无会话/播放器不支持，未执行")}");
        sb.AppendLine($"之后: {(after.HasSession ? $"{after.SourceAppId} | {after.DisplayTitle} - {after.Artist} | {after.PositionSeconds:F1}/{after.DurationSeconds:F1}s" : "(无会话)")}");

        WriteDiag("transport.txt", sb.ToString());
    }

    /// <summary>列出某个进程的所有顶层窗口（排查"能拿到什么"）。</summary>
    private static void RunWinList(string[] args)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        string proc = positional.Length > 0 ? positional[0] : "cloudmusic";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== {proc} 的顶层窗口 ===");

        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName(proc);
        }
        catch (Exception ex)
        {
            sb.AppendLine("取进程失败: " + ex.Message);
            WriteDiag("winlist.txt", sb.ToString());
            return;
        }

        var pids = procs.Select(p => p.Id).ToHashSet();
        sb.AppendLine($"进程: {string.Join(", ", pids)}");

        int n = 0;
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out int pid);
            if (!pids.Contains(pid))
            {
                return true;
            }

            var cls = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(h, cls, cls.Capacity);
            NativeMethods.GetWindowRect(h, out var r);
            n++;
            sb.AppendLine($"  [{n}] pid={pid} hwnd=0x{h:X} class={cls} 可见={NativeMethods.IsWindowVisible(h)} "
                          + $"最小化={NativeMethods.IsIconic(h)} rect={r.Left},{r.Top} {r.Width}x{r.Height} "
                          + $"标题=[{NativeMethods.ReadWindowTitle(h)}]");
            return true;
        }, IntPtr.Zero);

        sb.AppendLine($"共 {n} 个顶层窗口");
        WriteDiag("winlist.txt", sb.ToString());
    }

    /// <summary>验证媒体键能否驱动某个播放器（用于不支持 SMTC 的兜底通道）。</summary>
    private static void RunMediaKeyTest(string[] args)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        string proc = positional.Length > 0 ? positional[0] : "cloudmusic";
        string key = positional.Length > 1 ? positional[1].ToUpperInvariant() : "NEXT";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 媒体键测试 ===");
        sb.AppendLine($"目标进程: {proc}  按键: {key}");

        IntPtr hwnd = NativeMethods.FindMainWindow(proc);
        if (hwnd == IntPtr.Zero && !key.Contains('+'))
        {
            sb.AppendLine("找不到窗口（可能整个收进托盘了）");
            WriteDiag("mediakeytest.txt", sb.ToString());
            return;
        }

        sb.AppendLine($"窗口: 0x{hwnd:X}");

        // 注意：最小化时 IsWindowVisible 仍是 True，必须看 IsIconic
        bool wasIconic = NativeMethods.IsIconic(hwnd);
        bool wasVisible = NativeMethods.IsWindowVisible(hwnd);
        if (wasIconic || !wasVisible)
        {
            sb.AppendLine($"窗口处于最小化/隐藏（iconic={wasIconic} visible={wasVisible}）→ 先恢复（不抢焦点）");
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            Thread.Sleep(800);
            sb.AppendLine($"  恢复后: iconic={NativeMethods.IsIconic(hwnd)} 标题=[{ReadTitle(hwnd)}]");
        }

        if (key.Contains('+'))
        {
            // 组合键（例如 CTRL+ALT+P）：给播放器的全局快捷键用
            var keys = key.Split('+');
            var downs = new List<byte>();
            foreach (var k in keys)
            {
                switch (k.Trim().ToUpperInvariant())
                {
                    case "CTRL": case "CONTROL": downs.Add(0x11); break;
                    case "ALT": downs.Add(0x12); break;
                    case "SHIFT": downs.Add(0x10); break;
                    case "LEFT": downs.Add(0x25); break;
                    case "RIGHT": downs.Add(0x27); break;
                    case "UP": downs.Add(0x26); break;
                    case "DOWN": downs.Add(0x28); break;
                    case "P": downs.Add(0x50); break;
                    case "N": downs.Add(0x4E); break;
                    default:
                        if (k.Trim().Length == 1)
                        {
                            downs.Add((byte)char.ToUpperInvariant(k.Trim()[0]));
                        }

                        break;
                }
            }

            foreach (var code in downs)
            {
                NativeMethods.keybd_event(code, 0, 0, IntPtr.Zero);
            }

            Thread.Sleep(60);
            downs.Reverse();
            foreach (var code in downs)
            {
                NativeMethods.keybd_event(code, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
            }

            sb.AppendLine($"已发送组合键 {key}");
            Thread.Sleep(2500);
            sb.AppendLine($"标题: [{ReadTitle(hwnd)}]");
            WriteDiag("mediakeytest.txt", sb.ToString());
            return;
        }

        string before = ReadTitle(hwnd);
        sb.AppendLine($"按键前标题: [{before}]");

        if (key == "BROADCAST")
        {
            // 广播三条命令，看哪条能让标题变化
            foreach (var (name, cmd) in new[]
                     {
                         ("PLAY_PAUSE", NativeMethods.APPCOMMAND_MEDIA_PLAY_PAUSE),
                         ("NEXT", NativeMethods.APPCOMMAND_MEDIA_NEXTTRACK),
                         ("PREV", NativeMethods.APPCOMMAND_MEDIA_PREVIOUSTRACK),
                     })
            {
                NativeMethods.BroadcastMediaCommand(cmd);
                Thread.Sleep(1500);
                string now = ReadTitle(hwnd);
                sb.AppendLine($"  广播 {name} → 标题 [{now}] {(now != before ? "← 变了 ✓" : "")}");
            }

            WriteDiag("mediakeytest.txt", sb.ToString());
            return;
        }

        if (key == "CHANNELS")
        {
            // 依次尝试三条控制通道，哪条让标题变了就说明能用（成功即停，尽量少切歌）
            var channels = new (string Name, Action Send)[]
            {
                ("直接 SendMessage(WM_APPCOMMAND)", () => NativeMethods.SendAppCommand(hwnd, NativeMethods.APPCOMMAND_MEDIA_NEXTTRACK)),
                ("广播 WM_APPCOMMAND", () => NativeMethods.BroadcastMediaCommand(NativeMethods.APPCOMMAND_MEDIA_NEXTTRACK)),
                ("注入媒体键", () => NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_NEXT_TRACK)),
            };

            foreach (var (name, send) in channels)
            {
                string cur = ReadTitle(hwnd);
                send();
                string now = cur;
                for (int i = 0; i < 8; i++)
                {
                    Thread.Sleep(500);
                    now = ReadTitle(hwnd);
                    if (now != cur)
                    {
                        break;
                    }
                }

                bool ok = now != cur;
                sb.AppendLine($"  {name}: {(ok ? $"生效 ✓ 标题→[{now}]" : "无效")}");
                if (ok)
                {
                    sb.AppendLine($"结论: 可用控制通道 = {name}");
                    break;
                }
            }

            WriteDiag("mediakeytest.txt", sb.ToString());
            return;
        }

        byte vk = key switch
        {
            "PREV" => NativeMethods.VK_MEDIA_PREV_TRACK,
            "PLAY" => NativeMethods.VK_MEDIA_PLAY_PAUSE,
            "STOP" => NativeMethods.VK_MEDIA_STOP,
            _ => NativeMethods.VK_MEDIA_NEXT_TRACK,
        };

        NativeMethods.SendMediaKey(vk);
        sb.AppendLine($"已发送媒体键 VK=0x{vk:X2}，轮询标题 6 秒…");

        string after = before;
        for (int i = 0; i < 12; i++)
        {
            Thread.Sleep(500);
            after = ReadTitle(hwnd);
            if (after != before)
            {
                sb.AppendLine($"  +{(i + 1) * 0.5:F1}s 标题变了: [{after}]");
                break;
            }
        }

        if (after == before)
        {
            sb.AppendLine($"  6 秒内标题未变: [{after}]");
        }

        sb.AppendLine(after != before
            ? "结论: 媒体键对该播放器生效 ✓"
            : "结论: 媒体键没生效（或这首之后一直是同一首歌）");

        if (wasIconic || !wasVisible)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
            sb.AppendLine("窗口已还原为最小化");
        }

        WriteDiag("mediakeytest.txt", sb.ToString());
    }

    private static string ReadTitle(IntPtr hwnd) => NativeMethods.ReadWindowTitle(hwnd);

    /// <summary>验证逐字歌词链路：拉一次歌词并打印前几行与逐字时间轴。</summary>
    private static void RunLyricTest(string[] args)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        string title = positional.Length > 0 ? positional[0] : "打ち上げ花火";
        string artist = positional.Length > 1 ? positional[1] : "fox capture plan";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 逐字歌词测试 ===");
        sb.AppendLine($"查询: {title} - {artist}");

        // 先探网络：带代理 / 不带代理各试一次，把状态码和响应片段打出来
        foreach (bool useProxy in new[] { true, false })
        {
            sb.AppendLine();
            sb.AppendLine($"--- 探测(useProxy={useProxy}) ---");
            sb.AppendLine(LyricsService.ProbeNetwork(useProxy, title, artist));
        }

        using var lyrics = new LyricsService();
        var doc = lyrics.FetchAsync(title, artist).GetAwaiter().GetResult();

        if (doc is null || doc.IsEmpty)
        {
            sb.AppendLine("结果: 取不到歌词（网络或被限流）");
        }
        else
        {
            sb.AppendLine($"命中: {doc.Title} - {doc.Artist}  来源={doc.Source}  行数={doc.Lines.Count}");
            sb.AppendLine();
            int n = 0;
            foreach (var line in doc.Lines)
            {
                if (++n > 8)
                {
                    break;
                }

                string words = line.Words.Count > 0
                    ? string.Join("", line.Words.Take(6).Select(w => $"[{w.Text}:{w.StartMs}]"))
                    : "(无逐字时间轴)";
                sb.AppendLine($"  {line.StartMs,7}ms  {line.Text}");
                sb.AppendLine($"              {words}");
            }

            // 模拟定位
            sb.AppendLine();
            foreach (double t in new[] { 5.0, 30.0, 60.0 })
            {
                var (line, sung) = LyricsService.Locate(doc, t);
                sb.AppendLine($"  位置 {t:F0}s → 行='{(line?.Text ?? "(间奏/无)")}' 已唱字数={sung}");
            }
        }

        WriteDiag("lyrictest.txt", sb.ToString());
    }

    /// <summary>单次点击爱心（用于把状态调回原样，或现场验证）。</summary>
    private static void RunLikeClick(string[] args, int index)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        var settings = AppSettings.Load();
        string target = args.Length > index + 1 && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : "QQMusic.exe";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 爱心单次点击 ===");
        sb.AppendLine($"目标: {target}");
        sb.AppendLine($"窗口: {LikeService.DescribeWindow(target)}");

        var p0 = LikeService.Probe(target, settings, verbose: true);
        sb.AppendLine($"点击前: 会点='{p0.ButtonName}' 范围={p0.Scope} 已收藏={Render(p0.Liked)} 我喜欢数量={p0.FavoriteCount?.ToString() ?? "?"}");

        var r = LikeService.Toggle(target, settings);
        sb.AppendLine($"点击: ok={r.Ok} 生效={r.Verified} 方式={r.Tier} → {r.Message}");

        var p1 = LikeService.Probe(target, settings);
        sb.AppendLine($"点击后: 会点='{p1.ButtonName}' 已收藏={Render(p1.Liked)} 我喜欢数量={p1.FavoriteCount?.ToString() ?? "?"}");

        WriteDiag("likeclick.txt", sb.ToString());

        static string Render(bool? v) => v switch { true => "是", false => "否", null => "读不到" };
    }

    private static void WriteDiag(string fileName, string text)
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(Path.Combine(Log.Dir, fileName), text, System.Text.Encoding.UTF8);
        }
        catch
        {
            // ignore
        }

        try
        {
            Console.WriteLine(text);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 端到端验证：连续点两次爱心（互为反向），最后回到原状态。
    /// 用于确认"点击真的生效"，且不会给用户留下多余的收藏。
    /// </summary>
    private static void RunLikeToggle(string[] args, int index)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        var settings = AppSettings.Load();
        string target = args.Length > index + 1 && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : "QQMusic.exe";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 爱心端到端测试（点两次，回到原状态） ===");
        sb.AppendLine($"目标: {target}");
        sb.AppendLine($"窗口: {LikeService.DescribeWindow(target)}");

        var p0 = LikeService.Probe(target, settings, verbose: true);
        sb.AppendLine($"初始: 会点='{p0.ButtonName}' 范围={p0.Scope} 已收藏={Render(p0.Liked)} 我喜欢数量={p0.FavoriteCount?.ToString() ?? "?"}");

        var r1 = LikeService.Toggle(target, settings);
        sb.AppendLine($"第1次点击: ok={r1.Ok} 生效={r1.Verified} 方式={r1.Tier} → 收藏={Render(r1.LikedAfter)} | {r1.Message}");

        var p1 = LikeService.Probe(target, settings);
        sb.AppendLine($"第1次后: 已收藏={Render(p1.Liked)} 我喜欢数量={p1.FavoriteCount?.ToString() ?? "?"}");

        var r2 = LikeService.Toggle(target, settings);
        sb.AppendLine($"第2次点击: ok={r2.Ok} 生效={r2.Verified} 方式={r2.Tier} → 收藏={Render(r2.LikedAfter)} | {r2.Message}");

        var p2 = LikeService.Probe(target, settings);
        sb.AppendLine($"第2次后: 已收藏={Render(p2.Liked)} 我喜欢数量={p2.FavoriteCount?.ToString() ?? "?"}");

        sb.AppendLine();
        sb.AppendLine(p0.FavoriteCount.HasValue && p2.FavoriteCount.HasValue && p0.FavoriteCount == p2.FavoriteCount
            ? $"结论: 数量已回到初始值 {p0.FavoriteCount}，净变化为 0 ✓"
            : $"结论: 数量 {p0.FavoriteCount?.ToString() ?? "?"} → {p2.FavoriteCount?.ToString() ?? "?"}，请人工确认");

        WriteDiag("liketoggle.txt", sb.ToString());

        static string Render(bool? v) => v switch { true => "是", false => "否", null => "读不到" };
    }

    /// <summary>只读测试：报告爱心会命中哪个按钮，不点击（不动用户数据）。</summary>
    private static void RunLikeTest(string[] args, int index)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        var settings = AppSettings.Load();
        string target = args.Length > index + 1 && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : "QQMusic.exe";

        var probe = LikeService.Probe(target, settings, verbose: true);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 爱心命中测试（只读，不点击） ===");
        sb.AppendLine($"目标: {target}");
        sb.AppendLine($"窗口: {LikeService.DescribeWindow(target)}");
        sb.AppendLine($"扫到按钮: {probe.Count} 个（范围={probe.Scope}）");
        sb.AppendLine($"会点: {(probe.ButtonName.Length == 0 ? "(没命中)" : probe.ButtonName)}");
        sb.AppendLine($"当前是否已收藏: {probe.Liked switch { true => "是", false => "否", null => "读不到状态" }}");
        sb.AppendLine($"我喜欢数量: {probe.FavoriteCount?.ToString() ?? "读不到"}");
        sb.AppendLine("（候选打分明细见 log.txt 的「爱心候选」行）");

        string text = sb.ToString();

        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(Path.Combine(Log.Dir, "liketest.txt"), text, System.Text.Encoding.UTF8);
        }
        catch
        {
            // ignore
        }

        try
        {
            Console.WriteLine(text);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>诊断：dump 播放器窗口的 UIA 树，用来定位「我喜欢」按钮的真实名字。</summary>
    private static void RunUiaDump(string[] args, int index)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        string target = args.Length > index + 1 && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : "QQMusic";

        string text = UiaDump.Dump(target);

        string path = Path.Combine(Log.Dir, $"uia-{target}.txt");
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(path, text, System.Text.Encoding.UTF8);
        }
        catch
        {
            // ignore
        }

        try
        {
            Console.WriteLine($"UIA dump -> {path}");
        }
        catch
        {
            // ignore
        }
    }

    private static void RunShot(string[] args, int shotIndex)
    {
        string path = args.Length > shotIndex + 1 && !args[shotIndex + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[shotIndex + 1]
            : Path.Combine(Log.Dir, $"shot-{DateTime.Now:HHmmss}.png");

        bool full = args.Any(a => string.Equals(a, "--full", StringComparison.OrdinalIgnoreCase));

        int x = 0;
        int y = 0;
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        var rectArg = args.FirstOrDefault(a => a.StartsWith("--rect=", StringComparison.OrdinalIgnoreCase));
        if (rectArg is not null)
        {
            var parts = rectArg["--rect=".Length..].Split(',');
            if (parts.Length == 4
                && int.TryParse(parts[0], out int rx)
                && int.TryParse(parts[1], out int ry)
                && int.TryParse(parts[2], out int rw)
                && int.TryParse(parts[3], out int rh))
            {
                x = rx;
                y = ry;
                w = rw;
                h = rh;
                full = true;
            }
        }

        if (!full)
        {
            // 默认只截主显示器底部任务栏那一条
            var bar = TaskbarHost.DiscoverPrimary();
            if (bar is not null)
            {
                x = bar.Rect.Left;
                y = bar.Rect.Top - (int)Math.Round(8 * bar.Scale);
                w = bar.Rect.Width;
                h = bar.Rect.Height + (int)Math.Round(8 * bar.Scale);
            }
        }

        bool ok = ScreenShot.Capture(path, x, y, w, h);
        Log.Write($"截图 {(ok ? "成功" : "失败")}: {path} ({x},{y},{w},{h})");

        try
        {
            Console.WriteLine(ok ? "OK " + path : "FAIL");
        }
        catch
        {
            // ignore
        }
    }

    private static async Task RunDiagnosticsAsync()
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        var media = new MediaSessionService(new[] { "QQMusic", "QQ音乐" });
        await media.InitAsync();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 任务栏播控 · 媒体会话诊断 ===");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"进程: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        sb.AppendLine(media.DescribeSessions());
        sb.AppendLine("提示: 若这里没有 QQ 音乐，请到 QQ 音乐 设置 → 常规设置 → 通知 中开启「显示系统媒体传输控件(SMTC)」。");

        string text = sb.ToString();

        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(Path.Combine(Log.Dir, "diagnostics.txt"), text, System.Text.Encoding.UTF8);
        }
        catch
        {
            // ignore
        }

        try
        {
            Console.WriteLine(text);
        }
        catch
        {
            // 没有控制台时忽略
        }
    }
}
