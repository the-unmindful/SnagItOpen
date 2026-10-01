using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagItOpen.App.Capture;

namespace SnagItOpen.Windows.Tests;

public sealed class CapturePixelSamplerTests
{
    [Fact]
    public void Frozen_physical_pixel_formats_as_hex_and_rgb()
    {
        ThemeTokenTests.RunSta(() =>
        {
            var bitmap = BitmapSource.Create(2, 1, 144, 144, PixelFormats.Bgra32, null,
                new byte[] { 0x33, 0x22, 0x11, 255, 0xA6, 0x87, 0x65, 255 }, 8);
            bitmap.Freeze();
            Assert.Equal("#6587A6", CapturePixelSampler.ColorText(bitmap, 1, 0));
            Assert.Equal("rgb(101, 135, 166)", CapturePixelSampler.ColorText(bitmap, 1, 0, rgb: true));
            Assert.Equal("#112233", CapturePixelSampler.ColorText(bitmap, -1, 20));
            return true;
        });
    }
}
