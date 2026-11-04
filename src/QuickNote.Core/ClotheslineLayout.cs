using System;

namespace QuickNote.Core;

public static class ClotheslineLayout
{
    public const double CardWidth = 154;
    public const double CardHeight = 216;
    public const double NotebookLeft = 24;
    public const double NotebookWidth = 108;
    public const double FirstCardLeft = 166;
    public const double RightInset = 22;

    // Keep every card inside the monitor. Shrink gaps, not the paper, as the rope fills.
    public static double[] Positions(double width, int count)
    {
        if (!double.IsFinite(width) || width < 0 || count < 0) throw new ArgumentOutOfRangeException();
        var positions = new double[count];
        if (count == 0) return positions;
        var start = Math.Min(FirstCardLeft, Math.Max(0, width - RightInset - CardWidth));
        var span = Math.Max(0, width - RightInset - CardWidth - start);
        var step = count == 1 ? 0 : Math.Min(CardWidth + 18, span / (count - 1));
        for (var i = 0; i < count; i++) positions[i] = start + i * step;
        return positions;
    }

    public static double RopeY(double x, double width)
    {
        var t = width > 0 ? Math.Clamp(x / width, 0, 1) : 0;
        return 12 + 60 * t * (1 - t);
    }
}
