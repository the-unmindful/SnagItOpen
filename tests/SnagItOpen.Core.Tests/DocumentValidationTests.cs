using System.Text.Json;
using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

public class DocumentValidationTests
{
    private static DocumentState WithCrop(PixelRect crop)
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 100, 10));
        return d with { Images = [d.Images[0] with { SourceCrop = crop }] };
    }

    [Fact] public void Empty_document_is_valid_1x1() { var d = DocumentState.CreateEmpty(); Assert.True(DocumentValidator.IsValid(d)); Assert.Equal(new PixelRect(0, 0, 1, 1), d.ExportArea); }
    [Fact] public void Negative_crop_origin_fails() => Assert.False(DocumentValidator.IsValid(WithCrop(new PixelRect(-1, 0, 10, 10))));
    [Fact] public void Crop_past_right_edge_fails() => Assert.False(DocumentValidator.IsValid(WithCrop(new PixelRect(90, 0, 11, 10))));
    [Fact] public void Crop_inside_is_valid() => Assert.True(DocumentValidator.IsValid(WithCrop(new PixelRect(90, 0, 10, 10))));

    [Fact]
    public void Duplicate_layer_ids_fail()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10));
        Assert.False(DocumentValidator.IsValid(d with { Images = [d.Images[0], d.Images[0]], LayoutOrder = [d.Images[0].Id] }));
    }

    [Fact]
    public void Unknown_asset_fails()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10));
        Assert.False(DocumentValidator.IsValid(d with { Assets = [] }));
    }

    [Fact]
    public void Negative_destination_is_valid() => Assert.True(DocumentValidator.IsValid(TestData.FreeDoc(new PixelRect(-50, -20, 10, 10))));

    [Fact]
    public void Coordinate_overflow_fails() => Assert.False(DocumentValidator.IsValid(TestData.FreeDoc(new PixelRect(int.MaxValue - 5, 0, 10, 10))));

    [Fact]
    public void Layout_order_must_contain_every_image()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10), new PixelRect(20, 0, 10, 10));
        Assert.False(DocumentValidator.IsValid(d with { LayoutOrder = [d.Images[0].Id] }));
    }

    [Fact]
    public void Bad_orientation_fails()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10));
        Assert.False(DocumentValidator.IsValid(d with { Images = [d.Images[0] with { QuarterTurns = 4 }] }));
    }

    [Fact]
    public void Touching_half_open_rects_do_not_intersect()
    {
        var a = new PixelRect(0, 0, 10, 10);
        var b = new PixelRect(10, 0, 10, 10);
        Assert.True(a.Intersect(b).IsEmpty);
        Assert.Equal(new PixelRect(0, 0, 20, 10), a.Union(b));
        Assert.Equal(new PixelRect(5, 3, 10, 10), a.Translate(5, 3));
    }

    [Fact]
    public void Annotation_referencing_unknown_layer_fails()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10));
        var bad = d with { Annotations = [new RectangleAnnotation { ImageLayerId = Guid.NewGuid(), Bounds = new RectD(0, 0, 5, 5) }] };
        Assert.False(DocumentValidator.IsValid(bad));
    }

    [Fact]
    public void Json_roundtrip_preserves_polymorphic_annotations()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10));
        d = d with
        {
            Annotations =
            [
                new ArrowAnnotation { Start = new PointD(1, 2), End = new PointD(30, 40) },
                new CalloutAnnotation { Text = "Hi", Tail = new PointD(5, 5), Bounds = new RectD(10, 10, 50, 20) },
                new FreehandAnnotation { Points = [new PointD(0, 0), new PointD(3, 4)] },
                new RedactionAnnotation { Bounds = new RectD(1, 1, 3, 3) },
            ],
        };
        var json = JsonSerializer.Serialize(d);
        var back = JsonSerializer.Deserialize<DocumentState>(json)!;
        Assert.Equal(4, back.Annotations.Length);
        Assert.IsType<ArrowAnnotation>(back.Annotations[0]);
        Assert.Equal("Hi", ((CalloutAnnotation)back.Annotations[1]).Text);
        Assert.Equal(2, ((FreehandAnnotation)back.Annotations[2]).Points.Length);
        Assert.Equal(Rgba32.Black, back.Annotations[3].Color);
        Assert.True(DocumentValidator.IsValid(back));
    }

    [Fact]
    public void Unknown_annotation_discriminator_throws()
    {
        const string json = """{"type":"hologram","id":"00000000-0000-0000-0000-000000000001"}""";
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Annotation>(json));
    }
}
