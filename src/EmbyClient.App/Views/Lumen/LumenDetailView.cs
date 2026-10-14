using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView : UserControl
{
    private readonly LibraryViewModel _library;
    private readonly LumenPageDeck _deck;
    private readonly Grid _surface;
    private readonly Grid _backdropLayer;
    private readonly LumenArtwork _backdrop;
    private readonly LumenArtwork _fallbackPoster;
    private readonly Border _backdropBlur;
    private readonly Border _horizontalFade;
    private readonly Border _bottomFade;
    private readonly Border _episodeLinearShade;
    private readonly Border _detailPageShade;
    private readonly Grid _episodeRadialShade;
    private readonly Grid _detailAccentGlow;
    private readonly Border _episodeStripShade;
    private readonly Border _episodeStripDim;
    private readonly Border _interactionDim;
    private readonly Grid _overviewPage;
    private readonly Grid _overviewFrame;
    private readonly ScrollViewer _overviewScroll;
    private readonly Grid _episodePage;
    private readonly Grid _episodeFrame;
    private readonly Grid _castPage;
    private readonly Grid _castFrame;
    private readonly StackPanel _castContent;
    private readonly Grid _mediaPage;
    private readonly Grid _mediaFrame;
    private readonly Grid _floatingStage;
    private readonly StackPanel _hero;
    private readonly TextBlock _notice;
    private readonly StackPanel _childrenSection;
    private readonly StackPanel _castSection;
    private readonly StackPanel _mediaSection;
    private readonly StackPanel _similarSection;
    private ConnectedSession? _session;
    private CancellationTokenSource? _detailCancellation;
    private string? _detailId;
    private int _detailVersion;
    private bool _refreshQueued;
    private bool _forceRender;
    private bool _actionBusy;
    private bool _supplementaryLoading;
    private bool _similarFailed;
    private int _supplementaryRevision;
    private int _renderedSupplementaryRevision = -1;
    private BaseItemDto? _renderedItem;
    private BaseItemDto? _renderedPlayable;
    private BaseItemDto[] _renderedChildren = [];
    private BaseItemDto[] _renderedSeasons = [];
    private string? _renderedSeasonId;
    private string? _renderedNextEpisodeId;
    private bool _renderedBusy;
    private List<BaseItemDto> _localTrailers = [];
    private List<MediaCardViewModel> _similarItems = [];
    private Button? _playButton;
    private Button? _trailerButton;
    private Button? _favoriteButton;
    private Button? _watchedButton;
    private Button? _moreButton;
    private ScrollViewer? _episodeScroll;
    private ScrollViewer? _castScroll;
    private ScrollViewer? _similarScroll;
    private double? _pendingScrollOffset;
    private int? _pendingPageIndex;
    private bool _showWatchedMarks = true;
    private string? _pageConfiguration;
    private int _episodePageIndex = -1;
    private bool _descriptionExpanded;
    private TextBlock? _heroTitle;
    private TextBlock? _heroDescription;
    private TextBlock? _estimatedFinish;
    private FrameworkElement? _heroActionDivider;
    private Point? _stagePointer;
    private StackPanel? _floatingLabels;
    private bool _updatingResponsiveLayout;
    private bool _hasOpenDetailFlyout;
    private readonly ScaleTransform _floatingScale = new();

    public LumenDetailView(LibraryViewModel library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _library.BrowseStateRestored += (_, _) => RestoreEpisodeSelectionIntent();
        _surface = new Grid { Background = new SolidColorBrush(Color.FromArgb(255, 10, 8, 7)) };
        _backdrop = new LumenArtwork { CornerRadius = new CornerRadius(0), IsHitTestVisible = false };
        _backdrop.SetCoverFocalPoint(.5, .35);
        _backdropLayer = new Grid { IsHitTestVisible = false };
        CreateDetailBackdropLayers();
        _backdropBlur = new Border
        {
            Background = new AcrylicBrush
            {
                TintColor = Color.FromArgb(255, 10, 10, 11), TintOpacity = 0,
                TintLuminosityOpacity = 0, FallbackColor = Color.FromArgb(255, 24, 22, 20),
                TintTransitionDuration = TimeSpan.Zero
            },
            Opacity = 0
        };
        _backdropLayer.Children.Add(_backdropBlur);
        _horizontalFade = new Border
        {
            Background = Gradient(new Point(0, 0), new Point(1, 0),
                (0, Color.FromArgb(242, 10, 8, 6)), (.32, Color.FromArgb(209, 10, 8, 6)),
                (.62, Color.FromArgb(117, 10, 8, 6)), (.84, Color.FromArgb(36, 10, 8, 6)), (1, Color.FromArgb(0, 10, 8, 6)))
        };
        _backdropLayer.Children.Add(_horizontalFade);
        _bottomFade = new Border
        {
            Background = Gradient(new Point(0, 0), new Point(0, 1), (0, Color.FromArgb(0, 10, 8, 6)),
                (.42, Color.FromArgb(0, 10, 8, 6)), (.74, Color.FromArgb(148, 10, 8, 6)), (1, Color.FromArgb(240, 10, 8, 6)))
        };
        _backdropLayer.Children.Add(_bottomFade);
        _episodeLinearShade = new Border
        {
            Opacity = 0, Background = Gradient(new Point(0, 0), new Point(0, 1), (0, Color.FromArgb(0, 8, 8, 9)),
                (.34, Color.FromArgb(0, 8, 8, 9)), (.46, Color.FromArgb(36, 8, 8, 9)),
                (.70, Color.FromArgb(143, 8, 8, 9)), (1, Color.FromArgb(219, 8, 8, 9)))
        };
        _backdropLayer.Children.Add(_episodeLinearShade);
        _episodeRadialShade = new Grid { IsHitTestVisible = false, Opacity = 0 };
        _backdropLayer.Children.Add(_episodeRadialShade);
        _episodeStripShade = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Opacity = 0,
            Background = Gradient(new Point(0, 0), new Point(0, 1),
                (0, Color.FromArgb(0, 8, 8, 9)), (.66, Color.FromArgb(128, 8, 8, 9)), (1, Color.FromArgb(230, 8, 8, 9)))
        };
        _backdropLayer.Children.Add(_episodeStripShade);
        _detailPageShade = new Border
        {
            Opacity = 0, Background = Gradient(new Point(0, 0), new Point(0, 1),
                (0, Color.FromArgb(153, 10, 10, 11)), (1, Color.FromArgb(189, 10, 10, 11)))
        };
        _backdropLayer.Children.Add(_detailPageShade);
        _episodeStripDim = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 8, 8, 10)), IsHitTestVisible = false, Opacity = 0
        };
        _backdropLayer.Children.Add(_episodeStripDim);
        _interactionDim = new Border { Background = new SolidColorBrush(Color.FromArgb(255, 8, 8, 10)), Opacity = 0 };
        _backdropLayer.Children.Add(_interactionDim);
        _detailAccentGlow = new Grid
        {
            IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = 0
        };
        _backdropLayer.Children.Add(_detailAccentGlow);
        _backdropLayer.Children.Add(new Border
        {
            Height = 140, VerticalAlignment = VerticalAlignment.Top,
            Background = Gradient(new Point(0, 0), new Point(0, 1),
                (0, Color.FromArgb(128, 0, 0, 0)), (1, Color.FromArgb(0, 0, 0, 0)))
        });
        _surface.Children.Add(_backdropLayer);
        _hero = new StackPanel { MaxWidth = 1040, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Opacity = .66 };
        _notice = ImageText(string.Empty, 13);
        _notice.TextWrapping = TextWrapping.Wrap;
        _notice.Visibility = Visibility.Collapsed;
        _notice.HorizontalAlignment = HorizontalAlignment.Center;
        _notice.VerticalAlignment = VerticalAlignment.Bottom;
        _notice.Margin = new Thickness(24, 0, 24, 24);
        _childrenSection = new StackPanel { Spacing = 14, Visibility = Visibility.Collapsed };
        _castSection = new StackPanel { Spacing = 16, Visibility = Visibility.Collapsed };
        _mediaSection = new StackPanel { Spacing = 14, Visibility = Visibility.Collapsed };
        _similarSection = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        _overviewFrame = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _overviewFrame.Children.Add(_hero);
        _overviewScroll = CreatePageScroll(_overviewFrame);
        _overviewPage = new Grid();
        _fallbackPoster = new LumenArtwork
        {
            CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed
        };
        _overviewPage.Children.Add(_fallbackPoster);
        _overviewPage.Children.Add(_overviewScroll);
        _overviewPage.Tapped += (_, args) => HandleArtworkTap(args, _hero);
        _episodeFrame = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _childrenSection.VerticalAlignment = VerticalAlignment.Bottom;
        _episodeFrame.Children.Add(_childrenSection);
        _episodePage = new Grid();
        _episodePage.Children.Add(CreatePageScroll(_episodeFrame));
        _episodePage.Tapped += (_, args) => HandleArtworkTap(args, _childrenSection);
        _castContent = new StackPanel { Spacing = 40, VerticalAlignment = VerticalAlignment.Center };
        _castContent.Children.Add(_castSection);
        _castContent.Children.Add(_similarSection);
        _castFrame = new Grid();
        _castFrame.Children.Add(_castContent);
        _castPage = new Grid();
        _castPage.Children.Add(CreatePageScroll(_castFrame));
        _mediaFrame = new Grid();
        _mediaSection.VerticalAlignment = VerticalAlignment.Center;
        _mediaFrame.Children.Add(_mediaSection);
        _mediaPage = new Grid();
        _mediaPage.Children.Add(CreatePageScroll(_mediaFrame));
        _deck = new LumenPageDeck();
        _deck.PageChanged += (_, index) => OnDetailPageChanged(index);
        _surface.Children.Add(_deck);
        _floatingStage = new Grid { Width = 360, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
        _floatingStage.RenderTransform = _floatingScale;
        _floatingStage.PointerWheelChanged += (_, args) =>
        {
            if (_activeDialog is null && !_hasOpenDetailFlyout) _deck.HandleVerticalWheel(args);
        };
        _surface.Children.Add(_floatingStage);
        _surface.Children.Add(_notice);
        _surface.PointerMoved += (_, args) => { _stagePointer = args.GetCurrentPoint(_surface).Position; UpdateStageInteraction(); };
        _surface.PointerExited += (_, args) =>
        {
            var point = args.GetCurrentPoint(_surface).Position;
            if (point.X < 0 || point.Y < 0 || point.X >= ActualWidth || point.Y >= ActualHeight)
            { _stagePointer = null; UpdateStageInteraction(); }
        };
        _surface.GotFocus += (_, _) => UpdateStageInteraction();
        _surface.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(UpdateStageInteraction);
        Content = _surface;
        KeyDown += (_, args) => HandleNavigationKey(args);
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += (_, _) => { UpdateResponsiveLayout(); RestoreScrollOffset(); };
        Loaded += (_, _) => BuildDetailRadialShades();
        Unloaded += (_, _) => ReleaseDetailRadialShades();
        UpdateBackdropFade();
    }

    public event EventHandler<LumenPlayRequestEventArgs>? PlayRequested;
    public event EventHandler<MediaCardViewModel>? ItemRequested;
    public event EventHandler<MediaCardViewModel>? PersonRequested;

    public double ScrollOffset
    {
        get => _deck.SelectedIndex * Math.Max(1, _deck.ViewportHeight);
        set
        {
            _pendingScrollOffset = Math.Max(0, value);
            DispatcherQueue.TryEnqueue(RestoreScrollOffset);
        }
    }

    public bool HandleNavigationKey(KeyRoutedEventArgs args)
    {
        if (_activeDialog is not null || _hasOpenDetailFlyout) return false;
        if (_deck.SelectedIndex == _episodePageIndex && HandleEpisodeNavigationKey(args)) return true;
        return _deck.HandleNavigationKey(args);
    }

    public int SelectedPageIndex
    {
        get => _deck.SelectedIndex;
        set
        {
            _pendingPageIndex = Math.Max(0, value);
            DispatcherQueue.TryEnqueue(RestoreScrollOffset);
        }
    }

    public void FocusPrimaryAction() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!IsLoaded || Visibility != Visibility.Visible || !_library.HasDetails || _activeDialog is not null) return;
        if (_deck.SelectedIndex != 0 && _deck.SelectedIndex != _episodePageIndex)
        {
            if (!FocusFirstDetailControl(_deck.SelectedIndex == (_episodePageIndex >= 0 ? 2 : 1) ? _castPage : _mediaPage))
                FocusFirstDetailControl(_deck);
            return;
        }
        var episode = _deck.SelectedIndex == _episodePageIndex;
        var currentEpisode = episode ? _episodeCards.FirstOrDefault(card => card.Episode.Id == _focusedEpisode?.Id)?.Open : null;
        if (episode && _episodeStripExpanded && currentEpisode?.Focus(FocusState.Programmatic) == true) return;
        if (_floatingStage.Visibility == Visibility.Visible && _playButton?.IsEnabled == true
            && _playButton.Focus(FocusState.Programmatic)) return;
        if (!episode && _moreButton?.Focus(FocusState.Programmatic) == true) return;
        if (currentEpisode?.Focus(FocusState.Programmatic) == true) return;
        FocusFirstDetailControl(episode ? _episodePage : _overviewPage);
    });

    private static bool FocusFirstDetailControl(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Control { IsEnabled: true, IsTabStop: true, Visibility: Visibility.Visible } control
                && control.Focus(FocusState.Programmatic)) return true;
            if (FocusFirstDetailControl(child)) return true;
        }
        return false;
    }

    public void SetSession(ConnectedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_session?.Api, session.Api)) Clear();
        _session = session;
        _forceRender = true;
        Refresh();
    }

    public void ApplyPreferences(LumenPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var subtitlePreferencesChanged = _preferences.SubtitleLanguage != preferences.SubtitleLanguage
            || _preferences.SubtitleMode != preferences.SubtitleMode;
        _preferences = preferences;
        if (subtitlePreferencesChanged && _sources.Length > 0) _selectedSubtitleStreamIndex = DefaultSubtitleIndex();
        _showWatchedMarks = preferences.ShowWatchedMarks;
        _forceRender = true;
        UpdateBackdropFade();
        Refresh();
    }

    public void Refresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() => { _refreshQueued = false; RenderDetails(); })) _refreshQueued = false;
    }

    public void Clear()
    {
        ResetDetail();
        _session = null;
    }

    private void ResetDetail()
    {
        ClearSubtitleTimelines();
        ClearMediaThumbnailMetadata();
        _detailVersion++;
        _supplementaryRequestVersion++;
        _activeDialog?.Hide();
        _activeDialog = null;
        _itemActionCancellation?.Cancel();
        _itemActionCancellation = null;
        var previous = _detailCancellation;
        _detailCancellation = null;
        previous?.Cancel();
        previous?.Dispose();
        _detailId = null;
        _renderedItem = null;
        _backdropItemId = null;
        _backdropReference = null;
        _backdropIsBlurred = false;
        _renderedPlayable = null;
        _renderedChildren = [];
        _renderedSeasons = [];
        _renderedSeasonId = null;
        _renderedNextEpisodeId = null;
        _localTrailers.Clear();
        _similarItems.Clear();
        _supplementaryLoading = false;
        _similarFailed = false;
        _localTrailerFailed = false;
        _actionBusy = false;
        _hasOpenDetailFlyout = false;
        _forceRender = true;
        _descriptionExpanded = false;
        _pageConfiguration = null;
        _episodePageIndex = -1;
        _episodeInitialFocusId = null;
        ResetEpisodeScreen();
        ResetStreamSelection();
        ClearDetailBackdrops();
        _fallbackPoster.Visibility = Visibility.Collapsed;
        _hero.Children.Clear();
        foreach (var section in new[] { _childrenSection, _castSection, _mediaSection, _similarSection })
        {
            section.Children.Clear();
            section.Visibility = Visibility.Collapsed;
        }
        _notice.Text = string.Empty;
        _notice.Visibility = Visibility.Collapsed;
        _pendingScrollOffset = null;
        _pendingPageIndex = null;
        _deck.GoToPage(0, false);
        _floatingStage.Children.Clear();
        _floatingStage.Visibility = Visibility.Collapsed;
        _playButton = _trailerButton = _favoriteButton = _watchedButton = _moreButton = null;
        _episodeScroll = _castScroll = _similarScroll = null;
    }

    private void RenderDetails()
    {
        if (_session is null) return;
        if (!_library.HasDetails || string.IsNullOrWhiteSpace(_library.Detail.Id))
        {
            if (_detailId is not null) ResetDetail();
            return;
        }
        var detail = _library.Detail;
        var series = detail.Item.Type is "Series" or "Season";
        _backdrop.SetCoverFocalPoint(.5, .35);
        var newItem = !string.Equals(_detailId, detail.Id, StringComparison.Ordinal);
        if (newItem)
        {
            var restoreOffset = _pendingScrollOffset;
            var restorePage = _pendingPageIndex;
            ResetDetail();
            _pendingScrollOffset = restoreOffset;
            _pendingPageIndex = restorePage;
            _detailId = detail.Id;
            _detailCancellation = new CancellationTokenSource();
            _supplementaryLoading = _session is not null;
            _supplementaryRevision++;
            SetDetailBackdrop(detail);
            _fallbackPoster.Set(detail, _library, ArtworkKind.Poster, 520);
        }
        var childrenChanged = _forceRender || !_renderedChildren.SequenceEqual(_library.Items.Select(card => card.Item))
            || !_renderedSeasons.SequenceEqual(_library.Seasons.Select(card => card.Item))
            || _renderedSeasonId != _library.SelectedSeason?.Id || _renderedBusy != _library.IsBusy
            || _renderedNextEpisodeId != _library.NextEpisode?.Id;
        var metadataChanged = _forceRender || !ReferenceEquals(_renderedItem, detail.Item)
            || !ReferenceEquals(_renderedPlayable, _library.PlayableDetail.Item);
        if (metadataChanged)
        {
            UpdateStreamSelection(series ? _library.PlayableDetail.Item : detail.Item);
            RenderHero(detail, series);
            RenderCast(detail);
            RenderMediaInformation(detail);
        }
        if (childrenChanged) RenderChildren(detail);
        if (_forceRender || metadataChanged || _renderedSupplementaryRevision != _supplementaryRevision) RenderSimilarItems(detail);
        ConfigureDetailPages(detail);
        _renderedItem = detail.Item;
        _renderedPlayable = _library.PlayableDetail.Item;
        _renderedChildren = _library.Items.Select(card => card.Item).ToArray();
        _renderedSeasons = _library.Seasons.Select(card => card.Item).ToArray();
        _renderedSeasonId = _library.SelectedSeason?.Id;
        _renderedNextEpisodeId = _library.NextEpisode?.Id;
        _renderedBusy = _library.IsBusy;
        _renderedSupplementaryRevision = _supplementaryRevision;
        _forceRender = false;
        UpdateActionState();
        UpdateResponsiveLayout();
        RestoreScrollOffset();
        EnsureInitialEpisodeSelection();
        if (newItem)
        {
            LumenUi.AnimateEntrance(_overviewFrame);
            if (_session is not null && _detailCancellation is { } lifetime) _ = LoadSupplementaryAsync(detail.Id, _detailVersion, lifetime.Token);
        }
    }

    private void SetNotice(string message, bool isError = false)
    {
        _notice.Text = message;
        _notice.Foreground = LumenTheme.Brush(isError ? "Danger" : "ImageSub");
        _notice.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite);
    }

    private bool IsCurrentDetail(string itemId, int version) => _session is not null && _detailVersion == version
        && _detailId == itemId && _library.HasDetails && _library.Detail.Id == itemId
        && _detailCancellation?.IsCancellationRequested == false;
}
