namespace TaskbarMediaBar.Services;

/// <summary>区分播放器观察值与本地点击反馈，避免旧截图覆盖刚发生的点击。</summary>
public sealed class TrackLikeStateCache
{
    public sealed record State(bool? Liked, bool Confirmed, long Revision, DateTime ChangedAt);
    private readonly object _sync = new();
    private readonly Dictionary<string, State> _states = new();
    private long _revision;
    public State Get(string identity)
    {
        lock (_sync) return _states.GetValueOrDefault(identity) ?? new(null, false, _revision, DateTime.MinValue);
    }
    public State Intent(string identity, bool liked, DateTime now)
    {
        lock (_sync)
        {
            var state = new State(liked, false, ++_revision, now);
            _states[identity] = state;
            if (_states.Count > 128)
                _states.Remove(_states.Where(x => x.Key != identity).MinBy(x => x.Value.ChangedAt).Key);
            return state;
        }
    }
    public State Observe(string identity, bool? liked, long capturedRevision, DateTime now)
    {
        lock (_sync)
        {
            var prior = Get(identity);
            if (!liked.HasValue || capturedRevision != prior.Revision) return prior;
            // 发出快捷键后，客户端还没来得及画新的图标；匹配的确认可以立即接收。
            if (!prior.Confirmed && prior.Liked.HasValue && liked != prior.Liked
                && now - prior.ChangedAt < TimeSpan.FromSeconds(1)) return prior;
            var state = new State(liked, true, ++_revision, now);
            _states[identity] = state;
            if (_states.Count > 128)
                _states.Remove(_states.Where(x => x.Key != identity).MinBy(x => x.Value.ChangedAt).Key);
            return state;
        }
    }
    public void Clear() { lock (_sync) { _states.Clear(); _revision++; } }
}
