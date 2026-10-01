using SnagItOpen.Core.Geometry;
using SnagItOpen.Core.Layout;

namespace SnagItOpen.Core.Tests;

public sealed class SpacingGuidesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Equal_gaps_snap_on_either_axis_and_return_paired_markers(bool horizontal)
    {
        RectD Box(double at) => horizontal ? new(at, 0, 20, 20) : new(0, at, 20, 20);
        SnapEngine.Candidate[] neighbours = [new("a", Box(0)), new("b", Box(44))];
        var result = SpacingGuides.Snap(Box(91), neighbours, 6);
        Assert.Equal(horizontal ? -3 : 0, result.Dx);
        Assert.Equal(horizontal ? 0 : -3, result.Dy);
        var equal = Assert.Single(result.Equal);
        Assert.Equal(24, equal.Moving.Pixels);
        Assert.Equal(24, equal.Reference.Pixels);
        Assert.Equal(horizontal, equal.Moving.Horizontal);
    }

    [Theory]
    [InlineData(0.9, true)]
    [InlineData(1.1, false)]
    public void Equal_gap_detection_uses_one_document_pixel_tolerance(double error, bool expected)
    {
        SnapEngine.Candidate[] neighbours = [new("a", new(0, 0, 20, 20)), new("b", new(44, 0, 20, 20))];
        var result = SpacingGuides.Detect(new(88 + error, 0, 20, 20), neighbours);
        Assert.Equal(expected, result.Length > 0);
    }

    [Fact]
    public void Two_objects_have_distance_but_cannot_claim_equal_spacing()
    {
        SnapEngine.Candidate[] neighbours = [new("a", new(0, 0, 20, 20))];
        Assert.Empty(SpacingGuides.Detect(new(44, 0, 20, 20), neighbours));
        var result = SpacingGuides.Snap(new(43, 0, 20, 20), neighbours, 6);
        Assert.Equal(0, result.Dx);
        var distance = Assert.Single(SpacingGuides.Nearest(new(44, 0, 20, 20), neighbours));
        Assert.Equal(24, distance.Pixels);
    }

    [Fact]
    public void Unrelated_rows_and_overlapping_boxes_do_not_create_equal_spacing()
    {
        SnapEngine.Candidate[] neighbours = [new("a", new(0, 100, 20, 20)), new("b", new(44, 100, 20, 20))];
        Assert.Empty(SpacingGuides.Detect(new(88, 0, 20, 20), neighbours));
        Assert.Empty(SpacingGuides.Detect(new(10, 100, 20, 20), neighbours));
        // A nearby object cannot borrow a reference gap from a different row.
        neighbours = [.. neighbours, new("c", new(44, 0, 20, 20))];
        Assert.Empty(SpacingGuides.Detect(new(88, 0, 20, 20), neighbours));
    }

    [Fact]
    public void Distance_to_hovered_object_reports_both_axes_in_document_pixels()
    {
        var distances = SpacingGuides.Between(new(0, 0, 20, 20), new(44, 50, 20, 20), "hover");
        Assert.Equal(2, distances.Length);
        Assert.Equal(24, Assert.Single(distances, x => x.Horizontal).Pixels);
        Assert.Equal(30, Assert.Single(distances, x => !x.Horizontal).Pixels);
    }
}
