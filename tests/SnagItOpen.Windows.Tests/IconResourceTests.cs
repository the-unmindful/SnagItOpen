using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace SnagItOpen.Windows.Tests;

/// <summary>U04: every Icon.* geometry parses and fits inside the 16x16 grid; the PRD 5.5 set is complete.</summary>
public sealed class IconResourceTests
{
    internal static readonly string[] Required =
    [
        "Select", "Crop", "Arrow", "Line", "Rectangle", "Ellipse", "Text", "Callout", "Highlight", "Step", "Pen",
        "Redact", "Blur", "Pixelate", "Magnify", "Stamp", "CutOut",
        "Capture", "CaptureWindow", "CaptureScreen", "CaptureScroll", "Import", "Paste", "Undo", "Redo", "Copy",
        "Export", "Save", "Pin", "DragOut", "Vertical", "Horizontal", "Free", "ZoomIn", "ZoomOut", "Fit", "Eye",
        "EyeOff", "Lock", "Unlock", "Front", "Forward", "Backward", "Back", "AlignLeft", "AlignCenterX", "AlignRight",
        "AlignTop", "AlignMiddle", "AlignBottom", "DistributeH", "DistributeV", "Trash", "Duplicate", "More",
        "Search", "Settings", "Help", "Close", "Check", "Warning", "Info", "Error", "Library", "Palette",
        "ChevronDown", "ChevronRight", "Plus", "Minus", "Reset",
    ];

    [Fact]
    public void Every_icon_parses_and_fits_in_16x16_and_the_required_set_exists()
    {
        var problems = ThemeTokenTests.RunSta(() =>
        {
            var list = new List<string>();
            using var s = File.OpenRead(Path.Combine(ThemeTokenTests.ThemesFolder(), "Icons.xaml"));
            var d = (ResourceDictionary)XamlReader.Load(s);
            var keys = d.Keys.OfType<string>().ToHashSet();
            foreach (var name in Required)
                if (!keys.Contains("Icon." + name)) list.Add("missing Icon." + name);
            foreach (var key in keys.Where(k => k.StartsWith("Icon.", StringComparison.Ordinal)))
            {
                if (d[key] is not Geometry g) { list.Add(key + " is not a Geometry"); continue; }
                var b = g.Bounds;
                if (b.IsEmpty) { list.Add(key + " is empty"); continue; }
                const double eps = 0.01;
                if (b.Left < -eps || b.Top < -eps || b.Right > 16 + eps || b.Bottom > 16 + eps)
                    list.Add($"{key} bounds {b} exceed 0..16");
            }
            return list;
        });
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
