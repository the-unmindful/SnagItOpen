using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Controls;
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
public sealed class AnnotationPropertiesPanel : StackPanel
{
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
    private readonly List<Action> _updates = [];
    private string? _contextShape;
    private bool _syncing;
    private StyleGallery? _gallery;
    private EffectStyleGallery? _effectGallery;
    public int RebuildCount { get; private set; }

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
        var doc = _vm.Displayed;
        _targets = _vm.SelectedAnnotations.Select(doc.FindAnnotation).OfType<Annotation>().OrderBy(a => a.Kind, StringComparer.Ordinal).ToList();
        var tool = _tool();
        _protoKind = null;
        _sample = null;
        if (_targets.Count > 0)
            _sample = _targets[0];
        else if (IsAnnotationTool(tool))
        {
            _protoKind = tool.ToString();
            _sample = _services.AnnotationStyles.Prototype(_protoKind);
        }
        string shape = _targets.Count > 0 ? "selection:" + string.Join(",", _targets.Select(a => a.Kind).Distinct().OrderBy(k => k, StringComparer.Ordinal)) : "tool:" + tool;
        if (shape == _contextShape)
        {
            _syncing = true; try { foreach (var update in _updates) update(); } finally { _syncing = false; }
            if (_sample is not null) _gallery?.Refresh(_sample.Kind);
            else if (tool is ToolKind.Blur or ToolKind.Pixelate) _effectGallery?.Refresh(tool);
            return;
        }
        EndPreview(); _contextShape = shape; RebuildCount++; _updates.Clear();
        var sv = FindScroll(); double offset = sv?.VerticalOffset ?? 0;
        Children.Clear(); _into = this; _gallery = null; _effectGallery = null;
        if (tool is ToolKind.Blur or ToolKind.Pixelate && _targets.Count == 0)
        {
            _effectGallery = new EffectStyleGallery(_services, _vm, Refresh); _effectGallery.Refresh(tool); Children.Add(_effectGallery);
            var st = _toolStyle(tool); int min = tool == ToolKind.Blur ? 1 : 2, max = tool == ToolKind.Blur ? 32 : 64;
            SliderRow(tool == ToolKind.Blur ? "Radius" : "Block size", st.EffectStrength, min, max, 1, "0", null,
                v => _services.ToolStyles.Set(tool.ToString(), _toolStyle(tool) with { EffectStrength = (int)Math.Round(v) }), null, "px");
            return;
        }
        if (_sample is null) return;
        _gallery = new StyleGallery(_services, _vm, () => _sample, Refresh); _gallery.Refresh(_sample.Kind); Children.Add(_gallery);
        Build(_sample); _syncing = true; try { foreach (var update in _updates) update(); } finally { _syncing = false; }
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
        Section("Style", () => BuildStyle(a));
        switch (a)
        {
            case LineAnnotation l: Section("Line", () => BuildLine(l)); break;
            case TextAnnotation t: Section("Text", () => BuildText(t)); break;
            case StepAnnotation s: Section("Step", () => BuildStep(s)); break;
            case RectangleAnnotation or EllipseAnnotation or HighlightAnnotation or StampAnnotation or MagnifierAnnotation:
                Section("Shape", () => BuildShape(a)); break;
        }
    }

    // ------------------------------------------------------------------ style (fill / border / opacity)

    private void BuildStyle(Annotation a)
    {
        switch (a)
        {
            case RedactionAnnotation:
                ColorRow("Colour", a.Color, false, Mixed(x => x.Color), (x, c) => x with { Color = c!.Value with { A = 255 } }, "Redaction colour");
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
        ColorRow("  colour", f, false, Mixed(fill), (x, c) => fill(x) is null ? x : setFill(x, c), $"{fillLabel} colour", alphaHint: true, visibleWhen: x => fill(x) is not null);

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
        ColorRow("  colour", b, false, Mixed(border), (x, c) => x.StrokeWidth > 0 ? setBorderColor(x, c!.Value) : x, $"{borderLabel} colour", visibleWhen: x => border(x) is not null);
        SliderRow("  width", a.StrokeWidth, 0.5, 40, 0.5, "0.#", Mixed(x => x.StrokeWidth), null,
            (x, v) => x.StrokeWidth > 0 ? x with { StrokeWidth = v } : x, "px", 3, $"{borderLabel} width", visibleWhen: x => border(x) is not null);
    }

    private void BorderOnly(Annotation a, Func<Annotation, Rgba32?> border, Func<Annotation, Rgba32, Annotation> setColor)
    {
        var b = border(a);
        Segmented("Border", ["None", "Solid"], b is null ? 0 : 1,
            i => (x => i == 0 ? x with { StrokeWidth = 0 } : x.StrokeWidth > 0 ? x : x with { StrokeWidth = 2 }), "Border", ["No border", "Solid border"]);
        ColorRow("  colour", b, false, Mixed(border), (x, c) => setColor(x, c!.Value), "Border colour", visibleWhen: x => border(x) is not null);
        SliderRow("  width", a.StrokeWidth, 0.5, 20, 0.5, "0.#", Mixed(x => x.StrokeWidth), null, (x, v) => x.StrokeWidth > 0 ? x with { StrokeWidth = v } : x, "px", 2, "Border width", visibleWhen: x => border(x) is not null);
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
                Segmented("Pattern", ["Solid", "Dashed", "Dotted"], (int)r.Dash,
                    i => (x => x is RectangleAnnotation y ? y with { Dash = (LineDash)i } : x), "Border pattern", ["Solid", "Dashed", "Dotted"]);
                break;
            case HighlightAnnotation h:
                SliderRow("Strength", h.Opacity / 2.55, 5, 100, 1, "0", Mixed(x => ((x as HighlightAnnotation)?.Opacity ?? 0) / 2.55), null,
                    (x, v) => x is HighlightAnnotation y ? y with { Opacity = (byte)Math.Clamp(Math.Round(v * 2.55), 10, 255) } : x, "%", 43, "Highlight strength");
                break;
            case StampAnnotation st:
                Segmented("Symbol", ["✔", "✖", "★", "⚠", "ℹ", "?", "♥", "+1"], Array.IndexOf(StampSymbols.All, st.Symbol ?? StampSymbols.Check),
                    i => (x => x is StampAnnotation y && y.AssetId is null ? y with { Symbol = StampSymbols.All[i] } : x), "Symbol", StampSymbols.All, visibleWhen: x => x is StampAnnotation { AssetId: null });
                break;
            case MagnifierAnnotation m:
                Segmented("Lens", ["Round", "Square"], m.Circular ? 0 : 1, i => (x => x is MagnifierAnnotation y ? y with { Circular = i == 0 } : x), "Lens shape", ["Round lens", "Square lens"]);
                if (_protoKind is not null)
                {
                    var ts = _toolStyle(ToolKind.Magnifier);
                    SliderRow("Zoom", ts.Zoom, 1.25, 8, 0.25, "0.##", null, v => _services.ToolStyles.Set(nameof(ToolKind.Magnifier), _toolStyle(ToolKind.Magnifier) with { Zoom = v }), null, "×");
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
        SliderRow("Head size", l.HeadSize, 0.25, 4, 0.05, "0.##", Mixed(x => (x as LineAnnotation)?.HeadSize ?? 1), null,
            (x, v) => x is LineAnnotation y ? y with { HeadSize = v } : x, "×", 1, "Head size", visibleWhen: x => x is LineAnnotation line && (line.EffectiveStartCap != ArrowCap.None || line.EffectiveEndCap != ArrowCap.None));
        Segmented("Pattern", ["━━━", "╍╍╍", "┈┈┈"], (int)l.EffectiveDash,
            i => (x => x is LineAnnotation y ? y with { Dash = (LineDash)i, Dashed = false } : x), "Line pattern", ["Solid", "Dashed", "Dotted"]);
        Segmented("Path", ["Straight", "Curved"], l.Control is null ? 0 : 1,
            i => (x => x is LineAnnotation y ? y with { Control = i == 1 ? y.Control ?? DefaultBend(y) : null } : x), i => i == 1 ? "Curve" : "Straighten",
            ["Straight line", "Curved line (drag the diamond handle)"]);
        Hint("Drag the round end handles to aim and the diamond handle to bend. Shift snaps to 15°.");
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

        Segmented("Vertical", ["Top", "Middle", "Bottom"], (int)t.VerticalAlign,
            i => (x => x is TextAnnotation y ? FitHeight(y with { VerticalAlign = (TextVAlign)i }) : x), "Vertical alignment",
            ["Align text to the top", "Centre text vertically", "Align text to the bottom"]);
        Segmented("Box size", ["Fit text", "Fit height", "Fixed"], t.Sizing switch { TextSizing.AutoWidth => 0, TextSizing.AutoHeight => 1, _ => 2 },
            i => (x => x is TextAnnotation y ? FitHeight(y with { Sizing = i switch { 0 => TextSizing.AutoWidth, 1 => TextSizing.AutoHeight, _ => TextSizing.Fixed } }) : x), "Box size",
            ["Box grows to fit the text (no wrapping)", "Fixed width, wraps; height follows the text", "Keep the box size (it still grows if text would be hidden)"]);
        SliderRow("Padding H", t.PadX, 0, 80, 1, "0", Mixed(x => (x as TextAnnotation)?.PadX ?? 0), null,
            (x, v) => x is TextAnnotation y ? FitHeight(y with { PaddingX = v, PaddingY = y.PadY }) : x, "px", 10, "Horizontal padding");
        SliderRow("Padding V", t.PadY, 0, 80, 1, "0", Mixed(x => (x as TextAnnotation)?.PadY ?? 0), null,
            (x, v) => x is TextAnnotation y ? FitHeight(y with { PaddingY = v, PaddingX = y.PadX }) : x, "px", 6, "Vertical padding");
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
        return AnnotationRenderer.Fit(t);
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
        var custom = Row("Custom text", Labeled("Custom text", s.CustomText ?? "", v => Apply(x => x is StepAnnotation y ? y with { CustomText = v } : x, "Step text"), StepLabels.MaxCustom, false));
        _into.Children.Add(custom); _updates.Add(() => custom.Visibility = AllTargets().Any(x => x is StepAnnotation { LabelStyle: StepLabelStyle.Custom }) ? Visibility.Visible : Visibility.Collapsed);
        var affix = new UniformGrid { Columns = 2 };
        affix.Children.Add(Labeled("Prefix", s.Prefix, v => Apply(x => x is StepAnnotation y ? y with { Prefix = v } : x, "Step prefix")));
        affix.Children.Add(Labeled("Suffix", s.Suffix, v => Apply(x => x is StepAnnotation y ? y with { Suffix = v } : x, "Step suffix")));
        _into.Children.Add(Row("Affixes", affix));
        var fonts = FontNames.Contains(s.FontFamily) ? FontNames : [s.FontFamily, .. FontNames];
        Combo("Font", fonts, s.FontFamily, v => Apply(x => x is StepAnnotation y ? y with { FontFamily = v } : x, "Step font"));
        Segmented("Pointer", ["None", "Tail"], s.Tail is null ? 0 : 1,
            i => (x => x is StepAnnotation y ? y with { Tail = i == 1 ? y.Tail ?? new PointD(y.Bounds.Right + y.Bounds.Width, y.Bounds.Bottom + y.Bounds.Height * 0.6) : null } : x),
            i => i == 1 ? "Add pointer" : "Remove pointer", ["No pointer", "Pointer tail (drag the triangle handle)"]);

        if (_targets.Count > 0)
        {
            var row = new DockPanel();
            var minus = Button("−", () => _vm.AdjustStepNumbers(-1), "Decrease the number");
            var plus = Button("+", () => _vm.AdjustStepNumbers(1), "Increase the number");
            DockPanel.SetDock(minus, Dock.Left); DockPanel.SetDock(plus, Dock.Right); row.Children.Add(minus); row.Children.Add(plus);
            var caption = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(caption); _into.Children.Add(Row("Number", row));
            _updates.Add(() => { var labels = _targets.OfType<StepAnnotation>().Select(step => step.Label).Distinct().ToArray(); caption.Text = labels.Length == 1 ? labels[0] : "—"; });
            var renumber = Button("Renumber following", _vm.RenumberStepsFromSelected, "Later steps count up from this one");
            _into.Children.Add(Row("", renumber)); _updates.Add(() => renumber.Visibility = _targets.Count == 1 ? Visibility.Visible : Visibility.Collapsed);
        }
        else
        {
            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 4) }; _into.Children.Add(hint);
            _updates.Add(() => { if (_sample is StepAnnotation step) hint.Text = "The next step is " + StepLabels.Format(step.LabelStyle, DocumentOps.NextStepNumber(_vm.Document), step.CustomText, step.Prefix, step.Suffix) + "."; });
        }
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
            _previewing = true;
            _vm.SetPreview(DocumentOps.UpdateAnnotations(_vm.Document, _vm.SelectedAnnotations, f));
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
        var section = new InspectorSection(_services, "Annotation." + title, title) { Content = body };
        AutomationProperties.SetName(section, title);
        var previous = _into; _into = body;
        try { build(); } finally { _into = previous; }
        Children.Add(section);
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

    private static double NumericValue(Annotation? a, string label, string undo, double fallback)
    {
        if (a is null) return fallback;
        return (undo.Length > 0 ? undo : label.Trim()) switch
        {
            "Font size" => a is TextAnnotation text ? text.FontSize : fallback,
            "Step size" => a.Bounds.Width > 4 ? a.Bounds.Width : 32,
            "Corner radius" => a switch { RectangleAnnotation rect => rect.CornerRadius, TextAnnotation text => text.CornerRadius, _ => fallback },
            "Highlight strength" => a is HighlightAnnotation highlight ? highlight.Opacity / 2.55 : fallback,
            "Head size" => a is LineAnnotation line ? line.HeadSize : fallback,
            "Opacity" => a.Alpha * 100,
            "Padding" => a is TextAnnotation text ? text.Padding : fallback,
            "Tail width" => a is CalloutAnnotation callout ? callout.TailWidth : fallback,
            "Rotate" => a.Rotation,
            _ => a.StrokeWidth,
        };
    }

    private void SliderRow(string label, double value, double min, double max, double step, string fmt, double? common,
        Action<double>? setPlain, Func<Annotation, double, Annotation>? map, string unit, double? reset = null, string undoLabel = "", Func<Annotation, bool>? visibleWhen = null)
    {
        var row = new Controls.SliderRow { Label = label.Trim(), Minimum = min, Maximum = max, Step = step, Unit = unit, DefaultValue = reset, Margin = new Thickness(0, 2, 0, 2) };
        row.Slider.IsMoveToPointEnabled = true;
        row.Preview += v => { if (!_syncing && map is not null) Preview(x => map(x, v)); };
        row.Committed += v =>
        {
            if (_syncing) return;
            v = Math.Clamp(Math.Round(v / step) * step, min, max);
            if (map is not null) Apply(x => map(x, v), undoLabel.Length > 0 ? undoLabel : label.Trim());
            else { setPlain?.Invoke(v); Refresh(); }
        };
        _updates.Add(() =>
        {
            row.Visibility = visibleWhen is null || AllTargets().Any(visibleWhen) ? Visibility.Visible : Visibility.Collapsed;
            if (map is null) { var style = _toolStyle(_tool()); row.Update(label == "Zoom" ? style.Zoom : style.EffectStrength); return; }
            var values = AllTargets().Select(a => NumericValue(a, label, undoLabel, value)).ToArray();
            double current = NumericValue(_sample, label, undoLabel, value);
            row.Update(current, values.Length > 1 && values.Any(v => Math.Abs(v - values[0]) > 0.000001));
        });
        _into.Children.Add(row);
    }

    private void ColorRow(string label, Rgba32? value, bool allowNone, Func<Annotation, Rgba32?> get,
        Func<Annotation, Rgba32?, Annotation> set, string undoLabel, bool alphaHint = false, Func<Annotation, bool>? visibleWhen = null)
    {
        var button = new ColorSwatchButton { Label = label.Trim(), Value = value, AllowNone = allowNone, Recent = _services.AnnotationStyles.RecentColors, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Preview += c => { if (!_syncing) Preview(x => set(x, c)); };
        button.Committed += c => { if (c is { } color) _services.AnnotationStyles.UseColor(color); Apply(x => set(x, c), undoLabel); };
        button.Canceled += EndPreview;
        var row = Row(label, button);
        _updates.Add(() => { button.Value = _sample is null ? value : get(_sample); button.IsMixed = MixedColor(get); button.Recent = _services.AnnotationStyles.RecentColors; row.Visibility = visibleWhen is null || AllTargets().Any(visibleWhen) ? Visibility.Visible : Visibility.Collapsed; });
        _into.Children.Add(row);
    }

    private static int SegmentValue(Annotation a, string label) => label.Trim() switch
    {
        "Outline" => a is LineAnnotation line ? line.Outline is null ? 0 : line.Outline == Rgba32.White ? 1 : 2 : 0,
        "Text outline" => a is TextAnnotation text ? text.Outline is null ? 0 : text.Outline == Rgba32.White ? 1 : 2 : 0,
        "Fill" or "Background" => a switch { RectangleAnnotation rect => rect.Fill is null ? 0 : 1, EllipseAnnotation ellipse => ellipse.Fill is null ? 0 : 1, TextAnnotation text => text.Fill is null ? 0 : 1, _ => 0 },
        "Border" or "Border & tail" => a is TextAnnotation and not CalloutAnnotation ? a.StrokeWidth > 0 && ((TextAnnotation)a).Border is not null ? 1 : 0 : a.StrokeWidth > 0 ? 1 : 0,
        "Symbol" => a is StampAnnotation stamp ? Array.IndexOf(StampSymbols.All, stamp.Symbol ?? StampSymbols.Check) : 0,
        "Lens" => a is MagnifierAnnotation { Circular: false } ? 1 : 0,
        "Start" => a is LineAnnotation line ? (int)line.EffectiveStartCap : 0,
        "End" => a is LineAnnotation line ? (int)line.EffectiveEndCap : 0,
        "Pattern" => a switch { LineAnnotation line => (int)line.EffectiveDash, RectangleAnnotation rect => (int)rect.Dash, _ => 0 },
        "Path" => a is LineAnnotation { Control: not null } ? 1 : 0,
        "Bubble" => a is CalloutAnnotation callout ? (int)callout.Shape : 0,
        "Shape" => a is StepAnnotation step ? (int)step.Shape : 0,
        "Label" => a is StepAnnotation step ? (int)step.LabelStyle : 0,
        "Pointer" => a is StepAnnotation { Tail: not null } ? 1 : 0,
        _ => 0,
    };
    private void Segmented(string label, string[] glyphs, int selected, Func<int, Func<Annotation, Annotation>> map, string undoLabel, string[] tips, Func<int, string?>? guard = null, Func<Annotation, bool>? visibleWhen = null) =>
        Segmented(label, glyphs, selected, map, _ => undoLabel, tips, guard, visibleWhen);
    private void Segmented(string label, string[] glyphs, int selected, Func<int, Func<Annotation, Annotation>> map, Func<int, string> undoLabel, string[] tips, Func<int, string?>? guard = null, Func<Annotation, bool>? visibleWhen = null)
    {
        var control = new SegmentedControl(glyphs.Select((glyph, index) => new SegmentOption(index, glyph)));
        for (int i = 0; i < control.Buttons.Count; i++) AutomationProperties.SetName(control.Buttons[i], i < tips.Length ? tips[i] : glyphs[i]);
        control.SelectionChanged += value =>
        {
            if (_syncing || value is not int index) return;
            if (guard?.Invoke(index) is { } message) { _vm.Status = message; Refresh(); return; }
            Apply(map(index), undoLabel(index));
        };
        var row = Row(label, control);
        _updates.Add(() =>
        {
            row.Visibility = visibleWhen is null || AllTargets().Any(visibleWhen) ? Visibility.Visible : Visibility.Collapsed;
            var values = AllTargets().Select(a => SegmentValue(a, label)).Distinct().ToArray();
            control.SelectedValue = _sample is null ? selected : SegmentValue(_sample, label); control.IsMixed = values.Length > 1;
            for (int i = 0; i < control.Buttons.Count; i++) control.Buttons[i].ToolTip = (i < tips.Length ? tips[i] : glyphs[i]) + (control.IsMixed ? " (Mixed)" : "");
        });
        _into.Children.Add(row);
    }
    private static bool ToggleValue(Annotation a, string name) => a is TextAnnotation text && (name switch
    {
        "Bold" => text.Bold, "Italic" => text.Italic, "Underline" => text.Underline,
        "Align left" => text.Alignment == TextAlign.Left, "Align center" => text.Alignment == TextAlign.Center, "Align right" => text.Alignment == TextAlign.Right,
        _ => false,
    });
    private ToggleButton Toggle(string text, bool on, string name, Action<bool> set, FontWeight? weight = null, FontStyle? style = null, bool underline = false)
    {
        var button = new ToggleButton { IsChecked = on, IsThreeState = true, MinWidth = 28, MinHeight = 28, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 2, 2), ToolTip = name };
        var caption = new TextBlock { Text = text, FontWeight = weight ?? FontWeights.Normal, FontStyle = style ?? FontStyles.Normal }; if (underline) caption.TextDecorations = TextDecorations.Underline;
        button.Content = caption; AutomationProperties.SetName(button, name); bool mixed = false;
        button.Click += (_, _) => { if (_syncing) return; bool next = mixed || button.IsChecked == true; button.IsChecked = next; set(next); };
        _updates.Add(() => { var values = AllTargets().Select(a => ToggleValue(a, name)).Distinct().ToArray(); mixed = values.Length > 1; button.IsChecked = mixed ? null : _sample is not null ? ToggleValue(_sample, name) : on; });
        return button;
    }
    private void Text(string label, string value, int maxLength, Action<string> set) => _into.Children.Add(Row(label, Labeled(label, value, set, maxLength, false)));
    private FrameworkElement Labeled(string? placeholder, string value, Action<string> set, int maxLength = StepLabels.MaxAffix, bool showLabel = true)
    {
        var box = new TextBox { Text = value, MaxLength = maxLength, Margin = new Thickness(0, 0, 4, 0), ToolTip = placeholder };
        if (placeholder is not null) AutomationProperties.SetName(box, placeholder);
        string baseline = value;
        void Commit() { if (_syncing || box.Text == baseline) return; baseline = box.Text; set(box.Text); }
        box.LostKeyboardFocus += (_, _) => Commit(); box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { box.Text = baseline; e.Handled = true; } };
        string Read(Annotation a) => a is StepAnnotation step ? placeholder switch { "Prefix" => step.Prefix, "Suffix" => step.Suffix, "Custom text" => step.CustomText ?? "", _ => value } : value;
        _updates.Add(() => { var values = AllTargets().Select(Read).Distinct().ToArray(); string next = values.Length > 1 ? "—" : _sample is not null ? Read(_sample) : value; if (next != baseline) { baseline = next; box.Text = next; } box.ToolTip = values.Length > 1 ? "Mixed" : placeholder; });
        if (placeholder is null || !showLabel) return box;
        var stack = new StackPanel(); stack.Children.Add(new TextBlock { Text = placeholder, FontSize = 11 }); stack.Children.Add(box); return stack;
    }
    private void Combo<T>(string label, IEnumerable<T> items, T selected, Action<T> set)
    {
        var combo = new ComboBox { ItemsSource = items.ToList(), SelectedItem = selected, MinHeight = 28, Margin = new Thickness(0, 1, 0, 1) };
        combo.SelectionChanged += (_, _) => { if (!_syncing && combo.SelectedItem is T value) set(value); };
        _updates.Add(() =>
        {
            object? Read(Annotation a) => label == "Font" ? a switch { TextAnnotation text => text.FontFamily, StepAnnotation step => step.FontFamily, _ => selected } : selected;
            var values = AllTargets().Select(Read).Distinct().ToArray(); bool mixed = values.Length > 1; combo.SelectedItem = mixed ? null : _sample is not null ? Read(_sample) : selected; combo.ToolTip = mixed ? "Mixed" : null;
        });
        _into.Children.Add(Row(label, combo));
    }
    private void Check(string label, bool value, double? common, Func<bool, Func<Annotation, Annotation>> map, string undoLabel)
    {
        var check = new CheckBox { Content = label, IsThreeState = true, Margin = new Thickness(0, 3, 10, 3) }; bool mixed = false;
        bool Read(Annotation a) => undoLabel == "Lock" ? a.Locked : a.Shadow;
        _updates.Add(() => { var values = AllTargets().Select(Read).Distinct().ToArray(); mixed = values.Length > 1; check.IsChecked = mixed ? null : _sample is not null ? Read(_sample) : value; });
        check.Click += (_, _) => { if (_syncing) return; bool next = mixed || check.IsChecked == true; check.IsChecked = next; Apply(map(next), undoLabel); };
        _into.Children.Add(check);
    }
    private static Button Button(string text, Action a, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 3, 3), ToolTip = tip };
        AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => a();
        return b;
    }
}
