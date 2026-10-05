using Microsoft.Win32;

namespace TaskbarMediaBar.Services;

/// <summary>开机自启（写 HKCU\...\Run，不需要管理员）。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarMediaBar";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (enabled && string.IsNullOrEmpty(exe))
            {
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            Log.Write($"开机自启 => {enabled}");
            return IsEnabled() == enabled;
        }
        catch (Exception ex)
        {
            Log.Write("设置开机自启失败: " + ex.Message);
            return false;
        }
    }
}
