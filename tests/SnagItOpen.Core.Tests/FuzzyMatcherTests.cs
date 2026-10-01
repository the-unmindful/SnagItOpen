using SnagItOpen.Core.Search;

namespace SnagItOpen.Core.Tests;

public sealed class FuzzyMatcherTests
{
    [Fact]
    public void Prefix_beats_subsequence_and_matching_is_case_insensitive()
    {
        Assert.True(FuzzyMatcher.Score("cop", "Copy image") > FuzzyMatcher.Score("cop", "Compose"));
        Assert.Equal(FuzzyMatcher.Score("COPY", "Copy image"), FuzzyMatcher.Score("copy", "Copy image"));
        Assert.Equal(-1, FuzzyMatcher.Score("xyz", "Copy image"));
    }

    [Fact]
    public void Keywords_are_searchable_and_empty_query_matches_everything()
    {
        Assert.True(FuzzyMatcher.Score("clipboard", "Copy image", "clipboard output") >= 0);
        Assert.Equal(0, FuzzyMatcher.Score("", "Anything"));
    }
}
