using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

public class HistoryTests
{
    private static DocumentState Named(DocumentState d, string n) => d with { Name = n };

    [Fact]
    public void Undo_twice_redo_twice()
    {
        var a = DocumentState.CreateEmpty("A");
        var h = new History(a);
        h.Execute("B", Named(a, "B"));
        h.Execute("C", Named(h.Current, "C"));
        Assert.True(h.Undo()); Assert.True(h.Undo());
        Assert.Equal("A", h.Current.Name);
        Assert.True(h.Redo()); Assert.True(h.Redo());
        Assert.Equal("C", h.Current.Name);
        Assert.False(h.Redo());
    }

    [Fact]
    public void Edit_after_undo_clears_redo()
    {
        var h = new History(DocumentState.CreateEmpty("A"));
        h.Execute("B", Named(h.Current, "B"));
        h.Undo();
        h.Execute("D", Named(h.Current, "D"));
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Invalid_state_is_rejected_without_change()
    {
        var h = new History(DocumentState.CreateEmpty("A"));
        Assert.Throws<InvalidDocumentException>(() => h.Execute("bad", h.Current with { ExportArea = default }));
        Assert.False(h.CanUndo);
        Assert.Equal("A", h.Current.Name);
    }

    [Fact]
    public void Cap_evicts_oldest()
    {
        var h = new History(DocumentState.CreateEmpty("0"), capacity: 3);
        for (int i = 1; i <= 5; i++) h.Execute(i.ToString(), Named(h.Current, i.ToString()));
        Assert.Equal(3, h.UndoCount);
        while (h.Undo()) { }
        Assert.Equal("2", h.Current.Name);
    }

    [Fact]
    public void Gesture_commits_once_and_cancel_restores()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10));
        var id = d.Images[0].Id;
        var h = new History(d);
        var g = new Gesture(h);
        for (int i = 1; i <= 20; i++) g.Update(b => DocumentOps.Move(b, [id], [], i, 0));
        Assert.True(g.Commit("Move"));
        Assert.Equal(1, h.UndoCount);
        Assert.Equal(20, h.Current.Images[0].Bounds.X);

        var g2 = new Gesture(h);
        g2.Update(b => DocumentOps.Move(b, [id], [], 5, 5));
        g2.Cancel();
        Assert.False(g2.Commit("x"));
        Assert.Equal(1, h.UndoCount);
        Assert.Equal(20, h.Current.Images[0].Bounds.X);
    }

    [Fact]
    public void Older_state_arrays_are_unchanged()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10));
        var h = new History(d);
        var snapshot = d.Images;
        h.Execute("m", DocumentOps.Move(d, [d.Images[0].Id], [], 3, 4));
        Assert.Same(snapshot, d.Images);
        Assert.Equal(0, snapshot[0].Bounds.X);
    }
}

