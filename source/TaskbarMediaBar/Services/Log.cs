using System.IO;
using System.Text;

namespace TaskbarMediaBar.Services;

/// <summary>极简日志：写到 %APPDATA%\TaskbarMediaBar\log.txt，超过 512KB 自动截断。</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string Dir => Environment.GetEnvironmentVariable("TMB_DATA_DIR") is { Length: > 0 } isolated
        ? Path.GetFullPath(isolated)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarMediaBar");

    public static string FilePath => Path.Combine(Dir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    File.Delete(FilePath);
                }

                File.AppendAllText(
                    FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
