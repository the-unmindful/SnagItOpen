using System.Globalization;
using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Documents;

namespace SnagItOpen.App.Shell.DialogWindows;

public sealed record ScaleRequest(int Width, int Height, bool ScaleContent, double FactorX, double FactorY)
{
    public double Factor => FactorX;
}
public sealed record IntervalRequest(int Seconds, int Frames, CaptureDestination Destination);

/// <summary>Validation independent of focus, controls, or modal windows.</summary>
public static class DialogValidation
{
    public static string? ScaleError(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width != Math.Truncate(width) || height != Math.Truncate(height)) return "Enter whole pixel dimensions.";
        if (!Limits.IsAcceptableExportSize((long)width, (long)height)) return $"Use dimensions from 1 to {Limits.MaxDimension:N0} px, up to {Limits.MaxExportPixels / 1_000_000} million pixels.";
        return null;
    }
    public static string? SeamError(int overlap, int available) => overlap < 1 || overlap >= available ? $"Overlap must be between 1 and {Math.Max(0, available - 1)}." : null;
    public static string? OverlapError(int overlap, int available, bool allowZero) => allowZero && overlap == 0 && available > 0 ? null : SeamError(overlap, available);
    public static string? IntervalError(int seconds, int frames) => seconds is < 1 or > 60 ? "Use an interval from 1 to 60 seconds." : frames is < 1 or > 200 ? "Use 1 to 200 frames." : null;
    public static bool TryWhole(string text, out int value) => int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value);
}
