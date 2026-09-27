using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging.Rendering;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Editor;

/// <summary>
/// Live properties for the selected annotations, or (nothing selected) the style the current tool gives
/// new items. Sliders and the colour picker preview live on the canvas and commit one undo step when the
/// gesture ends. Multi-selection shows shared values; differing values show as "mixed".
/// </summary>
internal sealed class AnnotationPropertiesPanel : StackPanel
{
    private static readonly Dictionary<string, bool> Expanded = new(StringComparer.Ordinal)
    {
        ["Style"] = true, ["Text"] = true, ["Shape"] = true, ["Line"] = true, ["Step"] = true, ["Arrange"] = false, ["Quick styles"] = false,
    };

    private static string[]? _fonts;
    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private readonly Func<ToolKind> _tool;
    private readonly Func<ToolKind, ToolStyle> _toolStyle;
    private List<Annotation> _targets = [];
    private Annotation? _sample;
    private string? _protoKind;
    private bool _previewing;
    private StackPanel _into;

    public AnnotationPropertiesPanel(AppServices services, EditorViewModel vm, Func<ToolKind> tool, Func<ToolKind, ToolStyle> toolStyle)
    {
        _services = services;
        _vm = vm;
        _tool = tool;
        _toolStyle = toolStyle;
        _into = this;
        AutomationProperties.SetName(this, "Annotation properties");
    }

    private static string[] FontNames => _fonts ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToArray();

    private static bool IsAnnotationTool(ToolKind k) => k is not (ToolKind.Select or ToolKind.Crop or ToolKind.CutOut or ToolKind.Blur or ToolKind.Pixelate);

    // ================================================================== build

