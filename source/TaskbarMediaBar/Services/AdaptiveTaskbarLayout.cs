using TaskbarMediaBar.Interop;

namespace TaskbarMediaBar.Services;

public sealed record AdaptiveLayout(int X, int Y, int Width, int Height, double ContentScale)
{
    public bool Fits => Width > 0 && Height > 0;
}

/// <summary>Pure physical-pixel placement; no window mutation or persisted-setting changes.</summary>
public static class AdaptiveTaskbarLayout
{
    public static AdaptiveLayout Compute(TaskbarInfo bar, AppSettings settings,
        NativeMethods.RECT? tray, IReadOnlyList<OccupantInfo> occupants)
    {
        double dpi = double.IsFinite(bar.Scale) && bar.Scale > 0 ? bar.Scale : 1;
        var bounds = bar.Rect;
        // Tests and older callers may omit monitor bounds.
        var monitor = bar.MonitorRect.Width > 0 && bar.MonitorRect.Height > 0 ? bar.MonitorRect : bounds;
        int left = Math.Max(bounds.Left, monitor.Left), right = Math.Min(bounds.Right, monitor.Right);
        int top = Math.Max(bounds.Top, monitor.Top), bottom = Math.Min(bounds.Bottom, monitor.Bottom);
        int availableHeight = bottom - top;
        if (!bar.Visible || right <= left || availableHeight < 20 * dpi || bounds.Height > bounds.Width)
            return new(0, 0, 0, 0, 1);
        int pad = Math.Max(1, (int)Math.Round(Math.Min(4 * dpi, availableHeight / 12.0)));
        int height = Math.Min(availableHeight - 2 * pad, (int)Math.Round(54 * dpi));
        double contentScale = Math.Clamp(height / (40 * dpi), 0.5, 1.35);
        left += pad; right -= pad;
        if (tray is { } notification && notification.Right > left && notification.Left < right
            && notification.Bottom > top && notification.Top < bottom)
            right = Math.Min(right, notification.Left - pad);
        int desired = Math.Max(1, (int)Math.Round(Math.Clamp(settings.Width <= 0 ? 360 : settings.Width, 220, 800) * dpi * contentScale));
        int minimum = Math.Max(1, (int)Math.Ceiling(220 * dpi * contentScale));
        int gap = Math.Max(1, (int)Math.Round(6 * dpi));
        var spaces = new List<(int Left, int Right)>();
        int cursor = left;
        var blocks = settings.AvoidOthers ? occupants.Where(o => o.Rect.Bottom > top && o.Rect.Top < bottom)
            .Select(o => (Left: Math.Max(left, o.Rect.Left - gap), Right: Math.Min(right, o.Rect.Right + gap)))
            .Where(b => b.Right > b.Left).OrderBy(b => b.Left).ToArray() : Array.Empty<(int Left, int Right)>();
        foreach (var block in blocks)
        {
            if (block.Left > cursor) spaces.Add((cursor, block.Left));
            cursor = Math.Max(cursor, block.Right);
        }
        if (cursor < right) spaces.Add((cursor, right));
        var usable = spaces.Where(s => s.Right - s.Left >= minimum).ToArray();
        if (usable.Length == 0) return new(0, 0, 0, 0, contentScale);
        int width = Math.Min(desired, usable.Max(s => s.Right - s.Left));
        double offset = settings.Offset * dpi;
        double preferred = (settings.Anchor ?? "left").ToLowerInvariant() switch
        {
            "beforetray" => right - width - offset,
            "center" => left + (right - left - width) / 2.0 + offset,
            _ => left + offset,
        };
        int x = usable.Where(s => s.Right - s.Left >= width)
            .Select(s => (int)Math.Clamp(preferred, s.Left, s.Right - width))
            .OrderBy(p => Math.Abs(p - preferred)).First();
        return new(x - bounds.Left, top - bounds.Top + (availableHeight - height) / 2,
            width, height, contentScale);
    }
}