public class DocumentOpsTests
{
    [Fact]
    public void Move_two_images_by_negative_offset_undo_once()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10), new PixelRect(20, 5, 10, 10));
        var h = new History(d);
        h.Execute("Move", DocumentOps.Move(d, d.Images.Select(i => i.Id).ToArray(), [], -15, 8));
        Assert.Equal(new PixelRect(-15, 8, 10, 10), h.Current.Images[0].Bounds);
        Assert.Equal(new PixelRect(5, 13, 10, 10), h.Current.Images[1].Bounds);
        h.Undo();
        Assert.Equal(new PixelRect(0, 0, 10, 10), h.Current.Images[0].Bounds);
        Assert.Equal(new PixelRect(20, 5, 10, 10), h.Current.Images[1].Bounds);
    }

    [Fact]
    public void Switching_to_free_preserves_geometry()
    {
        var d = TestData.Doc(TestData.Vertical(), (100, 50), (80, 30));
        var before = d.Images.Select(i => i.Bounds).ToArray();
        var f = DocumentOps.SetMode(d, LayoutMode.Free);
        Assert.Equal(before, f.Images.Select(i => i.Bounds).ToArray());
        Assert.Equal(new PixelRect(0, 0, 110, 100), d.ExportArea);
    }

    [Fact]
    public void Switching_axes_twice_restores_positions()
    {
        var d = TestData.Doc(TestData.Vertical(), (100, 50), (80, 30));
        var back = DocumentOps.SetMode(DocumentOps.SetMode(d, LayoutMode.Horizontal), LayoutMode.Vertical);
        Assert.Equal(d.Images.Select(i => i.Bounds), back.Images.Select(i => i.Bounds));
        Assert.Equal(d.Assets.Length, back.Assets.Length);
    }

    [Fact]
    public void Crop_in_free_mode_matches_oracle()
    {
        var d = TestData.FreeDoc(new PixelRect(10, 20, 200, 100));
        // asset is 200x100 in FreeDoc; build 100x50 asset scaled 2x instead.
        var a = TestData.Asset(100, 50);
        var l = TestData.Layer(a) with { Bounds = new PixelRect(10, 20, 200, 100) };
        d = d with { Assets = [a], Images = [l], LayoutOrder = [l.Id] };
        var c = DocumentOps.Crop(d, l.Id, new PixelRect(10, 5, 80, 40));
        Assert.Equal(new PixelRect(30, 30, 160, 80), c.Images[0].Bounds);
        Assert.Equal(new PixelRect(10, 5, 80, 40), c.Images[0].SourceCrop);
        var reset = DocumentOps.ResetCrop(c, l.Id);
        Assert.Equal(a.FullRect, reset.Images[0].SourceCrop);
    }

    [Fact]
    public void Crop_in_layout_mode_reflows()
    {
        var d = TestData.Doc(TestData.Vertical(), (100, 50), (80, 30));
        var c = DocumentOps.Crop(d, d.Images[0].Id, new PixelRect(0, 0, 100, 20));
        Assert.Equal(new PixelRect(15, 35, 80, 30), c.Images[1].Bounds);
        Assert.Equal(70, c.ExportArea.Height);
    }

    [Fact]
    public void Fit_canvas_with_negative_content_matches_oracle()
    {
        var d = TestData.FreeDoc(new PixelRect(-20, -10, 100, 50), new PixelRect(100, 20, 80, 30));
        var f = DocumentOps.FitCanvas(d, 5);
        Assert.Equal(new PixelRect(-25, -15, 210, 70), f.ExportArea);
        Assert.Equal(d.Images.Select(i => i.Bounds), f.Images.Select(i => i.Bounds));
    }

    [Fact]
    public void Canvas_crop_changes_only_export_area()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 100, 100));
        var c = DocumentOps.SetExportArea(d, new PixelRect(10, 10, 20, 20));
        Assert.Equal(d.Images, c.Images);
        Assert.False(c.AutoCanvas);
    }

    [Fact]
    public void Duplicate_references_same_asset_with_offset()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10));
        var h = new History(d);
        var (dup, ids) = DocumentOps.Duplicate(d, [d.Images[0].Id]);
        h.Execute("dup", dup);
        Assert.Single(h.Current.Assets);
        Assert.Equal(2, h.Current.Images.Length);
        Assert.Equal(new PixelRect(16, 16, 10, 10), h.Current.FindImage(ids[0])!.Bounds);
        h.Undo();
        Assert.Single(h.Current.Assets);
    }

    [Fact]
    public void Removing_an_image_keeps_annotations_and_prunes_assets()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10), new PixelRect(20, 0, 10, 10));
        d = DocumentOps.AddAnnotation(d, new RectangleAnnotation { Bounds = new RectD(1, 1, 3, 3) });
        var r = DocumentOps.RemoveImages(d, [d.Images[0].Id]);
        Assert.Single(r.Images);
        Assert.Single(r.Annotations);
        Assert.Equal(new RectD(1, 1, 3, 3), r.Annotations[0].Bounds);
        Assert.Single(r.Assets);
        Assert.True(DocumentValidator.IsValid(r));
    }
}

/// <summary>Annotations are composition objects: images never clip, move, crop or delete them.</summary>
public class AnnotationCanvasTests
{
    [Fact]
    public void Legacy_linked_annotation_migrates_to_document_position()
    {
        // Image drawn at (100,50) scaled 2x; crop starts at source (10,0).
        var d = TestData.FreeDoc(new PixelRect(100, 50, 80, 40));
        var l = d.Images[0] with { SourceCrop = new PixelRect(10, 0, 40, 20) };
        d = d with { Images = [l] };
        var arrow = new ArrowAnnotation { ImageLayerId = l.Id, Start = new(10, 0), End = new(20, 5), StrokeWidth = 3 };
        var m = AnnotationCanvas.Normalize(d with { Annotations = [arrow] });
        var a = Assert.IsType<ArrowAnnotation>(m.Annotations[0]);
        Assert.Null(a.ImageLayerId);
        Assert.Equal(new PointD(100, 50), a.Start);
        Assert.Equal(new PointD(120, 60), a.End);
        Assert.Equal(6, a.StrokeWidth, 6);
    }

