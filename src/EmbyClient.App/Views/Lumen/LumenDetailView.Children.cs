using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private void RenderChildren(MediaCardViewModel detail)
    {
        var previousOffset = _episodeScroll?.HorizontalOffset ?? 0;
        var previousFocusId = _focusedEpisode?.Id;
        ClearEpisodeElements();
        _childrenSection.Children.Clear();
        _childrenSection.Spacing = 0;
        _childrenSection.Visibility = detail.IsFolder || detail.Item.Type == "Episode" && _library.NextEpisode is not null
            ? Visibility.Visible : Visibility.Collapsed;
        if (_childrenSection.Visibility == Visibility.Collapsed)
        {
            ResetEpisodeScreen();
            return;
        }

        var episodes = _library.DetailItemsAreEpisodes || detail.Item.Type == "Episode";
        if (episodes)
        {
            MediaCardViewModel[] items = detail.Item.Type == "Episode"
                ? _library.NextEpisode is { } next ? new[] { next } : []
                : _library.Items.ToArray();
            RenderEpisodeScreen(detail, items, previousFocusId, previousOffset);
        }
        else
        {
            ResetEpisodeScreen();
            _childrenSection.Spacing = 18;
            _childrenSection.Children.Add(SectionHeading("Contents"));
            var grid = new FlowPanel { Spacing = 20, RowSpacing = 30 };
            foreach (var item in _library.Items) grid.Children.Add(CreateMediaCard(item));
            LumenMediaCard.ConfigureNavigationGroup(grid);
            _childrenSection.Children.Add(grid);
            if (_library.HasMore)
            {
                var more = LumenUi.Button(LumenText.Get("Load more"), "chevron-down", onImage: true);
                more.IsEnabled = _library.CanLoadMore;
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.Click += async (_, _) => await _library.LoadMoreAsync();
                _childrenSection.Children.Add(more);
            }
        }

        if (_library.IsBusy)
        {
            var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            loading.Children.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22, Foreground = LumenTheme.Brush("ImageAccent") });
            var text = ImageText(LumenText.Get(episodes ? "Loading episodes..." : "Loading..."), 13, opacity: .7);
            text.VerticalAlignment = VerticalAlignment.Center;
            loading.Children.Add(text);
            loading.Margin = new Thickness(episodes ? EpisodeInset : 0, 14, episodes ? EpisodeInset : 0, 0);
            AutomationProperties.SetLiveSetting(loading, AutomationLiveSetting.Polite);
            _childrenSection.Children.Add(loading);
        }
        else if (_library.Items.Count == 0 && detail.Item.Type != "Episode" && !_library.HasError && _library.LoadOutcome == PageLoadOutcome.Succeeded)
        {
            var empty = ImageText(LumenText.Get(episodes ? "No episodes are available in this season." : "No items are available in this collection."), 14, opacity: .75);
            empty.Margin = new Thickness(episodes ? EpisodeInset : 0, 14, episodes ? EpisodeInset : 0, 0);
            _childrenSection.Children.Add(empty);
        }
        UpdateEpisodeResponsiveLayout();
    }

    private void RequestEpisodePlayback(MediaCardViewModel episode, bool fromBeginning = false)
    {
        if (_actionBusy || _library.IsMutating || !_library.CanUseSeasonActions) return;
        if (episode.CanPlay && _library.IsPlaybackAllowed)
            PlayRequested?.Invoke(this, new LumenPlayRequestEventArgs(episode.Item,
                !fromBeginning && !episode.IsPlayed && episode.CanResume ? episode.ResumeTicks : 0));
        else ItemRequested?.Invoke(this, episode);
    }

    private LumenMediaCard CreateMediaCard(MediaCardViewModel item)
    {
        var card = new LumenMediaCard(item, _library);
        card.SetCardWidth(160);
        card.SetOnImage();
        card.SetShowWatched(_showWatchedMarks);
        card.OpenRequested += (_, selected) => ItemRequested?.Invoke(this, selected);
        card.PlayRequested += (_, request) => { if (_library.IsPlaybackAllowed) PlayRequested?.Invoke(this, request); };
        card.FavoriteRequested += async (_, selected) => await _library.ToggleItemFavoriteAsync(selected);
        card.WatchedRequested += async (_, selected) => await _library.ToggleItemPlayedAsync(selected);
        card.MediaInformationRequested += async (_, selected) => await ShowItemMediaInformationAsync(selected);
        card.CollectionRequested += async (_, selected) => await AddItemToCollectionAsync(selected);
        return card;
    }

    private ScrollViewer HorizontalScroll(UIElement content)
    {
        var scroller = new ScrollViewer
        {
            Content = content, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = false,
            ZoomMode = ZoomMode.Disabled
        };
        content.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
        {
            var properties = args.GetCurrentPoint(scroller).Properties;
            if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0
                || (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0) return;
            if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == 0)
            {
                _deck.HandleVerticalWheel(args);
                return;
            }
            scroller.ChangeView(Math.Clamp(scroller.HorizontalOffset - properties.MouseWheelDelta, 0, scroller.ScrollableWidth), null, null);
            args.Handled = true;
        }), true);
        return scroller;
    }

    private TextBlock SectionHeading(string key)
    {
        var text = ImageText(LumenText.Get(key), Math.Clamp((ActualWidth > 0 ? ActualWidth : 1280) * .022, 24, 32), true);
        text.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        AutomationProperties.SetHeadingLevel(text, AutomationHeadingLevel.Level2);
        return text;
    }
}
