using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;

namespace EmbyClient.AppState.Tests;

public sealed class MediaInformationViewModelTests
{
    [Fact]
    public void Empty_or_unknown_streams_do_not_create_media_information()
    {
        var model = new MediaInformationViewModel(new BaseItemDto
        {
            MediaSources = [new() { Name = "Default" }],
            MediaStreams = [new() { Type = "Video", Index = 0 }, new() { Type = "FutureType", Codec = "opaque" }]
        });

        Assert.Equal(Visibility.Collapsed, model.Visibility);
        Assert.Empty(model.Sources);
    }

    [Fact]
    public void Real_tracks_keep_sparse_indices_defaults_and_independent_disclosures_without_artwork()
    {
        var model = new MediaInformationViewModel(new BaseItemDto
        {
            MediaSources = [new()
            {
                Id = "source-a", Container = "mkv", Size = 2_500_000_000, DefaultAudioStreamIndex = 7,
                MediaStreams =
                [
                    new() { Type = "Video", Index = 0, Codec = "hevc", Width = 3840, Height = 2160, AverageFrameRate = 23.976f, BitDepth = 10, ColorSpace = "bt2020nc", BitRate = 20_000_000 },
                    new() { Type = "Audio", Index = 7, Codec = "aac", DisplayLanguage = "English", Channels = 6, SampleRate = 48000 },
                    new() { Type = "Subtitle", Index = 11, Codec = "srt", Language = "English", IsExternal = false },
                    new() { Type = "Subtitle", Index = 13, Codec = "srt", Language = "English", IsHearingImpaired = true, IsExternal = true },
                    new() { Type = "Subtitle", Index = 17, Codec = "ass", Language = "Japanese", IsForced = true }
                ]
            }]
        });

        var source = Assert.Single(model.Sources);
        Assert.Equal(Visibility.Visible, model.Visibility);
        Assert.Contains("2.5 GB", source.Summary);
        Assert.Equal("Audio:7", Assert.Single(source.Audio.Tracks).Key);
        Assert.Contains("Default", source.Audio.Tracks[0].Tags);
        Assert.Contains(source.Audio.Tracks[0].Fields, field => field.Label == "Sample rate" && field.Value == "48 kHz");
        Assert.Contains(source.Video.Tracks[0].TechnicalFields, field => field.Label == "Bitrate" && field.Value == "20 Mbps");
        Assert.Contains(source.Video.Tracks[0].Fields, field => field.Label == "Resolution" && field.Value == "3840 × 2160");
        Assert.Equal(2, source.Subtitles.VisibleTracks.Count);
        Assert.Contains("SDH", source.Subtitles.Tracks[1].Tags);
        Assert.Contains("External", source.Subtitles.Tracks[1].Summary);
        Assert.NotEqual(source.Subtitles.Tracks[0].TechnicalAutomationName, source.Subtitles.Tracks[1].TechnicalAutomationName);

        source.Subtitles.IsExpanded = true;
        source.Video.Tracks[0].IsExpanded = true;

        Assert.Equal(3, source.Subtitles.VisibleTracks.Count);
        Assert.False(source.Audio.IsExpanded);
        Assert.False(source.Subtitles.Tracks[0].IsExpanded);
    }

    [Fact]
    public void Item_stream_fallback_preserves_available_source_metadata_and_versions()
    {
        var model = new MediaInformationViewModel(new BaseItemDto
        {
            MediaSources = [new() { Id = "first", Container = "mkv" }, new() { Id = "second", Name = "Alternative version", Container = "mp4" }],
            MediaStreams = [new() { Index = 5, Type = "Audio", Codec = "flac" }]
        });

        Assert.Equal(2, model.Sources.Count);
        Assert.Equal(Visibility.Visible, model.SourceSelectorVisibility);
        Assert.Equal("first", model.SelectedSource?.Key);
        Assert.Single(model.Sources[0].Audio.Tracks);
        Assert.Empty(model.Sources[1].Audio.Tracks);
        Assert.Contains("MKV", model.Sources[0].Summary);
    }

    [Fact]
    public void Source_streams_are_not_combined_with_duplicate_item_streams()
    {
        var model = new MediaInformationViewModel(new BaseItemDto
        {
            MediaSources = [new() { Id = "real", MediaStreams = [new() { Type = "Video", Codec = "h264", Index = 2 }] }],
            MediaStreams = [new() { Type = "Video", Codec = "hevc", Index = 0 }]
        });

        Assert.Equal("H264", Assert.Single(Assert.Single(model.Sources).Video.Tracks).Title);
    }

    [Fact]
    public void Primary_item_streams_remain_available_when_an_alternate_source_has_its_own_streams()
    {
        var model = new MediaInformationViewModel(new BaseItemDto
        {
            MediaSources = [new() { Id = "primary", Container = "mkv" }, new() { Id = "alternate", MediaStreams = [new() { Type = "Video", Codec = "h264" }] }],
            MediaStreams = [new() { Type = "Video", Codec = "hevc" }]
        });

        Assert.Equal("primary", model.SelectedSource?.Key);
        Assert.Equal("HEVC", Assert.Single(model.Sources[0].Video.Tracks).Title);
        Assert.Equal("H264", Assert.Single(model.Sources[1].Video.Tracks).Title);
    }

