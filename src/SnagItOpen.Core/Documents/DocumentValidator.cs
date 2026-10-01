using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Documents;

/// <summary>Validates structural integrity of a <see cref="DocumentState"/>.</summary>
public static class DocumentValidator
{
    public static IReadOnlyList<string> Validate(DocumentState doc)
    {
        var errors = new List<string>();
        if (doc is null) { errors.Add("Document is null."); return errors; }
        if (doc.SchemaVersion < 1 || doc.SchemaVersion > DocumentState.CurrentSchemaVersion)
            errors.Add($"Unsupported schema version {doc.SchemaVersion}.");
        if (doc.Assets is null || doc.Images is null || doc.LayoutOrder is null || doc.Annotations is null || doc.Layout is null)
        {
            errors.Add("Document has missing collections.");
            return errors;
        }
        if (doc.ExportArea.IsEmpty) errors.Add("Export area must have positive size.");
        else if (!Overflows(doc.ExportArea) && !Limits.IsAcceptableExportSize(doc.ExportArea.Width, doc.ExportArea.Height))
            errors.Add($"Export area {doc.ExportArea.Size} exceeds limits.");
        if (Overflows(doc.ExportArea)) errors.Add("Export area coordinates overflow.");

        if (doc.Assets.Length > Limits.MaxAssets) errors.Add($"Too many assets (max {Limits.MaxAssets}).");
        var assets = new Dictionary<string, ImageAsset>(StringComparer.Ordinal);
        foreach (var a in doc.Assets)
        {
            if (a is null || string.IsNullOrWhiteSpace(a.Id)) { errors.Add("Asset with empty ID."); continue; }
            if (!IsHash(a.Id)) errors.Add($"Asset ID '{a.Id}' is not a SHA-256 hex hash.");
            if (!assets.TryAdd(a.Id, a)) errors.Add($"Duplicate asset ID {a.Id}.");
            if (!Limits.IsAcceptableImageSize(a.Width, a.Height)) errors.Add($"Asset {Short(a.Id)} has invalid size {a.Width}×{a.Height}.");
        }

        var imageIds = new HashSet<Guid>();
        foreach (var l in doc.Images)
        {
            if (l is null) { errors.Add("Null image layer."); continue; }
            if (l.Id == Guid.Empty) errors.Add("Image layer with empty ID.");
            if (!imageIds.Add(l.Id)) errors.Add($"Duplicate image layer ID {l.Id}.");
            if (!assets.TryGetValue(l.AssetId ?? "", out var asset)) { errors.Add($"Layer {l.Id} references unknown asset."); }
            else
            {
                if (l.SourceCrop.IsEmpty) errors.Add($"Layer {l.Id} has an empty crop.");
                else if (!asset.FullRect.Contains(l.SourceCrop)) errors.Add($"Layer {l.Id} crop {l.SourceCrop} is outside source {asset.Size}.");
            }
            if (l.Bounds.IsEmpty) errors.Add($"Layer {l.Id} has empty bounds.");
            if (Overflows(l.Bounds)) errors.Add($"Layer {l.Id} bounds overflow.");
            else if (l.Bounds.Width > Limits.MaxDimension || l.Bounds.Height > Limits.MaxDimension) errors.Add($"Layer {l.Id} bounds exceed limits.");
            if (l.QuarterTurns is < 0 or > 3) errors.Add($"Layer {l.Id} orientation must be 0–3.");
            foreach (var e in l.Effects ?? [])
            {
                if (e is null) { errors.Add($"Layer {l.Id} has a null effect."); continue; }
                if (!e.IsValidStrength) errors.Add($"Layer {l.Id} effect strength out of range.");
                if (e.Region.IsEmpty) errors.Add($"Layer {l.Id} effect region is empty.");
            }
        }

        var layoutSet = new HashSet<Guid>();
        foreach (var id in doc.LayoutOrder)
        {
            if (!layoutSet.Add(id)) errors.Add($"Layout order contains {id} twice.");
            if (!imageIds.Contains(id)) errors.Add($"Layout order references unknown image {id}.");
        }
        foreach (var id in imageIds) if (!layoutSet.Contains(id)) errors.Add($"Image {id} missing from layout order.");

        var o = doc.Layout;
        if (o.Gap is < 0 or > Limits.MaxGap) errors.Add("Gap out of range.");
        if (o.Padding is < 0 or > Limits.MaxPadding) errors.Add("Padding out of range.");
        if (o.TargetCrossPixels is { } t && (t <= 0 || t > Limits.MaxDimension)) errors.Add("Target size out of range.");
        if (!Enum.IsDefined(o.Mode) || !Enum.IsDefined(o.Alignment) || !Enum.IsDefined(o.Scale)) errors.Add("Invalid layout enum value.");

        var annIds = new HashSet<Guid>();
        foreach (var a in doc.Annotations)
        {
            if (a is null) { errors.Add("Null annotation."); continue; }
            if (!annIds.Add(a.Id)) errors.Add($"Duplicate annotation ID {a.Id}.");
            if (a.ImageLayerId is { } lid && !imageIds.Contains(lid)) errors.Add($"Annotation {a.Id} references unknown image.");
            if (!IsFinite(a.Bounds)) errors.Add($"Annotation {a.Id} has non-finite geometry.");
            if (!double.IsFinite(a.StrokeWidth) || a.StrokeWidth < 0 || a.StrokeWidth > 200) errors.Add($"Annotation {a.Id} stroke width invalid.");
            if (!double.IsFinite(a.Rotation) || Math.Abs(a.Rotation) > 3600) errors.Add($"Annotation {a.Id} rotation invalid.");
            if (!double.IsFinite(a.Alpha) || a.Alpha is < 0 or > 1) errors.Add($"Annotation {a.Id} opacity must be 0–1.");
            switch (a)
            {
                case RectangleAnnotation rectangle when !Enum.IsDefined(rectangle.Dash):
                    errors.Add($"Rectangle {a.Id} border pattern invalid."); break;
                case LineAnnotation ln when !IsFinite(ln.Start) || !IsFinite(ln.End) || (ln.Control is { } cp && !IsFinite(cp))
                                         || !double.IsFinite(ln.HeadSize) || ln.HeadSize is < 0.25 or > 8
                                         || !Enum.IsDefined(ln.StartCap) || !Enum.IsDefined(ln.EndCap) || !Enum.IsDefined(ln.Dash):
                    errors.Add($"Line {a.Id} geometry or style invalid."); break;
                case CalloutAnnotation co when !IsFinite(co.Tail) || !double.IsFinite(co.TailWidth) || co.TailWidth is < 2 or > 500 || !Enum.IsDefined(co.Shape):
                    errors.Add($"Callout {a.Id} tail or shape invalid."); break;
                case StepAnnotation sp when (sp.Tail is { } stt && !IsFinite(stt)) || !Enum.IsDefined(sp.Shape) || !Enum.IsDefined(sp.LabelStyle)
                                         || (sp.Prefix?.Length ?? 0) > StepLabels.MaxAffix || (sp.Suffix?.Length ?? 0) > StepLabels.MaxAffix
                                         || (sp.CustomText?.Length ?? 0) > StepLabels.MaxCustom:
                    errors.Add($"Step {a.Id} style invalid."); break;
                case FreehandAnnotation f when f.Points.Length > FreehandAnnotation.MaxPoints:
                    errors.Add($"Freehand {a.Id} has too many points."); break;
                case StampAnnotation s when s.AssetId is { } sid && !assets.ContainsKey(sid):
                    errors.Add($"Stamp {a.Id} references unknown asset."); break;
                case TextAnnotation tx when tx.FontSize is <= 0 or > 1000 || !double.IsFinite(tx.FontSize):
                    errors.Add($"Text {a.Id} font size invalid."); break;
                case StepAnnotation st when st.Number is < 0 or > 9999:
                    errors.Add($"Step {a.Id} number invalid."); break;
            }
        }
        return errors;
    }

    public static bool IsValid(DocumentState doc) => Validate(doc).Count == 0;

    public static void EnsureValid(DocumentState doc)
    {
        var e = Validate(doc);
        if (e.Count > 0) throw new InvalidDocumentException(e);
    }

    private static bool Overflows(PixelRect r) =>
        (long)r.X + r.Width > int.MaxValue || (long)r.Y + r.Height > int.MaxValue ||
        (long)r.X + r.Width < int.MinValue || (long)r.Y + r.Height < int.MinValue;

    private static bool IsFinite(PointD p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    private static bool IsFinite(RectD r) =>
        double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height);

    public static bool IsHash(string s)
    {
        if (s.Length != 64) return false;
        foreach (var ch in s) if (!char.IsAsciiHexDigitLower(ch) && !char.IsAsciiDigit(ch)) return false;
        return true;
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;
}

public sealed class InvalidDocumentException(IReadOnlyList<string> errors)
    : Exception("Invalid document: " + string.Join(" ", errors.Take(5)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
