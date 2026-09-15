using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.UI.ViewManagement;

namespace EmbyClient.App.Views;

public sealed partial class PersonInvokedEventArgs(PersonCardViewModel person) : EventArgs
{
    public PersonCardViewModel Person { get; } = person;
}

public sealed partial class CastSection : UserControl
{
    private IReadOnlyList<PersonCardViewModel> _people = [];
    private readonly UISettings _displaySettings = new();
    private bool _expanded;
    private bool _textScaleSubscribed;
    private LibraryViewModel? _library;
    public ObservableCollection<PersonCardViewModel> VisiblePeople { get; } = [];
    internal LibraryViewModel? Library
    {
        get => _library;
        set
        {
            _library = value;
            if (value is not null) AttachArtwork(this, value);
        }
    }

    public CastSection()
    {
        InitializeComponent();
        Loaded += Section_Loaded;
        Unloaded += Section_Unloaded;
    }

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item),
        typeof(MediaCardViewModel), typeof(CastSection), new PropertyMetadata(null, ItemChanged));
    public MediaCardViewModel? Item
    {
        get => (MediaCardViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public event EventHandler<PersonInvokedEventArgs>? PersonInvoked;

    private static void ItemChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var section = (CastSection)sender;
        if (args.OldValue is MediaCardViewModel previous) previous.PropertyChanged -= section.Item_PropertyChanged;
        if (section.IsLoaded && section.Item is { } current) current.PropertyChanged += section.Item_PropertyChanged;
        section._expanded = false;
        section.RefreshPeople();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MediaCardViewModel.Item) or null or "") RefreshPeople();
    }

    private void RefreshPeople()
    {
        _people = Item is { } item ? PersonCardViewModel.FromItem(item.Item) : [];
        UpdatePeople();
    }

    private void UpdatePeople()
    {
        EmptyText.Visibility = _people.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeopleGrid.Visibility = _people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VisiblePeople.Clear();
        foreach (var person in _people) VisiblePeople.Add(person);
        ExpandButton.Visibility = _people.Count > 4 ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Content = _expanded ? "Show less" : $"View all ({_people.Count})";
        AutomationProperties.SetName(ExpandButton, _expanded ? "Show cast and crew in one row" : $"View all {_people.Count} cast and crew in a grid");
        var panel = (ItemsPanelTemplate)Resources[_expanded ? "CastGridPanel" : "CastRowPanel"];
        if (!ReferenceEquals(PeopleGrid.ItemsPanel, panel)) PeopleGrid.ItemsPanel = panel;
        ScrollViewer.SetHorizontalScrollMode(PeopleGrid, _expanded ? ScrollMode.Disabled : ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(PeopleGrid, _expanded ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollMode(PeopleGrid, _expanded ? ScrollMode.Enabled : ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(PeopleGrid, _expanded ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        UpdateTextScale();
    }

    private void Expand_Click(object sender, RoutedEventArgs args)
    {
        _expanded = !_expanded;
        UpdatePeople();
    }

    private void People_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is PersonCardViewModel { CanOpen: true } person) PersonInvoked?.Invoke(this, new(person));
    }

    private void People_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        AutomationProperties.SetName(args.ItemContainer,
            !args.InRecycleQueue && args.Item is PersonCardViewModel person ? person.AutomationName : string.Empty);
        ToolTipService.SetToolTip(args.ItemContainer,
            !args.InRecycleQueue && args.Item is PersonCardViewModel card ? $"{card.Name}\n{card.Role}" : null);
    }

    private void Artwork_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is PersonArtwork artwork && Library is { } library) artwork.SetImageLoader(library.LoadPosterAsync);
    }

    private void Section_Loaded(object sender, RoutedEventArgs args)
    {
        if (Item is { } item)
        {
            item.PropertyChanged -= Item_PropertyChanged;
            item.PropertyChanged += Item_PropertyChanged;
        }
        if (!_textScaleSubscribed)
        {
            try
            {
                _displaySettings.TextScaleFactorChanged += TextScaleFactorChanged;
                _textScaleSubscribed = true;
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        RefreshPeople();
        UpdateTextScale();
    }

    private void Section_Unloaded(object sender, RoutedEventArgs args)
    {
        if (Item is { } item) item.PropertyChanged -= Item_PropertyChanged;
        if (!_textScaleSubscribed) return;
        try { _displaySettings.TextScaleFactorChanged -= TextScaleFactorChanged; }
        catch (System.Runtime.InteropServices.COMException) { }
        _textScaleSubscribed = false;
    }

    private void TextScaleFactorChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (_textScaleSubscribed) UpdateTextScale(); });

    private void PeopleGrid_Loaded(object sender, RoutedEventArgs args) => UpdateTextScale();
    private void PeopleGrid_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateTextScale();

    private void UpdateTextScale()
    {
        var scale = 1d;
        try { scale = Math.Max(1, _displaySettings.TextScaleFactor); }
        catch (System.Runtime.InteropServices.COMException) { }
        var height = 176 + 80 * (scale - 1);
        if (PeopleGrid.ItemsPanelRoot is ItemsWrapGrid panel) panel.ItemHeight = height;
        var columns = Math.Max(1, (int)(PeopleGrid.ActualWidth / 140));
        var rows = Math.Max(1, (int)Math.Ceiling(_people.Count / (double)columns));
        PeopleGrid.Height = _expanded ? Math.Min(rows * height + 16, 448 + 160 * (scale - 1)) : height + 16;
    }

    private static void AttachArtwork(DependencyObject parent, LibraryViewModel library)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is PersonArtwork artwork) artwork.SetImageLoader(library.LoadPosterAsync);
            else AttachArtwork(child, library);
        }
    }
}
