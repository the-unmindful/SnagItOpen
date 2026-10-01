using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Capture;

/// <summary>Physical-pixel placement shared by per-monitor capture chrome.</summary>
public static class CaptureChromePlacement
{
    public static PixelRect ActionBar(PixelRect selection, PixelRect monitor, PixelSize size, int gap)
    {
        int y = selection.Bottom + gap;
        if (y + size.Height > monitor.Bottom) y = selection.Y - gap - size.Height;
        if (y < monitor.Y) y = selection.Y + gap;
        return Clamp(selection.X, y, monitor, size);
    }

    public static PixelRect Loupe(PixelPoint cursor, PixelRect monitor, PixelSize size, int gap)
    {
        int x = cursor.X + gap, y = cursor.Y + gap;
        if (x + size.Width > monitor.Right) x = cursor.X - gap - size.Width;
        if (y + size.Height > monitor.Bottom) y = cursor.Y - gap - size.Height;
        return Clamp(x, y, monitor, size);
    }

    private static PixelRect Clamp(int x, int y, PixelRect monitor, PixelSize size)
    {
        int w = Math.Min(size.Width, monitor.Width), h = Math.Min(size.Height, monitor.Height);
        return new PixelRect(Math.Clamp(x, monitor.X, monitor.Right - w),
            Math.Clamp(y, monitor.Y, monitor.Bottom - h), w, h);
    }
}