    [Fact]
    public void Legacy_link_to_missing_image_keeps_geometry()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10));
        var r = new RectangleAnnotation { ImageLayerId = Guid.NewGuid(), Bounds = new RectD(3, 4, 5, 6) };
        var m = AnnotationCanvas.Normalize(d with { Annotations = [r] });
        Assert.Null(m.Annotations[0].ImageLayerId);
        Assert.Equal(new RectD(3, 4, 5, 6), m.Annotations[0].Bounds);
        Assert.True(DocumentValidator.IsValid(m));
    }

    [Fact]
    public void Annotation_beyond_images_grows_auto_canvas()
    {
        var d = DocumentOps.Reflow(TestData.FreeDoc(new PixelRect(0, 0, 100, 50)) with { AutoCanvas = true });
        d = DocumentOps.AddAnnotation(d, new RectangleAnnotation { Bounds = new RectD(150, 80, 20, 10), StrokeWidth = 0 });
        Assert.True(d.ExportArea.Right >= 170 && d.ExportArea.Bottom >= 90, d.ExportArea.ToString());
    }

    [Fact]
    public void Crop_move_and_delete_of_image_leave_annotations_untouched()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 100, 50));
        var id = d.Images[0].Id;
        var ann = new ArrowAnnotation { Start = new(10, 10), End = new(90, 40) };
        d = DocumentOps.AddAnnotation(d, ann);
        d = DocumentOps.Crop(d, id, new PixelRect(0, 0, 20, 20));
        d = DocumentOps.Move(d, [id], [], 300, 0);
        Assert.Equal(ann.Start, ((ArrowAnnotation)d.Annotations[0]).Start);
        Assert.Equal(ann.End, ((ArrowAnnotation)d.Annotations[0]).End);
        d = DocumentOps.RemoveImages(d, [id]);
        Assert.Single(d.Annotations);
    }

    [Fact]
    public void Moving_annotation_does_not_move_images()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 100, 50));
        d = DocumentOps.AddAnnotation(d, new RectangleAnnotation { Bounds = new RectD(10, 10, 5, 5) });
        var m = DocumentOps.Move(d, [], [d.Annotations[0].Id], 200, 0);
        Assert.Equal(new RectD(210, 10, 5, 5), m.Annotations[0].Bounds);
        Assert.Equal(d.Images[0].Bounds, m.Images[0].Bounds);
    }

    [Fact]
    public void Duplicating_an_image_does_not_duplicate_annotations()
    {
        var d = TestData.FreeDoc(new PixelRect(0, 0, 10, 10));
        d = DocumentOps.AddAnnotation(d, new RectangleAnnotation { Bounds = new RectD(1, 1, 3, 3) });
        var (dup, _) = DocumentOps.Duplicate(d, [d.Images[0].Id]);
        Assert.Single(dup.Annotations);
    }

    [Fact]
    public void Z_order_is_independent_of_layout_order()
    {
        var d = TestData.Doc(TestData.Vertical(), (10, 10), (10, 10), (10, 10));
        var first = d.Images[0].Id;
        var z = DocumentOps.ChangeZOrder(d, [first], DocumentOps.ZMove.ToFront);
        Assert.Equal(first, z.Images[^1].Id);
        Assert.Equal(d.LayoutOrder, z.LayoutOrder);
        Assert.Equal(d.Images[0].Bounds, z.FindImage(first)!.Bounds);
    }

    [Fact]
    public void Move_in_layout_reorders()
    {
        var d = TestData.Doc(TestData.Vertical(0, 0), (10, 10), (10, 20));
        var m = DocumentOps.MoveInLayout(d, d.LayoutOrder[1], 0);
        Assert.Equal(0, m.FindImage(d.LayoutOrder[1])!.Bounds.Y);
        Assert.Equal(20, m.FindImage(d.LayoutOrder[0])!.Bounds.Y);
    }

    [Fact]
    public void Four_rotations_restore_identity_and_flip_twice_restores()
    {
        var d = TestData.FreeDoc(new PixelRect(3, 7, 100, 50));
        var id = d.Images[0].Id;
        var r = d;
        for (int i = 0; i < 4; i++) r = DocumentOps.Rotate(r, [id], 1);
        Assert.Equal(d.Images[0], r.Images[0]);
        var f = DocumentOps.Flip(DocumentOps.Flip(d, [id], true), [id], true);
        Assert.Equal(d.Images[0], f.Images[0]);
    }

    [Fact]
    public void Scale_document_200_percent_doubles_geometry()
    {
        var d = DocumentOps.FitCanvas(TestData.FreeDoc(new PixelRect(5, 10, 20, 30)), 0);
        var s = DocumentOps.ScaleDocument(d, 2);
        Assert.Equal(new PixelRect(10, 20, 40, 60), s.Images[0].Bounds);
        Assert.Equal(new PixelRect(10, 20, 40, 60), s.ExportArea);
    }

    [Fact]
    public void Renumber_steps_in_order()
    {
        var d = DocumentState.CreateEmpty();
        d = DocumentOps.AddAnnotation(d, new StepAnnotation { Number = 5, Bounds = new RectD(0, 0, 20, 20) });
        d = DocumentOps.AddAnnotation(d, new StepAnnotation { Number = 9, Bounds = new RectD(30, 0, 20, 20) });
        Assert.Equal(10, DocumentOps.NextStepNumber(d));
        var r = DocumentOps.RenumberSteps(d);
        Assert.Equal([1, 2], r.Annotations.OfType<StepAnnotation>().Select(s => s.Number));
    }
}
