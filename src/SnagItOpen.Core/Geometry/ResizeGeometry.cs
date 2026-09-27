namespace SnagItOpen.Core.Geometry;

/// <summary>Resize handle positions.</summary>
public enum ResizeHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>Resize math: the opposite corner/edge stays fixed; aspect is locked by default.</summary>
public static class ResizeGeometry
{
    /// <summary>
    /// Computes new bounds when <paramref name="handle"/> is dragged by (dx, dy) document pixels.
    /// With <paramref name="keepAspect"/>, corner handles preserve the original aspect ratio.
    /// Result is at least 1×1 and rounded once.
    /// </summary>
    public static PixelRect Resize(PixelRect start, ResizeHandle handle, double dx, double dy, bool keepAspect)
    {
        double l = start.X, t = start.Y, r = start.Right, b = start.Bottom;
        bool left = handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft;
        bool right = handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight;
        bool top = handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight;
        bool bottom = handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight;

        if (left) l += dx;
        if (right) r += dx;
        if (top) t += dy;
        if (bottom) b += dy;

        // Keep at least 1px, never flip past the fixed edge.
        if (left) l = Math.Min(l, r - 1);
        if (right) r = Math.Max(r, l + 1);
        if (top) t = Math.Min(t, b - 1);
        if (bottom) b = Math.Max(b, t + 1);

        bool corner = (left || right) && (top || bottom);
        if (keepAspect && corner && start.Width > 0 && start.Height > 0)
        {
            double aspect = (double)start.Width / start.Height;
            double w = r - l, h = b - t;
            // Follow whichever axis changed proportionally more.
            if (Math.Abs(w / start.Width - 1) >= Math.Abs(h / start.Height - 1)) h = w / aspect;
            else w = h * aspect;
            w = Math.Max(1, w); h = Math.Max(1, h);
            if (left) l = r - w; else r = l + w;
            if (top) t = b - h; else b = t + h;
        }
        return RectD.FromEdges(l, t, r, b).ToPixelRectRounded() is var p && p.Width >= 1 && p.Height >= 1
            ? p
            : new PixelRect((int)Math.Round(l), (int)Math.Round(t), Math.Max(1, p.Width), Math.Max(1, p.Height));
    }

    /// <summary>Sets width, optionally deriving height from aspect ratio. Top-left stays fixed.</summary>
    public static PixelRect WithWidth(PixelRect r, int width, bool keepAspect)
    {
        width = Math.Max(1, width);
        int h = keepAspect && r.Width > 0 ? Math.Max(1, MathUtil.RoundAway((double)width * r.Height / r.Width)) : r.Height;
        return r with { Width = width, Height = h };
    }

    public static PixelRect WithHeight(PixelRect r, int height, bool keepAspect)
    {
        height = Math.Max(1, height);
        int w = keepAspect && r.Height > 0 ? Math.Max(1, MathUtil.RoundAway((double)height * r.Width / r.Height)) : r.Width;
        return r with { Width = w, Height = height };
    }
}
