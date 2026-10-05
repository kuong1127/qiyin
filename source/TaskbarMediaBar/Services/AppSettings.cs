using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarMediaBar.Services;

/// <summary>落盘配置：%APPDATA%\TaskbarMediaBar\settings.json</summary>
public sealed class AppSettings
{
    /// <summary>显示宿主：taskbar（默认）| top（顶部浮岛试用）。</summary>
    public string Placement { get; set; } = "taskbar";

    /// <summary>顶部浮岛水平锚点：left | center | right。</summary>
    public string TopAnchor { get; set; } = "center";

    /// <summary>顶部浮岛显示器：primary | cursor；独立于任务栏选择。</summary>
    public string TopMonitor { get; set; } = "primary";

    /// <summary>顶部水平偏移，逻辑像素；保留任务栏的 Offset。</summary>
    public int TopOffsetX { get; set; } = 0;

    /// <summary>距所选屏幕顶端的逻辑像素偏移。</summary>
    public int TopOffsetY { get; set; } = 8;

    /// <summary>
    /// 落位模式：
    ///  overlay（默认）= 顶层浮层，浮在任务栏上（汽水音乐同款路线，Win11 下唯一稳定的做法）
    ///  dock = SetParent 进 Shell_TrayWnd 当子窗口（会被任务栏合成层遮挡，仅作兼容保留）
    /// </summary>
    public string Mode { get; set; } = "overlay";

    /// <summary>定位锚点：left（任务栏左端）/ beforeTray（通知区左侧）/ center（居中）。</summary>
    public string Anchor { get; set; } = "left";

    /// <summary>锚点偏移（逻辑像素，100% DPI 基准），拖拽会自动写回这里。</summary>
    public int Offset { get; set; } = 0;

    /// <summary>播控条宽度（逻辑像素）。0 = 使用默认 360。</summary>
    public int Width { get; set; } = 360;

    /// <summary>是否显示爱心（点赞）按钮。点赞走播放器 UI 自动化，不是系统接口。</summary>
    public bool ShowLikeButton { get; set; } = true;

    /// <summary>无媒体会话时自动隐藏。</summary>
    public bool HideWhenNoMedia { get; set; } = true;

    /// <summary>优先跟随的播放器（SourceAppUserModelId 匹配子串）。</summary>
    public List<string> PreferredApps { get; set; } = new() { "QQMusic", "QQ音乐", "cloudmusic", "网易云音乐", "Spotify" };

    /// <summary>显示封面。</summary>
    public bool ShowCover { get; set; } = true;

    /// <summary>自动避开任务栏上其他常驻窗口（例如汽水音乐自己那条播控）。</summary>
    public bool AvoidOthers { get; set; } = true;

    /// <summary>用户自定义的「喜欢」按钮名（优先级高于内置规则，命中即用）。</summary>
    public List<string> LikeButtonNames { get; set; } = new();

    /// <summary>启动后一直没检测到任何媒体会话时，显示一条提示而不是静默消失。</summary>
    public bool IdleHint { get; set; } = true;

    /// <summary>显示在哪块屏的任务栏：primary（主屏）| cursor（跟随鼠标所在屏）。</summary>
    public string Monitor { get; set; } = "primary";

    /// <summary>毛玻璃质感（acrylic）。</summary>
    public bool Glass { get; set; } = true;

    /// <summary>在艺人那一行显示歌词（逐字高亮）。</summary>
    public bool ShowLyrics { get; set; } = true;

    /// <summary>
    /// 窗口标题兜底通道：这些播放器不上报 SMTC，只能从窗口标题读「歌名 - 歌手」。
    /// 实测网易云音乐 Win32 原版就是这种（无 SMTC、无 UIA、不响应媒体命令），
    /// 因此只做只读显示（歌名/艺人/封面），控制类按钮置灰。
    /// </summary>
    public List<string> FallbackApps { get; set; } = new() { "cloudmusic", "网易云音乐" };

    /// <summary>
    /// 全局快捷键档案：播放器不上报 SMTC、又没有 UIA 树时的控制通道。
    /// 用户需要在播放器里勾选「启用全局快捷键」。
    /// </summary>
    public List<HotkeyProfile> Hotkeys { get; set; } = new()
    {
        new HotkeyProfile
        {
            App = "cloudmusic",
            PlayPause = "CTRL+ALT+P",
            Next = "CTRL+ALT+RIGHT",
            Prev = "CTRL+ALT+LEFT",
            Like = "CTRL+ALT+L",
        },
    };

    /// <summary>按进程名找快捷键档案（"cloudmusic" 或 "cloudmusic.exe" 都能匹配）。</summary>
    public HotkeyProfile? FindHotkeyProfile(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        string name = appId.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return Hotkeys.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.App)
                                           && h.App.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    [JsonIgnore]
    public static string FilePath => Path.Combine(Log.Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath, System.Text.Encoding.UTF8);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s is not null)
                {
                    if (s.PreferredApps.Count == 0)
                    {
                        s.PreferredApps.Add("QQMusic");
                    }

                    if (s.Width <= 0)
                    {
                        s.Width = 360;
                    }

                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("读取配置失败，使用默认值: " + ex.Message);
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            var json = JsonSerializer.Serialize(this, SerializerOptions);
            File.WriteAllText(FilePath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Write("保存配置失败: " + ex.Message);
        }
    }

    /// <summary>不转义中文，方便直接手改配置文件。</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>某个播放器的全局快捷键组合（形如 "CTRL+ALT+P"）。</summary>
public class HotkeyProfile
{
    public string App { get; set; } = "";

    public string PlayPause { get; set; } = "";

    public string Next { get; set; } = "";

    public string Prev { get; set; } = "";

    public string Like { get; set; } = "";
}
