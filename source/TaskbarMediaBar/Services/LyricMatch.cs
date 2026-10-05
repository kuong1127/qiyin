using System.Text;
using System.Text.RegularExpressions;

namespace TaskbarMediaBar.Services;

/// <summary>不把同名翻唱、新旧版或不同长度的录音当成同一条时间轴。</summary>
public static class LyricMatch
{
    public static string Normalize(string value) => string.Concat((value ?? "").Normalize(NormalizationForm.FormKC)
        .ToLowerInvariant().Where(char.IsLetterOrDigit));

    private static string Artists(string value) => string.Join("|", Regex.Split(value ?? "", @"[/&、,;；]")
        .Select(Normalize).Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal));

    public static double Score(string title, string artist, double duration, string expectedTitle, string expectedArtist, double expectedDuration)
    {
        if (Normalize(title) != Normalize(expectedTitle) || Normalize(title).Length == 0) return -1;
        if (Artists(expectedArtist).Length == 0 || Artists(artist) != Artists(expectedArtist)) return -1;
        double delta = Math.Abs(duration - expectedDuration);
        if (expectedDuration > 0 && (!double.IsFinite(duration) || duration <= 0 || delta > Math.Max(2.5, expectedDuration * 0.01))) return -1;
        return 100 - (expectedDuration > 0 ? delta : 0);
    }
}
