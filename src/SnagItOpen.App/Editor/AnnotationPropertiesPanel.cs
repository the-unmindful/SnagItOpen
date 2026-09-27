using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnagItOpen.App.Infrastructure;
using SnagItOpen.App.Shell;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Imaging.Rendering;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.App.Editor;

/// <summary>
/// Live properties for the selected annotations (every change is one undoable step), or, when nothing is
/// selected, the style the current drawing tool gives new items. Includes swatches, recent colours and
/// named quick styles.
/// </summary>
internal sealed class AnnotationPropertiesPanel : StackPanel
{
    private static readonly Rgba32[] Palette =
    [
        new(230, 40, 40, 255), new(245, 130, 0, 255), new(255, 210, 0, 255), new(40, 170, 70, 255), new(0, 120, 215, 255),
        new(130, 60, 200, 255), new(0, 0, 0, 255), new(255, 255, 255, 255), new(128, 128, 128, 255),
    ];

    private static string[]? _fonts;
    private readonly AppServices _services;
    private readonly EditorViewModel _vm;
    private readonly Func<ToolKind> _tool;
    private readonly Func<ToolKind, ToolStyle> _toolStyle;
    private List<Annotation> _targets = [];
    private Annotation? _sample;
    private string? _protoKind;

    public AnnotationPropertiesPanel(AppServices services, EditorViewModel vm, Func<ToolKind> tool, Func<ToolKind, ToolStyle> toolStyle)
    {
        _services = services;
        _vm = vm;
        _tool = tool;
        _toolStyle = toolStyle;
        AutomationProperties.SetName(this, "Annotation properties");
    }

    private static string[] FontNames => _fonts ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToArray();

    private static bool IsAnnotationTool(ToolKind k) => k is not (ToolKind.Select or ToolKind.Crop or ToolKind.CutOut or ToolKind.Blur or ToolKind.Pixelate);

    // ================================================================== build

