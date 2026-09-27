using System.Text.Json.Serialization;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Documents;

[JsonConverter(typeof(JsonStringEnumConverter<LayoutMode>))]
public enum LayoutMode { Vertical, Horizontal, Free }

[JsonConverter(typeof(JsonStringEnumConverter<CrossAlignment>))]
public enum CrossAlignment { Start, Center, End }

[JsonConverter(typeof(JsonStringEnumConverter<ScaleMode>))]
public enum ScaleMode { Original, MatchCrossAxis }

/// <summary>Settings for automatic vertical/horizontal combining.</summary>
public sealed record LayoutOptions(
    LayoutMode Mode,
    int Gap,
    int Padding,
    CrossAlignment Alignment,
    ScaleMode Scale,
    int? TargetCrossPixels,
    bool AllowUpscale)
{
    public static readonly LayoutOptions Default = new(LayoutMode.Vertical, 0, 0, CrossAlignment.Start, ScaleMode.Original, null, false);
}

/// <summary>A normalized PNG stored by SHA-256 content hash.</summary>
public sealed record ImageAsset(string Id, int Width, int Height, string RelativePngPath)
{
    [JsonIgnore] public PixelSize Size => new(Width, Height);
    [JsonIgnore] public PixelRect FullRect => new(0, 0, Width, Height);

    public static ImageAsset Create(string sha256, int width, int height) =>
        new(sha256, width, height, $"assets/{sha256}.png");
}

/// <summary>Optional per-image border, shadow, rounded corners and torn edges.</summary>
public sealed record EdgeStyle
{
    public int BorderWidth { get; init; }
    public Rgba32 BorderColor { get; init; } = new(60, 60, 60, 255);
    public int ShadowSize { get; init; }
    public int ShadowOffsetX { get; init; } = 4;
    public int ShadowOffsetY { get; init; } = 4;
    public Rgba32 ShadowColor { get; init; } = new(0, 0, 0, 110);
    public int CornerRadius { get; init; }
    public TornSides TornSides { get; init; }
    public int TornDepth { get; init; } = 10;
    public int TornSeed { get; init; } = 1;

    [JsonIgnore] public bool IsEmpty => BorderWidth <= 0 && ShadowSize <= 0 && CornerRadius <= 0 && TornSides == TornSides.None;

    /// <summary>How far the rendering extends beyond the image bounds on each side.</summary>
    public (int Left, int Top, int Right, int Bottom) Expansion()
    {
        int b = Math.Max(0, BorderWidth) / 2 + (BorderWidth > 0 ? 1 : 0);
        if (ShadowSize <= 0) return (b, b, b, b);
        int s = ShadowSize;
        return (Math.Max(b, s - ShadowOffsetX), Math.Max(b, s - ShadowOffsetY),
                Math.Max(b, s + ShadowOffsetX), Math.Max(b, s + ShadowOffsetY));
    }
}

[Flags]
[JsonConverter(typeof(JsonNumberEnumConverter<TornSides>))]
public enum TornSides { None = 0, Top = 1, Right = 2, Bottom = 4, Left = 8, All = 15 }

/// <summary>An image placed on the canvas. Geometry is in document pixels; crop in source pixels.</summary>
public sealed record ImageLayer
{
    public Guid Id { get; init; }
    public string AssetId { get; init; } = "";
    public PixelRect SourceCrop { get; init; }
    public PixelRect Bounds { get; init; }
    public bool Visible { get; init; } = true;
    /// <summary>Clockwise quarter turns 0–3.</summary>
    public int QuarterTurns { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
    public string? Name { get; init; }
    public EdgeStyle? Edge { get; init; }
    /// <summary>Blur/pixelate effects in source pixels, applied in order.</summary>
    public ImageEffect[] Effects { get; init; } = [];

    /// <summary>Crop dimensions after orientation (odd quarter turns swap).</summary>
    [JsonIgnore]
    public PixelSize OrientedSize => (QuarterTurns & 1) == 1
        ? new PixelSize(SourceCrop.Height, SourceCrop.Width)
        : new PixelSize(SourceCrop.Width, SourceCrop.Height);

    [JsonIgnore]
    public bool HasIdentityOrientation => QuarterTurns == 0 && !FlipHorizontal && !FlipVertical;

    public static ImageLayer ForAsset(ImageAsset asset, int x = 0, int y = 0, string? name = null) => new()
    {
        Id = Guid.NewGuid(),
        AssetId = asset.Id,
        SourceCrop = asset.FullRect,
        Bounds = new PixelRect(x, y, asset.Width, asset.Height),
        Name = name,
    };
}

/// <summary>The complete editable state of a composition. Treat arrays as immutable.</summary>
public sealed record DocumentState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Untitled";
    public PixelRect ExportArea { get; init; } = new(0, 0, 1, 1);
    /// <summary>When true the export area follows content automatically.</summary>
    public bool AutoCanvas { get; init; } = true;
    public Rgba32 Background { get; init; } = Rgba32.Transparent;
    public ImageAsset[] Assets { get; init; } = [];
    /// <summary>Bottom-to-top drawing order.</summary>
    public ImageLayer[] Images { get; init; } = [];
    /// <summary>Order for automatic layout; contains every image ID once.</summary>
    public Guid[] LayoutOrder { get; init; } = [];
    public LayoutOptions Layout { get; init; } = LayoutOptions.Default;
    public Annotations.Annotation[] Annotations { get; init; } = [];

    public static DocumentState CreateEmpty(string name = "Untitled") => new() { Name = name };

    [JsonIgnore] public bool IsEmpty => Images.Length == 0 && Annotations.Length == 0;
    [JsonIgnore] public bool HasVisibleContent => Images.Any(i => i.Visible) || Annotations.Length > 0;

    public ImageLayer? FindImage(Guid id)
    {
        foreach (var i in Images) if (i.Id == id) return i;
        return null;
    }

    public ImageAsset? FindAsset(string id)
    {
        foreach (var a in Assets) if (a.Id == id) return a;
        return null;
    }

    public Annotations.Annotation? FindAnnotation(Guid id)
    {
        foreach (var a in Annotations) if (a.Id == id) return a;
        return null;
    }

    /// <summary>Images in layout order.</summary>
    public IReadOnlyList<ImageLayer> ImagesInLayoutOrder()
    {
        var map = Images.ToDictionary(i => i.Id);
        var list = new List<ImageLayer>(LayoutOrder.Length);
        foreach (var id in LayoutOrder) if (map.TryGetValue(id, out var l)) list.Add(l);
        return list;
    }
}

/// <summary>Resource guardrails from the specification.</summary>
public static class Limits
{
    public const long MaxAssetPixels = 40_000_000;
    public const int MaxDimension = 32_767;
    public const long MaxExportPixels = 40_000_000;
    public const int MaxAssets = 100;
    public const int MaxUndo = 100;
    public const int MaxGap = 512;
    public const int MaxPadding = 2048;
    public const long DecodedCacheBytes = 256L * 1024 * 1024;
    public const long WorkingSetBytes = 1024L * 1024 * 1024;
    public const long ExportReserveBytes = 128L * 1024 * 1024;

    public static bool IsAcceptableImageSize(long width, long height) =>
        width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension && width * height <= MaxAssetPixels;

    public static bool IsAcceptableExportSize(long width, long height) =>
        width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension && width * height <= MaxExportPixels;
}
