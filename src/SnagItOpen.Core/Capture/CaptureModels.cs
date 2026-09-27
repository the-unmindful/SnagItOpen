using System.Text.Json.Serialization;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Capture;

/// <summary>A monitor in physical desktop pixels (origins may be negative).</summary>
public sealed record MonitorInfo(string Id, PixelRect Bounds, PixelRect WorkArea, int DpiX, int DpiY, bool IsPrimary)
{
    public double Scale => DpiX / 96.0;
}

public static class MonitorTopology
{
    /// <summary>Stable fingerprint of IDs, bounds and DPI; changes invalidate stored regions.</summary>
    public static string Fingerprint(IEnumerable<MonitorInfo> monitors) =>
        string.Join(";", monitors.OrderBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => $"{m.Id}@{m.Bounds.X},{m.Bounds.Y},{m.Bounds.Width},{m.Bounds.Height}:{m.DpiX}"));

    public static PixelRect VirtualBounds(IEnumerable<MonitorInfo> monitors)
    {
        PixelRect u = default;
        foreach (var m in monitors) u = u.Union(m.Bounds);
        return u;
    }

    /// <summary>Monitor containing the point, or null when the point is in a gap.</summary>
    public static MonitorInfo? At(IEnumerable<MonitorInfo> monitors, PixelPoint p) =>
        monitors.FirstOrDefault(m => m.Bounds.Contains(p.X, p.Y));

    /// <summary>Monitor with the largest intersection with <paramref name="r"/>.</summary>
    public static MonitorInfo? Dominant(IEnumerable<MonitorInfo> monitors, PixelRect r) =>
        monitors.Select(m => (m, a: m.Bounds.Intersect(r).Area)).Where(t => t.a > 0).OrderByDescending(t => t.a).Select(t => t.m).FirstOrDefault();

    /// <summary>Area of <paramref name="r"/> that lies on any monitor.</summary>
    public static long CoveredArea(IReadOnlyList<MonitorInfo> monitors, PixelRect r) =>
        monitors.Sum(m => m.Bounds.Intersect(r).Area); // monitors never overlap
}

[JsonConverter(typeof(JsonStringEnumConverter<CaptureMode>))]
public enum CaptureMode { Region, Window, Monitor, CurrentMonitor, AllMonitors, LastRegion, Scrolling, MultiRegion, Interval }

[JsonConverter(typeof(JsonStringEnumConverter<CaptureShape>))]
public enum CaptureShape { Rectangle, Ellipse, Freehand }

[JsonConverter(typeof(JsonStringEnumConverter<CaptureDestination>))]
public enum CaptureDestination { NewDocument, AppendBelow, AppendRight, AddToCanvas, CopyOnly }

/// <summary>Selection constraint applied in physical pixel space.</summary>
public sealed record SelectionConstraint
{
    public PixelSize? FixedSize { get; init; }
    /// <summary>Width/height ratio, e.g. 16/9.</summary>
    public double? AspectRatio { get; init; }

    public static readonly SelectionConstraint None = new();

    /// <summary>Constrains a drag from anchor to current point.</summary>
    public PixelRect Apply(PixelPoint anchor, PixelPoint current)
    {
        if (FixedSize is { } fs && !fs.IsEmpty)
            return new PixelRect(current.X - fs.Width / 2, current.Y - fs.Height / 2, fs.Width, fs.Height);
        int dx = current.X - anchor.X, dy = current.Y - anchor.Y;
        if (AspectRatio is { } ar && ar > 0 && double.IsFinite(ar))
        {
            int w = Math.Abs(dx), h = Math.Abs(dy);
            // Grow the smaller axis to match ratio.
            if (w >= h * ar) h = (int)Math.Round(w / ar, MidpointRounding.AwayFromZero);
            else w = (int)Math.Round(h * ar, MidpointRounding.AwayFromZero);
            dx = Math.Sign(dx == 0 ? 1 : dx) * w; dy = Math.Sign(dy == 0 ? 1 : dy) * h;
        }
        var r = PixelRect.FromPoints(anchor, new PixelPoint(anchor.X + dx, anchor.Y + dy));
        return r;
    }
}

public sealed record CaptureOptions
{
    public int DelaySeconds { get; init; }
    public bool IncludeCursor { get; init; }
    public CaptureShape Shape { get; init; } = CaptureShape.Rectangle;
    public SelectionConstraint Constraint { get; init; } = SelectionConstraint.None;
    public CaptureDestination Destination { get; init; } = CaptureDestination.AppendBelow;

    public static readonly int[] AllowedDelays = [0, 3, 5, 10];
}

public enum CaptureStatus { Success, Canceled, Failed }

/// <summary>Captured PNG with its physical bounds.</summary>
public sealed record CaptureResult(CaptureStatus Status, byte[]? Png, PixelRect Bounds, ErrorCode Error = ErrorCode.None, string? Message = null)
{
    public static CaptureResult Ok(byte[] png, PixelRect bounds) => new(CaptureStatus.Success, png, bounds);
    public static CaptureResult Canceled() => new(CaptureStatus.Canceled, null, default);
    public static CaptureResult Fail(ErrorCode code, string message) => new(CaptureStatus.Failed, null, default, code, message);
}

/// <summary>A stored last region with the topology it was captured on.</summary>
public sealed record LastRegion(PixelRect Bounds, string TopologyFingerprint);
