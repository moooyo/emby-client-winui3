using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView : UserControl
{
    private readonly LibraryViewModel _library;
    private readonly ScrollViewer _scroll;
    private readonly Grid _surface;
    private readonly Grid _backdropLayer;
    private readonly LumenArtwork _backdrop;
    private readonly Border _bottomFade;
    private readonly StackPanel _content;
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
    private Grid? _mediaTiles;
    private double? _pendingScrollOffset;
    private bool _showWatchedMarks = true;

    public LumenDetailView(LibraryViewModel library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _surface = new Grid { Background = LumenTheme.Brush("Background") };
        _backdrop = new LumenArtwork { CornerRadius = new CornerRadius(0), IsHitTestVisible = false };
        _backdrop.SetCoverFocalPoint(.5, .4);
        _backdropLayer = new Grid { Height = 800, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        _backdropLayer.Children.Add(_backdrop);
        _backdropLayer.Children.Add(new Border
        {
            Background = Gradient(new Point(0, 0), new Point(1, 0),
                (0, Color.FromArgb(235, 10, 8, 6)), (.32, Color.FromArgb(178, 10, 8, 6)),
                (.64, Color.FromArgb(38, 10, 8, 6)), (1, Color.FromArgb(10, 10, 8, 6)))
        });
        _backdropLayer.Children.Add(new Border
        {
            Background = Gradient(new Point(0, 0), new Point(0, 1),
                (0, Color.FromArgb(128, 0, 0, 0)), (.18, Color.FromArgb(0, 0, 0, 0)), (1, Color.FromArgb(0, 0, 0, 0)))
        });
        _bottomFade = new Border();
        _backdropLayer.Children.Add(_bottomFade);
        _surface.Children.Add(_backdropLayer);
        _content = new StackPanel { Margin = new Thickness(56, 118, 56, 56), Spacing = 32 };
        _hero = new StackPanel { MaxWidth = 680, MinHeight = 586, HorizontalAlignment = HorizontalAlignment.Left };
        _notice = LumenUi.Text(string.Empty, 13);
        _notice.TextWrapping = TextWrapping.Wrap;
        _notice.Visibility = Visibility.Collapsed;
        _childrenSection = new StackPanel { Spacing = 14, Visibility = Visibility.Collapsed };
        _castSection = new StackPanel { Spacing = 16, Visibility = Visibility.Collapsed };
        _mediaSection = new StackPanel { Spacing = 14, Visibility = Visibility.Collapsed };
        _similarSection = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        _content.Children.Add(_hero);
        _content.Children.Add(_notice);
        _content.Children.Add(_childrenSection);
        _content.Children.Add(_castSection);
        _content.Children.Add(_mediaSection);
        _content.Children.Add(_similarSection);
        _surface.Children.Add(_content);
        _scroll = new ScrollViewer
        {
            Content = _surface, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsTabStop = false
        };
        Content = _scroll;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += (_, _) => { UpdateResponsiveLayout(); RestoreScrollOffset(); };
        UpdateBackdropFade();
    }

    public event EventHandler<LumenPlayRequestEventArgs>? PlayRequested;
    public event EventHandler<MediaCardViewModel>? ItemRequested;
    public event EventHandler<MediaCardViewModel>? PersonRequested;

    public double ScrollOffset
    {
        get => _scroll.VerticalOffset;
        set
        {
            _pendingScrollOffset = Math.Max(0, value);
            DispatcherQueue.TryEnqueue(RestoreScrollOffset);
        }
    }

    public void FocusPrimaryAction() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!IsLoaded || Visibility != Visibility.Visible || !_library.HasDetails || _activeDialog is not null) return;
        if (_playButton?.IsEnabled == true) _playButton.Focus(FocusState.Programmatic);
        else _moreButton?.Focus(FocusState.Programmatic);
    });

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
        _surface.Background = LumenTheme.Brush("Background");
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
        _detailVersion++;
        _supplementaryRequestVersion++;
        _activeDialog?.Hide();
        _activeDialog = null;
        var previous = _detailCancellation;
        _detailCancellation = null;
        previous?.Cancel();
        previous?.Dispose();
        _detailId = null;
        _renderedItem = null;
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
        _forceRender = true;
        ResetStreamSelection();
        _backdrop.Set(new MediaCardViewModel(new BaseItemDto()), _library, ArtworkKind.Backdrop);
        _hero.Children.Clear();
        foreach (var section in new[] { _childrenSection, _castSection, _mediaSection, _similarSection })
        {
            section.Children.Clear();
            section.Visibility = Visibility.Collapsed;
        }
        _notice.Text = string.Empty;
        _notice.Visibility = Visibility.Collapsed;
        _pendingScrollOffset = null;
        _scroll.ChangeView(null, 0, null, true);
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
        _backdrop.SetCoverFocalPoint(.5, series ? .35 : .4);
        var newItem = !string.Equals(_detailId, detail.Id, StringComparison.Ordinal);
        if (newItem)
        {
            var restoreOffset = _pendingScrollOffset;
            ResetDetail();
            _pendingScrollOffset = restoreOffset;
            _detailId = detail.Id;
            _detailCancellation = new CancellationTokenSource();
            _supplementaryLoading = _session is not null;
            _supplementaryRevision++;
            _backdrop.Set(detail, _library, ArtworkKind.Backdrop, 1600);
        }
        var childrenChanged = _forceRender || !_renderedChildren.SequenceEqual(_library.Items.Select(card => card.Item))
            || !_renderedSeasons.SequenceEqual(_library.Seasons.Select(card => card.Item))
            || _renderedSeasonId != _library.SelectedSeason?.Id || _renderedBusy != _library.IsBusy
            || _renderedNextEpisodeId != _library.NextEpisode?.Id;
        var metadataChanged = _forceRender || !ReferenceEquals(_renderedItem, detail.Item)
            || !ReferenceEquals(_renderedPlayable, _library.PlayableDetail.Item);
        _backdropLayer.Height = series ? 560 : 800;
        _hero.MinHeight = series ? 350 : detail.IsFolder ? 438 : 586;
        _content.Margin = new Thickness(ActualWidth is > 0 and < 1000 ? 28 : 56, series ? 106 : 118,
            ActualWidth is > 0 and < 1000 ? 28 : 56, 56);
        if (metadataChanged)
        {
            UpdateStreamSelection(detail.Item);
            RenderHero(detail, series);
            RenderCast(detail);
            RenderMediaInformation(detail);
        }
        if (childrenChanged) RenderChildren(detail);
        if (_forceRender || metadataChanged || _renderedSupplementaryRevision != _supplementaryRevision) RenderSimilarItems(detail);
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
        if (newItem)
        {
            LumenUi.AnimateEntrance(_content);
            if (_session is not null && _detailCancellation is { } lifetime) _ = LoadSupplementaryAsync(detail.Id, _detailVersion, lifetime.Token);
        }
    }

    private void SetNotice(string message, bool isError = false)
    {
        _notice.Text = message;
        _notice.Foreground = LumenTheme.Brush(isError ? "Danger" : "Sub");
        _notice.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite);
    }

    private bool IsCurrentDetail(string itemId, int version) => _session is not null && _detailVersion == version
        && _detailId == itemId && _library.HasDetails && _library.Detail.Id == itemId
        && _detailCancellation?.IsCancellationRequested == false;
}