    public void Refresh()
    {
        EndPreview();
        var sv = FindScroll();
        double offset = sv?.VerticalOffset ?? 0;
        Children.Clear();
        _into = this;
        var doc = _vm.Document;
        _targets = _vm.SelectedAnnotations.Select(doc.FindAnnotation).OfType<Annotation>().ToList();
        var tool = _tool();
        _protoKind = null;
        _sample = null;
        if (_targets.Count > 0)
        {
            _sample = _targets[0];
            Header(_targets.Count == 1 ? $"{_sample.Kind}" : $"{_targets.Count} annotations selected");
            if (_targets.Count > 1) Hint("Changes apply to every selected item that has the property. Differing values show as mixed.");
        }
        else if (IsAnnotationTool(tool))
        {
            _protoKind = tool.ToString();
            _sample = _services.AnnotationStyles.Prototype(_protoKind);
            Header($"{_sample.Kind} tool");
            Hint("Style for the next items you draw. Select an item to edit it.");
        }
        else if (tool is ToolKind.Blur or ToolKind.Pixelate)
        {
            var st = _toolStyle(tool);
            Header(tool == ToolKind.Blur ? "Blur" : "Pixelate");
            int min = tool == ToolKind.Blur ? 1 : 2, max = tool == ToolKind.Blur ? 32 : 64;
            SliderRow(tool == ToolKind.Blur ? "Radius" : "Block size", st.EffectStrength, min, max, 1, "0", null,
                v => { _services.ToolStyles.Set(tool.ToString(), st with { EffectStrength = (int)Math.Round(v) }); }, null, "px");
            Hint("Visual effect only. Use Redact for secure hiding.");
            return;
        }
        else
        {
            Header("Annotation");
            Hint("Select an annotation to edit it, or pick a drawing tool to set its style.");
            return;
        }
        Build(_sample);
        if (sv is not null) Dispatcher.BeginInvoke(() => sv.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private ScrollViewer? FindScroll()
    {
        DependencyObject? d = this;
        while (d is not null and not ScrollViewer) d = VisualTreeHelper.GetParent(d);
        return d as ScrollViewer;
    }

    private void Build(Annotation a)
    {
        bool redaction = a is RedactionAnnotation;
        Section("Style", () => BuildStyle(a));
        switch (a)
        {
            case LineAnnotation l: Section("Line", () => BuildLine(l)); break;
            case TextAnnotation t: Section("Text", () => BuildText(t)); break;
            case StepAnnotation s: Section("Step", () => BuildStep(s)); break;
            case RectangleAnnotation or EllipseAnnotation or HighlightAnnotation or StampAnnotation or MagnifierAnnotation:
                Section("Shape", () => BuildShape(a)); break;
        }
        if (_targets.Count > 0) Section("Arrange", () => BuildArrange(a));
        Section("Defaults & style", () => BuildDefaults(a));
        if (!redaction) Section("Quick styles", () => BuildQuickStyles(a));
    }

    // ------------------------------------------------------------------ style (fill / border / opacity)

    private void BuildStyle(Annotation a)
    {
        switch (a)
        {
            case RedactionAnnotation:
                ColorRow("Colour", a.Color, false, Mixed(x => x.Color), (x, c) => x with { Color = c!.Value with { A = 255 } }, "Redaction colour");
                Hint("Redaction is always a solid, fully opaque, unrotated box.");
                return;
            case RectangleAnnotation or EllipseAnnotation:
                FillBorder(a,
                    fill: x => x switch { RectangleAnnotation r => r.Fill, EllipseAnnotation e => e.Fill, _ => null },
                    setFill: (x, c) => x switch { RectangleAnnotation r => r with { Fill = c }, EllipseAnnotation e => e with { Fill = c }, _ => x },
                    border: x => x.StrokeWidth > 0 ? x.Color : null,
                    setBorderColor: (x, c) => x with { Color = c },
                    fillLabel: "Fill", borderLabel: "Border");
                break;
            case CalloutAnnotation:
                FillBorder(a,
                    fill: x => (x as TextAnnotation)?.Fill,
                    setFill: (x, c) => x is TextAnnotation t ? t with { Fill = c } : x,
                    border: x => x.StrokeWidth > 0 ? x.Color : null,
                    setBorderColor: (x, c) => x with { Color = c },
                    fillLabel: "Background", borderLabel: "Border & tail");
                ColorRow("Text colour", ((CalloutAnnotation)a).TextColor, false, Mixed(x => (x as CalloutAnnotation)?.TextColor),
                    (x, c) => x is CalloutAnnotation y ? y with { TextColor = c!.Value } : x, "Text colour");
                break;
            case TextAnnotation:
                ColorRow("Text colour", a.Color, false, Mixed(x => x.Color), (x, c) => x with { Color = c!.Value }, "Text colour");
                FillBorder(a,
                    fill: x => (x as TextAnnotation)?.Fill,
                    setFill: (x, c) => x is TextAnnotation t ? t with { Fill = c } : x,
                    border: x => x is TextAnnotation t && x.StrokeWidth > 0 ? t.Border : null,
                    setBorderColor: (x, c) => x is TextAnnotation t ? t with { Border = c } : x,
                    fillLabel: "Background", borderLabel: "Border", allowBothOff: true);
                break;
            case StepAnnotation s:
                ColorRow("Fill", s.Color, false, Mixed(x => x.Color), (x, c) => x with { Color = c!.Value }, "Step fill");
                ColorRow("Number", s.TextColor, false, Mixed(x => (x as StepAnnotation)?.TextColor), (x, c) => x is StepAnnotation y ? y with { TextColor = c!.Value } : x, "Number colour");
                BorderOnly(a, x => x.StrokeWidth > 0 ? ((x as StepAnnotation)?.Border ?? Rgba32.White) : null,
                    (x, c) => x is StepAnnotation y ? y with { Border = c } : x);
                break;
            case LineAnnotation l:
                ColorRow("Colour", l.Color, false, Mixed(x => x.Color), (x, c) => x with { Color = c!.Value }, "Colour");
                SliderRow("Width", l.StrokeWidth, 1, 40, 0.5, "0.#", Mixed(x => x.StrokeWidth), null, (x, v) => x with { StrokeWidth = v }, "px", 4, "Line width");
                Segmented("Outline", ["None", "White", "Black"], l.Outline is null ? 0 : l.Outline == Rgba32.White ? 1 : 2,
                    i => (x => x is LineAnnotation y ? y with { Outline = i switch { 0 => null, 1 => Rgba32.White, _ => Rgba32.Black } } : x), "Outline",
                    ["No outline", "White outline for dark backgrounds", "Black outline for light backgrounds"]);
                break;
            case FreehandAnnotation:
                ColorRow("Colour", a.Color, false, Mixed(x => x.Color), (x, c) => x with { Color = c!.Value }, "Colour");
                SliderRow("Width", a.StrokeWidth, 1, 40, 0.5, "0.#", Mixed(x => x.StrokeWidth), null, (x, v) => x with { StrokeWidth = v }, "px", 3, "Pen width");
                break;
            case HighlightAnnotation:
                ColorRow("Marker", a.Color, false, Mixed(x => x.Color), (x, c) => x with { Color = c!.Value }, "Marker colour");
                break;
            case MagnifierAnnotation:
                ColorRow("Border", a.Color, false, Mixed(x => x.Color), (x, c) => x with { Color = c!.Value }, "Border colour");
                SliderRow("Border width", a.StrokeWidth, 0, 20, 0.5, "0.#", Mixed(x => x.StrokeWidth), null, (x, v) => x with { StrokeWidth = v }, "px", 3, "Border width");
                break;
            default:
                ColorRow("Colour", a.Color, false, Mixed(x => x.Color), (x, c) => x with { Color = c!.Value }, "Colour");
                break;
        }

        SliderRow("Opacity", a.Alpha * 100, 5, 100, 1, "0", Mixed(x => x.Alpha * 100), null, (x, v) => x with { Alpha = v / 100 }, "%", 100, "Opacity");
        Check("Drop shadow", a.Shadow, Mixed(x => x.Shadow ? 1 : 0), v => (x => x with { Shadow = v }), "Shadow");
    }

    /// <summary>Fill + border rows with a None/Solid switch each. At least one stays on unless <paramref name="allowBothOff"/>.</summary>
    private void FillBorder(Annotation a, Func<Annotation, Rgba32?> fill, Func<Annotation, Rgba32?, Annotation> setFill,
        Func<Annotation, Rgba32?> border, Func<Annotation, Rgba32, Annotation> setBorderColor,
        string fillLabel, string borderLabel, bool allowBothOff = false)
    {
        var f = fill(a);
        var b = border(a);

        Segmented(fillLabel, ["None", "Solid"], f is null ? 0 : 1, i => (x =>
        {
            if (i == 0)
            {
                if (!allowBothOff && border(x) is null) return x;
                return setFill(x, null);
            }
            return fill(x) is null ? setFill(x, DefaultFill(x)) : x;
        }), fillLabel, [$"No {fillLabel.ToLowerInvariant()} (click-through)", $"Solid {fillLabel.ToLowerInvariant()}"],
        guard: i => i == 0 && !allowBothOff && _targets.Concat(_sample is null ? [] : [_sample]).Any(x => border(x) is null)
            ? $"Turn the {borderLabel.ToLowerInvariant()} on first so the shape stays visible." : null);
        if (f is { } fc)
            ColorRow("  colour", fc, false, Mixed(fill), (x, c) => fill(x) is null ? x : setFill(x, c), $"{fillLabel} colour", alphaHint: true);

        Segmented(borderLabel, ["None", "Solid"], b is null ? 0 : 1, i => (x =>
        {
            if (i == 0)
            {
                if (!allowBothOff && fill(x) is null) return x;
                return x with { StrokeWidth = 0 };
            }
            if (x.StrokeWidth > 0 && border(x) is not null) return x;
            var y = x with { StrokeWidth = Math.Max(2, x.StrokeWidth) };
            return border(y) is null ? setBorderColor(y, x.Color) : y;
        }), borderLabel, [$"No {borderLabel.ToLowerInvariant()}", $"Solid {borderLabel.ToLowerInvariant()}"],
        guard: i => i == 0 && !allowBothOff && _targets.Concat(_sample is null ? [] : [_sample]).Any(x => fill(x) is null)
            ? $"Turn the {fillLabel.ToLowerInvariant()} on first so the shape stays visible." : null);
        if (b is { } bc)
        {
            ColorRow("  colour", bc, false, Mixed(border), (x, c) => x.StrokeWidth > 0 ? setBorderColor(x, c!.Value) : x, $"{borderLabel} colour");
            SliderRow("  width", a.StrokeWidth, 0.5, 40, 0.5, "0.#", Mixed(x => x.StrokeWidth), null,
                (x, v) => x.StrokeWidth > 0 ? x with { StrokeWidth = v } : x, "px", 3, $"{borderLabel} width");
        }
    }

    private void BorderOnly(Annotation a, Func<Annotation, Rgba32?> border, Func<Annotation, Rgba32, Annotation> setColor)
    {
        var b = border(a);
        Segmented("Border", ["None", "Solid"], b is null ? 0 : 1,
            i => (x => i == 0 ? x with { StrokeWidth = 0 } : x.StrokeWidth > 0 ? x : x with { StrokeWidth = 2 }), "Border", ["No border", "Solid border"]);
        if (b is { } bc)
        {
            ColorRow("  colour", bc, false, Mixed(border), (x, c) => setColor(x, c!.Value), "Border colour");
            SliderRow("  width", a.StrokeWidth, 0.5, 20, 0.5, "0.#", Mixed(x => x.StrokeWidth), null, (x, v) => x.StrokeWidth > 0 ? x with { StrokeWidth = v } : x, "px", 2, "Border width");
        }
    }

    private static Rgba32 DefaultFill(Annotation x) => x switch
    {
        CalloutAnnotation or TextAnnotation => Rgba32.White,
        _ => x.Color with { A = 90 },
    };

    // ------------------------------------------------------------------ type sections

    private void BuildShape(Annotation a)
    {
        switch (a)
        {
            case RectangleAnnotation r:
                SliderRow("Corner radius", r.CornerRadius, 0, 100, 1, "0", Mixed(x => (x as RectangleAnnotation)?.CornerRadius ?? 0), null,
                    (x, v) => x is RectangleAnnotation y ? y with { CornerRadius = v } : x, "px", 0, "Corner radius");
                break;
            case HighlightAnnotation h:
                SliderRow("Strength", h.Opacity / 2.55, 5, 100, 1, "0", Mixed(x => ((x as HighlightAnnotation)?.Opacity ?? 0) / 2.55), null,
                    (x, v) => x is HighlightAnnotation y ? y with { Opacity = (byte)Math.Clamp(Math.Round(v * 2.55), 10, 255) } : x, "%", 43, "Highlight strength");
                break;
            case StampAnnotation st when st.AssetId is null:
                Segmented("Symbol", ["✔", "✖", "★", "⚠", "ℹ", "?", "♥", "+1"], Array.IndexOf(StampSymbols.All, st.Symbol ?? StampSymbols.Check),
                    i => (x => x is StampAnnotation y && y.AssetId is null ? y with { Symbol = StampSymbols.All[i] } : x), "Symbol", StampSymbols.All);
                break;
            case MagnifierAnnotation m:
                Segmented("Lens", ["Round", "Square"], m.Circular ? 0 : 1, i => (x => x is MagnifierAnnotation y ? y with { Circular = i == 0 } : x), "Lens shape", ["Round lens", "Square lens"]);
                if (_protoKind is not null)
                {
                    var ts = _toolStyle(ToolKind.Magnifier);
                    SliderRow("Zoom", ts.Zoom, 1.25, 8, 0.25, "0.##", null, v => _services.ToolStyles.Set(nameof(ToolKind.Magnifier), ts with { Zoom = v }), null, "×");
                }
                break;
        }
        if (a.CanRotate) RotationRow(a);
    }

    private void BuildLine(LineAnnotation l)
    {
        string[] capGlyphs = ["—", "▶", "▷", "●", "■", "┃"];
        string[] capTips = ["No cap", "Filled arrowhead", "Open arrowhead", "Dot", "Square", "Bar"];
        Segmented("Start", capGlyphs, (int)l.EffectiveStartCap,
            i => (x => x is ArrowAnnotation ar ? ar with { StartCap = (ArrowCap)i, DoubleHeaded = false } : x is LineAnnotation y ? y with { StartCap = (ArrowCap)i } : x), "Start style", capTips);
        Segmented("End", capGlyphs, (int)l.EffectiveEndCap,
            i => (x => x is LineAnnotation y ? y with { EndCap = (ArrowCap)i } : x), "End style", capTips);
        if (l.EffectiveStartCap != ArrowCap.None || l.EffectiveEndCap != ArrowCap.None)
            SliderRow("Head size", l.HeadSize, 0.25, 4, 0.05, "0.##", Mixed(x => (x as LineAnnotation)?.HeadSize ?? 1), null,
                (x, v) => x is LineAnnotation y ? y with { HeadSize = v } : x, "×", 1, "Head size");
        Segmented("Pattern", ["━━━", "╍╍╍", "┈┈┈"], (int)l.EffectiveDash,
            i => (x => x is LineAnnotation y ? y with { Dash = (LineDash)i, Dashed = false } : x), "Line pattern", ["Solid", "Dashed", "Dotted"]);
        Segmented("Path", ["Straight", "Curved"], l.Control is null ? 0 : 1,
            i => (x => x is LineAnnotation y ? y with { Control = i == 1 ? y.Control ?? DefaultBend(y) : null } : x), i => i == 1 ? "Curve" : "Straighten",
            ["Straight line", "Curved line (drag the square handle)"]);
        if (_targets.Count == 1) Hint("Drag the round end handles to aim, the square handle to bend, the blue dot to rotate. Shift snaps to 15°.");
    }

    /// <summary>A gentle bend: the curve's midpoint moves sideways by 20% of the line length.</summary>
    private static PointD DefaultBend(LineAnnotation l)
    {
        var mid = new PointD((l.Start.X + l.End.X) / 2, (l.Start.Y + l.End.Y) / 2);
        var d = l.End - l.Start;
        double len = Math.Max(20, d.Length);
        var n = d.Length < 1e-6 ? new PointD(0, -1) : new PointD(d.Y / d.Length, -d.X / d.Length);
        var through = mid + n * (len * 0.2);
        return new PointD(2 * through.X - mid.X, 2 * through.Y - mid.Y);
    }

    private void BuildText(TextAnnotation t)
    {
        var fonts = FontNames.Contains(t.FontFamily) ? FontNames : [t.FontFamily, .. FontNames];
        Combo("Font", fonts, t.FontFamily, v => Apply(x => x is TextAnnotation y ? FitHeight(y with { FontFamily = v }) : x, "Font"));
        SliderRow("Size", t.FontSize, 6, 160, 1, "0", Mixed(x => (x as TextAnnotation)?.FontSize ?? 0), null,
            (x, v) => x is TextAnnotation y ? FitHeight(y with { FontSize = v }) : x, "pt", 24, "Font size");

        var styles = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        styles.Children.Add(Toggle("B", t.Bold, "Bold", v => Apply(x => x is TextAnnotation y ? FitHeight(y with { Bold = v }) : x, "Bold"), FontWeights.Bold));
        styles.Children.Add(Toggle("I", t.Italic, "Italic", v => Apply(x => x is TextAnnotation y ? FitHeight(y with { Italic = v }) : x, "Italic"), style: FontStyles.Italic));
        styles.Children.Add(Toggle("U", t.Underline, "Underline", v => Apply(x => x is TextAnnotation y ? y with { Underline = v } : x, "Underline"), underline: true));
        styles.Children.Add(new Border { Width = 10 });
        foreach (var (al, glyph) in new[] { (TextAlign.Left, "⯇≡"), (TextAlign.Center, "≡"), (TextAlign.Right, "≡⯈") })
        {
            var align = al;
            styles.Children.Add(Toggle(glyph, t.Alignment == align, $"Align {align.ToString().ToLowerInvariant()}",
                v => { if (v) Apply(x => x is TextAnnotation y ? y with { Alignment = align } : x, "Text alignment"); else Refresh(); }));
        }
        _into.Children.Add(Row("Style", styles));

        SliderRow("Padding", t.Padding, 0, 60, 1, "0", Mixed(x => (x as TextAnnotation)?.Padding ?? 0), null,
            (x, v) => x is TextAnnotation y ? FitHeight(y with { Padding = v }) : x, "px", 6, "Padding");
        SliderRow("Corner radius", t.CornerRadius, 0, 60, 1, "0", Mixed(x => (x as TextAnnotation)?.CornerRadius ?? 0), null,
            (x, v) => x is TextAnnotation y ? y with { CornerRadius = v } : x, "px", 4, "Corner radius");
        Segmented("Text outline", ["None", "White", "Black"], t.Outline is null ? 0 : t.Outline == Rgba32.White ? 1 : 2,
            i => (x => x is TextAnnotation y ? y with { Outline = i switch { 0 => null, 1 => Rgba32.White, _ => Rgba32.Black } } : x), "Text outline",
            ["No glyph outline", "White glyph outline", "Black glyph outline"]);

        if (t is CalloutAnnotation co)
        {
            Segmented("Bubble", ["▢", "□", "◯"], (int)co.Shape,
                i => (x => x is CalloutAnnotation y ? FitHeight(y with { Shape = (CalloutShape)i }) : x), "Callout shape", ["Rounded", "Rectangle", "Ellipse"]);
            SliderRow("Tail width", co.TailWidth, 4, 120, 1, "0", Mixed(x => (x as CalloutAnnotation)?.TailWidth ?? 0), null,
                (x, v) => x is CalloutAnnotation y ? y with { TailWidth = v } : x, "px", 24, "Tail width");
        }
        RotationRow(t);
        if (_targets.Count == 1) Hint("Double-click the text on the canvas to edit it. Drag the side handles to re-wrap.");
    }

    /// <summary>Grows a text box so all its text stays visible after a formatting change.</summary>
    private static TextAnnotation FitHeight(TextAnnotation t)
    {
        if (t.Bounds.IsEmpty || string.IsNullOrEmpty(t.Text)) return t;
        double h = AnnotationRenderer.MeasureTextHeight(t);
        return h > t.Bounds.Height ? t with { Bounds = t.Bounds with { Height = h } } : t;
    }

    private void BuildStep(StepAnnotation s)
    {
        double size = s.Bounds.Width > 4 ? s.Bounds.Width : 32;
        SliderRow("Size", size, 12, 200, 1, "0", Mixed(x => x.Bounds.Width), null,
            (x, v) => x is StepAnnotation y ? y with { Bounds = Square(y.Bounds, v) } : x, "px", 32, "Step size");
        Segmented("Shape", ["●", "▢", "■", "◆"], (int)s.Shape, i => (x => x is StepAnnotation y ? y with { Shape = (StepShape)i } : x), "Step shape",
            ["Circle", "Rounded square", "Square", "Diamond"]);
        Segmented("Label", ["1", "A", "a", "I", "Aa…"], (int)s.LabelStyle, i => (x => x is StepAnnotation y ? y with { LabelStyle = (StepLabelStyle)i } : x), "Step label",
            ["1, 2, 3", "A, B, C", "a, b, c", "I, II, III", "Custom text"]);
        if (s.LabelStyle == StepLabelStyle.Custom)
            Text("Custom text", s.CustomText ?? "", StepLabels.MaxCustom, v => Apply(x => x is StepAnnotation y ? y with { CustomText = v } : x, "Step text"));
        var affix = new UniformGrid { Columns = 2 };
        affix.Children.Add(Labeled("Prefix", s.Prefix, v => Apply(x => x is StepAnnotation y ? y with { Prefix = v } : x, "Step prefix")));
        affix.Children.Add(Labeled("Suffix", s.Suffix, v => Apply(x => x is StepAnnotation y ? y with { Suffix = v } : x, "Step suffix")));
        _into.Children.Add(Row("Affixes", affix));
        var fonts = FontNames.Contains(s.FontFamily) ? FontNames : [s.FontFamily, .. FontNames];
        Combo("Font", fonts, s.FontFamily, v => Apply(x => x is StepAnnotation y ? y with { FontFamily = v } : x, "Step font"));
        Segmented("Pointer", ["None", "Tail"], s.Tail is null ? 0 : 1,
            i => (x => x is StepAnnotation y ? y with { Tail = i == 1 ? y.Tail ?? new PointD(y.Bounds.Right + y.Bounds.Width, y.Bounds.Bottom + y.Bounds.Height * 0.6) : null } : x),
            i => i == 1 ? "Add pointer" : "Remove pointer", ["No pointer", "Pointer tail (drag the yellow handle)"]);

        if (_targets.Count > 0)
        {
            if (_targets.Count == 1)
            {
                var row = new DockPanel();
                var minus = Button("−", () => _vm.AdjustStepNumbers(-1), "Decrease the number (−)");
                var plus = Button("+", () => _vm.AdjustStepNumbers(1), "Increase the number (+)");
                DockPanel.SetDock(minus, Dock.Left);
                DockPanel.SetDock(plus, Dock.Right);
                row.Children.Add(minus);
                row.Children.Add(plus);
                row.Children.Add(new TextBlock { Text = s.Label, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                _into.Children.Add(Row("Number", row));
            }
            else _into.Children.Add(Row("Number", Pair(Button("− 1", () => _vm.AdjustStepNumbers(-1), "Decrease"), Button("+ 1", () => _vm.AdjustStepNumbers(1), "Increase"))));
            if (_targets.Count == 1) _into.Children.Add(Row("", Button("Renumber following", _vm.RenumberStepsFromSelected, "Later steps count up from this one")));
        }
        else Hint($"The next step is {StepLabels.Format(s.LabelStyle, DocumentOps.NextStepNumber(_vm.Document), s.CustomText, s.Prefix, s.Suffix)}.");
        RotationRow(s);
    }

    private static RectD Square(RectD b, double size)
    {
        var c = b.IsEmpty ? new PointD(size / 2, size / 2) : b.Center;
        return new RectD(c.X - size / 2, c.Y - size / 2, size, size);
    }

    private void RotationRow(Annotation a)
    {
        if (!a.CanRotate || _targets.Count == 0) return;
        SliderRow("Rotation", a.Rotation, -180, 180, 1, "0.#", Mixed(x => x.Rotation), null,
            (x, v) => x.CanRotate ? x with { Rotation = Rotation2D.Normalize(v) } : x, "°", 0, "Rotate");
        var quick = new WrapPanel();
        foreach (var (lbl, deg) in new[] { ("−90°", -90.0), ("0°", 0.0), ("+90°", 90.0) })
        {
            var d = deg;
            quick.Children.Add(Button(lbl, () => Apply(x => x.CanRotate ? x with { Rotation = d == 0 ? 0 : Rotation2D.Normalize(x.Rotation + d) } : x, "Rotate"),
                d == 0 ? "Reset rotation" : $"Rotate {lbl}"));
        }
        _into.Children.Add(Row("", quick));
    }

    private void BuildArrange(Annotation a)
    {
        var stack = new WrapPanel();
        stack.Children.Add(Button("⤒ Front", () => _vm.ZOrder(DocumentOps.ZMove.ToFront), "Bring to front (Ctrl+Shift+])"));
        stack.Children.Add(Button("▲ Forward", () => _vm.ZOrder(DocumentOps.ZMove.Forward), "Bring forward (Ctrl+])"));
        stack.Children.Add(Button("▼ Backward", () => _vm.ZOrder(DocumentOps.ZMove.Backward), "Send backward (Ctrl+[)"));
        stack.Children.Add(Button("⤓ Back", () => _vm.ZOrder(DocumentOps.ZMove.ToBack), "Send to back (Ctrl+Shift+[)"));
        _into.Children.Add(Row("Order", stack));
        if (_targets.Count >= 2)
        {
            var al = new WrapPanel();
            foreach (var (m, g, tip) in new[] { (AlignMode.Left, "⊢", "Align left"), (AlignMode.CenterX, "⊕", "Centre horizontally"), (AlignMode.Right, "⊣", "Align right"),
                         (AlignMode.Top, "⊤", "Align top"), (AlignMode.Middle, "⊖", "Centre vertically"), (AlignMode.Bottom, "⊥", "Align bottom") })
            {
                var mode = m;
                al.Children.Add(Button(g, () => _vm.Align(mode), tip));
            }
            _into.Children.Add(Row("Align", al));
        }
        if (_targets.Count >= 3)
            _into.Children.Add(Row("Distribute", Pair(Button("↔ Across", () => _vm.Distribute(true), "Equal horizontal spacing"),
                Button("↕ Down", () => _vm.Distribute(false), "Equal vertical spacing"))));
        Check("Locked (can't be moved)", a.Locked, Mixed(x => x.Locked ? 1 : 0), v => (x => x with { Locked = v }), "Lock");
    }

    private void BuildDefaults(Annotation a)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        if (_targets.Count > 0)
        {
            wrap.Children.Add(Button("★ Set as default", () => { _vm.SetAsDefaultStyle(); Refresh(); },
                $"New {a.Kind.ToLowerInvariant()} items will look like this one"));
            wrap.Children.Add(Button("Copy style", _vm.CopyStyle, "Copy this look (Ctrl+Alt+C)"));
            if (_vm.HasStyleClipboard)
                wrap.Children.Add(Button("Paste style", () => { _vm.PasteStyle(); Refresh(); }, "Apply the copied look to the selection (Ctrl+Alt+V)"));
        }
        wrap.Children.Add(Button("↺ Reset default", () => { _vm.ResetDefaultStyle(a.Kind); Refresh(); },
            $"Restore the built-in look for new {a.Kind.ToLowerInvariant()} items"));
        _into.Children.Add(wrap);
        Hint(_targets.Count > 0
            ? "Set as default: new items of this kind start with this look. With no selection, the panel edits the tool default directly."
            : "You are editing the default for new items. Changes here apply to the next items you draw.");
    }

    private void BuildQuickStyles(Annotation a)
    {
        var list = _services.AnnotationStyles.QuickStyles;
        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var q in list)
        {
            var qs = q;
            var b = Button(qs.Name, () => Apply(x => AnnotationStyle.Transfer(qs.Style, x), $"Style {qs.Name}"), "Apply this style (right-click to delete)");
            var menu = new ContextMenu();
            var del = new MenuItem { Header = $"Delete '{qs.Name}'" };
            del.Click += (_, _) => { _services.AnnotationStyles.DeleteQuick(qs.Name); Refresh(); };
            menu.Items.Add(del);
            b.ContextMenu = menu;
            wrap.Children.Add(b);
        }
        wrap.Children.Add(Button("＋ Save current…", () =>
        {
            var name = Dialogs.Prompt(Window.GetWindow(this), "Save quick style", "Style name:", $"My {a.Kind.ToLowerInvariant()}");
            if (name is null) return;
            try { _services.AnnotationStyles.SaveQuick(name, a); _vm.Status = $"Saved style '{name.Trim()}'."; }
            catch (ArgumentException ex) { _vm.Status = ex.Message; }
            Refresh();
        }, "Save this look as a named style"));
        _into.Children.Add(wrap);
        if (list.Count == 0) Hint("Saved styles appear here for one-click reuse.");
    }

    // ================================================================== apply / preview

    private IEnumerable<Annotation> AllTargets() => _targets.Count > 0 ? _targets : _sample is null ? [] : [_sample];

    /// <summary>Common numeric value across targets, or null when they differ.</summary>
    private double? Mixed(Func<Annotation, double> get)
    {
        var vals = AllTargets().Select(get).ToList();
        if (vals.Count == 0) return null;
        return vals.All(v => Math.Abs(v - vals[0]) < 1e-6) ? vals[0] : null;
    }

    private bool MixedColor(Func<Annotation, Rgba32?> get)
    {
        var vals = AllTargets().Select(get).ToList();
        return vals.Count > 1 && vals.Distinct().Count() > 1;
    }

    private Func<Annotation, Rgba32?> Mixed(Func<Annotation, Rgba32?> get) => get;

    private void Apply(Func<Annotation, Annotation> f, string label)
    {
        EndPreview();
        if (_targets.Count > 0) _vm.UpdateSelectedAnnotations(f, label);
        else if (_protoKind is { } k && _sample is { } s) _services.AnnotationStyles.SetPrototype(k, f(s));
        Refresh();
    }

    private void Preview(Func<Annotation, Annotation> f)
    {
        if (_targets.Count == 0) return;
        try
        {
            _vm.SetPreview(DocumentOps.UpdateAnnotations(_vm.Document, _vm.SelectedAnnotations, f));
            _previewing = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
    }

    private void EndPreview()
    {
        if (!_previewing) return;
        _previewing = false;
        _vm.SetPreview(null);
    }

    // ================================================================== controls

    private void Header(string text) =>
        Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, 4) });

