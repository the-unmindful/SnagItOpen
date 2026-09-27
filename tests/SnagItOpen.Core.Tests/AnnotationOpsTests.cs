using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

public class AnnotationOpsTests
{
    private static DocumentState WithAnns(params Annotation[] anns) =>
        DocumentState.CreateEmpty() with { Annotations = anns };

    private static StepAnnotation Step(int n, double x = 0) => new() { Number = n, Bounds = new RectD(x, 0, 20, 20) };

    [Fact]
    public void Renumber_from_keeps_earlier_steps_and_counts_up()
    {
        var a = Step(1); var b = Step(7, 30); var c = Step(2, 60); var d = Step(9, 90);
        var doc = DocumentOps.RenumberStepsFrom(WithAnns(a, b, c, d), b.Id);
        var nums = doc.Annotations.Cast<StepAnnotation>().Select(s => s.Number).ToArray();
        Assert.Equal([1, 7, 8, 9], nums);
        var doc2 = DocumentOps.RenumberStepsFrom(WithAnns(a, b, c, d), b.Id, start: 2);
        Assert.Equal([1, 2, 3, 4], doc2.Annotations.Cast<StepAnnotation>().Select(s => s.Number).ToArray());
    }

    [Fact]
    public void Adjust_step_numbers_clamps_and_affects_only_selected()
    {
        var a = Step(1); var b = Step(5, 30);
        var doc = DocumentOps.AdjustStepNumbers(WithAnns(a, b), [a.Id], -3);
        Assert.Equal(0, ((StepAnnotation)doc.Annotations[0]).Number);
        Assert.Equal(5, ((StepAnnotation)doc.Annotations[1]).Number);
    }

    [Theory]
    [InlineData(StepLabelStyle.Numbers, 12, "12")]
    [InlineData(StepLabelStyle.UpperLetters, 1, "A")]
    [InlineData(StepLabelStyle.UpperLetters, 28, "AB")]
    [InlineData(StepLabelStyle.LowerLetters, 3, "c")]
    [InlineData(StepLabelStyle.Roman, 14, "XIV")]
    public void Step_labels_format(StepLabelStyle style, int n, string expected) =>
        Assert.Equal(expected, StepLabels.Format(style, n, null, "", ""));

    [Fact]
    public void Step_label_prefix_suffix_and_custom()
    {
        Assert.Equal("(3).", StepLabels.Format(StepLabelStyle.Numbers, 3, null, "(", ")."));
        Assert.Equal("Go", StepLabels.Format(StepLabelStyle.Custom, 3, "Go", "", ""));
    }

    [Fact]
    public void Align_left_moves_unlocked_items_only()
    {
        var a = new RectangleAnnotation { Bounds = new RectD(10, 0, 10, 10), StrokeWidth = 0 };
        var b = new RectangleAnnotation { Bounds = new RectD(50, 30, 10, 10), StrokeWidth = 0 };
        var locked = new RectangleAnnotation { Bounds = new RectD(80, 60, 10, 10), StrokeWidth = 0, Locked = true };
        var doc = DocumentOps.AlignAnnotations(WithAnns(a, b, locked), [a.Id, b.Id, locked.Id], AlignMode.Left);
        Assert.Equal(a.Bounds.X, doc.FindAnnotation(b.Id)!.Bounds.X, 6);
        Assert.Equal(80, doc.FindAnnotation(locked.Id)!.Bounds.X, 6);
    }

    [Fact]
    public void Distribute_equalizes_gaps()
    {
        var a = new RectangleAnnotation { Bounds = new RectD(0, 0, 10, 10), StrokeWidth = 0 };
        var b = new RectangleAnnotation { Bounds = new RectD(15, 0, 10, 10), StrokeWidth = 0 };
        var c = new RectangleAnnotation { Bounds = new RectD(90, 0, 10, 10), StrokeWidth = 0 };
        var doc = DocumentOps.DistributeAnnotations(WithAnns(a, b, c), [a.Id, b.Id, c.Id], horizontal: true);
        double ax = doc.FindAnnotation(a.Id)!.Bounds.X, bx = doc.FindAnnotation(b.Id)!.Bounds.X, cx = doc.FindAnnotation(c.Id)!.Bounds.X;
        Assert.Equal(bx - ax, cx - bx, 6);
        Assert.Equal(0, ax, 6);
        Assert.Equal(90, cx, 6);
    }

    [Fact]
    public void Paste_creates_new_unlocked_copies_with_offset()
    {
        var a = new ArrowAnnotation { Start = new(0, 0), End = new(10, 0), Locked = true };
        var (doc, ids) = DocumentOps.PasteAnnotations(WithAnns(a), [a], 16, 16);
        Assert.Equal(2, doc.Annotations.Length);
        var copy = (ArrowAnnotation)doc.FindAnnotation(ids[0])!;
        Assert.NotEqual(a.Id, copy.Id);
        Assert.False(copy.Locked);
        Assert.Equal(new PointD(16, 16), copy.Start);
    }

    [Fact]
    public void Paste_drops_missing_stamp_asset_reference()
    {
        var s = new StampAnnotation { AssetId = new string('a', 64), Bounds = new RectD(0, 0, 10, 10) };
        var (doc, ids) = DocumentOps.PasteAnnotations(DocumentState.CreateEmpty(), [s], 0, 0);
        Assert.Null(((StampAnnotation)doc.FindAnnotation(ids[0])!).AssetId);
        Assert.True(DocumentValidator.IsValid(doc));
    }
}
