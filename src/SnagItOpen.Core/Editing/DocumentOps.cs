using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Core.Layout;

namespace SnagItOpen.Core.Editing;

/// <summary>
/// Pure document transformations. Every method returns a new <see cref="DocumentState"/> with fresh
/// arrays and never mutates its input. Callers commit results through <see cref="History"/>.
/// </summary>
public static class DocumentOps
{
    public const int DuplicateOffset = 16;

    // ---------------------------------------------------------------- layout / canvas

    /// <summary>Re-runs automatic layout (non-Free) and automatic canvas sizing.</summary>
    public static DocumentState Reflow(DocumentState doc)
    {
        // Annotations are canvas objects: convert any legacy image-linked ones before images move.
        var d = AnnotationCanvas.Normalize(doc);
        PixelRect? layoutArea = null;
        if (d.Layout.Mode != LayoutMode.Free && d.Images.Length > 0)
        {
            var ordered = d.ImagesInLayoutOrder();
            var result = LayoutEngine.Arrange(ordered, d.Layout);
            var map = result.Images.ToDictionary(i => i.Id);
            d = d with { Images = d.Images.Select(i => map.TryGetValue(i.Id, out var n) ? n : i).ToArray() };
            layoutArea = result.ExportArea;
        }
        if (d.AutoCanvas)
        {
            var fit = DocumentBounds.Fit(d, d.Layout.Padding);
            var area = layoutArea is { } la ? (DocumentBounds.ContentBounds(d).IsEmpty ? la : la.Union(fit)) : fit;
            if (!Limits.IsAcceptableExportSize(area.Width, area.Height))
                throw new LayoutLimitException($"Canvas {area.Width}×{area.Height} exceeds the limit of {Limits.MaxDimension}px per side / {Limits.MaxExportPixels / 1_000_000} MP.");
            d = d with { ExportArea = area };
        }
        return d;
    }

    /// <summary>Changes layout options. Switching to Free preserves current geometry.</summary>
    public static DocumentState SetLayout(DocumentState doc, LayoutOptions options)
    {
        LayoutEngine.ValidateOptions(options);
        return Reflow(doc with { Layout = options });
    }

    public static DocumentState SetMode(DocumentState doc, LayoutMode mode) =>
        SetLayout(doc, doc.Layout with { Mode = mode });

    /// <summary>Explicit canvas rectangle; disables automatic canvas.</summary>
    public static DocumentState SetExportArea(DocumentState doc, PixelRect area)
    {
        if (area.IsEmpty || !Limits.IsAcceptableExportSize(area.Width, area.Height))
            throw new ArgumentOutOfRangeException(nameof(area), $"Canvas {area.Width}×{area.Height} is outside allowed limits.");
        return doc with { ExportArea = area, AutoCanvas = false };
    }

    /// <summary>Fits canvas around visible content plus padding and re-enables automatic canvas sizing.</summary>
    public static DocumentState FitCanvas(DocumentState doc, int? padding = null)
    {
        var o = padding is { } p ? doc.Layout with { Padding = Math.Clamp(p, 0, Limits.MaxPadding) } : doc.Layout;
        return Reflow(doc with { Layout = o, AutoCanvas = true });
    }

    public static DocumentState SetBackground(DocumentState doc, Rgba32 color) => doc with { Background = color };

    // ---------------------------------------------------------------- add / remove

    /// <summary>Appends images (and their assets if new). In layout modes they join the end of the order.</summary>
    public static DocumentState AddImages(DocumentState doc, IEnumerable<(ImageAsset Asset, ImageLayer Layer)> items)
    {
        var assets = doc.Assets.ToList();
        var images = doc.Images.ToList();
        var order = doc.LayoutOrder.ToList();
        foreach (var (asset, layer) in items)
        {
            if (!assets.Any(a => a.Id == asset.Id)) assets.Add(asset);
            images.Add(layer);
            order.Add(layer.Id);
        }
        if (assets.Count > Limits.MaxAssets)
            throw new LayoutLimitException($"A document can contain at most {Limits.MaxAssets} distinct images.");
        return Reflow(doc with { Assets = assets.ToArray(), Images = images.ToArray(), LayoutOrder = order.ToArray() });
    }

