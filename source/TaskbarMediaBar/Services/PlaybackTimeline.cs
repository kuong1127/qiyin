using System.Diagnostics;

namespace TaskbarMediaBar.Services;

/// <summary>把播放器的采样时间投影为当前进度；暂停后不继续计时。</summary>
public static class PlaybackTimeline
{
    public static double Project(double position, double duration, bool playing, double rate, double elapsed)
    {
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(position)) return 0;
        if (playing && double.IsFinite(elapsed) && elapsed > 0 && double.IsFinite(rate) && rate > 0)
            position += elapsed * rate;
        return Math.Clamp(position, 0, duration);
    }

    public static double Since(long timestamp) => timestamp > 0 ? Stopwatch.GetElapsedTime(timestamp).TotalSeconds : 0;
}
