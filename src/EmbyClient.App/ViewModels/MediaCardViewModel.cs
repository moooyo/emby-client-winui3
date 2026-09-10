using CommunityToolkit.Mvvm.ComponentModel;
using EmbyClient.Api;
using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;
using System.Globalization;

namespace EmbyClient.App.ViewModels;

public sealed partial class MediaCardViewModel(BaseItemDto item, string? personRole = null) : ObservableObject
{
    public BaseItemDto Item { get; private set; } = item;
    public ObservableCollection<MediaCardViewModel> Cast { get; } = new(CreateCast(item));
    public string Id => Item.Id ?? string.Empty;
    public string Title => string.IsNullOrWhiteSpace(Item.Name) ? "Untitled item" : Item.Name;
    public string DisplayTitle => Item.Type == "Episode" && !string.IsNullOrWhiteSpace(Item.SeriesName) ? Item.SeriesName : Title;
    public string PersonRole { get; } = personRole ?? string.Empty;
    public string CardSubtitle => Item.Type switch
    {
        "Person" => PersonRole,
        "Episode" => $"{EpisodeNumber} · {Title}",
        "Season" => Item.ChildCount is { } count ? $"{count} {(count == 1 ? "episode" : "episodes")}" : string.Empty,
        _ => Item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
    };
    public string Subtitle => CardSubtitle;
    public string EpisodeTitle => Item.IndexNumber is { } index ? $"{index}. {Title}" : Title;
    public string EpisodeSummary => Item.Overview ?? string.Empty;
    public string EpisodeMetadata => string.Join(" · ", new[]
    {
        Item.PremiereDate?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
        RuntimeLabel
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string RuntimeLabel => Item.RunTimeTicks is > 0 ? FormatRuntime(Item.RunTimeTicks.Value) : string.Empty;
    public string Metadata => string.Join(" · ", new[]
    {
        Item.CommunityRating is { } rating ? $"★ {rating.ToString("0.0", CultureInfo.InvariantCulture)}" : null,
        Item.ProductionYear?.ToString(CultureInfo.InvariantCulture),
        Item.OfficialRating,
        RuntimeLabel,
        Item.Type == "Episode" ? EpisodeNumber : null
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string Overview => string.IsNullOrWhiteSpace(Item.Overview) ? "No description is available for this item." : Item.Overview;
    public string Genres => Item.Genres is { Length: > 0 } ? string.Join(" · ", Item.Genres) : string.Empty;
    public string People => Item.People is { Length: > 0 }
        ? string.Join(", ", Item.People.Where(person => person.Type is "Actor" or "Director").Take(8).Select(person => person.Name).Where(name => !string.IsNullOrWhiteSpace(name)))
        : string.Empty;
    public string Directors => Item.People is { Length: > 0 }
        ? string.Join(", ", Item.People.Where(person => person.Type == "Director").Select(person => person.Name).Where(name => !string.IsNullOrWhiteSpace(name)))
        : string.Empty;
    public string MediaFormat
    {
        get
        {
            var streams = Item.MediaStreams is { Length: > 0 } mediaStreams ? mediaStreams : Item.MediaSources?.FirstOrDefault()?.MediaStreams ?? [];
            var video = streams.FirstOrDefault(stream => stream.Type == "Video");
            var audio = streams.FirstOrDefault(stream => stream.Type == "Audio" && stream.IsDefault == true)
                ?? streams.FirstOrDefault(stream => stream.Type == "Audio");
            var resolution = video?.Height is { } height ? height >= 2160 ? "4K" : $"{height}p" : null;
            var audioLabel = audio?.DisplayTitle ?? string.Join(" ", new[]
            {
                audio?.Language,
                audio?.Codec?.ToUpperInvariant(),
                audio?.ChannelLayout ?? (audio?.Channels is { } channels ? $"{channels} ch" : null)
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return string.Join(" · ", new[] { resolution, video?.Codec?.ToUpperInvariant(), audioLabel }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }
    public bool IsFolder => Item.IsFolder == true || Item.Type is "Series" or "Season" or "BoxSet" or "CollectionFolder" or "Folder" or "MusicAlbum" or "Playlist";
    public bool CanPlay => !IsFolder && Item.LocationType != "Virtual" && !string.IsNullOrWhiteSpace(Id)
        && (Item.MediaType is "Video" or "Audio" || Item.Type is "Movie" or "Episode" or "Video" or "Audio" or "MusicVideo" or "TvChannel");
    public bool IsFavorite => Item.UserData?.IsFavorite == true;
    public bool IsPlayed => Item.UserData?.Played == true;
    public long ResumeTicks => Math.Max(0, Item.UserData?.PlaybackPositionTicks ?? 0);
    public bool CanResume => CanPlay && ResumeTicks > 0 && (Item.RunTimeTicks is not > 0 || ResumeTicks < Item.RunTimeTicks.Value);
    public string ResumeLabel => $"Resume at {FormatTime(ResumeTicks)}";
    public string FavoriteLabel => IsFavorite ? "Remove favorite" : "Add favorite";
    public string PlayedLabel => IsPlayed ? "Mark unplayed" : "Mark played";
    public string UnplayedCountLabel => Item.Type == "Series" && Item.UserData?.UnplayedItemCount is > 0
        ? Item.UserData.UnplayedItemCount.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    public string StateLabel => string.Join(" · ", new[]
    {
        IsFavorite ? "Favorite" : null,
        IsPlayed ? "Played" : CanResume ? ResumeLabel : null
    }.Where(value => value is not null));
    public double Progress => Item.UserData?.PlayedPercentage is { } percentage
        ? Math.Clamp(percentage, 0, 100)
        : Item.RunTimeTicks is > 0 ? Math.Clamp(100d * ResumeTicks / Item.RunTimeTicks.Value, 0, 100) : 0;
    public Visibility ProgressVisibility => Progress is > 0 and < 100 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StateVisibility => StateLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlayVisibility => CanPlay ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResumeVisibility => CanResume ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GenresVisibility => Genres.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PeopleVisibility => People.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DirectorsVisibility => Directors.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CastVisibility => Cast.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MediaFormatVisibility => MediaFormat.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnplayedCountVisibility => UnplayedCountLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public override string ToString() => Title;

    public void ApplyUserData(UserItemDataDto userData)
    {
        Item = Item with { UserData = userData };
        OnPropertyChanged(nameof(Item));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(IsPlayed));
        OnPropertyChanged(nameof(ResumeTicks));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(ResumeLabel));
        OnPropertyChanged(nameof(FavoriteLabel));
        OnPropertyChanged(nameof(PlayedLabel));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(StateVisibility));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(ResumeVisibility));
        OnPropertyChanged(nameof(UnplayedCountLabel));
        OnPropertyChanged(nameof(UnplayedCountVisibility));
    }

    public static string FormatTime(long ticks)
    {
        var duration = TimeSpan.FromTicks(Math.Max(0, ticks));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    public static string FormatRuntime(long ticks)
    {
        var duration = TimeSpan.FromTicks(Math.Max(0, ticks));
        var minutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
        return minutes >= 60 ? minutes % 60 == 0 ? $"{minutes / 60} hr" : $"{minutes / 60} hr {minutes % 60} min" : $"{minutes} min";
    }

    private static IEnumerable<MediaCardViewModel> CreateCast(BaseItemDto item) =>
        (item.People ?? []).Where(person => (person.Type is "Actor" or "GuestStar") && !string.IsNullOrWhiteSpace(person.Name))
            .Select(person => new MediaCardViewModel(new BaseItemDto
            {
                Id = person.Id,
                Name = person.Name,
                Type = "Person",
                ImageTags = string.IsNullOrWhiteSpace(person.PrimaryImageTag) ? null : new() { ["Primary"] = person.PrimaryImageTag }
            }, person.Role ?? person.Type));

    public string EpisodeNumber => Item.ParentIndexNumber is { } season && Item.IndexNumber is { } episode
        ? Item.IndexNumberEnd is { } end && end != episode ? $"S{season:00} E{episode:00}–{end:00}" : $"S{season:00} E{episode:00}"
        : Item.IndexNumber is { } index ? $"Episode {index}" : "Episode";
}