    public void Refresh()
    {
        Children.Clear();
        var doc = _vm.Document;
        _targets = _vm.SelectedAnnotations.Select(doc.FindAnnotation).OfType<Annotation>().ToList();
        var tool = _tool();
        _protoKind = null;
        _sample = null;
        if (_targets.Count > 0)
        {
            _sample = _targets[0];
            Header(_targets.Count == 1 ? $"{_sample.Kind} properties" : $"{_targets.Count} annotations selected");
            if (_targets.Count > 1) Hint("Changes apply to every selected item that has the property.");
        }
        else if (IsAnnotationTool(tool))
        {
            _protoKind = tool.ToString();
            _sample = _services.AnnotationStyles.Prototype(_protoKind);
            Header($"{_sample.Kind} tool style");
            Hint("Applies to the next items you draw. Select an item to edit it.");
        }
        else if (tool is ToolKind.Blur or ToolKind.Pixelate)
        {
            var st = _toolStyle(tool);
            Header(tool == ToolKind.Blur ? "Blur" : "Pixelate");
            int min = tool == ToolKind.Blur ? 1 : 2, max = tool == ToolKind.Blur ? 32 : 64;
            Number(tool == ToolKind.Blur ? "Radius (px)" : "Block (px)", st.EffectStrength, min, max,
                v => { _services.ToolStyles.Set(tool.ToString(), st with { EffectStrength = (int)Math.Round(v) }); Refresh(); }, "0");
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
    }

    private void Build(Annotation a)
    {
        bool redaction = a is RedactionAnnotation;
        string mainLabel = a switch
        {
            CalloutAnnotation => "Border",
            TextAnnotation => "Text colour",
            StepAnnotation => "Fill",
            HighlightAnnotation => "Marker",
            _ => "Colour",
        };
        ColorRow(mainLabel, a.Color, false, c => Apply(x => x with { Color = x is RedactionAnnotation ? c!.Value with { A = 255 } : c!.Value }, "Colour"));
        Swatches(c => Apply(x => x with { Color = x is RedactionAnnotation ? c with { A = 255 } : c }, "Colour"));

        switch (a)
        {
            case RectangleAnnotation r:
                ColorRow("Fill", r.Fill, true, c => Apply(x => x is RectangleAnnotation y ? y with { Fill = c } : x is EllipseAnnotation e ? e with { Fill = c } : x, "Fill"));
                Number("Corner radius", r.CornerRadius, 0, 500, v => Apply(x => x is RectangleAnnotation y ? y with { CornerRadius = v } : x, "Corner radius"));
                break;
            case EllipseAnnotation e:
                ColorRow("Fill", e.Fill, true, c => Apply(x => x is EllipseAnnotation y ? y with { Fill = c } : x is RectangleAnnotation r2 ? r2 with { Fill = c } : x, "Fill"));
                break;
        }

        if (a is not (HighlightAnnotation or StampAnnotation or RedactionAnnotation))
            Number(a is TextAnnotation and not CalloutAnnotation ? "Border width" : "Line width", a.StrokeWidth, 0, 64,
                v => Apply(x => x with { StrokeWidth = v }, "Line width"));

        switch (a)
        {
            case LineAnnotation l: BuildLine(l); break;
            case TextAnnotation t: BuildText(t); break;
            case StepAnnotation s: BuildStep(s); break;
            case HighlightAnnotation h:
                Number("Strength", h.Opacity, 10, 255, v => Apply(x => x is HighlightAnnotation y ? y with { Opacity = (byte)Math.Round(v) } : x, "Highlight strength"), "0");
                break;
            case StampAnnotation st when st.AssetId is null:
                Combo("Symbol", StampSymbols.All, st.Symbol ?? StampSymbols.Check, v => Apply(x => x is StampAnnotation y && y.AssetId is null ? y with { Symbol = v } : x, "Symbol"));
                break;
            case MagnifierAnnotation m:
                Check("Round lens", m.Circular, v => Apply(x => x is MagnifierAnnotation y ? y with { Circular = v } : x, "Lens shape"));
                if (_protoKind is not null)
                {
                    var ts = _toolStyle(ToolKind.Magnifier);
                    Number("Zoom ×", ts.Zoom, 1.25, 8, v => { _services.ToolStyles.Set(nameof(ToolKind.Magnifier), ts with { Zoom = v }); Refresh(); });
                }
                break;
        }

        Section("Appearance");
        if (!redaction)
            Number("Opacity %", a.Alpha * 100, 5, 100, v => Apply(x => x is RedactionAnnotation ? x : x with { Alpha = v / 100 }, "Opacity"), "0");
        if (a.CanRotate && _targets.Count > 0)
            Number("Rotation °", a.Rotation, -180, 180, v => Apply(x => x.CanRotate ? x with { Rotation = Rotation2D.Normalize(v) } : x, "Rotate"), "0.#");
        if (!redaction) Check("Drop shadow", a.Shadow, v => Apply(x => x is RedactionAnnotation ? x : x with { Shadow = v }, "Shadow"));
        if (_targets.Count > 0) Check("Locked (can't be moved)", a.Locked, v => Apply(x => x with { Locked = v }, v ? "Lock" : "Unlock"));
        if (redaction) Hint("Redaction is always a solid, fully opaque, unrotated box.");

        if (!redaction) BuildQuickStyles(a);
    }

    private void BuildLine(LineAnnotation l)
    {
        Section("Line");
        Combo("Start", Enum.GetValues<ArrowCap>(), l.EffectiveStartCap, v => Apply(x => x is LineAnnotation y ? (y is ArrowAnnotation ar ? ar with { StartCap = v, DoubleHeaded = false } : y with { StartCap = v }) : x, "Start style"));
        Combo("End", Enum.GetValues<ArrowCap>(), l.EffectiveEndCap, v => Apply(x => x is LineAnnotation y ? y with { EndCap = v } : x, "End style"));
        Number("Head size ×", l.HeadSize, 0.25, 8, v => Apply(x => x is LineAnnotation y ? y with { HeadSize = v } : x, "Head size"));
        Combo("Pattern", Enum.GetValues<LineDash>(), l.EffectiveDash, v => Apply(x => x is LineAnnotation y ? y with { Dash = v, Dashed = false } : x, "Line pattern"));
        Check("Curved (drag the square handle)", l.Control is not null, v => Apply(x => x is LineAnnotation y ? y with { Control = v ? DefaultBend(y) : null } : x, v ? "Curve" : "Straighten"));
        Check("White contrast outline", l.Outline is not null, v => Apply(x => x is LineAnnotation y ? y with { Outline = v ? Rgba32.White : null } : x, "Outline"));
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
        if (t is CalloutAnnotation c)
        {
            ColorRow("Text colour", c.TextColor, false, v => Apply(x => x is CalloutAnnotation y ? y with { TextColor = v!.Value } : x, "Text colour"));
            ColorRow("Fill", c.Fill, true, v => Apply(x => x is TextAnnotation y ? y with { Fill = v } : x, "Fill"));
        }
        else
        {
            ColorRow("Box fill", t.Fill, true, v => Apply(x => x is TextAnnotation y ? y with { Fill = v } : x, "Fill"));
            ColorRow("Box border", t.Border, true, v => Apply(x => x is TextAnnotation y and not CalloutAnnotation ? y with { Border = v, StrokeWidth = v is null ? y.StrokeWidth : Math.Max(1, y.StrokeWidth) } : x, "Border"));
        }

        Section("Text");
        var fonts = FontNames.Contains(t.FontFamily) ? FontNames : [t.FontFamily, .. FontNames];
        Combo("Font", fonts, t.FontFamily, v => Apply(x => x is TextAnnotation y ? y with { FontFamily = v } : x is StepAnnotation s ? s with { FontFamily = v } : x, "Font"));
        Number("Size", t.FontSize, 4, 400, v => Apply(x => x is TextAnnotation y ? FitHeight(y with { FontSize = v }) : x, "Font size"));
        var styles = new WrapPanel();
        styles.Children.Add(CheckBox("Bold", t.Bold, v => Apply(x => x is TextAnnotation y ? FitHeight(y with { Bold = v }) : x, "Bold")));
        styles.Children.Add(CheckBox("Italic", t.Italic, v => Apply(x => x is TextAnnotation y ? FitHeight(y with { Italic = v }) : x, "Italic")));
        styles.Children.Add(CheckBox("Underline", t.Underline, v => Apply(x => x is TextAnnotation y ? y with { Underline = v } : x, "Underline")));
        Children.Add(styles);
        Combo("Align", Enum.GetValues<TextAlign>(), t.Alignment, v => Apply(x => x is TextAnnotation y ? y with { Alignment = v } : x, "Text alignment"));
        Number("Padding", t.Padding, 0, 100, v => Apply(x => x is TextAnnotation y ? FitHeight(y with { Padding = v }) : x, "Padding"));
        Number("Corner radius", t.CornerRadius, 0, 100, v => Apply(x => x is TextAnnotation y ? y with { CornerRadius = v } : x, "Corner radius"));
        Check("Glyph outline (readability)", t.Outline is not null, v => Apply(x => x is TextAnnotation y ? y with { Outline = v ? Rgba32.White : null } : x, "Text outline"));

        if (t is CalloutAnnotation co)
        {
            Section("Callout");
            Combo("Shape", Enum.GetValues<CalloutShape>(), co.Shape, v => Apply(x => x is CalloutAnnotation y ? FitHeight(y with { Shape = v }) : x, "Callout shape"));
            Number("Tail width", co.TailWidth, 4, 200, v => Apply(x => x is CalloutAnnotation y ? y with { TailWidth = v } : x, "Tail width"));
        }
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
        ColorRow("Number colour", s.TextColor, false, v => Apply(x => x is StepAnnotation y ? y with { TextColor = v!.Value } : x, "Number colour"));
        ColorRow("Border", s.Border, true, v => Apply(x => x is StepAnnotation y ? y with { Border = v } : x, "Border"));

        Section("Step");
        double size = s.Bounds.Width > 4 ? s.Bounds.Width : 32;
        Number("Size", size, 12, 400, v => Apply(x => x is StepAnnotation y ? y with { Bounds = Square(y.Bounds, v) } : x, "Step size"), "0");
        Combo("Shape", Enum.GetValues<StepShape>(), s.Shape, v => Apply(x => x is StepAnnotation y ? y with { Shape = v } : x, "Step shape"));
        Combo("Label", Enum.GetValues<StepLabelStyle>(), s.LabelStyle, v => Apply(x => x is StepAnnotation y ? y with { LabelStyle = v } : x, "Step label"));
        if (s.LabelStyle == StepLabelStyle.Custom)
            Text("Custom text", s.CustomText ?? "", StepLabels.MaxCustom, v => Apply(x => x is StepAnnotation y ? y with { CustomText = v } : x, "Step text"));
        Text("Prefix", s.Prefix, StepLabels.MaxAffix, v => Apply(x => x is StepAnnotation y ? y with { Prefix = v } : x, "Step prefix"));
        Text("Suffix", s.Suffix, StepLabels.MaxAffix, v => Apply(x => x is StepAnnotation y ? y with { Suffix = v } : x, "Step suffix"));
        var fonts = FontNames.Contains(s.FontFamily) ? FontNames : [s.FontFamily, .. FontNames];
        Combo("Font", fonts, s.FontFamily, v => Apply(x => x is StepAnnotation y ? y with { FontFamily = v } : x, "Step font"));
        Check("Pointer tail", s.Tail is not null, v => Apply(x => x is StepAnnotation y ? y with { Tail = v ? new PointD(y.Bounds.Right + y.Bounds.Width, y.Bounds.Bottom + y.Bounds.Height * 0.6) : null } : x, v ? "Add pointer" : "Remove pointer"));

        if (_targets.Count > 0)
        {
            if (_targets.Count == 1) Number("Number", s.Number, 0, 9999, v => Apply(x => x is StepAnnotation y ? y with { Number = (int)Math.Round(v) } : x, "Step number"), "0");
            var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(Button("− 1", () => _vm.AdjustStepNumbers(-1), "Decrease the number (−)"));
            row.Children.Add(Button("+ 1", () => _vm.AdjustStepNumbers(1), "Increase the number (+)"));
            if (_targets.Count == 1) row.Children.Add(Button("Renumber following", _vm.RenumberStepsFromSelected, "Later steps count up from this one"));
            Children.Add(row);
        }
        else Hint($"The next step is {StepLabels.Format(s.LabelStyle, Core.Editing.DocumentOps.NextStepNumber(_vm.Document), s.CustomText, s.Prefix, s.Suffix)}.");
    }

    private static RectD Square(RectD b, double size)
    {
        var c = b.IsEmpty ? new PointD(size / 2, size / 2) : b.Center;
        return new RectD(c.X - size / 2, c.Y - size / 2, size, size);
    }

    private void BuildQuickStyles(Annotation a)
    {
        Section("Quick styles");
        var list = _services.AnnotationStyles.QuickStyles;
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        if (list.Count > 0)
        {
            var combo = new ComboBox { MinWidth = 140, Margin = new Thickness(0, 0, 4, 4), ItemsSource = list.Select(q => q.Name).ToList(), SelectedIndex = 0 };
            AutomationProperties.SetName(combo, "Quick style");
            row.Children.Add(combo);
            row.Children.Add(Button("Apply", () =>
            {
                if (combo.SelectedItem is string n && list.FirstOrDefault(q => q.Name == n) is { } q)
                    Apply(x => AnnotationStyle.Transfer(q.Style, x), $"Style {q.Name}");
            }, "Apply the chosen style"));
            row.Children.Add(Button("Delete", () =>
            {
                if (combo.SelectedItem is string n) { _services.AnnotationStyles.DeleteQuick(n); Refresh(); }
            }, "Delete the chosen style"));
        }
        row.Children.Add(Button("Save as…", () =>
        {
            var name = Dialogs.Prompt(Window.GetWindow(this), "Save quick style", "Style name:", $"My {a.Kind.ToLowerInvariant()}");
            if (name is null) return;
            try { _services.AnnotationStyles.SaveQuick(name, a); _vm.Status = $"Saved style '{name.Trim()}'."; }
            catch (ArgumentException ex) { _vm.Status = ex.Message; }
            Refresh();
        }, "Save this look as a named style"));
        Children.Add(row);
    }

    // ================================================================== apply

    private void Apply(Func<Annotation, Annotation> f, string label)
    {
        if (_targets.Count > 0) _vm.UpdateSelectedAnnotations(f, label);
        else if (_protoKind is { } k && _sample is { } s) _services.AnnotationStyles.SetPrototype(k, f(s));
        Refresh();
    }

    private void ApplyColor(Rgba32 c, Action<Rgba32?> set)
    {
        _services.AnnotationStyles.UseColor(c);
        set(c);
    }

    // ================================================================== controls

    private void Header(string text) =>
        Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });

    private void Section(string text) =>
        Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 8, 0, 2) });

    private void Hint(string text) =>
        Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });

    private static Grid Row(string label, FrameworkElement c)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var lb = new Label { Content = label, Padding = new Thickness(0, 3, 6, 0), Target = c };
        AutomationProperties.SetName(c, label);
        Grid.SetColumn(c, 1);
        g.Children.Add(lb);
        g.Children.Add(c);
        return g;
    }

    /// <summary>Adds a TextBox committed on Enter / focus loss (once), validated and clamped.</summary>
    private void Number(string label, double value, double min, double max, Action<double> set, string fmt = "0.##")
    {
        var tb = new TextBox { Text = value.ToString(fmt, CultureInfo.CurrentCulture), ToolTip = $"{min.ToString(CultureInfo.CurrentCulture)}–{max.ToString(CultureInfo.CurrentCulture)}" };
        bool done = false;
        void Commit()
        {
            if (done) return;
            if (!double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || !double.IsFinite(v))
            {
                _vm.Status = $"{label}: enter a number from {min.ToString(CultureInfo.CurrentCulture)} to {max.ToString(CultureInfo.CurrentCulture)}.";
                tb.Text = value.ToString(fmt, CultureInfo.CurrentCulture);
                return;
            }
            v = Math.Clamp(v, min, max);
            if (Math.Abs(v - value) < 1e-9) { tb.Text = v.ToString(fmt, CultureInfo.CurrentCulture); return; }
            done = true;
            set(v);
        }
        tb.LostKeyboardFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { tb.Text = value.ToString(fmt, CultureInfo.CurrentCulture); e.Handled = true; }
        };
        Children.Add(Row(label, tb));
    }

    private void Text(string label, string value, int maxLength, Action<string> set)
    {
        var tb = new TextBox { Text = value, MaxLength = maxLength };
        bool done = false;
        void Commit()
        {
            if (done || tb.Text == value) return;
            done = true;
            set(tb.Text);
        }
        tb.LostKeyboardFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        Children.Add(Row(label, tb));
    }

    private void ColorRow(string label, Rgba32? value, bool allowNone, Action<Rgba32?> set)
    {
        var tb = new TextBox { Text = value?.ToHex() ?? "", ToolTip = allowNone ? "#RRGGBB or #RRGGBBAA; empty = none" : "#RRGGBB or #RRGGBBAA" };
        var chip = new Border
        {
            Width = 18, Height = 18, Margin = new Thickness(4, 0, 0, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Background = value is { } v ? new SolidColorBrush(v.ToColor()) : Brushes.Transparent,
        };
        var dock = new DockPanel();
        DockPanel.SetDock(chip, Dock.Right);
        dock.Children.Add(chip);
        dock.Children.Add(tb);
        bool done = false;
        void Commit()
        {
            if (done) return;
            var text = tb.Text.Trim();
            if (text.Length == 0)
            {
                if (!allowNone) { tb.Text = value?.ToHex() ?? ""; _vm.Status = $"{label} needs a colour."; return; }
                if (value is null) return;
                done = true;
                set(null);
                return;
            }
            if (!Rgba32.TryParse(text, out var c)) { _vm.Status = $"{label}: use #RRGGBB or #RRGGBBAA."; tb.Text = value?.ToHex() ?? ""; return; }
            if (c == value) return;
            done = true;
            ApplyColor(c, set);
        }
        tb.LostKeyboardFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        AutomationProperties.SetName(tb, label);
        Children.Add(Row(label, dock));
    }

    private void Swatches(Action<Rgba32> set)
    {
        var wrap = new WrapPanel { Margin = new Thickness(100, 0, 0, 4) };
        foreach (var c in Palette.Concat(_services.AnnotationStyles.RecentColors.Except(Palette)).Take(18))
        {
            var b = new Button
            {
                Width = 18, Height = 18, Margin = new Thickness(0, 0, 3, 3), Padding = new Thickness(0),
                Background = new SolidColorBrush(c.ToColor()), BorderBrush = Brushes.Gray, ToolTip = c.ToHex(),
            };
            AutomationProperties.SetName(b, "Colour " + c.ToHex());
            b.Click += (_, _) => { _services.AnnotationStyles.UseColor(c); set(c); };
            wrap.Children.Add(b);
        }
        Children.Add(wrap);
    }

    private void Combo<T>(string label, IEnumerable<T> items, T selected, Action<T> set)
    {
        var cb = new ComboBox { ItemsSource = items.ToList(), SelectedItem = selected, Margin = new Thickness(0, 1, 0, 1) };
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is T v && !EqualityComparer<T>.Default.Equals(v, selected)) set(v);
        };
        Children.Add(Row(label, cb));
    }

    private void Check(string label, bool value, Action<bool> set) => Children.Add(CheckBox(label, value, set));

    private static CheckBox CheckBox(string label, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 2, 10, 2) };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    private static Button Button(string text, Action a, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 4), ToolTip = tip };
        b.Click += (_, _) => a();
        return b;
    }
}
