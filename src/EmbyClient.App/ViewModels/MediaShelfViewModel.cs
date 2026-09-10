using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;

namespace EmbyClient.App.ViewModels;

public sealed partial class MediaShelfViewModel(string title, bool isLandscape, HomeSection? section = null, string? libraryId = null)
{
    public string Title { get; } = title;
    public string SeeAllLabel => $"See all: {Title}";
    public bool IsLandscape { get; } = isLandscape;
    public HomeSection? Section { get; } = section;
    public string? LibraryId { get; } = libraryId;
    public ObservableCollection<MediaCardViewModel> Items { get; } = [];
    public Visibility LandscapeVisibility => IsLandscape ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PosterVisibility => IsLandscape ? Visibility.Collapsed : Visibility.Visible;
}
