namespace SnagItOpen.Core.Geometry;

/// <summary>
/// Document-pixel ↔ viewport-DIP mapping: viewport = document * Zoom + Pan.
/// DPI is never baked into document coordinates.
/// </summary>
public readonly record struct ViewportTransform(double Zoom, double PanX, double PanY)
{
    public const double MinZoom = 0.1;
    public const double MaxZoom = 8.0;

    public static readonly ViewportTransform Identity = new(1, 0, 0);

    public PointD ToViewport(PointD doc) => new(doc.X * Zoom + PanX, doc.Y * Zoom + PanY);
    public PointD ToDocument(PointD view) => new((view.X - PanX) / Zoom, (view.Y - PanY) / Zoom);

    public RectD ToViewport(RectD r) => new(r.X * Zoom + PanX, r.Y * Zoom + PanY, r.Width * Zoom, r.Height * Zoom);
    public RectD ToDocument(RectD r) => new((r.X - PanX) / Zoom, (r.Y - PanY) / Zoom, r.Width / Zoom, r.Height / Zoom);

    /// <summary>Converts a viewport length (e.g. snap tolerance in DIPs) into document pixels.</summary>
    public double ViewportToDocumentLength(double dips) => dips / Zoom;

    /// <summary>Zooms so the document point under <paramref name="anchor"/> stays under it.</summary>
    public ViewportTransform ZoomAbout(PointD anchor, double newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        var docPt = ToDocument(anchor);
        return new ViewportTransform(newZoom, anchor.X - docPt.X * newZoom, anchor.Y - docPt.Y * newZoom);
    }

    public ViewportTransform PanBy(double dx, double dy) => this with { PanX = PanX + dx, PanY = PanY + dy };

    /// <summary>Fits <paramref name="content"/> into a viewport of the given size with a margin.</summary>
    public static ViewportTransform Fit(RectD content, double viewWidth, double viewHeight, double margin = 24, double maxZoom = 1)
    {
        if (content.IsEmpty || viewWidth <= 0 || viewHeight <= 0) return Identity;
        double z = Math.Min((viewWidth - 2 * margin) / content.Width, (viewHeight - 2 * margin) / content.Height);
        z = Math.Clamp(Math.Min(z, maxZoom), MinZoom, MaxZoom);
        double px = (viewWidth - content.Width * z) / 2 - content.X * z;
        double py = (viewHeight - content.Height * z) / 2 - content.Y * z;
        return new ViewportTransform(z, px, py);
    }
}
