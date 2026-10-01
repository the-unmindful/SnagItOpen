using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Layout;

/// <summary>A measured edge-to-edge gap in document pixels, with marker endpoints.</summary>
public sealed record SpacingDistance(bool Horizontal, PointD Start, PointD End, double Pixels, string SourceId);
public sealed record EqualSpacing(SpacingDistance Moving, SpacingDistance Reference);
public sealed record SpacingSnapResult(double Dx, double Dy, EqualSpacing[] Equal);

/// <summary>Pure neighbour measurements and equal-gap detection. No viewport or UI state is retained.</summary>
public static class SpacingGuides
{
    public const double EqualityTolerancePixels = 1;

    /// <summary>Non-overlapping edge distances to one object. Diagonal objects have two distances.</summary>
    public static SpacingDistance[] Between(RectD moving, RectD target, string id)
    {
        var result = new List<SpacingDistance>();
        foreach (bool horizontal in new[] { true, false })
            if (Gap(moving, target, id, horizontal) is { } gap) result.Add(gap);
        return result.ToArray();
    }

    /// <summary>Nearest non-overlapping neighbour on each axis, sharing that axis's row or column.</summary>
    public static SpacingDistance[] Nearest(RectD moving, IReadOnlyList<SnapEngine.Candidate> targets)
    {
        var result = new List<SpacingDistance>();
        foreach (bool horizontal in new[] { true, false })
        {
            var nearest = targets.Where(t => CrossOverlap(moving, t.Rect, horizontal))
                .Select(t => Gap(moving, t.Rect, t.Id, horizontal)).OfType<SpacingDistance>()
                .OrderBy(g => g.Pixels).ThenBy(g => g.SourceId, StringComparer.Ordinal).FirstOrDefault();
            if (nearest is not null) result.Add(nearest);
        }
        return result.ToArray();
    }

    /// <summary>Returns at most one pair of equal-spacing markers per axis (within ±1 document pixel).</summary>
    public static EqualSpacing[] Detect(RectD moving, IReadOnlyList<SnapEngine.Candidate> targets)
    {
        var result = new List<EqualSpacing>();
        foreach (bool horizontal in new[] { true, false })
        {
            var references = References(targets, horizontal, moving).ToArray();
            var match = targets.Where(t => CrossOverlap(moving, t.Rect, horizontal))
                .Select(t => Gap(moving, t.Rect, t.Id, horizontal)).OfType<SpacingDistance>()
                .SelectMany(g => references.Where(r => Math.Abs(r.Pixels - g.Pixels) <= EqualityTolerancePixels)
                    .Select(r => new EqualSpacing(g, r)))
                .OrderBy(e => Math.Abs(e.Moving.Pixels - e.Reference.Pixels))
                .ThenBy(e => e.Moving.Pixels).ThenBy(e => e.Moving.SourceId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (match is not null) result.Add(match);
        }
        return result.ToArray();
    }

    /// <summary>Finds the closest equal-gap position within a document-pixel snap tolerance.</summary>
    public static SpacingSnapResult Snap(RectD moving, IReadOnlyList<SnapEngine.Candidate> targets, double tolerancePixels)
    {
        double dx = BestAdjustment(moving, targets, true, tolerancePixels);
        double dy = BestAdjustment(moving, targets, false, tolerancePixels);
        return new(dx, dy, Detect(moving.Offset(dx, dy), targets));
    }

    private static double BestAdjustment(RectD moving, IReadOnlyList<SnapEngine.Candidate> targets, bool horizontal, double tolerance)
    {
        double best = double.MaxValue, delta = 0;
        string bestId = "";
        foreach (var reference in References(targets, horizontal, moving))
            foreach (var target in targets)
            {
                if (!CrossOverlap(moving, target.Rect, horizontal)) continue;
                double start = Start(moving, horizontal), end = End(moving, horizontal);
                double ts = Start(target.Rect, horizontal), te = End(target.Rect, horizontal);
                double adjustment;
                if (start >= te) adjustment = te + reference.Pixels - start;
                else if (end <= ts) adjustment = ts - reference.Pixels - end;
                else continue;
                double distance = Math.Abs(adjustment);
                if (distance > tolerance) continue;
                if (distance < best - 1e-9 || (Math.Abs(distance - best) < 1e-9 && string.CompareOrdinal(target.Id, bestId) < 0))
                {
                    best = distance; delta = adjustment; bestId = target.Id;
                }
            }
        return delta;
    }

    private static IEnumerable<SpacingDistance> References(IReadOnlyList<SnapEngine.Candidate> targets, bool horizontal, RectD moving)
    {
        for (int i = 0; i < targets.Count; i++)
            for (int j = i + 1; j < targets.Count; j++)
            {
                var a = targets[i]; var b = targets[j];
                if (!CrossOverlap(a.Rect, b.Rect, horizontal) || !CrossOverlap(moving, a.Rect, horizontal) || !CrossOverlap(moving, b.Rect, horizontal)) continue;
                var gap = Gap(a.Rect, b.Rect, a.Id + ":" + b.Id, horizontal);
                if (gap is null) continue;
                // A pair with another neighbour in its gap is not adjacent.
                double low = Math.Min(horizontal ? gap.Start.X : gap.Start.Y, horizontal ? gap.End.X : gap.End.Y);
                double high = low + gap.Pixels;
                bool blocked = targets.Where((_, k) => k != i && k != j).Any(t =>
                    CrossOverlap(a.Rect, t.Rect, horizontal) && Start(t.Rect, horizontal) < high && End(t.Rect, horizontal) > low);
                if (!blocked) yield return gap;
            }
    }

    private static SpacingDistance? Gap(RectD a, RectD b, string id, bool horizontal)
    {
        double a0 = Start(a, horizontal), a1 = End(a, horizontal), b0 = Start(b, horizontal), b1 = End(b, horizontal);
        double from, to;
        if (a1 <= b0) { from = a1; to = b0; }
        else if (b1 <= a0) { from = b1; to = a0; }
        else return null;
        double cross = horizontal ? (a.Center.Y + b.Center.Y) / 2 : (a.Center.X + b.Center.X) / 2;
        return horizontal ? new(true, new(from, cross), new(to, cross), to - from, id)
            : new(false, new(cross, from), new(cross, to), to - from, id);
    }

    private static double Start(RectD r, bool horizontal) => horizontal ? r.X : r.Y;
    private static double End(RectD r, bool horizontal) => horizontal ? r.Right : r.Bottom;
    private static bool CrossOverlap(RectD a, RectD b, bool horizontal) => horizontal
        ? a.Y < b.Bottom && b.Y < a.Bottom : a.X < b.Right && b.X < a.Right;
}