    [Fact]
    public void Explicit_source_defaults_override_file_flags_and_allow_subtitles_to_be_off()
    {
        var model = new MediaInformationViewModel(new BaseItemDto
        {
            MediaSources = [new()
            {
                DefaultAudioStreamIndex = 7, DefaultSubtitleStreamIndex = -1,
                MediaStreams = [new() { Type = "Audio", Index = 2, Codec = "aac", IsDefault = true }, new() { Type = "Audio", Index = 7, Codec = "aac" },
                    new() { Type = "Subtitle", Index = 11, Codec = "srt", IsDefault = true }]
            }]
        });

        var source = Assert.Single(model.Sources);
        Assert.DoesNotContain("Default", source.Audio.Tracks[0].Tags);
        Assert.Contains("Default", source.Audio.Tracks[1].Tags);
        Assert.DoesNotContain("Default", source.Subtitles.Tracks[0].Tags);
    }

    [Fact]
    public void Refresh_preserves_source_and_disclosure_choices_and_removes_empty_data()
    {
        var item = new BaseItemDto
        {
            Id = "movie-a",
            MediaSources = [new() { Id = "a", Name = "A", Container = "mkv" }, new()
            {
                Id = "b", Name = "B", MediaStreams =
                [new() { Type = "Video", Index = 0, Codec = "h264", Profile = "High" },
                 new() { Type = "Subtitle", Index = 2, Codec = "srt" }, new() { Type = "Subtitle", Index = 3, Codec = "srt" }, new() { Type = "Subtitle", Index = 4, Codec = "srt" }]
            }]
        };
        var card = new MediaCardViewModel(item);
        var model = card.MediaInformation;
        model.SelectedSource = model.Sources[1];
        model.SelectedSource.Video.Tracks[0].IsExpanded = true;
        model.SelectedSource.Subtitles.IsExpanded = true;

        card.ApplyUserData(new() { IsFavorite = true });
        Assert.Same(model, card.MediaInformation);
        Assert.True(model.SelectedSource.Video.Tracks[0].IsExpanded);

        card.ApplyItem(item with { Overview = "Updated description" });
        Assert.Equal("b", model.SelectedSource?.Key);
        Assert.True(model.SelectedSource?.Video.Tracks[0].IsExpanded);
        Assert.Equal(3, model.SelectedSource?.Subtitles.VisibleTracks.Count);

        card.ApplyItem(new() { Id = "movie-a" });
        Assert.Equal(Visibility.Collapsed, model.Visibility);
        Assert.Null(model.SelectedSource);
    }

    [Fact]
    public void History_restores_choices_for_matching_sources_and_tracks_only()
    {
        var item = new BaseItemDto { MediaStreams = [new() { Type = "Audio", Index = 7, Codec = "aac", Profile = "LC" }] };
        var previous = new MediaInformationViewModel(item);
        previous.SelectedSource!.Audio.Tracks[0].IsExpanded = true;
        var restored = new MediaInformationViewModel(item);

        restored.RestoreDisclosureState(previous);

        Assert.True(restored.SelectedSource!.Audio.Tracks[0].IsExpanded);
    }

    [Fact]
    public void Missing_fields_stay_absent_and_remote_access_urls_are_not_displayed()
    {
        var model = new MediaInformationViewModel(new BaseItemDto
        {
            MediaSources = [new()
            {
                Path = "https://example.invalid/video?api_key=secret", MediaStreams =
                [new() { Type = "Video", Width = 1920, BitRate = 0, SampleRate = -1, RefFrames = -1 }]
            }]
        });
        var source = Assert.Single(model.Sources);
        var video = Assert.Single(source.Video.Tracks);

        Assert.Equal(string.Empty, source.Summary);
        Assert.DoesNotContain("secret", source.Name);
        Assert.Contains(video.Fields, field => field.Value == "1920 px wide");
        Assert.Empty(video.TechnicalFields);
    }

    [Fact]
    public void Episode_identity_retains_series_episode_number_and_title_after_refresh()
    {
        var card = new MediaCardViewModel(new() { Id = "ep-a", Type = "Episode", SeriesId = "show-a", SeriesName = "A series", ParentIndexNumber = 2, IndexNumber = 3, Name = "A title" });

        Assert.Equal("A series · S02 E03 · A title", card.SceneIdentity);
        Assert.Equal("A series", card.ParentSeriesLabel);
        Assert.Equal(Visibility.Visible, card.ParentSeriesVisibility);

        card.ApplyItem(card.Item with { Name = "A revised title", LocationType = "Virtual" });

        Assert.Contains("A revised title", card.SceneIdentity);
        Assert.Equal(Visibility.Visible, card.PlaybackUnavailableVisibility);
        Assert.False(card.CanPlay);
    }
}
