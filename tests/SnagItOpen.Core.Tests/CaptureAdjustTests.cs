using SnagItOpen.Core.Capture;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

public sealed class CaptureAdjustTests
{
    private static RegionSelection Adjust(SelectionConstraint? constraint = null)
    {
        var s = new RegionSelection(new PixelRect(0, 0, 1000, 800), constraint: constraint, captureOnRelease: false);
        s.Down(new PixelPoint(100, 100));
        s.Up(new PixelPoint(199, 199));
        Assert.Equal("Adjusting", s.Phase.ToString());
        return s;
    }

    [Fact]
    public void Release_can_enter_adjusting_without_producing_a_result()
    {
        var s = Adjust();
        Assert.Equal(new PixelRect(100, 100, 100, 100), s.Current);
        Assert.Empty(s.Result);
    }

    [Fact]
    public void Escape_returns_to_idle_then_cancels()
    {
        var s = Adjust();
        s.Key(SelectionKey.Escape);
        Assert.Equal(SelectionPhase.Idle, s.Phase);
        Assert.Null(s.Current);
        s.Key(SelectionKey.Escape);
        Assert.Equal(SelectionPhase.Canceled, s.Phase);
    }

    [Fact]
    public void Enter_commits_adjusted_selection()
    {
        var s = Adjust();
        s.Key(SelectionKey.Enter);
        Assert.Equal(new PixelRect(100, 100, 100, 100), Assert.Single(s.Result));
    }

    [Fact]
    public void Enter_while_dragging_commits_in_adjust_mode()
    {
        var s = new RegionSelection(new PixelRect(0, 0, 1000, 800), captureOnRelease: false);
        s.Down(new PixelPoint(10, 10));
        s.Move(new PixelPoint(29, 39));
        s.Key(SelectionKey.Enter);
        Assert.Equal(new PixelRect(10, 10, 20, 30), Assert.Single(s.Result));
    }

    [Fact]
    public void Existing_window_fixed_size_and_multi_commit_rules_survive_adjust_preference()
    {
        var desktop = new PixelRect(0, 0, 1000, 800);
        var window = new PixelRect(20, 20, 200, 100);
        var s = new RegionSelection(desktop, windows: [window], windowsOnly: true, captureOnRelease: false);
        s.Down(new PixelPoint(30, 30)); s.Up(new PixelPoint(40, 40));
        Assert.Equal(window, Assert.Single(s.Result));
        var fixedSize = new RegionSelection(desktop, constraint: new SelectionConstraint { FixedSize = new PixelSize(640, 480) }, captureOnRelease: false);
        fixedSize.Down(new PixelPoint(500, 400));
        Assert.Equal(new PixelSize(640, 480), Assert.Single(fixedSize.Result).Size);
        var multi = new RegionSelection(desktop, multiRegion: true, captureOnRelease: false);
        multi.Down(new PixelPoint(10, 10)); multi.Up(new PixelPoint(19, 19));
        Assert.Equal(SelectionPhase.Idle, multi.Phase);
        Assert.Single(multi.Committed);
    }

    [Theory]
    [InlineData(SelectionHandle.TopLeft)]
    [InlineData(SelectionHandle.Top)]
    [InlineData(SelectionHandle.TopRight)]
    [InlineData(SelectionHandle.Right)]
    [InlineData(SelectionHandle.BottomRight)]
    [InlineData(SelectionHandle.Bottom)]
    [InlineData(SelectionHandle.BottomLeft)]
    [InlineData(SelectionHandle.Left)]
    public void Every_aspect_handle_preserves_ratio_inside_the_desktop(SelectionHandle handle)
    {
        var s = Adjust(new SelectionConstraint { AspectRatio = 16.0 / 9 });
        s.BeginAdjust(handle, new PixelPoint(150, 150));
        s.Move(new PixelPoint(0, 799));
        var r = s.Current!.Value;
        Assert.True(s.Desktop.Contains(r));
        Assert.InRange(Math.Abs(r.Width - r.Height * 16.0 / 9), 0, 1);
    }

