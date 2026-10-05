namespace TaskbarMediaBar.Services;

public sealed record MediaSourceState(string Key, bool Playing, bool Preferred);
public sealed record MediaSourceOption(string Key, string Label, bool Playing, bool Selected);

/// <summary>暂停保留来源；只有新开始播放的来源才触发自动切换。</summary>
public sealed class MediaSourceSelector
{
    private Dictionary<string, bool> _previous = new();
    public string? CurrentKey { get; private set; }
    public string? LockedKey { get; private set; }
    public void Select(string key) => LockedKey = CurrentKey = key;
    public void Automatic() => LockedKey = null;

    public string? Update(IReadOnlyList<MediaSourceState> sources, string? heldKey = null)
    {
        bool Exists(string? key) => key is not null && sources.Any(s => s.Key == key);
        if (!Exists(LockedKey)) LockedKey = null;
        string? chosen = Exists(heldKey) ? heldKey : LockedKey;
        if (chosen is null)
        {
            var started = sources.Where(s => s.Playing && (!_previous.TryGetValue(s.Key, out bool wasPlaying) || !wasPlaying));
            chosen = started.OrderByDescending(s => s.Preferred).FirstOrDefault()?.Key;
            if (chosen is null && Exists(CurrentKey)) chosen = CurrentKey;
            chosen ??= sources.OrderByDescending(s => s.Playing).ThenByDescending(s => s.Preferred).FirstOrDefault()?.Key;
        }
        _previous = sources.ToDictionary(s => s.Key, s => s.Playing);
        return CurrentKey = chosen;
    }
}

public static class MediaSourceNames
{
    public static bool IsQq(string id) => id.Contains("QQMusic", StringComparison.OrdinalIgnoreCase) || id.Contains("QQ音乐");
    public static bool IsCloudMusic(string id) => id.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) || id.Contains("网易云音乐");
    public static bool IsMusic(string id) => IsQq(id) || IsCloudMusic(id) || id.Contains("Spotify", StringComparison.OrdinalIgnoreCase);
    public static string Display(string id)
    {
        if (IsQq(id)) return "QQ 音乐";
        if (IsCloudMusic(id)) return "网易云音乐";
        if (id.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (id.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (id.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (id.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        return string.IsNullOrWhiteSpace(id) ? "媒体来源" : id.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
    }
}
