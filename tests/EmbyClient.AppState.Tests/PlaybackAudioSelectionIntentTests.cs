using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.AppState.Tests;

public sealed class PlaybackAudioSelectionIntentTests
{
    [Fact]
    public void Displaying_a_default_audio_track_does_not_request_an_explicit_override()
    {
        var selection = new PlaybackAudioSelectionIntent();

        selection.UseDefault(1);

        Assert.Equal(1, selection.DisplayIndex);
        Assert.Null(selection.RequestIndex);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void A_user_selected_track_keeps_explicit_intent_even_when_it_equals_the_displayed_default(int index)
    {
        var selection = new PlaybackAudioSelectionIntent();
        selection.UseDefault(1);

        selection.Select(index);

        Assert.Equal(index, selection.DisplayIndex);
        Assert.Equal(index, selection.RequestIndex);
    }

    [Fact]
    public void A_new_item_or_version_default_clears_the_previous_track_override()
    {
        var selection = new PlaybackAudioSelectionIntent();
        selection.UseDefault(1);
        selection.Select(7);

        selection.UseDefault(101);

        Assert.Equal(101, selection.DisplayIndex);
        Assert.Null(selection.RequestIndex);
    }

    [Fact]
    public void Clearing_a_detail_removes_both_the_display_and_request_indexes()
    {
        var selection = new PlaybackAudioSelectionIntent();
        selection.Select(7);

        selection.UseDefault(null);

        Assert.Null(selection.DisplayIndex);
        Assert.Null(selection.RequestIndex);
    }
}
