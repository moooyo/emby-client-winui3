using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Globalization;
using Windows.System;
using Windows.UI.Core;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private FrameworkElement MakeListRow(MediaCardViewModel item)
    {
        var row = new Grid { MinHeight = 84, ColumnSpacing = 12, Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var main = new Grid { ColumnSpacing = 16 };
        foreach (var width in new[] { new GridLength(48), new GridLength(2, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(48), new GridLength(112), new GridLength(52), new GridLength(52) })
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var art = new Grid { Width = 48, Height = 72, CornerRadius = new CornerRadius(6) };
        var image = new LumenArtwork { Width = 48, Height = 72, CornerRadius = new CornerRadius(6) };
        image.Set(item, ViewModel, ArtworkKind.Poster, 160);
        art.Children.Add(image);
        var fill = new Border { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = LumenTheme.Brush("ImageAccent") };
        var track = new Border { Height = 3, Background = LumenTheme.Brush("Track"), VerticalAlignment = VerticalAlignment.Bottom, Child = fill };
        art.Children.Add(track);
        main.Children.Add(art);
        var information = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = LumenUi.Text(item.DisplayTitle, 15);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        titleRow.Children.Add(title);
        var unwatchedNumber = LumenUi.Text(item.UnplayedCountLabel, 11);
        unwatchedNumber.Foreground = LumenTheme.Brush("ImageAccentInk");
        var unwatched = new Border { MinWidth = 20, Height = 20, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(10), Background = LumenTheme.Brush("ImageAccent"), Child = unwatchedNumber };
        Grid.SetColumn(unwatched, 1);
        titleRow.Children.Add(unwatched);
        information.Children.Add(titleRow);
        var original = LumenUi.Text(item.Item.OriginalTitle ?? string.Empty, 12);
        original.Foreground = LumenTheme.Brush("Muted");
        original.TextWrapping = TextWrapping.NoWrap;
        original.TextTrimming = TextTrimming.CharacterEllipsis;
        information.Children.Add(original);
        Grid.SetColumn(information, 1);
        main.Children.Add(information);
        TextBlock Metadata(string value, int column)
        {
            var text = LumenUi.Text(value, 13);
            text.Foreground = LumenTheme.Brush("Sub");
            text.VerticalAlignment = VerticalAlignment.Center;
            text.TextWrapping = TextWrapping.NoWrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(text, column);
            main.Children.Add(text);
            return text;
        }
        var genres = Metadata(string.Join(" / ", item.Item.Genres ?? []), 2);
        var year = Metadata(item.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "\u2014", 3);
        var length = Metadata(item.Item.Type == "Series" && (item.Item.SeasonCount ?? item.Item.ChildCount) is { } seasons
            ? LumenText.Get("{0} seasons", seasons)
            : item.Item.RunTimeTicks is > 0 ? LumenText.Get("{0} min", Math.Round(TimeSpan.FromTicks(item.Item.RunTimeTicks.Value).TotalMinutes)) : "\u2014", 4);
        var rating = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        rating.Children.Add(LumenUi.Icon("star", 13));
        rating.Children.Add(LumenUi.Text(item.Item.CommunityRating?.ToString("0.0", CultureInfo.InvariantCulture) ?? "\u2014", 13));
        Grid.SetColumn(rating, 5);
        main.Children.Add(rating);
        var qualityText = LumenUi.Text(CatalogQualityLabel(item), 10);
        qualityText.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        qualityText.Foreground = LumenTheme.Brush("Sub");
        var quality = new Border { Child = qualityText, CornerRadius = new CornerRadius(4), BorderBrush = LumenTheme.Brush("LineStrong"), BorderThickness = new Thickness(1), Padding = new Thickness(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(quality, 6);
        main.Children.Add(quality);
        var open = new Button
        {
            Content = main, Tag = item, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0), UseSystemFocusVisuals = true, CornerRadius = new CornerRadius(8)
        };
        open.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        open.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Colors.Transparent);
        AutomationProperties.SetName(open, item.AutomationLabel);
        open.Click += async (_, _) => await OpenItemAsync(item);
        row.Children.Add(open);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var watched = LumenUi.IconButton("check", "Mark watched", 34);
        var favorite = LumenUi.IconButton("heart", "Add favorite", 34);
        var play = LumenUi.IconButton("play", "Play", 34);
        foreach (var button in new[] { watched, favorite, play })
        {
            button.IsTabStop = false;
            button.BorderThickness = new Thickness(0);
            button.Background = new SolidColorBrush(Colors.Transparent);
            actions.Children.Add(button);
        }
        play.Background = LumenTheme.Brush("Ink");
        play.Content = new Microsoft.UI.Xaml.Controls.Image
        {
            Width = 14, Height = 14,
            Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri($"ms-appx:///Assets/Lumen/Icons/{(LumenTheme.IsDark ? "d" : "w")}/play_24_filled.svg"))
        };
        play.IsEnabled = ViewModel.IsPlaybackAllowed;
        play.Visibility = item.CanPlay || item.Item.Type is "Series" or "Season" ? Visibility.Visible : Visibility.Collapsed;
        watched.Click += async (_, _) => await ViewModel.ToggleItemPlayedAsync(item);
        favorite.Click += async (_, _) => await ViewModel.ToggleItemFavoriteAsync(item);
        play.Click += (_, _) => ForwardPlay(new(item.Item, item.CanResume ? item.ResumeTicks : 0));
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        void RefreshState()
        {
            title.Text = item.DisplayTitle;
            original.Text = item.Item.OriginalTitle ?? string.Empty;
            unwatchedNumber.Text = item.UnplayedCountLabel;
            unwatched.Visibility = item.UnplayedCountLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            track.Visibility = item.ProgressVisibility;
            fill.Width = 48 * Math.Clamp(item.Progress, 0, 100) / 100;
            watched.Content = LumenUi.Icon(item.IsPlayed ? "check-filled" : "check", 16);
            favorite.Content = LumenUi.Icon(item.IsFavorite ? "heart-filled" : "heart", 16);
            AutomationProperties.SetName(watched, LumenText.Get(item.IsPlayed ? "Mark unwatched" : "Mark watched"));
            AutomationProperties.SetName(favorite, LumenText.Get(item.IsFavorite ? "Remove favorite" : "Add favorite"));
            quality.Visibility = qualityText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        PropertyChangedEventHandler changed = (_, args) => { if (args.PropertyName is null or "" or nameof(MediaCardViewModel.Item)) RefreshState(); };
        row.Loaded += (_, _) => { item.PropertyChanged += changed; RefreshState(); };
        row.Unloaded += (_, _) => item.PropertyChanged -= changed;
        row.SizeChanged += (_, args) =>
        {
            var full = args.NewSize.Width >= 960;
            var medium = args.NewSize.Width >= 750;
            genres.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
            length.Visibility = medium ? Visibility.Visible : Visibility.Collapsed;
            quality.Visibility = medium && qualityText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            main.ColumnDefinitions[2].Width = full ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            main.ColumnDefinitions[4].Width = medium ? new GridLength(112) : new GridLength(0);
            main.ColumnDefinitions[6].Width = medium ? new GridLength(52) : new GridLength(0);
        };
        var pointer = false;
        row.PointerEntered += (_, _) => { pointer = true; row.Background = LumenTheme.Brush("Hover"); SetAmbience(item); };
        row.PointerExited += (_, _) => { pointer = false; row.Background = new SolidColorBrush(Colors.Transparent); };
        row.GotFocus += (_, _) => { row.Background = LumenTheme.Brush("Hover"); SetAmbience(item); };
        row.LostFocus += (_, _) => { if (!pointer) row.Background = new SolidColorBrush(Colors.Transparent); };
        MenuFlyout? menu = null;
        void ShowMenu()
        {
            menu?.Hide();
            menu = CreateListContextMenu(item);
            menu.ShowAt(open);
        }
        row.RightTapped += (_, args) => { ShowMenu(); args.Handled = true; };
        row.Unloaded += (_, _) => menu?.Hide();
        open.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Space)
            {
                args.Handled = true;
                ForwardPlay(new(item.Item, item.CanResume ? item.ResumeTicks : 0));
            }
            else if (args.Key == VirtualKey.Application || args.Key == VirtualKey.F10
                && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0)
            {
                args.Handled = true;
                ShowMenu();
            }
            else if (args.Key == VirtualKey.Escape) { args.Handled = true; _scroll.Focus(FocusState.Programmatic); }
        };
        open.PreviewKeyUp += (_, args) => { if (args.Key == VirtualKey.Space) args.Handled = true; };
        RefreshState();
        return row;
    }

    private MenuFlyout CreateListContextMenu(MediaCardViewModel item)
    {
        var flyout = new MenuFlyout();
        var presenter = new Style(typeof(MenuFlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.BackgroundProperty, LumenTheme.Brush("Pop")));
        presenter.Setters.Add(new Setter(Control.ForegroundProperty, LumenTheme.Brush("Ink")));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(16)));
        flyout.MenuFlyoutPresenterStyle = presenter;
        void Add(string label, Symbol symbol, Func<Task> action, bool enabled = true)
        {
            var command = new MenuFlyoutItem { Text = LumenText.Get(label), Icon = new SymbolIcon(symbol), IsEnabled = enabled };
            command.Click += async (_, _) => await action();
            flyout.Items.Add(command);
        }
        Add("Details", Symbol.View, () => OpenItemAsync(item));
        if (item.CanPlay || item.Item.Type is "Series" or "Season")
        {
            Add(item.CanResume ? "Resume" : "Play", Symbol.Play, () => { ForwardPlay(new(item.Item, item.CanResume ? item.ResumeTicks : 0)); return Task.CompletedTask; }, ViewModel.IsPlaybackAllowed);
            if (item.CanResume) Add("Play from beginning", Symbol.Previous, () => { ForwardPlay(new(item.Item)); return Task.CompletedTask; }, ViewModel.IsPlaybackAllowed);
            Add("Add to queue", Symbol.Add, () => { ForwardPlay(new(item.Item, 0, true)); return Task.CompletedTask; }, ViewModel.IsPlaybackAllowed);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add(item.IsPlayed ? "Mark unwatched" : "Mark watched", Symbol.Accept, () => ViewModel.ToggleItemPlayedAsync(item));
        Add(item.IsFavorite ? "Remove favorite" : "Add favorite", Symbol.Favorite, () => ViewModel.ToggleItemFavoriteAsync(item));
        if (item.Item.Type != "BoxSet") Add("Add to collection", Symbol.Add, () => _detail.AddItemToCollectionAsync(item));
        Add("Media information", Symbol.Document, () => OpenItemMediaInformationAsync(item));
        return flyout;
    }

    private static string CatalogQualityLabel(MediaCardViewModel item)
    {
        var streams = item.Item.MediaStreams ?? item.Item.MediaSources?.SelectMany(source => source.MediaStreams ?? []).ToArray();
        var video = streams?.FirstOrDefault(stream => stream.Type == "Video");
        return video?.Width is >= 3840 || video?.Height is >= 1800 ? "4K"
            : video?.Width is >= 1900 and <= 2100 ? "1080p"
            : video?.Height is > 0 ? $"{video.Height}p" : string.Empty;
    }
}
