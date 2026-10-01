using System.Windows.Input;
namespace SnagItOpen.App.Editor;
public sealed record ToolDescriptor(ToolKind Kind, string Name, string Icon, Key Shortcut, string Description, int Group);
public static class ToolCatalog
{
    public static IReadOnlyList<ToolDescriptor> All { get; } = Array.AsReadOnly<ToolDescriptor>([
        new(ToolKind.Select, "Select", "Icon.Select", Key.V, "Select, move and resize", 0),
        new(ToolKind.Crop, "Crop", "Icon.Crop", Key.C, "Drag over an image to keep that area", 0),
        new(ToolKind.CutOut, "Cut out", "Icon.CutOut", Key.U, "Remove a strip of rows or columns", 0),
        new(ToolKind.Arrow, "Arrow", "Icon.Arrow", Key.A, "Draw an arrow", 1),
        new(ToolKind.Line, "Line", "Icon.Line", Key.L, "Draw a straight line", 1),
        new(ToolKind.Freehand, "Pen", "Icon.Pen", Key.P, "Draw freehand", 1),
        new(ToolKind.Highlight, "Highlight", "Icon.Highlight", Key.H, "Translucent highlighter", 1),
        new(ToolKind.Rectangle, "Rectangle", "Icon.Rectangle", Key.R, "Draw a rectangle", 2),
        new(ToolKind.Ellipse, "Ellipse", "Icon.Ellipse", Key.E, "Draw an ellipse", 2),
        new(ToolKind.Text, "Text", "Icon.Text", Key.T, "Add text", 3),
        new(ToolKind.Callout, "Callout", "Icon.Callout", Key.K, "Text with a pointer", 3),
        new(ToolKind.Step, "Step", "Icon.Step", Key.N, "Numbered steps", 3),
        new(ToolKind.Stamp, "Stamp", "Icon.Stamp", Key.S, "Place a symbol", 3),
        new(ToolKind.Redaction, "Redact", "Icon.Redact", Key.D, "Secure: solid fill in exported images", 4),
        new(ToolKind.Blur, "Blur", "Icon.Blur", Key.B, "Visual only, not secure. Use Redact to hide information securely", 4),
        new(ToolKind.Pixelate, "Pixelate", "Icon.Pixelate", Key.X, "Visual only, not secure. Use Redact to hide information securely", 4),
        new(ToolKind.Magnifier, "Magnify", "Icon.Magnify", Key.M, "Enlarge a detail", 4),
    ]);
    public static ToolDescriptor Get(ToolKind kind) => All.First(t => t.Kind == kind);
    public static ToolDescriptor? ForKey(Key key) => All.FirstOrDefault(t => t.Shortcut == key);
}
