using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

public class RegionSelectionTests
{
    private static readonly PixelRect Desktop = new(-1920, 0, 3840, 1080);

    [Fact]
    public void Reverse_drag_normalizes_and_includes_both_endpoints()
    {
        var s = new RegionSelection(Desktop);
        s.Down(new PixelPoint(100, 80));
        s.Move(new PixelPoint(40, 20));
        s.Up(new PixelPoint(40, 20));
        Assert.Equal(SelectionPhase.Done, s.Phase);
        Assert.Equal(new PixelRect(40, 20, 61, 61), Assert.Single(s.Result));
    }

    [Fact]
    public void Click_without_move_selects_one_pixel()
    {
        var s = new RegionSelection(Desktop);
        s.Down(new PixelPoint(5, 5));
        s.Up(new PixelPoint(5, 5));
        Assert.Equal(new PixelRect(5, 5, 1, 1), Assert.Single(s.Result));
    }

    [Fact]
    public void Click_on_window_selects_window_bounds()
    {
        var win = new PixelRect(10, 10, 200, 100);
        var s = new RegionSelection(Desktop, windows: [win]);
        s.Move(new PixelPoint(50, 50));
        Assert.Equal(win, s.Hover);
        s.Down(new PixelPoint(50, 50));
        s.Up(new PixelPoint(50, 50));
        Assert.Equal(win, Assert.Single(s.Result));
    }

    [Fact]
    public void Drag_across_negative_origin_monitors()
    {
        var s = new RegionSelection(Desktop);
        s.Down(new PixelPoint(-10, 0));
        s.Up(new PixelPoint(9, 9));
        Assert.Equal(new PixelRect(-10, 0, 20, 10), Assert.Single(s.Result));
    }

    [Fact]
    public void Escape_cancels_with_no_result_at_any_state()
    {
        var idle = new RegionSelection(Desktop);
        idle.Key(SelectionKey.Escape);
        Assert.Equal(SelectionPhase.Canceled, idle.Phase);
        Assert.Empty(idle.Result);

        var dragging = new RegionSelection(Desktop);
        dragging.Down(new PixelPoint(0, 0));
        dragging.Move(new PixelPoint(50, 50));
        dragging.Key(SelectionKey.Escape);
        Assert.Empty(dragging.Result);

        var multi = new RegionSelection(Desktop, multiRegion: true);
        multi.Down(new PixelPoint(0, 0)); multi.Up(new PixelPoint(9, 9));
        multi.Key(SelectionKey.Escape);
        Assert.Empty(multi.Result);
        Assert.Empty(multi.Committed);
    }

    [Fact]
    public void Topology_cancel_ignores_later_input()
    {
        var s = new RegionSelection(Desktop);
        s.Down(new PixelPoint(0, 0));
        s.Cancel();
        s.Up(new PixelPoint(20, 20));
        Assert.Equal(SelectionPhase.Canceled, s.Phase);
        Assert.Empty(s.Result);
    }

    [Fact]
    public void Multi_region_keeps_order_backspace_removes_last_enter_commits()
    {
        var s = new RegionSelection(Desktop, multiRegion: true);
        void Drag(int x, int y) { s.Down(new PixelPoint(x, y)); s.Up(new PixelPoint(x + 9, y + 9)); }
        Drag(0, 0); Drag(100, 0); Drag(200, 0);
        Assert.Equal(SelectionPhase.Idle, s.Phase);
        s.Key(SelectionKey.Backspace);
        s.Key(SelectionKey.Enter);
        Assert.Equal(SelectionPhase.Done, s.Phase);
        Assert.Equal([new PixelRect(0, 0, 10, 10), new PixelRect(100, 0, 10, 10)], s.Result);
    }

    [Fact]
    public void Multi_region_enter_with_nothing_does_not_finish()
    {
        var s = new RegionSelection(Desktop, multiRegion: true);
        s.Key(SelectionKey.Enter);
        Assert.False(s.IsFinished);
    }

    [Fact]
    public void Fixed_size_commits_on_press_clamped_to_desktop()
    {
        var s = new RegionSelection(new PixelRect(0, 0, 1000, 800),
            constraint: new SelectionConstraint { FixedSize = new PixelSize(640, 480) });
        s.Move(new PixelPoint(990, 790));
        Assert.Equal(new PixelRect(360, 320, 640, 480), s.Current);
        s.Down(new PixelPoint(990, 790));
        Assert.Equal(new PixelRect(360, 320, 640, 480), Assert.Single(s.Result));
    }

    [Fact]
    public void Aspect_constraint_yields_16_by_9()
    {
        var s = new RegionSelection(Desktop, constraint: new SelectionConstraint { AspectRatio = 16.0 / 9 });
        s.Down(new PixelPoint(0, 0));
        s.Up(new PixelPoint(160, 10));
        var r = Assert.Single(s.Result);
        Assert.Equal(160, r.Width);
        Assert.Equal(90, r.Height);
    }

    [Fact]
    public void Freehand_produces_bounded_polygon_relative_to_rect()
    {
        var s = new RegionSelection(Desktop, CaptureShape.Freehand);
        s.Down(new PixelPoint(10, 10));
        s.Move(new PixelPoint(60, 10));
        s.Move(new PixelPoint(60, 60));
        s.Move(new PixelPoint(10, 60));
        s.Up(new PixelPoint(10, 60));
        var r = Assert.Single(s.Result);
        Assert.Equal(new PixelRect(10, 10, 51, 51), r);
        Assert.True(s.ResultPolygon.Count >= 3);
        Assert.All(s.ResultPolygon, p => Assert.InRange(p.X, 0, r.Width));
    }

    [Fact]
    public void Degenerate_freehand_is_ignored()
    {
        var s = new RegionSelection(Desktop, CaptureShape.Freehand);
        s.Down(new PixelPoint(10, 10));
        s.Move(new PixelPoint(12, 10));
        s.Up(new PixelPoint(12, 10));
        Assert.False(s.IsFinished);
    }

    [Fact]
    public void Freehand_points_are_bounded()
    {
        var s = new RegionSelection(Desktop, CaptureShape.Freehand);
        s.Down(new PixelPoint(0, 0));
        for (int i = 0; i < 10_000; i++) s.Move(new PixelPoint(i % 1000 * 2, i / 1000 * 50 + (i % 2) * 3));
        Assert.True(s.Path.Count <= RegionSelection.MaxFreehandPoints);
    }

    [Fact]
    public void Windows_only_mode_ignores_empty_space_clicks()
    {
        var win = new PixelRect(10, 10, 20, 20);
        var s = new RegionSelection(Desktop, windows: [win], windowsOnly: true);
        s.Down(new PixelPoint(500, 500)); s.Up(new PixelPoint(600, 600));
        Assert.False(s.IsFinished);
        s.Down(new PixelPoint(15, 15)); s.Up(new PixelPoint(15, 15));
        Assert.Equal(win, Assert.Single(s.Result));
    }
}
