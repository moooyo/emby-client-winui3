using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using Windows.UI.ViewManagement;

namespace EmbyClient.App.Views;

public sealed partial class MediaInformationSection : UserControl
{
    private readonly UISettings _displaySettings = new();
    private bool _textScaleSubscribed;
    private MediaInformationViewModel? _model;
    private string? _itemId;
    private readonly Dictionary<string, MediaInformationViewModel> _history = new(StringComparer.Ordinal);

    public MediaInformationSection()
    {
        InitializeComponent();
        Loaded += Section_Loaded;
        Unloaded += Section_Unloaded;
    }

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item),
        typeof(MediaCardViewModel), typeof(MediaInformationSection), new PropertyMetadata(null, ItemChanged));

    public MediaCardViewModel? Item
    {
        get => (MediaCardViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public MediaInformationViewModel? Model => _model;

    private static void ItemChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MediaInformationSection)sender).UpdateModel();

    private void UpdateModel()
    {
        if (_model is not null) _model.PropertyChanged -= Model_PropertyChanged;
        if (_model is not null && !string.IsNullOrWhiteSpace(_itemId))
        {
            _history.Remove(_itemId);
            _history[_itemId] = _model;
            while (_history.Count > 32) _history.Remove(_history.Keys.First());
        }
        _model = Item?.MediaInformation;
        _itemId = Item?.Id;
        if (_model is not null && _itemId is not null && _history.TryGetValue(_itemId, out var previous) && !ReferenceEquals(previous, _model))
            _model.RestoreDisclosureState(previous);
        if (_model is not null && IsLoaded) _model.PropertyChanged += Model_PropertyChanged;
        Bindings.Update();
        UpdateLayoutColumns();
    }

    public void ClearDisclosureState()
    {
        _history.Clear();
        _itemId = null;
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MediaInformationViewModel.SelectedSource) or nameof(MediaInformationViewModel.Visibility)) UpdateLayoutColumns();
    }

    private void SourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Model is { } model && SourceSelector.SelectedItem is MediaSourceViewModel selected) model.SelectedSource = selected;
        UpdateLayoutColumns();
    }

    private void CardsGrid_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayoutColumns();

    private void UpdateLayoutColumns()
    {
        if (CardsGrid is null) return;
        var scale = 1d;
        try { scale = Math.Max(1, _displaySettings.TextScaleFactor); }
        catch (System.Runtime.InteropServices.COMException) { }
        var count = Math.Clamp((int)((CardsGrid.ActualWidth + 16) / (260 * scale + 16)), 1, 3);
        for (var index = 0; index < 3; index++) CardsGrid.ColumnDefinitions[index].Width = index < count ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        var source = Model?.SelectedSource;
        var groups = new[] { (Card: VideoCard, Group: source?.Video), (Card: AudioCard, Group: source?.Audio), (Card: SubtitlesCard, Group: source?.Subtitles) }
            .Where(pair => pair.Group?.Tracks.Count > 0).Select(pair => pair.Card).ToArray();
        for (var index = 0; index < groups.Length; index++)
        {
            Grid.SetRow(groups[index], index / count);
            Grid.SetColumn(groups[index], index % count);
        }
    }

    private void Section_Loaded(object sender, RoutedEventArgs args)
    {
        if (!_textScaleSubscribed)
        {
            try
            {
                _displaySettings.TextScaleFactorChanged += TextScaleFactorChanged;
                _textScaleSubscribed = true;
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        UpdateModel();
    }

    private void Section_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_model is not null) _model.PropertyChanged -= Model_PropertyChanged;
        if (!_textScaleSubscribed) return;
        try { _displaySettings.TextScaleFactorChanged -= TextScaleFactorChanged; }
        catch (System.Runtime.InteropServices.COMException) { }
        _textScaleSubscribed = false;
    }

    private void TextScaleFactorChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (_textScaleSubscribed) UpdateLayoutColumns(); });
}
