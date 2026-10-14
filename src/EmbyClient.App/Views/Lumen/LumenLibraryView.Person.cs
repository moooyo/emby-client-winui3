using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private PersonDetailsViewModel? _personModel;
    private readonly Grid _personPage = new() { Margin = new Thickness(56, 120, 56, 64) };
    private readonly StackPanel _personProfile = new() { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _personMain = new();
    private readonly Grid _personAvatar = new() { Width = 240, Height = 240 };
    private readonly Border _personAvatarShadowHost = new() { IsHitTestVisible = false };
    private readonly Border _personAvatarFallback = new() { CornerRadius = new CornerRadius(120) };
    private readonly TextBlock _personInitial = LumenUi.Text(string.Empty, 96, true);
    private readonly LumenArtwork _personPortrait = new() { Width = 240, Height = 240, CornerRadius = new CornerRadius(120), Opacity = 0 };
    private readonly TextBlock _personName = LumenUi.Text(string.Empty, 56, true);
    private readonly TextBlock _personOriginalName = LumenUi.Text(string.Empty, 12);
    private readonly TextBlock _personProfession = LumenUi.Text(string.Empty, 13);
    private readonly TextBlock _personBirth = LumenUi.Text(string.Empty, 13);
    private readonly TextBlock _personWorksCount = LumenUi.Text(string.Empty, 13);
    private readonly StackPanel _personSources = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _personBiography = LumenUi.Text(string.Empty, 15);
    private readonly TextBlock _personBiographyError = LumenUi.Text(string.Empty, 13);
    private readonly HyperlinkButton _personBiographyToggle = new();
    private readonly HyperlinkButton _personBiographyRetry = new();
    private readonly Grid _personToolbar = new() { Margin = new Thickness(-14, 34, 0, 0) };
    private readonly StackPanel _personTabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly List<(ToggleButton Button, TextBlock Label, TextBlock Count, Border Indicator, string Filter)> _personTabButtons = [];
    private readonly Button _personSort = LumenUi.Button(string.Empty, height: 34);
    private readonly TextBlock _personSortLabel = LumenUi.Text(string.Empty, 13);
    private readonly Flyout _personSortFlyout = new();
    private readonly List<(Button Button, FrameworkElement Check, string Sort)> _personSortButtons = [];
    private readonly ItemsRepeater _personWorks = new() { VerticalCacheLength = 1, Margin = new Thickness(0, 18, 0, 0) };
    private readonly UniformGridLayout _personGridLayout = new()
    {
        Orientation = Orientation.Horizontal, MinColumnSpacing = 20, MinRowSpacing = 28,
        ItemsStretch = UniformGridLayoutItemsStretch.None
    };
    private readonly TextBlock _personEmptyWorks = LumenUi.Text(string.Empty, 14);
    private readonly TextBlock _personWorksError = LumenUi.Text(string.Empty, 13);
    private readonly Button _personMore = LumenUi.Button("Load more", "chevron-down", height: 36);
    private readonly HashSet<LumenMediaCard> _personCards = [];
    private PersonPortraitShadow? _personPortraitShadow;
    private bool _personBuilt;
    private bool _personBiographyCanExpand;
    private double _personCardWidth = 140;

    private FrameworkElement BuildPerson()
    {
        if (_personBuilt) return _personPage;
        _personBuilt = true;
        RegisterPersonStrings();
        AutomationProperties.SetAutomationId(_personPage, "LumenPersonPage");
        _personPage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        _personPage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _personPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _personPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _personProfile.VerticalAlignment = VerticalAlignment.Top;
        _personPage.Children.Add(_personProfile);
        Grid.SetColumn(_personMain, 1);
        _personPage.Children.Add(_personMain);
        BuildPersonProfile();

        var eyebrow = Label("People", 13);
        eyebrow.Foreground = LumenTheme.Brush("Sub");
        _personMain.Children.Add(eyebrow);
        _personName.FontWeight = FontWeights.Black;
        _personName.CharacterSpacing = 50;
        _personName.LineHeight = 62;
        _personName.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetHeadingLevel(_personName, AutomationHeadingLevel.Level1);
        _personMain.Children.Add(_personName);
        _personOriginalName.FontWeight = FontWeights.SemiBold;
        _personOriginalName.CharacterSpacing = 280;
        _personOriginalName.Foreground = LumenTheme.Brush("Sub");
        _personOriginalName.Margin = new Thickness(0, 6, 0, 0);
        _personMain.Children.Add(_personOriginalName);
        _personBiography.LineHeight = 27;
        _personBiography.MaxWidth = 760;
        _personBiography.HorizontalAlignment = HorizontalAlignment.Left;
        _personBiography.Foreground = LumenTheme.Brush("Sub");
        _personBiography.Margin = new Thickness(0, 18, 0, 0);
        _personBiography.MaxLines = 3;
        _personBiography.TextTrimming = TextTrimming.CharacterEllipsis;
        _personBiography.IsTextSelectionEnabled = true;
        _personBiography.IsTextTrimmedChanged += (_, _) =>
        {
            if (_personModel?.IsBiographyExpanded != true) _personBiographyCanExpand = _personBiography.IsTextTrimmed;
            UpdatePersonBiographyToggle();
        };
        _personMain.Children.Add(_personBiography);
        ConfigurePersonTextLink(_personBiographyToggle);
        _personBiographyToggle.Margin = new Thickness(0, 8, 0, 0);
        _personBiographyToggle.Click += (_, _) =>
        {
            if (_personModel is null) return;
            _personModel.IsBiographyExpanded = !_personModel.IsBiographyExpanded;
            _personBiography.MaxLines = _personModel.IsBiographyExpanded ? 0 : 3;
            UpdatePersonBiographyToggle();
        };
        _personMain.Children.Add(_personBiographyToggle);
        _personBiographyError.Margin = new Thickness(0, 12, 0, 0);
        _personBiographyError.Foreground = LumenTheme.Brush("Danger");
        AutomationProperties.SetLiveSetting(_personBiographyError, AutomationLiveSetting.Polite);
        _personMain.Children.Add(_personBiographyError);
        ConfigurePersonTextLink(_personBiographyRetry);
        _personBiographyRetry.Content = LumenText.Get("Retry biography");
        _personBiographyRetry.Margin = new Thickness(0, 8, 0, 0);
        _personBiographyRetry.Click += async (_, _) => { if (_personModel is { } person) await person.LoadBiographyAsync(); };
        _personMain.Children.Add(_personBiographyRetry);

        BuildPersonToolbar();
        _personMain.Children.Add(_personToolbar);
        _personWorks.Layout = _personGridLayout;
        _personWorks.ItemTemplate = new PersonCardFactory(this);
        AutomationProperties.SetAutomationId(_personWorks, "LumenPersonWorks");
        LumenMediaCard.ConfigureNavigationGroup(_personWorks);
        _personMain.Children.Add(_personWorks);
        _personEmptyWorks.Foreground = LumenTheme.Brush("Muted");
        AutomationProperties.SetLiveSetting(_personEmptyWorks, AutomationLiveSetting.Polite);
        _personEmptyWorks.TextAlignment = TextAlignment.Center;
        _personEmptyWorks.Margin = new Thickness(0, 36, 0, 24);
        _personMain.Children.Add(_personEmptyWorks);
        _personWorksError.Foreground = LumenTheme.Brush("Danger");
        _personWorksError.Margin = new Thickness(0, 18, 0, 0);
        AutomationProperties.SetLiveSetting(_personWorksError, AutomationLiveSetting.Polite);
        _personMain.Children.Add(_personWorksError);
        _personMore.HorizontalAlignment = HorizontalAlignment.Center;
        _personMore.Margin = new Thickness(0, 26, 0, 0);
        _personMore.Click += async (_, _) => { if (_personModel is { } person) await person.LoadMoreAsync(); };
        _personMain.Children.Add(_personMore);
        return _personPage;
    }

    private void BuildPersonProfile()
    {
        _personInitial.HorizontalAlignment = HorizontalAlignment.Center;
        _personInitial.VerticalAlignment = VerticalAlignment.Center;
        _personInitial.FontWeight = FontWeights.Black;
        _personInitial.Foreground = LumenTheme.Brush("ImageInk");
        _personAvatarFallback.Child = _personInitial;
        _personAvatar.Children.Add(_personAvatarShadowHost);
        _personAvatar.Children.Add(_personAvatarFallback);
        _personAvatar.Children.Add(_personPortrait);
        _personAvatar.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(120), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(36, 255, 255, 255)),
            BorderThickness = new Thickness(1), IsHitTestVisible = false
        });
        _personPortrait.Image.RegisterPropertyChangedCallback(Image.SourceProperty,
            (_, _) => _personPortrait.Opacity = _personPortrait.Image.Source is null ? 0 : 1);
        _personAvatar.Loaded += (_, _) => _personPortraitShadow ??= new PersonPortraitShadow(_personAvatarShadowHost);
        _personAvatar.Unloaded += (_, _) => { _personPortraitShadow?.Dispose(); _personPortraitShadow = null; };
        AutomationProperties.SetAccessibilityView(_personAvatar, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_personInitial, AccessibilityView.Raw);
        _personProfile.Children.Add(_personAvatar);

        var facts = new Grid { Margin = new Thickness(0, 30, 0, 0), RowSpacing = 12, ColumnSpacing = 16 };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddPersonFact(facts, "Profession", _personProfession);
        AddPersonFact(facts, "Born", _personBirth);
        AddPersonFact(facts, "Works", _personWorksCount);
        AutomationProperties.SetLiveSetting(_personWorksCount, AutomationLiveSetting.Polite);
        AddPersonFact(facts, "Sources", _personSources);
        _personProfile.Children.Add(facts);
    }

    private void BuildPersonToolbar()
    {
        _personToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _personToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _personToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _personToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _personToolbar.Children.Add(_personTabs);
        foreach (var entry in new[] { (Label: "All", Filter: "all"), (Label: "Movies", Filter: "Movie"), (Label: "Series", Filter: "Series") })
        {
            var label = Label(entry.Label, 15);
            var count = LumenUi.Text(string.Empty, 12);
            count.Foreground = LumenTheme.Brush("Muted");
            count.VerticalAlignment = VerticalAlignment.Center;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(label);
            row.Children.Add(count);
            var content = new Grid();
            content.Children.Add(row);
            var indicator = new Border
            {
                Width = 5, Height = 5, CornerRadius = new CornerRadius(3), Background = LumenTheme.Brush("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom
            };
            content.Children.Add(indicator);
            var button = new ToggleButton
            {
                Height = 40, MinHeight = 40, MinWidth = 0, Padding = new Thickness(14, 0, 14, 0),
                BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent),
                Content = content, VerticalContentAlignment = VerticalAlignment.Stretch, UseSystemFocusVisuals = true,
                CornerRadius = new CornerRadius(6)
            };
            foreach (var key in new[] { "ToggleButtonBackground", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPressed" })
                button.Resources[key] = new SolidColorBrush(Colors.Transparent);
            button.Resources["ToggleButtonBackgroundPointerOver"] = LumenTheme.Brush("Control");
            button.Resources["ToggleButtonBackgroundPressed"] = LumenTheme.Brush("ControlStrong");
            button.Click += async (_, _) =>
            {
                if (_personModel is not { } person) return;
                button.IsChecked = true;
                await person.SetWorksOptionsAsync(entry.Filter, person.SelectedWorkSort);
                if (ReferenceEquals(person, _personModel)) RenderPersonWorks(person);
            };
            _personTabs.Children.Add(button);
            _personTabButtons.Add((button, label, count, indicator, entry.Filter));
        }

        _personSort.Padding = new Thickness(12, 0, 10, 0);
        _personSort.BorderBrush = LumenTheme.Brush("Line");
        _personSort.VerticalAlignment = VerticalAlignment.Center;
        _personSort.HorizontalAlignment = HorizontalAlignment.Right;
        var sortContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sortContent.Children.Add(LumenUi.Icon("sort", 16));
        sortContent.Children.Add(_personSortLabel);
        sortContent.Children.Add(LumenUi.Icon("chevron-down", 12));
        _personSort.Content = sortContent;
        Grid.SetColumn(_personSort, 1);
        _personToolbar.Children.Add(_personSort);
        var sortOptions = new StackPanel { MinWidth = 148 };
        foreach (var entry in new[] { (Label: "By year", Sort: "ProductionYear"), (Label: "By rating", Sort: "CommunityRating"), (Label: "By name", Sort: "SortName") })
        {
            var button = LumenUi.Button(entry.Label, height: 38);
            button.BorderThickness = new Thickness(0);
            button.CornerRadius = new CornerRadius(10);
            button.Padding = new Thickness(10, 0, 18, 0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var check = LumenUi.Icon("check", 15);
            row.Children.Add(check);
            row.Children.Add(Label(entry.Label, 13));
            button.Content = row;
            button.Click += async (_, _) =>
            {
                _personSortFlyout.Hide();
                if (_personModel is { } person) await person.SetWorksOptionsAsync(person.SelectedWorkType, entry.Sort);
            };
            sortOptions.Children.Add(button);
            _personSortButtons.Add((button, check, entry.Sort));
        }
        _personSortFlyout.Content = sortOptions;
        _personSortFlyout.Placement = FlyoutPlacementMode.BottomEdgeAlignedRight;
        var flyoutStyle = new Style(typeof(FlyoutPresenter)) { BasedOn = PopupStyle() };
        flyoutStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
        _personSortFlyout.FlyoutPresenterStyle = flyoutStyle;
        _personSortFlyout.Opened += (_, _) => _personSort.Background = LumenTheme.Brush("ControlStrong");
        _personSortFlyout.Closed += (_, _) => _personSort.Background = LumenTheme.Brush("Control");
        _personSort.Flyout = _personSortFlyout;
    }

    private void RenderPerson()
    {
        var person = ViewModel.ActivePerson;
        if (person is null) return;
        if (!ReferenceEquals(person, _personModel))
        {
            DetachPerson();
            _personModel = person;
            _personBiographyCanExpand = person.IsBiographyExpanded;
            person.PropertyChanged += PersonChanged;
            person.Works.CollectionChanged += PersonWorksChanged;
            _personWorks.ItemsSource = person.Works;
        }
        if (!ReferenceEquals(_personPortrait.Tag, person.Portrait))
        {
            _personPortrait.Tag = person.Portrait;
            _personPortrait.Set(person.Portrait, ViewModel, ArtworkKind.Poster, 480);
        }
        _personName.Text = person.Person.Name;
        _personInitial.Text = string.IsNullOrWhiteSpace(person.Person.Name) ? string.Empty : StringInfo.GetNextTextElement(person.Person.Name);
        _personAvatarFallback.Background = PersonAvatarBrush(person.Person.Id.Length > 0 ? person.Person.Id : person.Person.Name);
        var originalName = person.Portrait.Item.OriginalTitle;
        _personOriginalName.Text = originalName ?? string.Empty;
        _personOriginalName.Visibility = !string.IsNullOrWhiteSpace(originalName) && !string.Equals(originalName, person.Person.Name, StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;
        _personBiography.Text = string.IsNullOrWhiteSpace(person.Biography) && person.BiographyError.Length == 0
            ? LumenText.Get("Loading...") : LumenText.Get(person.Biography);
        _personBiography.MaxLines = person.IsBiographyExpanded ? 0 : 3;
        _personBiographyCanExpand |= person.IsBiographyExpanded || _personBiography.IsTextTrimmed;
        UpdatePersonBiographyToggle();
        _personBiographyError.Text = LumenText.Get(person.BiographyError);
        _personBiographyError.Visibility = person.BiographyError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _personBiographyRetry.Visibility = _personBiographyError.Visibility;
        _personBiographyRetry.IsEnabled = !person.IsLoadingBiography;
        _personWorksError.Text = LumenText.Get(person.WorksError);
        _personWorksError.Visibility = person.WorksError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _personMore.Visibility = person.HasMore || person.WorksError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _personMore.IsEnabled = !person.IsLoadingWorks;
        var loadLabel = person.IsLoadingWorks ? "Loading..." : person.WorksError.Length > 0 ? "Retry" : "Load more";
        _personMore.Content = ActionContent(loadLabel, "chevron-down");
        AutomationProperties.SetName(_personMore, LumenText.Get(loadLabel));
        RenderPersonFacts(person);
        RenderPersonWorks(person);
        UpdatePersonLayout(ActualWidth > 0 ? ActualWidth : 1440);
        SetAmbience(person.Works.FirstOrDefault() ?? person.Portrait);
    }

    private void RenderPersonFacts(PersonDetailsViewModel person)
    {
        var professions = person.Works.SelectMany(item => item.Item.People ?? [])
            .Where(entry => string.Equals(entry.Id, person.Person.Id, StringComparison.Ordinal))
            .Select(entry => entry.Type).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => PersonRoleLabel(value!)).Distinct(StringComparer.Ordinal).ToArray();
        var knownRole = PersonRoleLabel(person.Person.Profession);
        _personProfession.Text = knownRole.Length > 0 ? knownRole : professions.Length > 0 ? string.Join(" \u00b7 ", professions) : LumenText.Get("Not provided");
        var item = person.Portrait.Item;
        var born = PersonExtensionText(item, "BirthDate");
        if (DateTimeOffset.TryParse(born, CultureInfo.InvariantCulture, DateTimeStyles.None, out var birthDate)) born = birthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else if (item.PremiereDate is { } premiere) born = premiere.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var location = PersonExtensionText(item, "BirthPlace");
        if (location.Length == 0) location = PersonExtensionText(item, "ProductionLocations");
        _personBirth.Text = string.Join(" \u00b7 ", new[] { born, location }.Where(value => value.Length > 0));
        if (_personBirth.Text.Length == 0) _personBirth.Text = LumenText.Get("Not provided");
        _personWorksCount.Text = LumenText.Get("{0} works (Movies {1} / Series {2})",
            PersonCount(person.TotalWorkCount), PersonCount(person.MovieWorkCount), PersonCount(person.SeriesWorkCount));
        _personSources.Children.Clear();
        if (item.ExtensionData is { } extension && extension.TryGetValue("ProviderIds", out var providers) && providers.ValueKind == JsonValueKind.Object)
        {
            foreach (var source in providers.EnumerateObject().Where(entry => entry.Name.Equals("Tmdb", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("Imdb", StringComparison.OrdinalIgnoreCase)))
            {
                if (source.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(source.Value.GetString())) continue;
                var label = LumenUi.Text(source.Name.Equals("Tmdb", StringComparison.OrdinalIgnoreCase) ? "TMDb" : "IMDb", 11);
                label.FontWeight = FontWeights.SemiBold;
                var badge = new Border
                {
                    BorderBrush = LumenTheme.Brush("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(9, 2, 9, 2), Child = label
                };
                ToolTipService.SetToolTip(badge, source.Value.GetString());
                _personSources.Children.Add(badge);
            }
        }
        if (_personSources.Children.Count == 0) _personSources.Children.Add(Label("Not provided", 13));
    }

    private void RenderPersonWorks(PersonDetailsViewModel person)
    {
        foreach (var tab in _personTabButtons)
        {
            var selected = tab.Filter == person.SelectedWorkType;
            var count = tab.Filter switch { "Movie" => person.MovieWorkCount, "Series" => person.SeriesWorkCount, _ => person.TotalWorkCount };
            tab.Button.IsChecked = selected;
            tab.Label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            tab.Label.Foreground = LumenTheme.Brush(selected ? "Ink" : "Sub");
            tab.Count.Text = PersonCount(count);
            tab.Indicator.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(tab.Button, $"{tab.Label.Text} {tab.Count.Text}");
        }
        _personSortLabel.Text = LumenText.Get(person.SelectedWorkSort switch { "CommunityRating" => "By rating", "SortName" => "By name", _ => "By year" });
        AutomationProperties.SetName(_personSort, _personSortLabel.Text);
        foreach (var option in _personSortButtons)
        {
            option.Check.Opacity = option.Sort == person.SelectedWorkSort ? 1 : 0;
            option.Button.Background = option.Sort == person.SelectedWorkSort ? LumenTheme.Brush("ControlStrong") : new SolidColorBrush(Colors.Transparent);
        }
        _personEmptyWorks.Text = LumenText.Get(person.IsLoadingWorks || !person.HasLoadedWorks ? "Loading..."
            : person.SelectedWorkType == "all" ? "No works are available on this server." : "No works in this category.");
        _personEmptyWorks.Visibility = person.Works.Count == 0 && person.WorksError.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_renderedPage == "person")
            foreach (var card in _personCards) card.SetSubtitleOverride(PersonWorkSubtitle(card.Item, person.Person.Id));
    }

    private void UpdatePersonBiographyToggle()
    {
        var expanded = _personModel?.IsBiographyExpanded == true;
        _personBiographyToggle.Content = LumenText.Get(expanded ? "Show less" : "Read full biography");
        _personBiographyToggle.Visibility = expanded || _personBiographyCanExpand ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_personBiographyToggle, LumenText.Get(expanded ? "Show less about this person" : "Read more about this person"));
    }

    private void PersonChanged(object? sender, PropertyChangedEventArgs args) => QueueRender();
    private void PersonWorksChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueRender();

    private void DetachPerson()
    {
        _personSortFlyout.Hide();
        if (_personModel is not { } person) return;
        person.PropertyChanged -= PersonChanged;
        person.Works.CollectionChanged -= PersonWorksChanged;
        _personModel = null;
    }

    private void UpdatePersonLayout(double width)
    {
        if (!_personBuilt) return;
        _personPage.Margin = new Thickness(_pageMargin, 120, _pageMargin, 64);
        var usable = Math.Max(1, width - _pageMargin * 2);
        var gap = Math.Clamp(width * .04, 28, 56);
        var stacked = usable < 240 + gap + 420;
        _personPage.ColumnSpacing = stacked ? 0 : gap;
        _personPage.RowSpacing = stacked ? gap : 0;
        _personPage.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(240);
        _personPage.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(_personMain, stacked ? 0 : 1);
        Grid.SetRow(_personMain, stacked ? 1 : 0);
        var worksWidth = stacked ? usable : Math.Max(1, usable - 240 - gap);
        var columns = Math.Max(1, (int)Math.Floor((worksWidth + 20) / 160));
        _personCardWidth = Math.Max(88, Math.Floor((worksWidth - 20 * (columns - 1)) / columns * 4) / 4);
        _personWorks.Width = worksWidth;
        _personWorks.HorizontalAlignment = HorizontalAlignment.Left;
        _personGridLayout.MaximumRowsOrColumns = columns;
        _personGridLayout.MinItemWidth = _personCardWidth;
        _personGridLayout.MinItemHeight = _personCardWidth * 1.5 + 48;
        var tabWidth = _personTabButtons.Sum(tab => 64 + tab.Count.Text.Length * 8) + 4;
        var compactToolbar = worksWidth + 14 < tabWidth + 140;
        Grid.SetRow(_personSort, compactToolbar ? 1 : 0);
        Grid.SetColumn(_personSort, compactToolbar ? 0 : 1);
        _personSort.Margin = compactToolbar ? new Thickness(14, 10, 0, 0) : new Thickness(12, 0, 0, 0);
        _personSort.HorizontalAlignment = compactToolbar ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        if (_renderedPage == "person") foreach (var card in _personCards) card.SetCardWidth(_personCardWidth);
    }

    private static void AddPersonFact(Grid grid, string label, FrameworkElement value)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var key = Label(label, 13);
        key.Foreground = LumenTheme.Brush("Muted");
        key.LineHeight = 19.5;
        if (value is TextBlock text) text.LineHeight = 19.5;
        Grid.SetRow(key, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(key);
        grid.Children.Add(value);
    }

    private static void ConfigurePersonTextLink(HyperlinkButton button)
    {
        button.FontFamily = LumenUi.SansFont;
        button.FontSize = 13;
        button.FontWeight = FontWeights.SemiBold;
        button.Foreground = LumenTheme.Brush("Accent");
        button.Padding = new Thickness(0);
        button.MinWidth = 0;
        button.MinHeight = 20;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.UseSystemFocusVisuals = true;
        button.Resources["HyperlinkButtonForeground"] = LumenTheme.Brush("Accent");
        button.Resources["HyperlinkButtonForegroundPointerOver"] = LumenTheme.Brush("Accent");
        button.Resources["HyperlinkButtonForegroundPressed"] = LumenTheme.Brush("Accent");
    }

    private static string PersonCount(int? count) => count?.ToString("N0", CultureInfo.CurrentCulture) ?? "\u2014";

    private static string PersonWorkSubtitle(MediaCardViewModel work, string personId)
    {
        var roles = (work.Item.People ?? []).Where(entry => string.Equals(entry.Id, personId, StringComparison.Ordinal))
            .Select(entry => string.IsNullOrWhiteSpace(entry.Role) ? PersonRoleLabel(entry.Type ?? string.Empty) : entry.Role)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        var role = roles.Length > 0 ? string.Join(" / ", roles) : PersonRoleLabel(work.PersonRole);
        return string.Join(" \u00b7 ", new[] { work.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture), role }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string PersonRoleLabel(string value) => LumenText.Get(value is "GuestStar" or "Guest star" ? "Actor" : value);

    private static string PersonExtensionText(BaseItemDto item, string property)
    {
        if (item.ExtensionData is not { } extension || !extension.TryGetValue(property, out var value)) return string.Empty;
        if (value.ValueKind == JsonValueKind.String) return value.GetString()?.Trim() ?? string.Empty;
        return value.ValueKind == JsonValueKind.Array
            ? string.Join(" / ", value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString()).Where(text => !string.IsNullOrWhiteSpace(text)))
            : string.Empty;
    }

    private static SolidColorBrush PersonAvatarBrush(string identity)
    {
        var hash = identity.Aggregate(0u, (value, character) => unchecked(value * 31 + character));
        var colors = new[] { (R: (byte)102, G: (byte)76, B: (byte)63), (R: (byte)61, G: (byte)92, B: (byte)80), (R: (byte)70, G: (byte)80, B: (byte)111), (R: (byte)96, G: (byte)70, B: (byte)94) };
        var color = colors[hash % (uint)colors.Length];
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, color.R, color.G, color.B));
    }

    private static void RegisterPersonStrings() => LumenText.Register(new Dictionary<string, string>
    {
        ["Profession"] = "\u804c\u4e1a", ["Born"] = "\u51fa\u751f", ["Sources"] = "\u8d44\u6599", ["Not provided"] = "\u672a\u63d0\u4f9b",
        ["By year"] = "\u6309\u5e74\u4efd", ["By rating"] = "\u6309\u8bc4\u5206", ["By name"] = "\u6309\u540d\u79f0",
        ["Read full biography"] = "\u5c55\u5f00\u5168\u6587", ["Retry biography"] = "\u91cd\u8bd5\u52a0\u8f7d\u7b80\u4ecb",
        ["Read more about this person"] = "\u5c55\u5f00\u4eba\u7269\u7b80\u4ecb", ["Show less about this person"] = "\u6536\u8d77\u4eba\u7269\u7b80\u4ecb",
        ["{0} works (Movies {1} / Series {2})"] = "{0} \u90e8\uff08\u7535\u5f71 {1} \u00b7 \u5267\u96c6 {2}\uff09",
        ["No works are available on this server."] = "\u8fd9\u53f0\u670d\u52a1\u5668\u4e0a\u6682\u65e0\u76f8\u5173\u4f5c\u54c1\u3002",
        ["No works in this category."] = "\u8be5\u5206\u7c7b\u6682\u65e0\u76f8\u5173\u4f5c\u54c1\u3002",
        ["The server did not return any new library works. Try again."] = "\u670d\u52a1\u5668\u672a\u8fd4\u56de\u66f4\u591a\u4f5c\u54c1\uff0c\u8bf7\u91cd\u8bd5\u3002",
        ["Your session has expired. Connect again to view this content."] = "\u4f1a\u8bdd\u5df2\u8fc7\u671f\uff0c\u8bf7\u91cd\u65b0\u8fde\u63a5\u4ee5\u67e5\u770b\u5185\u5bb9\u3002",
        ["This information is no longer available on the server."] = "\u670d\u52a1\u5668\u4e0a\u5df2\u65e0\u6b64\u4fe1\u606f\u3002",
        ["Actor"] = "\u6f14\u5458", ["Director"] = "\u5bfc\u6f14", ["Writer"] = "\u7f16\u5267", ["Producer"] = "\u5236\u7247\u4eba",
        ["Composer"] = "\u4f5c\u66f2", ["Cast and crew"] = "\u6f14\u804c\u5458"
    });

    private sealed partial class PersonCardFactory(LumenLibraryView owner) : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            if (args.Data is not MediaCardViewModel item) return new Border();
            var card = owner.MakeCard(item, width: owner._personCardWidth);
            card.SetSubtitleOverride(PersonWorkSubtitle(item, owner._personModel?.Person.Id ?? string.Empty));
            card.Loaded += (_, _) => owner._personCards.Add(card);
            card.Unloaded += (_, _) => owner._personCards.Remove(card);
            return card;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) { }
    }

    private sealed partial class PersonPortraitShadow : IDisposable
    {
        private readonly Border _host;
        private readonly CompositionEllipseGeometry _geometry;
        private readonly CompositionColorBrush _color;
        private readonly CompositionSpriteShape _shape;
        private readonly ShapeVisual _caster;
        private readonly CompositionVisualSurface _surface;
        private readonly CompositionSurfaceBrush _mask;
        private readonly DropShadow _shadow;
        private readonly SpriteVisual _visual;

        public PersonPortraitShadow(Border host)
        {
            _host = host;
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            _geometry = compositor.CreateEllipseGeometry();
            _geometry.Center = new Vector2(120);
            _geometry.Radius = new Vector2(120);
            _color = compositor.CreateColorBrush(Colors.White);
            _shape = compositor.CreateSpriteShape(_geometry);
            _shape.FillBrush = _color;
            _caster = compositor.CreateShapeVisual();
            _caster.Size = new Vector2(240);
            _caster.Shapes.Add(_shape);
            _surface = compositor.CreateVisualSurface();
            _surface.SourceVisual = _caster;
            _surface.SourceSize = new Vector2(240);
            _mask = compositor.CreateSurfaceBrush(_surface);
            _shadow = compositor.CreateDropShadow();
            _shadow.Color = Colors.Black;
            _shadow.BlurRadius = 60;
            _shadow.Opacity = .35f;
            _shadow.Offset = new Vector3(0, 24, 0);
            _shadow.Mask = _mask;
            _visual = compositor.CreateSpriteVisual();
            _visual.Size = new Vector2(240);
            _visual.Shadow = _shadow;
            ElementCompositionPreview.SetElementChildVisual(host, _visual);
        }

        public void Dispose()
        {
            ElementCompositionPreview.SetElementChildVisual(_host, null);
            _visual.Shadow = null;
            _shadow.Mask = null;
            _mask.Surface = null;
            _surface.SourceVisual = null;
            _caster.Shapes.Clear();
            _visual.Dispose();
            _shadow.Dispose();
            _mask.Dispose();
            _surface.Dispose();
            _caster.Dispose();
            _shape.Dispose();
            _color.Dispose();
            _geometry.Dispose();
        }
    }
}
