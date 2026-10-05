using TaskbarMediaBar.Interop;
using System.Runtime.InteropServices;

namespace TaskbarMediaBar.Services;

/// <summary>
/// 全局快捷键发送器。
///
/// 用在"播放器不上报 SMTC、又没有 UIA 树"的场景（网易云就是）：
/// 网易云自带全局快捷键（默认 喜欢歌曲 Ctrl+Alt+L、播放暂停 Ctrl+Alt+P、
/// 上一首 Ctrl+Alt+←、下一首 Ctrl+Alt+→），把它当控制通道用，
/// 比往客户端里注入插件干净得多——零注入、零卡顿。
///
/// 前提：用户要在播放器「设置 → 快捷键」里勾选「启用全局快捷键」。
/// </summary>
public static class HotkeySender
{
    private static readonly object SendLock = new();
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputData { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputData Data; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private static Input Key(byte key, bool up) => new()
    {
        Type = 1,
        Data = new InputData { Keyboard = new KeyboardInput { Key = key,
            Flags = (up ? NativeMethods.KEYEVENTF_KEYUP : 0) | (key is >= 0x25 and <= 0x28 ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0) } }
    };
    /// <summary>发送形如 "CTRL+ALT+L" / "CTRL+ALT+RIGHT" 的组合键。</summary>
    public static bool Send(string combo, out string error, Func<bool>? isCurrent = null)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(combo))
        {
            error = "未配置快捷键";
            return false;
        }

        var codes = new List<byte>();
        foreach (var raw in combo.Split('+'))
        {
            string k = raw.Trim().ToUpperInvariant();
            if (k.Length == 0)
            {
                continue;
            }

            byte? vk = k switch
            {
                "CTRL" or "CONTROL" => 0x11,
                "ALT" => 0x12,
                "SHIFT" => 0x10,
                "WIN" => 0x5B,
                "LEFT" => 0x25,
                "RIGHT" => 0x27,
                "UP" => 0x26,
                "DOWN" => 0x28,
                "SPACE" => 0x20,
                "ENTER" => 0x0D,
                _ => k.Length == 1 ? (byte)char.ToUpperInvariant(k[0]) : null,
            };

            if (vk is null)
            {
                error = $"无法识别的按键: {raw}";
                return false;
            }

            if (!codes.Contains(vk.Value)) codes.Add(vk.Value);
        }

        if (codes.Count == 0 || codes.All(k => k is 0x10 or 0x11 or 0x12 or 0x5B))
        {
            error = "快捷键缺少主键";
            return false;
        }

        try
        {
            lock (SendLock)
            {
                if (isCurrent?.Invoke() == false)
                {
                    error = "当前歌曲或播放器已改变";
                    return false;
                }
                // 一次提交按下与抬起，收藏和播放不会把各自的 Ctrl/Alt 交叉释放。
                // 用户正按着修饰键时不注入，也不释放用户自己的按键。
                if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Concat(codes.Select(x => (int)x))
                    .Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
                {
                    error = "请松开键盘按键后重试";
                    return false;
                }
                var inputs = codes.Select(k => Key(k, false)).Concat(codes.AsEnumerable().Reverse().Select(k => Key(k, true))).ToArray();
                uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
                if (sent != inputs.Length)
                {
                    // 部分投递失败时，只清理本次可能按下的键；不重试整条指令。
                    if (sent > 0)
                    {
                        var release = codes.Take(Math.Min((int)sent, codes.Count)).Reverse().Select(k => Key(k, true)).ToArray();
                        SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
                    }
                    error = "按键指令未完整发送";
                    return false;
                }
            }

            Log.Write($"快捷键: 已发送 {combo}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
