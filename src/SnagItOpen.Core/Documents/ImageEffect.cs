using System.Text.Json.Serialization;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Documents;

[JsonConverter(typeof(JsonStringEnumConverter<ImageEffectKind>))]
public enum ImageEffectKind { Blur, Pixelate }

/// <summary>
/// Visual obscuring effect in normalized source pixels of one layer. Applied in array order before
/// orientation/scale and before linked annotations. Not a secure redaction.
/// </summary>
public sealed record ImageEffect(Guid Id, ImageEffectKind Kind, PixelRect Region, int Strength)
{
    public const int MinBlur = 1, MaxBlur = 32, MinBlock = 2, MaxBlock = 64;

    public bool IsValidStrength => Kind == ImageEffectKind.Blur
        ? Strength is >= MinBlur and <= MaxBlur
        : Strength is >= MinBlock and <= MaxBlock;
}