    /// <summary>Places a new image in Free mode next to <paramref name="anchor"/> or at a point.</summary>
    public static ImageLayer PlaceLayer(ImageAsset asset, PixelPoint topLeft, string? name = null) =>
        ImageLayer.ForAsset(asset, topLeft.X, topLeft.Y, name);

    /// <summary>
    /// Removes image layers and assets no longer referenced. Annotations are canvas objects and stay.
    /// </summary>
    public static DocumentState RemoveImages(DocumentState doc, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return doc;
        var set = ids.ToHashSet();
        var src = AnnotationCanvas.Normalize(doc);
        var d = src with
        {
            Images = src.Images.Where(i => !set.Contains(i.Id)).ToArray(),
            LayoutOrder = src.LayoutOrder.Where(i => !set.Contains(i)).ToArray(),
        };
        return Reflow(PruneAssets(d));
    }

    /// <summary>Drops asset records that no layer or stamp references.</summary>
    public static DocumentState PruneAssets(DocumentState doc)
    {
        var used = new HashSet<string>(doc.Images.Select(i => i.AssetId), StringComparer.Ordinal);
        foreach (var a in doc.Annotations) if (a is StampAnnotation { AssetId: { } s }) used.Add(s);
        return doc.Assets.All(a => used.Contains(a.Id)) ? doc : doc with { Assets = doc.Assets.Where(a => used.Contains(a.Id)).ToArray() };
    }

    /// <summary>Adds an asset (e.g. for stamps) without creating a layer.</summary>
    public static DocumentState EnsureAsset(DocumentState doc, ImageAsset asset) =>
        doc.Assets.Any(a => a.Id == asset.Id) ? doc : doc with { Assets = [.. doc.Assets, asset] };

    /// <summary>Duplicates image layers (same asset, new ID, +16px offset). Annotations are not duplicated.</summary>
    public static (DocumentState Doc, Guid[] NewIds) Duplicate(DocumentState doc, IReadOnlyCollection<Guid> ids)
    {
        doc = AnnotationCanvas.Normalize(doc);
        var images = doc.Images.ToList();
        var order = doc.LayoutOrder.ToList();
        var created = new List<Guid>();
        foreach (var src in doc.Images.Where(i => ids.Contains(i.Id)))
        {
            var copy = src with { Id = Guid.NewGuid(), Bounds = src.Bounds.Translate(DuplicateOffset, DuplicateOffset), Name = src.Name };
            images.Add(copy);
            int idx = order.IndexOf(src.Id);
            order.Insert(idx < 0 ? order.Count : idx + 1, copy.Id);
            created.Add(copy.Id);
        }
        var d = doc with { Images = images.ToArray(), LayoutOrder = order.ToArray() };
        return (Reflow(d), created.ToArray());
    }

    /// <summary>Duplicates annotations with a +16px offset; returns the new IDs.</summary>
    public static (DocumentState Doc, Guid[] NewIds) DuplicateAnnotations(DocumentState doc, IReadOnlyCollection<Guid> ids, double dx = DuplicateOffset, double dy = DuplicateOffset)
    {
        doc = AnnotationCanvas.Normalize(doc);
        var anns = doc.Annotations.ToList();
        var created = new List<Guid>();
        foreach (var a in doc.Annotations.Where(a => ids.Contains(a.Id)))
        {
            var c = a.Offset(dx, dy) with { Id = Guid.NewGuid() };
            anns.Add(c);
            created.Add(c.Id);
        }
        return (Reflow(doc with { Annotations = anns.ToArray() }), created.ToArray());
    }

    // ---------------------------------------------------------------- ordering

    /// <summary>Moves an image to a new position in layout order.</summary>
    public static DocumentState MoveInLayout(DocumentState doc, Guid id, int newIndex)
    {
        var order = doc.LayoutOrder.ToList();
        int i = order.IndexOf(id);
        if (i < 0) return doc;
        newIndex = Math.Clamp(newIndex, 0, order.Count - 1);
        if (i == newIndex) return doc;
        order.RemoveAt(i);
        order.Insert(newIndex, id);
        return Reflow(doc with { LayoutOrder = order.ToArray() });
    }

