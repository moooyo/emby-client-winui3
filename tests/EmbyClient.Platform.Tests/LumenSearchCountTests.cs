using System.Globalization;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class LumenSearchCountTests
{
    [Theory]
    [InlineData(0, 0, false, false, true)]
    [InlineData(3, 0, true, true, false)]
    [InlineData(39, 12, true, false, true)]
    [InlineData(39, 12, false, true, false)]
    public void Known_totals_remain_exact_during_loading_or_inactive_scope(int total, int loaded, bool hasMore, bool loading, bool scopeComplete)
    {
        var count = LumenSearchCount.From(total, loaded, hasMore, loading, scopeComplete);

        Assert.Equal((long)total, count.Value);
        Assert.True(count.IsExact);
        Assert.Equal(total.ToString("N0", CultureInfo.InvariantCulture), count.Format(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null, 3, false, false, true, true, "3")]
    [InlineData(null, 3, true, false, true, false, "3+")]
    [InlineData(null, 3, false, true, true, false, "3+")]
    [InlineData(null, 3, false, false, false, false, "3+")]
    [InlineData(-1, 3, false, false, true, true, "3")]
    [InlineData(-1, 3, true, true, true, false, "3+")]
    [InlineData(null, 0, false, false, true, true, "0")]
    [InlineData(null, 0, true, false, true, false, "0+")]
    [InlineData(null, 0, false, true, true, false, "0+")]
    public void Unknown_totals_use_loaded_counts_only_after_the_scope_is_complete(int? total, int loaded, bool hasMore,
        bool loading, bool scopeComplete, bool exact, string formatted)
    {
        var count = LumenSearchCount.From(total, loaded, hasMore, loading, scopeComplete);

        Assert.Equal((long)loaded, count.Value);
        Assert.Equal(exact, count.IsExact);
        Assert.Equal(formatted, count.Format(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public void Negative_loaded_counts_are_rejected(int? total) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LumenSearchCount.From(total, -1, false, false));

    [Theory]
    [InlineData(false, false, "17")]
    [InlineData(true, false, "17+")]
    [InlineData(false, true, "17+")]
    [InlineData(true, true, "17+")]
    public void All_results_are_exact_only_when_both_media_and_people_are_exact(bool mediaHasMore, bool peopleHasMore, string formatted)
    {
        var media = LumenSearchCount.From(null, 14, mediaHasMore, false);
        var people = LumenSearchCount.From(null, 3, peopleHasMore, false);

        Assert.Equal(formatted, LumenSearchCount.Sum(media, people).Format(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Cached_all_media_total_does_not_make_partial_people_exact()
    {
        var cachedAll = LumenSearchCount.From(14, 6, false, false, scopeComplete: false);
        var people = LumenSearchCount.From(null, 3, true, false);

        Assert.Equal("17+", LumenSearchCount.Sum(cachedAll, people).Format(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Inactive_all_uses_current_type_media_only_as_a_lower_bound()
    {
        var media = LumenSearchCount.From(null, 6, false, false, scopeComplete: false);
        var people = LumenSearchCount.From(3, 3, false, false);

        Assert.Equal("9+", LumenSearchCount.Sum(media, people).Format(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Complete_all_scope_can_supply_exact_inactive_type_counts()
    {
        var movies = LumenSearchCount.From(null, 6, false, false, scopeComplete: true);

        Assert.Equal("6", movies.Format(CultureInfo.InvariantCulture, hideZeroLowerBound: true));
    }

    [Fact]
    public void Cached_inactive_type_total_is_not_replaced_by_an_unrelated_scope_subset()
    {
        var movies = LumenSearchCount.From(6, 0, false, false, scopeComplete: false);

        Assert.Equal("6", movies.Format(CultureInfo.InvariantCulture, hideZeroLowerBound: true));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "0")]
    public void Tabs_can_hide_zero_lower_bounds_without_hiding_exact_zero(bool complete, string formatted)
    {
        var count = LumenSearchCount.From(null, 0, false, false, scopeComplete: complete);

        Assert.Equal(formatted, count.Format(CultureInfo.InvariantCulture, hideZeroLowerBound: true));
    }

    [Fact]
    public void Combined_integer_totals_do_not_overflow()
    {
        var media = LumenSearchCount.From(int.MaxValue, 0, false, false);
        var people = LumenSearchCount.From(int.MaxValue, 0, false, false);
        var all = LumenSearchCount.Sum(media, people);

        Assert.Equal(4_294_967_294L, all.Value);
        Assert.True(all.IsExact);
        Assert.Equal("4,294,967,294", all.Format(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("en-US", "1,234+")]
    [InlineData("de-DE", "1.234+")]
    public void Formatting_keeps_culture_specific_grouping(string cultureName, string formatted)
    {
        var count = LumenSearchCount.From(null, 1234, true, false);

        Assert.Equal(formatted, count.Format(CultureInfo.GetCultureInfo(cultureName)));
    }

    [Fact]
    public void Null_culture_uses_current_culture()
    {
        var count = LumenSearchCount.From(1234, 0, false, false);

        Assert.Equal(1234.ToString("N0", CultureInfo.CurrentCulture), count.Format());
    }
}
