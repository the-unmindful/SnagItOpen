using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Imaging;

/// <summary>
/// Straight-alpha BGRA32 pixels packed as uint (A&lt;&lt;24 | R&lt;&lt;16 | G&lt;&lt;8 | B), row-major.
/// Deterministic CPU pixel operations live on this type.
/// </summary>
public sealed class PixelBuffer
{
    public PixelBuffer(int width, int height) : this(width, height, new uint[checked(width * height)]) { }

    public PixelBuffer(int width, int height, uint[] data)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be positive.");
        if (data is null || data.LongLength != (long)width * height) throw new ArgumentException("Pixel data length does not match dimensions.", nameof(data));
        Width = width; Height = height; Data = data;
    }

    public int Width { get; }
    public int Height { get; }
    public uint[] Data { get; }
    public int Stride => Width * 4;
    public PixelRect FullRect => new(0, 0, Width, Height);

    public uint this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }

    public static uint Pack(byte r, byte g, byte b, byte a) => (uint)(a << 24 | r << 16 | g << 8 | b);
    public static uint Pack(Rgba32 c) => Pack(c.R, c.G, c.B, c.A);
    public static Rgba32 Unpack(uint p) => new((byte)(p >> 16), (byte)(p >> 8), (byte)p, (byte)(p >> 24));

    public PixelBuffer Clone() => new(Width, Height, (uint[])Data.Clone());

    public static PixelBuffer Solid(int w, int h, Rgba32 c)
    {
        var b = new PixelBuffer(w, h);
        Array.Fill(b.Data, Pack(c));
        return b;
    }

    /// <summary>Copies pixels from any bitmap (converted to straight BGRA32).</summary>
    public static PixelBuffer FromBitmap(BitmapSource src)
    {
        ArgumentNullException.ThrowIfNull(src);
        BitmapSource s = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = s.PixelWidth, h = s.PixelHeight;
        var data = new uint[checked(w * h)];
        s.CopyPixels(data, w * 4, 0);
        return new PixelBuffer(w, h, data);
    }

    /// <summary>Frozen 96-DPI BGRA32 bitmap.</summary>
    public BitmapSource ToBitmap()
    {
        var b = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Data, Stride);
        b.Freeze();
        return b;
    }

    public byte[] EncodePng()
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(ToBitmap()));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static PixelBuffer DecodeImage(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        return FromBitmap(dec.Frames[0]);
    }

    public PixelBuffer Crop(PixelRect r)
    {
        var c = r.Intersect(FullRect);
        if (c.IsEmpty) throw new ArgumentOutOfRangeException(nameof(r), "Crop is outside the image.");
        var o = new PixelBuffer(c.Width, c.Height);
        for (int y = 0; y < c.Height; y++)
            Array.Copy(Data, (c.Y + y) * Width + c.X, o.Data, y * c.Width, c.Width);
        return o;
    }

    /// <summary>Draws <paramref name="src"/> at (x, y), replacing pixels (no blending), clipped to this buffer.</summary>
    public void Blit(PixelBuffer src, int x, int y)
    {
        var dst = new PixelRect(x, y, src.Width, src.Height).Intersect(FullRect);
        if (dst.IsEmpty) return;
        for (int row = 0; row < dst.Height; row++)
            Array.Copy(src.Data, (dst.Y - y + row) * src.Width + (dst.X - x), Data, (dst.Y + row) * Width + dst.X, dst.Width);
    }

    /// <summary>
    /// Applies an EXIF orientation (1–8) producing upright pixels.
    /// 2 mirror, 3 rotate 180, 4 flip, 5 transpose, 6 rotate 90 CW, 7 transverse, 8 rotate 270 CW.
    /// </summary>
    public PixelBuffer Orient(int exifOrientation)
    {
        if (exifOrientation is < 2 or > 8) return this;
        int w = Width, h = Height;
        bool swap = exifOrientation >= 5;
        var o = swap ? new PixelBuffer(h, w) : new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (nx, ny) = exifOrientation switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (h - 1 - y, x),
                    7 => (h - 1 - y, w - 1 - x),
                    _ => (y, w - 1 - x),
                };
                o.Data[ny * o.Width + nx] = Data[y * w + x];
            }
        return o;
    }

    /// <summary>Rotates clockwise by quarter turns.</summary>
    public PixelBuffer RotateQuarterTurns(int q) => (((q % 4) + 4) % 4) switch
    {
        1 => Orient(6),
        2 => Orient(3),
        3 => Orient(8),
        _ => this,
    };

    /// <summary>Encodes into a JPEG after flattening alpha onto <paramref name="background"/>.</summary>
    public byte[] EncodeJpeg(int quality, Rgba32 background)
    {
        var flat = FlattenOnto(background);
        var bmp = new FormatConvertedBitmap(flat.ToBitmap(), PixelFormats.Bgr24, null, 0);
        var enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Composites over an opaque color; result is fully opaque.</summary>
    public PixelBuffer FlattenOnto(Rgba32 bg)
    {
        var o = new PixelBuffer(Width, Height);
        for (int i = 0; i < Data.Length; i++)
        {
            var p = Data[i];
            int a = (int)(p >> 24);
            if (a == 255) { o.Data[i] = p; continue; }
            int r = (int)((p >> 16) & 0xFF), g = (int)((p >> 8) & 0xFF), b = (int)(p & 0xFF);
            byte R = (byte)((r * a + bg.R * (255 - a) + 127) / 255);
            byte G = (byte)((g * a + bg.G * (255 - a) + 127) / 255);
            byte B = (byte)((b * a + bg.B * (255 - a) + 127) / 255);
            o.Data[i] = Pack(R, G, B, 255);
        }
        return o;
    }
}