    public static DocumentState SetLayoutOrder(DocumentState doc, IReadOnlyList<Guid> order)
    {
        if (order.Count != doc.LayoutOrder.Length || !order.ToHashSet().SetEquals(doc.LayoutOrder))
            throw new ArgumentException("Order must contain every image exactly once.", nameof(order));
        return Reflow(doc with { LayoutOrder = order.ToArray() });
    }

    public enum ZMove { Forward, Backward, ToFront, ToBack }

    /// <summary>Changes drawing (z) order; independent of layout order.</summary>
    public static DocumentState ChangeZOrder(DocumentState doc, IReadOnlyCollection<Guid> ids, ZMove move)
    {
        var list = doc.Images.ToList();
        var sel = list.Where(i => ids.Contains(i.Id)).ToList();
        if (sel.Count == 0) return doc;
        switch (move)
        {
            case ZMove.ToFront:
                list.RemoveAll(i => ids.Contains(i.Id)); list.AddRange(sel); break;
            case ZMove.ToBack:
                list.RemoveAll(i => ids.Contains(i.Id)); list.InsertRange(0, sel); break;
            case ZMove.Forward:
                for (int k = list.Count - 2; k >= 0; k--)
                    if (ids.Contains(list[k].Id) && !ids.Contains(list[k + 1].Id)) (list[k], list[k + 1]) = (list[k + 1], list[k]);
                break;
            case ZMove.Backward:
                for (int k = 1; k < list.Count; k++)
                    if (ids.Contains(list[k].Id) && !ids.Contains(list[k - 1].Id)) (list[k], list[k - 1]) = (list[k - 1], list[k]);
                break;
        }
        return doc with { Images = list.ToArray() };
    }

    // ---------------------------------------------------------------- geometry

    private static DocumentState EnsureFree(DocumentState doc) =>
        doc.Layout.Mode == LayoutMode.Free ? doc : doc with { Layout = doc.Layout with { Mode = LayoutMode.Free } };

    private static DocumentState ReplaceImages(DocumentState doc, Func<ImageLayer, ImageLayer> f, IReadOnlyCollection<Guid> ids) =>
        WithImages(AnnotationCanvas.Normalize(doc), f, ids);

    private static DocumentState WithImages(DocumentState n, Func<ImageLayer, ImageLayer> f, IReadOnlyCollection<Guid> ids) =>
        n with { Images = n.Images.Select(i => ids.Contains(i.Id) ? f(i) : i).ToArray() };

    /// <summary>
    /// Moves images and annotations by an integer offset. Moving images switches to Free mode,
    /// preserving other geometry. Annotations are canvas objects and move only when selected.
    /// </summary>
    public static DocumentState Move(DocumentState doc, IReadOnlyCollection<Guid> imageIds, IReadOnlyCollection<Guid> annotationIds, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return doc;
        var d = AnnotationCanvas.Normalize(doc);
        if (imageIds.Count > 0) d = ReplaceImages(EnsureFree(d), i => i with { Bounds = i.Bounds.Translate(dx, dy) }, imageIds);
        if (annotationIds.Count > 0)
            d = d with { Annotations = d.Annotations.Select(a => annotationIds.Contains(a.Id) ? a.Offset(dx, dy) : a).ToArray() };
        return Reflow(d);
    }

    /// <summary>Sets exact document bounds of one image (switches to Free).</summary>
    public static DocumentState SetBounds(DocumentState doc, Guid id, PixelRect bounds)
    {
        if (bounds.Width < 1 || bounds.Height < 1) throw new ArgumentOutOfRangeException(nameof(bounds), "Size must be at least 1×1.");
        if (bounds.Width > Limits.MaxDimension || bounds.Height > Limits.MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(bounds), $"Size must not exceed {Limits.MaxDimension}px.");
        return Reflow(ReplaceImages(EnsureFree(doc), i => i with { Bounds = bounds }, [id]));
    }

    public static DocumentState SetVisible(DocumentState doc, Guid id, bool visible) =>
        Reflow(ReplaceImages(doc, i => i with { Visible = visible }, [id]));