    private void Section(string title, Action build)
    {
        var body = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
        var ex = new Expander
        {
            Header = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold },
            IsExpanded = Expanded.TryGetValue(title, out var open) ? open : true,
            Content = body, Margin = new Thickness(0, 4, 0, 0),
        };
        AutomationProperties.SetName(ex, title);
        ex.Expanded += (_, _) => Expanded[title] = true;
        ex.Collapsed += (_, _) => Expanded[title] = false;
        var prev = _into;
        _into = body;
        try { build(); } finally { _into = prev; }
        Children.Add(ex);
    }

    private void Hint(string text) =>
        _into.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });

    private static Grid Row(string label, FrameworkElement c)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var lb = new Label { Content = label, Padding = new Thickness(0, 3, 6, 0), Target = c, VerticalAlignment = VerticalAlignment.Center };
        if (label.Trim().Length > 0) AutomationProperties.SetName(c, label.Trim());
        Grid.SetColumn(c, 1);
        g.Children.Add(lb);
        g.Children.Add(c);
        return g;
    }

    private static UniformGrid Pair(FrameworkElement a, FrameworkElement b)
    {
        var u = new UniformGrid { Columns = 2 };
        u.Children.Add(a);
        u.Children.Add(b);
        return u;
    }

    /// <summary>
    /// Slider + number box. Dragging previews on the canvas and commits once on release; clicks, arrow keys
    /// and typed numbers commit immediately. Either commit to annotations (<paramref name="map"/>) or run
    /// <paramref name="setPlain"/> for tool settings. The ↺ button restores <paramref name="reset"/>.
    /// </summary>
    private void SliderRow(string label, double value, double min, double max, double step, string fmt, double? common,
        Action<double>? setPlain, Func<Annotation, double, Annotation>? map, string unit, double? reset = null, string undoLabel = "")
    {
        bool mixed = AllTargets().Count() > 1 && common is null && map is not null;
        value = Math.Clamp(value, min, max);
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 10,
            IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        };
        var box = new TextBox { Width = 46, Text = mixed ? "" : value.ToString(fmt, CultureInfo.CurrentCulture), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = mixed ? "Mixed values" : null };
        if (mixed) box.Tag = "mixed";
        var unitText = new TextBlock { Text = unit, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0), MinWidth = 14 };
        AutomationProperties.SetName(slider, label.Trim());
        AutomationProperties.SetName(box, label.Trim() + " value");

        bool dragging = false, syncing = false;
        double committed = value;
        void Commit(double v)
        {
            v = Math.Clamp(Math.Round(v / step) * step, min, max);
            if (Math.Abs(v - committed) < 1e-9 && !mixed) { EndPreview(); return; }
            committed = v;
            if (map is not null) Apply(x => map(x, v), undoLabel.Length > 0 ? undoLabel : label.Trim());
            else { setPlain?.Invoke(v); }
        }
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            syncing = true;
            box.Text = e.NewValue.ToString(fmt, CultureInfo.CurrentCulture);
            syncing = false;
            if (dragging) { if (map is not null) Preview(x => map(x, e.NewValue)); }
            else Commit(e.NewValue);
        };
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { dragging = false; Commit(slider.Value); }));
        void CommitBox()
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v))
            {
                if (box.Text.Length > 0) _vm.Status = $"{label.Trim()} must be a number between {min} and {max}.";
                box.Text = mixed ? "" : committed.ToString(fmt, CultureInfo.CurrentCulture);
                return;
            }
            Commit(v);
        }
        box.LostKeyboardFocus += (_, _) => CommitBox();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitBox(); e.Handled = true; }
            else if (e.Key == Key.Escape) { box.Text = committed.ToString(fmt, CultureInfo.CurrentCulture); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down)
            {
                double d = (e.Key == Key.Up ? 1 : -1) * step * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
                Commit((double.TryParse(box.Text, out var cur) ? cur : committed) + d);
                e.Handled = true;
            }
        };

        var dock = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(box);
        right.Children.Add(unitText);
        if (reset is { } r && map is not null)
        {
            var rb = new Button { Content = "↺", Width = 20, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), ToolTip = $"Reset to {r.ToString(fmt, CultureInfo.CurrentCulture)}{unit}", Focusable = true };
            AutomationProperties.SetName(rb, $"Reset {label.Trim()}");
            rb.Click += (_, _) => Commit(r);
            right.Children.Add(rb);
        }
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(slider);
        _into.Children.Add(Row(label, dock));
    }

    /// <summary>Colour chip that opens the picker. Previews live; one undo step when the picker closes.</summary>
    private void ColorRow(string label, Rgba32? value, bool allowNone, Func<Annotation, Rgba32?> get,
        Func<Annotation, Rgba32?, Annotation> set, string undoLabel, bool alphaHint = false)
    {
        bool mixed = MixedColor(get);
        var chip = ColorPicker.Chip(mixed ? null : value, 20);
        var text = new TextBlock
        {
            Text = mixed ? "Mixed" : value?.ToHex() ?? "None", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            FontFamily = new FontFamily("Consolas"), Opacity = 0.85,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(chip);
        content.Children.Add(text);
        var btn = new Button
        {
            Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(3, 2, 6, 2),
            ToolTip = "Click to choose a colour" + (alphaHint ? " (use the transparency slider for a see-through fill)" : ""),
        };
        AutomationProperties.SetName(btn, $"{label.Trim()} colour, {(mixed ? "mixed" : value?.ToHex() ?? "none")}");
        btn.Click += (_, _) =>
        {
            ColorPicker.Open(btn, value, allowNone, _services.AnnotationStyles.RecentColors,
                preview: c => { if (_targets.Count > 0) Preview(x => set(x, c)); },
                commit: c =>
                {
                    if (c is { } cc) _services.AnnotationStyles.UseColor(cc);
                    Apply(x => set(x, c), undoLabel);
                },
                cancel: () => { EndPreview(); _vm.Status = "Colour unchanged."; });
        };
        _into.Children.Add(Row(label, btn));
    }

    /// <summary>Segmented buttons (one active). <paramref name="map"/> maps the chosen index to an edit.</summary>
    private void Segmented(string label, string[] glyphs, int selected, Func<int, Func<Annotation, Annotation>> map, string undoLabel, string[] tips,
        Func<int, string?>? guard = null) =>
        Segmented(label, glyphs, selected, map, _ => undoLabel, tips, guard);

    private void Segmented(string label, string[] glyphs, int selected, Func<int, Func<Annotation, Annotation>> map, Func<int, string> undoLabel, string[] tips,
        Func<int, string?>? guard = null)
    {
        var wrap = new WrapPanel();
        for (int i = 0; i < glyphs.Length; i++)
        {
            int idx = i;
            var tb = new ToggleButton
            {
                Content = glyphs[i], IsChecked = i == selected, MinWidth = 28, Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 2, 2), ToolTip = i < tips.Length ? tips[i] : null,
            };
            AutomationProperties.SetName(tb, i < tips.Length ? tips[i] : glyphs[i]);
            tb.Click += (_, _) =>
            {
                if (idx == selected) { tb.IsChecked = true; return; }
                if (guard?.Invoke(idx) is { } msg) { _vm.Status = msg; tb.IsChecked = false; return; }
                Apply(map(idx), undoLabel(idx));
            };
            wrap.Children.Add(tb);
        }
        _into.Children.Add(Row(label, wrap));
    }

    private ToggleButton Toggle(string text, bool on, string name, Action<bool> set, FontWeight? weight = null, FontStyle? style = null, bool underline = false)
    {
        var tb = new ToggleButton { IsChecked = on, MinWidth = 28, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 2, 2), ToolTip = name };
        var t = new TextBlock { Text = text, FontWeight = weight ?? FontWeights.Normal, FontStyle = style ?? FontStyles.Normal };
        if (underline) t.TextDecorations = TextDecorations.Underline;
        tb.Content = t;
        AutomationProperties.SetName(tb, name);
        tb.Click += (_, _) => set(tb.IsChecked == true);
        return tb;
    }

    private void Text(string label, string value, int maxLength, Action<string> set) => _into.Children.Add(Row(label, Labeled(null, value, set, maxLength)));

    private static FrameworkElement Labeled(string? placeholder, string value, Action<string> set, int maxLength = StepLabels.MaxAffix)
    {
        var tb = new TextBox { Text = value, MaxLength = maxLength, Margin = new Thickness(0, 0, 4, 0), ToolTip = placeholder };
        if (placeholder is not null) AutomationProperties.SetName(tb, placeholder);
        bool done = false;
        void Commit()
        {
            if (done || tb.Text == value) return;
            done = true;
            set(tb.Text);
        }
        tb.LostKeyboardFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        if (placeholder is null) return tb;
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = placeholder, FontSize = 10, Opacity = 0.7 });
        sp.Children.Add(tb);
        return sp;
    }

    private void Combo<T>(string label, IEnumerable<T> items, T selected, Action<T> set)
    {
        var cb = new ComboBox { ItemsSource = items.ToList(), SelectedItem = selected, Margin = new Thickness(0, 1, 0, 1) };
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is T v && !EqualityComparer<T>.Default.Equals(v, selected)) set(v);
        };
        _into.Children.Add(Row(label, cb));
    }

    private void Check(string label, bool value, double? common, Func<bool, Func<Annotation, Annotation>> map, string undoLabel)
    {
        bool mixed = AllTargets().Count() > 1 && common is null;
        var c = new CheckBox { Content = label, IsChecked = mixed ? null : value, IsThreeState = false, Margin = new Thickness(0, 3, 10, 3) };
        c.Click += (_, _) => Apply(map(c.IsChecked == true), undoLabel);
        _into.Children.Add(c);
    }

    private static Button Button(string text, Action a, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 3, 3), ToolTip = tip };
        AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => a();
        return b;
    }
}
