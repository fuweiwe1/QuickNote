namespace QuickNote.Core;

public readonly record struct DockBounds(double Left, double Top, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= Left && x < Left + Width && y >= Top && y < Top + Height;
}

/// <summary>Monotonic-time state machine shared by the real pointer loop and behavior checks.</summary>
public sealed class EdgeDockState
{
    public const long LeaveDelayMilliseconds = 200;
    public bool Expanded { get; private set; }
    private long lastInside;
    private int holds;

    public void Reveal(long now) { Expanded = true; lastInside = now; }
    public void Hold() => holds++;
    public void Release(long now) { if (holds > 0) holds--; lastInside = now; }
    public void KeepAlive(long now) => lastInside = now;

    public bool Update(double x, double y, DockBounds bounds, double edgeThickness, long now)
    {
        var atEdge = x >= bounds.Left && x < bounds.Left + bounds.Width && y >= bounds.Top && y < bounds.Top + edgeThickness;
        if (atEdge || (Expanded && bounds.Contains(x, y))) Reveal(now);
        else if (Expanded && holds == 0 && now - lastInside >= LeaveDelayMilliseconds) Expanded = false;
        return Expanded;
    }
}
