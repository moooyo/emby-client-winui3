using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private void RenderCast(MediaCardViewModel detail)
    {
        var previousOffset = _castScroll?.HorizontalOffset ?? 0;
        _castSection.Children.Clear();
        _castScroll = null;
        var people = (detail.Item.People ?? []).Where(person => !string.IsNullOrWhiteSpace(person.Name))
            .DistinctBy(person => person.Id ?? person.Name, StringComparer.Ordinal).ToArray();
        _castSection.Visibility = Visibility.Visible;
        _castSection.Spacing = 0;
        if (people.Length == 0)
        {
            _castSection.Children.Add(SectionHeading("Cast & crew"));
            var empty = ImageText(LumenText.Get("No cast information is available."), 14, opacity: .65);
            empty.Margin = new Thickness(0, 16, 0, 0);
            _castSection.Children.Add(empty);
            return;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26, Padding = new Thickness(DetailInset, 20, DetailInset, 6) };
        foreach (var person in people)
        {
            var item = new MediaCardViewModel(new BaseItemDto
            {
                Id = person.Id, Name = person.Name, Type = "Person",
                ImageTags = string.IsNullOrWhiteSpace(person.PrimaryImageTag) ? null : new() { ["Primary"] = person.PrimaryImageTag }
            }, person.Role ?? person.Type);
            var labels = new StackPanel { Width = 96 };
            var accent = LumenTheme.Color("ImageAccent");
            var portrait = new Border
            {
                Width = 84, Height = 84, CornerRadius = new CornerRadius(42), HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(41, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(140, accent.R, accent.G, accent.B)), BorderThickness = new Thickness(1)
            };
            var initial = ImageText(StringInfo.GetNextTextElement(person.Name!.Trim()), 30, serif: true);
            initial.FontWeight = Microsoft.UI.Text.FontWeights.Black;
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            initial.VerticalAlignment = VerticalAlignment.Center;
            portrait.Child = initial;
            labels.Children.Add(portrait);
            var name = ImageText(person.Name!, 14, weight: true);
            name.TextAlignment = TextAlignment.Center;
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Margin = new Thickness(0, 10, 0, 0);
            labels.Children.Add(name);
            var role = ImageText(!string.IsNullOrWhiteSpace(person.Role) ? person.Role : LumenText.Get(person.Type ?? "Cast"), 12, opacity: .6);
            role.TextAlignment = TextAlignment.Center;
            role.TextWrapping = TextWrapping.NoWrap;
            role.TextTrimming = TextTrimming.CharacterEllipsis;
            role.Margin = new Thickness(0, 2, 0, 0);
            labels.Children.Add(role);
            var button = new Button
            {
                Content = labels, Tag = item, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(8),
                IsEnabled = !string.IsNullOrWhiteSpace(item.Id), UseSystemFocusVisuals = true
            };
            LumenUi.SetResource(button.Resources, "ButtonBackgroundPointerOver", ImageBrush(0));
            LumenUi.SetResource(button.Resources, "ButtonBackgroundPressed", ImageBrush(0));
            var transform = new TranslateTransform();
            button.RenderTransform = transform;
            button.PointerEntered += (_, _) => transform.Y = -3;
            button.PointerExited += (_, _) => transform.Y = 0;
            button.GotFocus += (_, _) => transform.Y = -3;
            button.LostFocus += (_, _) => transform.Y = 0;
            AutomationProperties.SetName(button, person.Name + (string.IsNullOrWhiteSpace(person.Role) ? string.Empty : ", " + person.Role));
            button.Click += (_, _) => PersonRequested?.Invoke(this, item);
            row.Children.Add(button);
        }
        LumenMediaCard.ConfigureNavigationGroup(row, horizontalOnly: true);
        _castScroll = HorizontalScroll(row);
        _castScroll.Loaded += (_, _) => _castScroll?.ChangeView(previousOffset, null, null, true);
        _castSection.Children.Add(CreateDetailRowHeader("Cast & crew", Format("{0} people", people.Length), _castScroll));
        _castSection.Children.Add(_castScroll);
    }

    private void RenderMediaInformation(MediaCardViewModel detail) => RenderMediaVisualization(detail);

    private void RenderSimilarItems(MediaCardViewModel detail)
    {
        var previousOffset = _similarScroll?.HorizontalOffset ?? 0;
        _similarSection.Children.Clear();
        _similarScroll = null;
        _similarSection.Visibility = Visibility.Visible;
        _similarSection.Spacing = 0;
        const string key = "Similar recommendations";
        if (_similarItems.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Padding = new Thickness(DetailInset, 14, DetailInset, 10) };
            var width = Math.Clamp(Math.Min(Math.Max(640, ActualWidth) * .115, Math.Max(500, ActualHeight) * .19), 104, 196);
            foreach (var item in _similarItems)
            {
                var card = CreateMediaCard(item);
                card.SetRecommendationStyle();
                card.SetCardWidth(width);
                row.Children.Add(card);
            }
            LumenMediaCard.ConfigureNavigationGroup(row, horizontalOnly: true);
            _similarScroll = HorizontalScroll(row);
            _similarScroll.Loaded += (_, _) => _similarScroll?.ChangeView(previousOffset, null, null, true);
            _similarSection.Children.Add(CreateDetailRowHeader(key, null, _similarScroll));
            _similarSection.Children.Add(_similarScroll);
        }
        else _similarSection.Children.Add(SectionHeading(key));
        if (_supplementaryLoading)
        {
            var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 16, 0, 0) };
            loading.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, Foreground = LumenTheme.Brush("ImageAccent") });
            loading.Children.Add(ImageText(LumenText.Get("Loading recommendations..."), 13, opacity: .7));
            _similarSection.Children.Add(loading);
        }
        else if (_similarFailed)
        {
            var error = ImageText(LumenText.Get("Recommendations could not be loaded."), 13, opacity: .7);
            error.Margin = new Thickness(0, 16, 0, 0);
            _similarSection.Children.Add(error);
            var retry = LumenUi.Button(LumenText.Get("Try again"), "refresh", onImage: true);
            retry.HorizontalAlignment = HorizontalAlignment.Left;
            retry.Margin = new Thickness(0, 12, 0, 0);
            retry.Click += async (_, _) =>
            {
                if (_detailId is not null && _detailCancellation is not null)
                    await LoadSupplementaryAsync(_detailId, _detailVersion, _detailCancellation.Token);
            };
            _similarSection.Children.Add(retry);
        }
        else if (_similarItems.Count == 0)
        {
            var empty = ImageText(LumenText.Get("No recommendations are available."), 14, opacity: .65);
            empty.Margin = new Thickness(0, 16, 0, 0);
            _similarSection.Children.Add(empty);
        }
    }

    private Grid CreateDetailRowHeader(string titleKey, string? count, ScrollViewer scroller)
    {
        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = SectionHeading(titleKey);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(count))
        {
            var countText = ImageText(count, 13, opacity: .5);
            countText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(countText, 1);
            header.Children.Add(countText);
        }
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var previous = LumenUi.IconButton("chevron-left", LumenText.Get("Previous items"), 34, true);
        var next = LumenUi.IconButton("chevron-right", LumenText.Get("Next items"), 34, true);
        previous.Click += (_, _) => scroller.ChangeView(Math.Max(0, scroller.HorizontalOffset - scroller.ViewportWidth * .85), null, null);
        next.Click += (_, _) => scroller.ChangeView(Math.Min(scroller.ScrollableWidth, scroller.HorizontalOffset + scroller.ViewportWidth * .85), null, null);
        void UpdateArrows()
        {
            previous.IsEnabled = scroller.HorizontalOffset > 1;
            next.IsEnabled = scroller.HorizontalOffset < scroller.ScrollableWidth - 1;
            previous.Opacity = previous.IsEnabled ? 1 : .28;
            next.Opacity = next.IsEnabled ? 1 : .28;
        }
        scroller.ViewChanged += (_, _) => UpdateArrows();
        scroller.SizeChanged += (_, _) => UpdateArrows();
        scroller.Loaded += (_, _) => UpdateArrows();
        UpdateArrows();
        arrows.Children.Add(previous);
        arrows.Children.Add(next);
        Grid.SetColumn(arrows, 2);
        header.Children.Add(arrows);
        return header;
    }
}