    public static DocumentState Rename(DocumentState doc, Guid id, string? name) =>
        ReplaceImages(doc, i => i with { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() }, [id]);

    /// <summary>
    /// Nondestructive crop in source pixels. Free mode keeps surviving pixels at the same document
    /// position and scale; layout modes reflow.
    /// </summary>
    public static DocumentState Crop(DocumentState doc, Guid id, PixelRect newCrop)
    {
        var layer = doc.FindImage(id) ?? throw new ArgumentException("Unknown image.", nameof(id));
        var asset = doc.FindAsset(layer.AssetId) ?? throw new InvalidOperationException("Missing asset.");
        var crop = newCrop.Intersect(asset.FullRect);
        if (crop.IsEmpty) throw new ArgumentOutOfRangeException(nameof(newCrop), "Crop must keep at least one pixel.");
        if (crop == layer.SourceCrop) return doc;

        ImageLayer updated;
        if (doc.Layout.Mode == LayoutMode.Free)
        {
            var docRect = ImageTransform.SourceRectToDocument(layer, crop.ToRectD()).ToPixelRectRounded();
            updated = layer with
            {
                SourceCrop = crop,
                Bounds = docRect with { Width = Math.Max(1, docRect.Width), Height = Math.Max(1, docRect.Height) },
            };
        }
        else
        {
            updated = layer with { SourceCrop = crop, Bounds = layer.Bounds with { Width = Math.Max(1, crop.Width), Height = Math.Max(1, crop.Height) } };
        }
        return Reflow(ReplaceImages(doc, _ => updated, [id]));
    }

    public static DocumentState ResetCrop(DocumentState doc, Guid id)
    {
        var layer = doc.FindImage(id) ?? throw new ArgumentException("Unknown image.", nameof(id));
        var asset = doc.FindAsset(layer.AssetId) ?? throw new InvalidOperationException("Missing asset.");
        return Crop(doc, id, asset.FullRect);
    }

    /// <summary>Rotates by quarter turns (positive = clockwise) about the image center.</summary>
    public static DocumentState Rotate(DocumentState doc, IReadOnlyCollection<Guid> ids, int quarterTurns)
    {
        int q = ((quarterTurns % 4) + 4) % 4;
        if (q == 0 || ids.Count == 0) return doc;
        var d = ReplaceImages(doc, i =>
        {
            var b = i.Bounds;
            var nb = (q & 1) == 1
                ? new PixelRect(b.X + (b.Width - b.Height) / 2, b.Y + (b.Height - b.Width) / 2, b.Height, b.Width)
                : b;
            return i with { QuarterTurns = (i.QuarterTurns + q) % 4, Bounds = nb };
        }, ids);
        return Reflow(d);
    }

    /// <summary>Flips visually horizontal/vertical (in document axes), accounting for current rotation.</summary>
    public static DocumentState Flip(DocumentState doc, IReadOnlyCollection<Guid> ids, bool horizontal)
    {
        // Document flip D after rotation R^q: D·R^q = R^-q·D, and D commutes with source flips,
        // so toggle the same-axis source flip and negate the turn count.
        var d = ReplaceImages(doc, i =>
        {
            int q = (4 - i.QuarterTurns) % 4;
            return horizontal
                ? i with { FlipHorizontal = !i.FlipHorizontal, QuarterTurns = q }
                : i with { FlipVertical = !i.FlipVertical, QuarterTurns = q };
        }, ids);
        return Reflow(d);
    }

