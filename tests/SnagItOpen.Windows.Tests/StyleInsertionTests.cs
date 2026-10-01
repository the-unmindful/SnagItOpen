using SnagItOpen.App.Editor;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;
using SnagItOpen.Storage.Settings;

namespace SnagItOpen.Windows.Tests;

/// <summary>Plan F-STY1: every saved style can be inserted as a sensible, valid annotation around the drop point.</summary>
public class StyleInsertionTests
{
    [Fact]
    public void High_dpi_thumbnails_keep_the_sample_centred_in_the_tile()
    {
        ThemeTokenTests.RunSta<bool>(() =>
        {
            var bitmap = new StyleThumbnailRenderer().Render(BuiltInStyles.For("Arrow")[0].Style, SnagItOpen.App.Infrastructure.EffectiveTheme.Light, 2);
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight; var px = new byte[w * h * 4]; bitmap.CopyPixels(px, w * 4, 0);
            var red = Enumerable.Range(0, w * h).Where(i => px[i * 4 + 2] > 160 && px[i * 4 + 1] < 100).Select(i => (X: i % w, Y: i / w)).ToList();
            Assert.NotEmpty(red);
            double cx = red.Average(p => p.X) / w, cy = red.Average(p => p.Y) / h;
            Assert.InRange(cx, 0.3, 0.7); Assert.InRange(cy, 0.3, 0.7); // was drawn off-tile when DPI scaling was applied twice
            return true;
        });
    }

    [Fact]
    public void Every_built_in_style_inserts_near_the_point_and_keeps_its_look()
    {
        ThemeTokenTests.RunSta<bool>(() =>
        {
            var doc = DocumentState.CreateEmpty();
            var c = new PointD(400, 300);
            foreach (string kind in BuiltInStyles.Kinds)
                foreach (var entry in BuiltInStyles.For(kind))
                {
                    var a = StyleInsertion.Create(entry.Style, c, doc);
                    var e = a.Extent();
                    Assert.True(e.Width > 4 && e.Height > 4, $"{kind}/{entry.Name} has no size");
                    Assert.True(PointD.Distance(e.Center, c) < 120, $"{kind}/{entry.Name} placed far from the drop point");
                    Assert.Equal(entry.Style.Color, a.Color);
                    Assert.NotEqual(Guid.Empty, a.Id);
                    doc = DocumentOps.AddAnnotation(doc, a);
                }
            Assert.Equal(BuiltInStyles.Kinds.Sum(k => BuiltInStyles.For(k).Count()), doc.Annotations.Count());
            return true;
        });
    }
}
