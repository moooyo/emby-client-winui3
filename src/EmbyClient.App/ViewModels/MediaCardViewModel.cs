using CommunityToolkit.Mvvm.ComponentModel;
using EmbyClient.Api;
using Microsoft.UI.Xaml;
using System.Globalization;

namespace EmbyClient.App.ViewModels;

public sealed partial class MediaCardViewModel(BaseItemDto item) : ObservableObject
{
    public BaseItemDto Item { get; private set; } = item;
    public string Id => Item.Id ?? string.Empty;
    public string Title => string.IsNullOrWhiteSpace(Item.Name) ? "Untitled item" : Item.Name;
    public string Subtitle => Item.Type == "Episode"
        ? string.Join(" · ", new[] { Item.SeriesName, EpisodeNumber }.Where(value => !string.IsNullOrWhiteSpace(value)))
        : string.Join(" · ", new[] { Item.ProductionYear?.ToString(CultureInfo.InvariantCulture), Item.Type }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string Metadata => string.Join(" · ", new[]
    {
        Item.ProductionYear?.ToString(CultureInfo.InvariantCulture),
        Item.OfficialRating,
        Item.RunTimeTicks is > 0 ? FormatTime(Item.RunTimeTicks.Value) : null,
        Item.CommunityRating is { } rating ? $"{rating.ToString("0.0", CultureInfo.InvariantCulture)} / 10" : null,
        Item.Type == "Episode" ? EpisodeNumber : Item.Type
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string Overview => string.IsNullOrWhiteSpace(Item.Overview) ? "No description is available for this item." : Item.Overview;
    public string Genres => Item.Genres is { Length: > 0 } ? string.Join(" · ", Item.Genres) : string.Empty;
    public string People => Item.People is { Length: > 0 }
        ? string.Join(", ", Item.People.Where(person => person.Type is "Actor" or "Director").Take(8).Select(person => person.Name).Where(name => !string.IsNullOrWhiteSpace(name)))
        : string.Empty;
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
    }

    public static string FormatTime(long ticks)
    {
        var duration = TimeSpan.FromTicks(Math.Max(0, ticks));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    private string EpisodeNumber => Item.ParentIndexNumber is { } season && Item.IndexNumber is { } episode
        ? Item.IndexNumberEnd is { } end && end != episode ? $"S{season:00} E{episode:00}–{end:00}" : $"S{season:00} E{episode:00}"
        : Item.IndexNumber is { } index ? $"Episode {index}" : "Episode";
}