    /// <summary>Scales all geometry (images, document annotations, export area) by a factor.</summary>
    public static DocumentState ScaleDocument(DocumentState doc, double factor)
    {
        if (!(factor > 0) || !double.IsFinite(factor)) throw new ArgumentOutOfRangeException(nameof(factor));
        static int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        var images = doc.Images.Select(i =>
        {
            var b = i.Bounds;
            int x0 = R(b.X * factor), y0 = R(b.Y * factor);
            int w = Math.Max(1, R(b.Right * factor) - x0), h = Math.Max(1, R(b.Bottom * factor) - y0);
            return i with { Bounds = new PixelRect(x0, y0, w, h) };
        }).ToArray();
        doc = AnnotationCanvas.Normalize(doc);
        var anns = doc.Annotations.Select(a =>
        {
            var s = a.MapGeometry(p => p * factor);
            return s switch
            {
                TextAnnotation t => t with { FontSize = Math.Max(1, t.FontSize * factor), StrokeWidth = a.StrokeWidth * factor },
                _ => s with { StrokeWidth = a.StrokeWidth * factor },
            };
        }).ToArray();
        var e = doc.ExportArea;
        int ex = R(e.X * factor), ey = R(e.Y * factor);
        var area = new PixelRect(ex, ey, Math.Max(1, R(e.Right * factor) - ex), Math.Max(1, R(e.Bottom * factor) - ey));
        if (!Limits.IsAcceptableExportSize(area.Width, area.Height))
            throw new LayoutLimitException($"Scaled canvas {area.Width}×{area.Height} exceeds limits.");
        var layout = doc.Layout with
        {
            Gap = Math.Clamp(R(doc.Layout.Gap * factor), 0, Limits.MaxGap),
            Padding = Math.Clamp(R(doc.Layout.Padding * factor), 0, Limits.MaxPadding),
            Mode = LayoutMode.Free,
        };
        return doc with { Images = images, Annotations = anns, ExportArea = area, Layout = layout };
    }

    // ---------------------------------------------------------------- annotations

    public static DocumentState AddAnnotation(DocumentState doc, Annotation a) =>
        Reflow(doc with { Annotations = [.. doc.Annotations, a] });

    public static DocumentState UpdateAnnotation(DocumentState doc, Annotation a) =>
        Reflow(doc with { Annotations = doc.Annotations.Select(x => x.Id == a.Id ? a : x).ToArray() });

    public static DocumentState UpdateAnnotations(DocumentState doc, IReadOnlyCollection<Guid> ids, Func<Annotation, Annotation> f) =>
        Reflow(doc with { Annotations = doc.Annotations.Select(x => ids.Contains(x.Id) ? f(x) : x).ToArray() });

    public static DocumentState RemoveAnnotations(DocumentState doc, IReadOnlyCollection<Guid> ids) =>
        ids.Count == 0 ? doc : Reflow(PruneAssets(doc with { Annotations = doc.Annotations.Where(a => !ids.Contains(a.Id)).ToArray() }));

    public static DocumentState ChangeAnnotationZOrder(DocumentState doc, IReadOnlyCollection<Guid> ids, ZMove move)
    {
        var list = doc.Annotations.ToList();
        var sel = list.Where(a => ids.Contains(a.Id)).ToList();
        if (sel.Count == 0) return doc;
        list.RemoveAll(a => ids.Contains(a.Id));
        if (move is ZMove.ToFront or ZMove.Forward) list.AddRange(sel); else list.InsertRange(0, sel);
        return doc with { Annotations = list.ToArray() };
    }

    /// <summary>Renumbers step annotations 1..n in their current drawing order.</summary>
    public static DocumentState RenumberSteps(DocumentState doc)
    {
        int n = 1;
        return doc with { Annotations = doc.Annotations.Select(a => a is StepAnnotation s ? s with { Number = n++ } : a).ToArray() };
    }

    public static int NextStepNumber(DocumentState doc) =>
        doc.Annotations.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>
    /// Renumbers steps in drawing order starting at the given step: it keeps <paramref name="start"/>
    /// (or its own number) and every later step counts up from there. Earlier steps are unchanged.
    /// </summary>
    public static DocumentState RenumberStepsFrom(DocumentState doc, Guid fromId, int? start = null)
    {
        int idx = Array.FindIndex(doc.Annotations, a => a.Id == fromId && a is StepAnnotation);
        if (idx < 0) return doc;
        int n = Math.Clamp(start ?? ((StepAnnotation)doc.Annotations[idx]).Number, 0, 9999);
        var arr = doc.Annotations.ToArray();
        for (int i = idx; i < arr.Length; i++)
            if (arr[i] is StepAnnotation s) arr[i] = s with { Number = Math.Min(9999, n++) };
        return doc with { Annotations = arr };
    }

