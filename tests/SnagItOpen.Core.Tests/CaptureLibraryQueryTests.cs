using SnagItOpen.Core.Capture;

namespace SnagItOpen.Core.Tests;

public sealed class CaptureLibraryQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(5.5));
    private static readonly CaptureLibraryMetadata[] Items =
    [
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Settings", 1280, 720, Now, false, 100),
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Article", 640, 480, Now.AddDays(-1), true, 200),
        new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Old settings", 1920, 1080, Now.AddDays(-7), true, 300),
    ];
    [Theory]
    [InlineData("SETTINGS", 2)]
    [InlineData("1280×720", 1)]
    [InlineData("2026-10-01", 1)]
    [InlineData("settings 1280", 1)]
    [InlineData("missing", 0)]
    public void Search_matches_name_dimensions_and_date(string text, int count) =>
        Assert.Equal(count, CaptureLibraryQuery.Apply(Items, text, CaptureLibraryFilter.All, CaptureLibrarySort.Newest, Now).Count);
    [Theory]
    [InlineData(CaptureLibraryFilter.Pinned, 2)]
    [InlineData(CaptureLibraryFilter.Today, 1)]
    [InlineData(CaptureLibraryFilter.ThisWeek, 2)]
    public void Filters_use_local_calendar_boundaries(CaptureLibraryFilter filter, int count) =>
        Assert.Equal(count, CaptureLibraryQuery.Apply(Items, "", filter, CaptureLibrarySort.Newest, Now).Count);
    [Fact]
    public void Sort_orders_by_name_size_and_age()
    {
        Assert.Equal("Article", CaptureLibraryQuery.Apply(Items, "", CaptureLibraryFilter.All, CaptureLibrarySort.Name, Now)[0].Name);
        Assert.Equal("Old settings", CaptureLibraryQuery.Apply(Items, "", CaptureLibraryFilter.All, CaptureLibrarySort.Size, Now)[0].Name);
        Assert.Equal("Old settings", CaptureLibraryQuery.Apply(Items, "", CaptureLibraryFilter.All, CaptureLibrarySort.Oldest, Now)[0].Name);
    }
    [Fact]
    public void Search_and_filter_are_combined_without_mutating_entries()
    {
        var result = CaptureLibraryQuery.Apply(Items, "settings", CaptureLibraryFilter.Pinned, CaptureLibrarySort.Newest, Now);
        Assert.Equal("Old settings", Assert.Single(result).Name);
        Assert.Equal(3, Items.Length);
    }
}
