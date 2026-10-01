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
