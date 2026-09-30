using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private PersonDetailsViewModel? _personModel;
    private readonly StackPanel _personPage = new() { Spacing = 32, Margin = new Thickness(56, 116, 56, 64) };
    private readonly LumenArtwork _personPortrait = new() { Width = 160, Height = 160, CornerRadius = new CornerRadius(80), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _personName = LumenUi.Text(string.Empty, 44, true);
    private readonly TextBlock _personBiography = LumenUi.Text(string.Empty, 15);
    private readonly TextBlock _personError = LumenUi.Text(string.Empty, 13);
    private readonly Button _personBiographyToggle = LumenUi.Button("Read more", "chevron-down", height: 36);
    private readonly ItemsRepeater _personWorks = new() { VerticalCacheLength = 1 };
    private readonly UniformGridLayout _personGridLayout = new() { Orientation = Orientation.Horizontal, MinColumnSpacing = 20, MinRowSpacing = 30, ItemsStretch = UniformGridLayoutItemsStretch.None };
    private readonly TextBlock _personWorksCount = LumenUi.Text(string.Empty, 13);
    private readonly Button _personMore = LumenUi.Button("Load more", "chevron-down");
    private bool _personBuilt;
    private double _personCardWidth = 145;

    private FrameworkElement BuildPerson()
    {
        if (_personBuilt) return _personPage;
        _personBuilt = true;
        var header = new Grid { ColumnSpacing = 32 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(_personPortrait);
        var information = new StackPanel { Spacing = 14, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        _personName.TextWrapping = TextWrapping.Wrap;
        information.Children.Add(_personName);
        _personBiography.LineHeight = 25;
        _personBiography.MaxLines = 5;
        _personBiography.TextTrimming = TextTrimming.CharacterEllipsis;
        information.Children.Add(_personBiography);
        _personBiographyToggle.HorizontalAlignment = HorizontalAlignment.Left;
        _personBiographyToggle.Click += (_, _) =>
        {
            if (_personModel is null) return;
            _personModel.IsBiographyExpanded = !_personModel.IsBiographyExpanded;
            RenderPerson();
        };
        information.Children.Add(_personBiographyToggle);
        _personError.TextWrapping = TextWrapping.Wrap;
        information.Children.Add(_personError);
        Grid.SetColumn(information, 1);
        header.Children.Add(information);
        _personPage.Children.Add(header);
        _personPage.Children.Add(LumenUi.Divider());
        var worksHeading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        worksHeading.Children.Add(Label("Works", 22, true));
        _personWorksCount.VerticalAlignment = VerticalAlignment.Bottom;
        _personWorksCount.Margin = new Thickness(0, 0, 0, 3);
        worksHeading.Children.Add(_personWorksCount);
        _personPage.Children.Add(worksHeading);
        _personWorks.Layout = _personGridLayout;
        _personWorks.ItemTemplate = new LibraryCardFactory(this, () => _personCardWidth);
        _personPage.Children.Add(_personWorks);
        _personMore.HorizontalAlignment = HorizontalAlignment.Center;
        _personMore.Click += async (_, _) => { if (_personModel is { } person) await person.LoadMoreAsync(); };
        _personPage.Children.Add(_personMore);
        return _personPage;
    }

    private void RenderPerson()
    {
        var person = ViewModel.ActivePerson;
        if (person is null) return;
        if (!ReferenceEquals(person, _personModel))
        {
            DetachPerson();
            _personModel = person;
            person.PropertyChanged += PersonChanged;
            person.Works.CollectionChanged += PersonWorksChanged;
            _personWorks.ItemsSource = person.Works;
            _personPortrait.Tag = person.Portrait;
            _personPortrait.Set(person.Portrait, ViewModel, ArtworkKind.Poster, 480);
        }
        if (!ReferenceEquals(_personPortrait.Tag, person.Portrait))
        {
            _personPortrait.Tag = person.Portrait;
            _personPortrait.Set(person.Portrait, ViewModel, ArtworkKind.Poster, 480);
        }
        _personName.Text = person.Person.Name;
        _personBiography.Text = LumenText.Get(person.Biography);
        _personBiography.MaxLines = person.IsBiographyExpanded ? 0 : 5;
        _personBiographyToggle.Content = ActionContent(person.IsBiographyExpanded ? "Show less" : "Read more", person.IsBiographyExpanded ? "chevron-up" : "chevron-down");
        _personBiographyToggle.Visibility = person.Biography.Length > 240 ? Visibility.Visible : Visibility.Collapsed;
        _personError.Text = string.Join("\n", new[] { LumenText.Get(person.BiographyError), LumenText.Get(person.WorksError) }.Where(text => text.Length > 0));
        _personError.Visibility = _personError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _personError.Foreground = LumenTheme.Brush("Danger");
        _personMore.Visibility = person.HasMore || person.WorksError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _personMore.IsEnabled = !person.IsLoadingWorks;
        _personWorksCount.Text = person.WorkCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        _personWorksCount.Foreground = LumenTheme.Brush("Muted");
        UpdatePersonLayout(ActualWidth > 0 ? ActualWidth : 1440);
        SetAmbience(person.Works.FirstOrDefault() ?? person.Portrait);
    }

    private void PersonChanged(object? sender, PropertyChangedEventArgs args) => QueueRender();
    private void PersonWorksChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueRender();

    private void DetachPerson()
    {
        if (_personModel is not { } person) return;
        person.PropertyChanged -= PersonChanged;
        person.Works.CollectionChanged -= PersonWorksChanged;
        _personModel = null;
    }

    private void UpdatePersonLayout(double width)
    {
        _personPage.Margin = new Thickness(_pageMargin, 116, _pageMargin, 64);
        var usable = Math.Max(1, width - _pageMargin * 2);
        var columns = Math.Clamp(Math.Min(_columns, (int)(usable / 115)), 2, 10);
        _personCardWidth = Math.Max(80, Math.Floor((usable - 20 * (columns - 1)) / columns * 4) / 4);
        _personWorks.Width = usable;
        _personWorks.HorizontalAlignment = HorizontalAlignment.Left;
        _personGridLayout.MaximumRowsOrColumns = columns;
        _personGridLayout.MinItemWidth = _personCardWidth;
        _personGridLayout.MinItemHeight = _personCardWidth * 1.5 + 48;
        if (_renderedPage == "person") foreach (var card in _realizedCards) card.SetCardWidth(_personCardWidth);
    }
}
