using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Layout;

/// <summary>A snap target line along one axis.</summary>
public readonly record struct SnapLine(bool Vertical, double Position, string SourceId);

/// <summary>Result of snapping a moving rectangle: adjusted offset plus lines to draw as guides.</summary>
public sealed record SnapResult(double Dx, double Dy, SnapLine[] Guides);

/// <summary>
/// Edge/center snapping. Tolerance is expressed in viewport DIPs and converted by zoom so the
/// perceived tolerance is constant. Ties are broken by distance, then by source ID (ordinal).
/// </summary>
public static class SnapEngine
{
    public const double DefaultToleranceDips = 6;

    public sealed record Candidate(string Id, RectD Rect);

    /// <summary>
    /// Snaps <paramref name="moving"/> translated by (dx, dy) to candidate edges/centers.
    /// </summary>
    public static SnapResult SnapMove(RectD moving, double dx, double dy, IReadOnlyList<Candidate> targets, double zoom, double toleranceDips = DefaultToleranceDips)
    {
        double tol = toleranceDips / Math.Max(zoom, 1e-6);
        var r = moving.Offset(dx, dy);
        var (bx, gx) = Best([r.X, r.Center.X, r.Right], targets, vertical: true, tol);
        var (by, gy) = Best([r.Y, r.Center.Y, r.Bottom], targets, vertical: false, tol);
        var guides = new List<SnapLine>();
        if (gx is { } lx) guides.Add(lx);
        if (gy is { } ly) guides.Add(ly);
        return new SnapResult(dx + bx, dy + by, guides.ToArray());
    }

    /// <summary>Snaps a single value (e.g. a resize edge) along one axis.</summary>
    public static (double Value, SnapLine? Guide) SnapValue(double value, bool vertical, IReadOnlyList<Candidate> targets, double zoom, double toleranceDips = DefaultToleranceDips)
    {
        double tol = toleranceDips / Math.Max(zoom, 1e-6);
        var (d, g) = Best([value], targets, vertical, tol);
        return (value + d, g);
    }

    private static (double Delta, SnapLine? Guide) Best(double[] probes, IReadOnlyList<Candidate> targets, bool vertical, double tol)
    {
        double bestDist = double.MaxValue, bestDelta = 0;
        SnapLine? guide = null;
        string bestId = "";
        foreach (var t in targets)
        {
            double[] lines = vertical ? [t.Rect.X, t.Rect.Center.X, t.Rect.Right] : [t.Rect.Y, t.Rect.Center.Y, t.Rect.Bottom];
            foreach (var line in lines)
                foreach (var p in probes)
                {
                    double delta = line - p, dist = Math.Abs(delta);
                    if (dist > tol) continue;
                    bool better = dist < bestDist - 1e-9 ||
                                  (Math.Abs(dist - bestDist) <= 1e-9 && string.CompareOrdinal(t.Id, bestId) < 0);
                    if (!better) continue;
                    bestDist = dist; bestDelta = delta; bestId = t.Id;
                    guide = new SnapLine(vertical, line, t.Id);
                }
        }
        return (bestDelta, guide);
    }
}
