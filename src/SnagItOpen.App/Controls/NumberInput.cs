using System.Globalization;

namespace SnagItOpen.App.Controls;

/// <summary>Shared numeric rules, independent of WPF input and focus events.</summary>
public static class NumberInput
{
    public static bool TryParse(string text, double minimum, double maximum, CultureInfo culture, out double value)
    {
        value = 0;
        if (!double.TryParse(text, NumberStyles.Float, culture, out var parsed) || !double.IsFinite(parsed)) return false;
        value = Math.Clamp(parsed, minimum, Math.Max(minimum, maximum));
        return true;
    }
    public static double StepValue(double value, double step, int direction, bool shift, double minimum, double maximum) =>
        Math.Clamp(value + step * direction * (shift ? 10 : 1), minimum, Math.Max(minimum, maximum));
    public static string Format(double value, bool mixed, CultureInfo culture) => mixed ? "—" : value.ToString("0.###", culture);
}