    [Theory]
    [InlineData(SelectionHandle.TopLeft, 90, 90, 110, 110)]
    [InlineData(SelectionHandle.Top, 100, 90, 100, 110)]
    [InlineData(SelectionHandle.TopRight, 100, 90, 110, 110)]
    [InlineData(SelectionHandle.Right, 100, 100, 110, 100)]
    [InlineData(SelectionHandle.BottomRight, 100, 100, 110, 110)]
    [InlineData(SelectionHandle.Bottom, 100, 100, 100, 110)]
    [InlineData(SelectionHandle.BottomLeft, 90, 100, 110, 110)]
    [InlineData(SelectionHandle.Left, 90, 100, 110, 100)]
    public void Each_handle_resizes_its_edges(SelectionHandle handle, int x, int y, int w, int h)
    {
        var s = Adjust();
        s.BeginAdjust(handle, new PixelPoint(150, 150));
        int dx = handle is SelectionHandle.TopLeft or SelectionHandle.BottomLeft or SelectionHandle.Left ? -10 : 10;
        int dy = handle is SelectionHandle.TopLeft or SelectionHandle.Top or SelectionHandle.TopRight ? -10 : 10;
        s.Move(new PixelPoint(150 + dx, 150 + dy));
        s.Up(s.Cursor);
        Assert.Equal(new PixelRect(x, y, w, h), s.Current);
    }

    [Fact]
    public void Body_move_clamps_without_changing_size()
    {
        var s = Adjust();
        s.BeginAdjust(SelectionHandle.Move, new PixelPoint(150, 150));
        s.Move(new PixelPoint(999, 799));
        Assert.Equal(new PixelRect(900, 700, 100, 100), s.Current);
        s.Move(new PixelPoint(-500, -500));
        Assert.Equal(new PixelRect(0, 0, 100, 100), s.Current);
    }

    [Fact]
    public void Aspect_adjust_stays_locked_and_inside_the_screen()
    {
        var s = Adjust(new SelectionConstraint { AspectRatio = 16.0 / 9 });
        s.BeginAdjust(SelectionHandle.BottomRight, new PixelPoint(199, 199));
        s.Move(new PixelPoint(999, 799));
        var r = s.Current!.Value;
        Assert.True(s.Desktop.Contains(r));
        Assert.InRange(Math.Abs(r.Width - r.Height * 16.0 / 9), 0, 1);
    }

    [Fact]
    public void Keyboard_moves_and_resizes_physical_edges()
    {
        var s = Adjust();
        s.Key(SelectionKey.Right);
        s.Key(SelectionKey.Down, shift: true);
        Assert.Equal(new PixelRect(101, 110, 100, 100), s.Current);
        s.Key(SelectionKey.Right, control: true);
        Assert.Equal(new PixelRect(101, 110, 101, 100), s.Current);
        s.Key(SelectionKey.Left, shift: true, control: true);
        Assert.Equal(new PixelRect(100, 110, 102, 100), s.Current);
        s.Key(SelectionKey.Tab);
        Assert.Equal(SelectionHandle.Top, s.ActiveEdge);
    }

    [Fact]
    public void Switching_mode_discards_a_pending_adjustment()
    {
        var s = Adjust();
        s.Configure(CaptureShape.Ellipse);
        Assert.Equal(SelectionPhase.Idle, s.Phase);
        Assert.Null(s.Current);
        Assert.Equal(CaptureShape.Ellipse, s.Shape);
    }

    [Theory]
    [InlineData(100, 100, 100, 100, 208)]
    [InlineData(100, 700, 100, 80, 652)]
    [InlineData(0, 0, 1000, 800, 8)]
    public void Action_bar_flips_below_above_then_inside(int x, int y, int w, int h, int expectedY)
    {
        var monitor = new PixelRect(0, 0, 1000, 800);
        var placed = CaptureChromePlacement.ActionBar(new PixelRect(x, y, w, h), monitor, new PixelSize(300, 40), 8);
        Assert.Equal(expectedY, placed.Y);
        Assert.True(monitor.Contains(placed));
    }

    [Fact]
    public void Loupe_flips_and_clamps_on_a_negative_origin_monitor()
    {
        var monitor = new PixelRect(-1920, -100, 1920, 1080);
        var placed = CaptureChromePlacement.Loupe(new PixelPoint(-5, 970), monitor, new PixelSize(120, 180), 24);
        Assert.Equal(-149, placed.X);
        Assert.Equal(766, placed.Y);
        Assert.True(monitor.Contains(placed));
    }
}
