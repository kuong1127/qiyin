namespace TaskbarMediaBar.Services;

/// <summary>拒绝重复点击和过期结果；后台操作不需要访问 WPF 控件。</summary>
public sealed class TrackOperationGate
{
    public readonly record struct Ticket(long Generation, long Request, string Identity);
    private readonly object _sync = new();
    private string _identity = "";
    private long _generation;
    private long _request;
    private bool _busy;
    private Ticket _active;

    public bool Busy { get { lock (_sync) return _busy; } }
    public bool CurrentBusy { get { lock (_sync) return _busy && IsCurrent(_active); } }
    public void Observe(string identity)
    {
        lock (_sync)
        {
            if (_identity == identity) return;
            _identity = identity;
            _generation++;
        }
    }

    public Ticket Capture() { lock (_sync) return new(_generation, _request, _identity); }
    public bool TryBegin(out Ticket ticket)
    {
        lock (_sync)
        {
            ticket = default;
            if (_busy || string.IsNullOrEmpty(_identity)) return false;
            _busy = true;
            ticket = new(_generation, ++_request, _identity);
            _active = ticket;
            return true;
        }
    }

    public bool IsCurrent(Ticket ticket)
    {
        lock (_sync)
            return ticket.Generation == _generation && ticket.Request == _request
                   && ticket.Identity == _identity;
    }

    public void End(Ticket ticket)
    {
        lock (_sync)
            if (ticket.Request == _request) _busy = false;
    }
}
