using System.Drawing;

namespace DingLater.App.Services;

internal static class TrayHoverPlacement
{
    internal static Rectangle Calculate(Rectangle workArea, Rectangle anchor, Size size, int gap)
    {
        var width = Math.Min(size.Width, Math.Max(1, workArea.Width - 2 * gap));
        var height = Math.Min(size.Height, Math.Max(1, workArea.Height - 2 * gap));
        var x = Math.Clamp(anchor.Right - width + gap, workArea.Left + gap, workArea.Right - gap - width);
        var y = anchor.Top - gap - height;
        if (y < workArea.Top + gap) y = anchor.Bottom + gap;
        y = Math.Clamp(y, workArea.Top + gap, workArea.Bottom - gap - height);
        return new Rectangle(x, y, width, height);
    }
}
