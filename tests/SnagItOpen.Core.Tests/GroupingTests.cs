using SnagItOpen.Core.Documents;
using SnagItOpen.Core.Documents.Annotations;
using SnagItOpen.Core.Editing;
using SnagItOpen.Core.Geometry;

namespace SnagItOpen.Core.Tests;

/// <summary>Plan F-GRP: annotation groups.</summary>
public class GroupingTests
{
    private static RectangleAnnotation Box(double x) => new() { Bounds = new RectD(x, 0, 10, 10) };

    [Fact]
    public void Group_makes_members_contiguous_at_the_topmost_member_and_expands_selection()
    {
        var a = Box(0); var b = Box(20); var c = Box(40); var d = Box(60);
        var doc = DocumentState.CreateEmpty() with { Annotations = [a, b, c, d] };
        var (grouped, gid) = DocumentOps.Group(doc, [a.Id, c.Id]);
        Assert.Equal([b.Id, a.Id, c.Id, d.Id], grouped.Annotations.Select(x => x.Id).ToArray());
        Assert.All(grouped.Annotations.Where(x => x.Id == a.Id || x.Id == c.Id), x => Assert.Equal(gid, x.GroupId));
        Assert.Equal(new[] { a.Id, c.Id }.Order(), DocumentOps.ExpandGroups(grouped, [c.Id]).Order());
        Assert.Throws<InvalidOperationException>(() => DocumentOps.Group(doc, [a.Id]));
    }

    [Fact]
    public void Ungroup_clears_the_whole_group_and_copies_get_a_new_group()
    {
        var a = Box(0); var b = Box(20);
        var (grouped, gid) = DocumentOps.Group(DocumentState.CreateEmpty() with { Annotations = [a, b] }, [a.Id, b.Id]);
        var (dup, newIds) = DocumentOps.DuplicateAnnotations(grouped, [a.Id, b.Id]);
        var copies = dup.Annotations.Where(x => newIds.Contains(x.Id)).ToList();
        Assert.Equal(2, copies.Count);
        Assert.Single(copies.Select(x => x.GroupId).Distinct());
        Assert.NotEqual(gid, copies[0].GroupId);
        var (plain, members) = DocumentOps.Ungroup(dup, [a.Id]);
        Assert.Equal(2, members.Length);
        Assert.All(plain.Annotations.Where(x => members.Contains(x.Id)), x => Assert.Null(x.GroupId));
        Assert.All(plain.Annotations.Where(x => newIds.Contains(x.Id)), x => Assert.NotNull(x.GroupId)); // other group untouched
    }

    [Fact]
    public void Applying_a_style_keeps_the_target_group()
    {
        var g = Guid.NewGuid(); var target = Box(0) with { GroupId = g };
        Assert.Equal(g, AnnotationStyle.Transfer(new RectangleAnnotation { Color = Rgba32.Black }, target).GroupId);
    }
}