    /// <summary>Adds <paramref name="delta"/> to the numbers of the given steps (clamped 0–9999).</summary>
    public static DocumentState AdjustStepNumbers(DocumentState doc, IReadOnlyCollection<Guid> ids, int delta) =>
        doc with
        {
            Annotations = doc.Annotations.Select(a => a is StepAnnotation s && ids.Contains(s.Id)
                ? s with { Number = Math.Clamp(s.Number + delta, 0, 9999) } : a).ToArray(),
        };

    /// <summary>Aligns selected (unlocked) annotations to the union of their extents.</summary>
    public static DocumentState AlignAnnotations(DocumentState doc, IReadOnlyCollection<Guid> ids, AlignMode mode) =>
        ApplyOffsets(doc, AnnotationGeometry.Align(Boxes(doc, ids), mode));

    /// <summary>Spaces three or more selected annotations evenly (first and last stay put).</summary>
    public static DocumentState DistributeAnnotations(DocumentState doc, IReadOnlyCollection<Guid> ids, bool horizontal) =>
        ApplyOffsets(doc, AnnotationGeometry.Distribute(Boxes(doc, ids), horizontal));

    private static List<(Guid, RectD)> Boxes(DocumentState doc, IReadOnlyCollection<Guid> ids) =>
        doc.Annotations.Where(a => ids.Contains(a.Id) && !a.Locked).Select(a => (a.Id, a.Extent())).ToList();

    private static DocumentState ApplyOffsets(DocumentState doc, IReadOnlyList<(Guid Id, double Dx, double Dy)> moves)
    {
        if (moves.Count == 0) return doc;
        var map = moves.ToDictionary(m => m.Id);
        return Reflow(doc with
        {
            Annotations = doc.Annotations.Select(a => map.TryGetValue(a.Id, out var m) && (Math.Abs(m.Dx) > 1e-9 || Math.Abs(m.Dy) > 1e-9)
                ? a.Offset(m.Dx, m.Dy) : a).ToArray(),
        });
    }

    /// <summary>Pastes copies of annotations (new IDs, offset). Returns the new IDs.</summary>
    public static (DocumentState Doc, Guid[] NewIds) PasteAnnotations(DocumentState doc, IReadOnlyList<Annotation> items, double dx, double dy)
    {
        var copies = items.Where(a => a is not null)
            .Select(a => a.Offset(dx, dy) with { Id = Guid.NewGuid(), ImageLayerId = null, Locked = false })
            // Pasted stamps that reference an asset this document lacks become plain symbol stamps.
            .Select(a => a is StampAnnotation { AssetId: { } sid } st && doc.FindAsset(sid) is null ? st with { AssetId = null } : a)
            .ToArray();
        return (Reflow(doc with { Annotations = [.. doc.Annotations, .. copies] }), copies.Select(c => c.Id).ToArray());
    }

    // ---------------------------------------------------------------- effects / edges

    public static DocumentState SetEffects(DocumentState doc, Guid id, ImageEffect[] effects) =>
        ReplaceImages(doc, i => i with { Effects = effects }, [id]);

    public static DocumentState AddEffect(DocumentState doc, Guid id, ImageEffect effect)
    {
        var l = doc.FindImage(id) ?? throw new ArgumentException("Unknown image.", nameof(id));
        var asset = doc.FindAsset(l.AssetId)!;
        var region = effect.Region.Intersect(asset.FullRect);
        if (region.IsEmpty) throw new ArgumentOutOfRangeException(nameof(effect), "Effect region is outside the image.");
        return SetEffects(doc, id, [.. l.Effects, effect with { Region = region }]);
    }

    public static DocumentState SetEdge(DocumentState doc, IReadOnlyCollection<Guid> ids, EdgeStyle? edge) =>
        Reflow(ReplaceImages(doc, i => i with { Edge = edge is { IsEmpty: true } ? null : edge }, ids));

    // ---------------------------------------------------------------- queries

    /// <summary>Topmost visible image containing a document point.</summary>
    public static ImageLayer? HitTestImage(DocumentState doc, PointD p)
    {
        for (int k = doc.Images.Length - 1; k >= 0; k--)
        {
            var l = doc.Images[k];
            if (l.Visible && l.Bounds.ToRectD().Contains(p)) return l;
        }
        return null;
    }
}
