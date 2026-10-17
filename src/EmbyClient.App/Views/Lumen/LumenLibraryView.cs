using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView : UserControl
{
    private readonly Grid _root = new();
    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        IsTabStop = true
    };
    private readonly StackPanel _page = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Grid _ambience = new() { Height = 640, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly LumenAmbientArtwork _ambienceArtwork = new();
    private readonly ProgressBar _loading = new() { Height = 2, IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 84, 0, 0) };
    private readonly Border _notice = new() { CornerRadius = new CornerRadius(16), Margin = new Thickness(56, 96, 56, 0), Padding = new Thickness(18, 14, 14, 14), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _noticeText = LumenUi.Text(string.Empty, 13);
    private readonly StackPanel _empty = new() { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 560, Margin = new Thickness(32, 160, 32, 48) };
    private readonly TextBlock _emptyTitle = LumenUi.Text(string.Empty, 28, true);
    private readonly TextBlock _emptyMessage = LumenUi.Text(string.Empty, 14);
    private readonly Button _emptyRefresh = LumenUi.Button("Refresh", "refresh-cw");
    private readonly LumenDetailView _detail;
    private readonly HashSet<MediaShelfViewModel> _observedShelves = [];
    private readonly HashSet<LumenMediaCard> _realizedCards = [];
    private ConnectedSession? _session;
    private LumenPreferences _preferences = new();
    private string _renderedPage = string.Empty;
    private string? _ambienceId;
    private string? _lastInvokedId;
    private bool _renderLoaded;
    private bool _renderEnabled;
    private long _renderEpoch;
    private bool _renderQueued;
    private bool _restorePending;
    private bool _autoPageBlocked;
    private bool _lastHeaderScrolled;
    private int _sessionGeneration;
    private double _pageMargin = 56;

    public LumenLibraryView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetAutomationId(this, "LumenLibrary");
        AutomationProperties.SetAutomationId(_scroll, "LumenBrowseScroller");
        _detail = new LumenDetailView(ViewModel);
        _detail.PlayRequested += (_, args) => ForwardPlay(args);
        _detail.ItemRequested += async (_, item) => await OpenItemAsync(item);
        _detail.PersonRequested += async (_, person) => await OpenPersonAsync(person);
        _scroll.Content = _page;
        _ambience.Children.Add(_ambienceArtwork);
        _ambience.Children.Add(new Border { Background = BackgroundFade() });
        _root.Children.Add(_ambience);
        _root.Children.Add(_scroll);
        _root.Children.Add(_detail);
        _root.Children.Add(_empty);
        _root.Children.Add(_loading);
        var noticeContent = new Grid { ColumnSpacing = 14 };
        noticeContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        noticeContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _noticeText.TextWrapping = TextWrapping.Wrap;
        noticeContent.Children.Add(_noticeText);
        var retry = LumenUi.IconButton("refresh-cw", "Retry", 36);
        Grid.SetColumn(retry, 1);
        retry.Click += async (_, _) => await RefreshAsync();
        noticeContent.Children.Add(retry);
        _notice.Child = noticeContent;
        _root.Children.Add(_notice);
        _emptyTitle.TextAlignment = TextAlignment.Center;
        _emptyMessage.TextAlignment = TextAlignment.Center;
        _emptyMessage.TextWrapping = TextWrapping.Wrap;
        _empty.Children.Add(_emptyTitle);
        _empty.Children.Add(_emptyMessage);
        _empty.Children.Add(_emptyRefresh);
        _emptyRefresh.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyRefresh.Click += async (_, _) => await RefreshAsync();
        Content = _root;
        BuildHome();
        BuildWall();
        BuildSearch();
        ViewModel.PropertyChanged += ViewModelChanged;
        ViewModel.Items.CollectionChanged += ItemsChanged;
        ViewModel.HomeRows.CollectionChanged += HomeRowsChanged;
        ViewModel.Libraries.CollectionChanged += (_, _) => QueueRender();
        ViewModel.SearchPeople.CollectionChanged += (_, _) => QueueRender();
        ViewModel.SessionExpired += (_, args) => SessionExpired?.Invoke(this, args);
        ViewModel.MediaLibrarySelectionChanged += (_, args) => MediaLibrarySelectionChanged?.Invoke(this, args);
        ViewModel.BrowseStateCapturing += CaptureViewport;
        ViewModel.BrowseStateRestored += RestoreViewport;
        _scroll.ViewChanged += ScrollChanged;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        _scroll.SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += (_, _) =>
        {
            _renderLoaded = true;
            ActivateRenderLifecycle();
            SubscribeHomeAnimationSettings();
            QueueRender();
            UpdateHomeAnimationPreference();
        };
        Unloaded += (_, _) =>
        {
            _renderLoaded = false;
            UnsubscribeHomeAnimationSettings();
            InvalidateRenderLifecycle();
            _heroTimer.Stop();
            _homeBackgroundFallbackTimer.Stop();
            foreach (var shelf in _homeShelves) shelf.Stop();
            DetachPerson();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (_renderEnabled && _renderLoaded) UpdateHeroTimer();
        });
        KeyDown += LibraryKeyDown;
        ApplyPreferences(_preferences);
        RenderPage();
    }

    public LibraryViewModel ViewModel { get; } = new();
    public event EventHandler<LumenPlayRequestEventArgs>? PlayRequested;
    public event EventHandler? SessionExpired;
    public event EventHandler? NavigationStateChanged;
    public event EventHandler<LumenPreferences>? PreferencesChanged;
    public event EventHandler<MediaLibrarySelectionChangedEventArgs>? MediaLibrarySelectionChanged;
    public string ActiveNavigation { get; private set; } = "home";
    public bool IsDetail => ViewModel.HasDetails || ViewModel.IsPerson;
    public bool CanGoBack => ViewModel.CanGoBack;
    public bool CanGoForward => ViewModel.CanGoForward;
    public string WindowTitle
    {
        get
        {
            if (ViewModel.HasDetails || ViewModel.IsPerson) return "Lumen \u00b7 " + ViewModel.Title;
            var pageTitle = ViewModel.IsHome || ViewModel.IsHomeSection ? "Home" : ViewModel.IsSearch ? "Search"
                : ViewModel.IsFavorites ? "Favorites" : ViewModel.BrowseMediaType == "Movie" ? "Movies"
                : ViewModel.BrowseMediaType == "Series" ? "TV shows" : ViewModel.Title;
            var title = "Lumen \u00b7 " + LumenText.Get(pageTitle);
            if (!ViewModel.IsHome && !ViewModel.IsFavorites && !ViewModel.IsSearch
                && ViewModel.SelectedLibraryId is { } libraryId
                && ViewModel.Libraries.FirstOrDefault(library => library.Id == libraryId) is { } library)
                title += " \u00b7 " + library.Title;
            return title;
        }
    }
    public bool IsHeaderScrolled => _renderedPage is "wall" or "search" or "person" && _scroll.VerticalOffset > 8;
    public bool HandleNavigationKey(KeyRoutedEventArgs args) => ViewModel.HasDetails
        ? _detail.HandleNavigationKey(args) : HandleHomeNavigationKey(args);

    public async Task SetSessionAsync(ConnectedSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ClearSession();
        var generation = _sessionGeneration;
        _session = session;
        ResetLibraryCounts(true);
        _detail.SetSession(session);
        ActivateRenderLifecycle();
        QueueRender();
        await ViewModel.SetSessionAsync(session.Api, session.Server.Id ?? session.AccountKey, session.User, cancellationToken);
        if (generation != _sessionGeneration) return;
        QueueRender();
    }

    public void ClearSession()
    {
        InvalidateRenderLifecycle();
        _sessionGeneration++;
        _session = null;
        ResetLibraryCounts(false);
        ResetCatalogPresentation();
        DetachSearchCards();
        _searchViewports.Clear();
        ClearHome();
        _ambienceId = null;
        _ambienceArtwork.Clear();
        DetachPerson();
        _detail.Clear();
        ViewModel.ClearSession();
        ActiveNavigation = "home";
        _renderedPage = string.Empty;
        _autoPageBlocked = false;
        _searchTab = "all";
        _searchInputVersion++;
        _searchTabVersion++;
        _searchTypeOperation = Task.CompletedTask;
        _searchMedia.Clear();
        _searchSummarySignature = string.Empty;
        _searchTypeTotals.Clear();
        _searchCountQuery = string.Empty;
        _collectionPreviews.Clear();
        _genreSignature = string.Empty;
        _genreNavigationRevision = -1;
        _personWorks.ItemsSource = null;
        _personPortrait.Image.Source = null;
        SetSearchDraft(string.Empty);
        CloseBrowseFlyouts();
        QueueRender();
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task NavigateAsync(string key)
    {
        if (_session is null) return;
        CloseBrowseFlyouts();
        _autoPageBlocked = false;
        if (key.StartsWith("library:", StringComparison.Ordinal))
        {
            var libraryId = key["library:".Length..];
            var library = ViewModel.Libraries.FirstOrDefault(item => item.Id == libraryId);
            if (library is null) return;
            ActiveNavigation = LibraryViewModel.PrimaryNavigationForLibrary(library.Item.CollectionType,
                ViewModel.BrowseMediaType) ?? key;
            await ViewModel.ShowLibraryAsync(library);
            QueueRender();
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (key == ActiveNavigation && ViewModel.IsPrimaryNavigationPage(key))
        {
            if (key == "home") _homeDeck.GoToPage(0, false);
            else _scroll.ChangeView(null, 0, null, false);
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        ActiveNavigation = key;
        switch (key)
        {
            case "movies": await ViewModel.ShowMediaTypeAsync("Movie"); break;
            case "series": await ViewModel.ShowMediaTypeAsync("Series"); break;
            case "favs": await ViewModel.ShowFavoritesAsync(); break;
            case "search":
                if (!ViewModel.IsSearch) _searchTab = "all";
                await ViewModel.ShowSearchAsync();
                break;
            default:
                ActiveNavigation = "home";
                _homeDeck.GoToPage(0, false);
                await ViewModel.ShowHomeAsync();
                break;
        }
        QueueRender();
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        if (key == "search") RequestSearchInputFocus();
    }

    public async Task NavigateLatestMediaAsync(string type)
    {
        if (_session is null) return;
        CloseBrowseFlyouts();
        _autoPageBlocked = false;
        ActiveNavigation = type == "Series" ? "series" : "movies";
        await ViewModel.ShowLatestMediaTypeAsync(type);
        QueueRender();
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task NavigateBackAsync()
    {
        CloseBrowseFlyouts();
        await ViewModel.GoBackAsync();
        QueueRender();
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task NavigateForwardAsync()
    {
        CloseBrowseFlyouts();
        await ViewModel.GoForwardAsync();
        QueueRender();
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ResetLibraryCounts(_session is not null);
        _autoPageBlocked = false;
        await ViewModel.RefreshAsync(cancellationToken);
        await ViewModel.ReconcileMediaLibrarySelectionAsync(cancellationToken);
        QueueRender();
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RefreshHomeShelvesAsync(CancellationToken cancellationToken = default)
    {
        var generation = _sessionGeneration;
        ResetLibraryCounts(_session is not null);
        var refreshed = await ViewModel.RefreshHomeShelvesAsync(cancellationToken);
        if (generation != _sessionGeneration) return false;
        QueueRender();
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        return refreshed;
    }

    public void FocusCurrentDetails()
    {
        if (ViewModel.HasDetails) _detail.FocusPrimaryAction();
        else if (ViewModel.IsPerson) _scroll.Focus(FocusState.Programmatic);
        else if (_lastInvokedId is { } itemId && FindItemButton(ViewModel.IsHome ? _home : _page, itemId) is { } button) button.Focus(FocusState.Programmatic);
        else if (ViewModel.IsHome) _homeDeck.Focus(FocusState.Programmatic);
        else _scroll.Focus(FocusState.Programmatic);
    }

    public void ApplyPreferences(LumenPreferences preferences)
    {
        _preferences = preferences;
        _root.Background = LumenTheme.Brush("Background");
        _notice.Background = LumenTheme.Brush("Pop");
        _notice.BorderBrush = LumenTheme.Brush("LineStrong");
        _notice.BorderThickness = new Thickness(1);
        _noticeText.Foreground = LumenTheme.Brush("Ink");
        _emptyTitle.Foreground = LumenTheme.Brush("Ink");
        _emptyMessage.Foreground = LumenTheme.Brush("Sub");
        _ambience.Children[1].SetValue(Border.BackgroundProperty, BackgroundFade());
        _ambienceArtwork.ApplyTheme();
        _detail.ApplyPreferences(preferences);
        ApplyHomeTheme();
        ApplyWallTheme();
        ApplySearchTheme();
        foreach (var card in _realizedCards) card.SetShowWatched(preferences.ShowWatchedMarks);
        UpdateResponsiveLayout();
        UpdateHeroTimer();
        QueueRender();
    }
}
