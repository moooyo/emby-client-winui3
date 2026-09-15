using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.UI.ViewManagement;
using WinRT;

namespace EmbyClient.App.Views;

public sealed partial class PersonInvokedEventArgs(PersonCardViewModel person) : EventArgs
{
    public PersonCardViewModel Person { get; } = person;
}

public sealed partial class CastSection : UserControl
{
    private const double CastItemSlotWidth = 140; // The 132-DIP container plus its 8-DIP trailing margin.
    private IReadOnlyList<PersonCardViewModel> _people = [];
    private PersonInfo[] _peopleMetadata = [];
    private readonly UISettings _displaySettings = new();
    private bool _expanded;
    private bool _textScaleSubscribed;
    private bool _updatingCastLayout;
    private bool _castLayoutPending;
    private ItemsWrapGrid? _castGridPanel;
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
        var metadata = Item?.Item.People ?? [];
        if (!_peopleMetadata.SequenceEqual(metadata))
        {
            _peopleMetadata = metadata.ToArray();
            _people = Item is { } item ? PersonCardViewModel.FromItem(item.Item) : [];
        }
        UpdatePeople();
    }

    private void UpdatePeople()
    {
        EmptyText.Visibility = _people.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeopleGrid.Visibility = _people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var panel = (ItemsPanelTemplate)Resources[_expanded ? "CastGridPanel" : "CastRowPanel"];
        if (!ReferenceEquals(PeopleGrid.ItemsPanel, panel)) PeopleGrid.ItemsPanel = panel;
        ScrollViewer.SetHorizontalScrollMode(PeopleGrid, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(PeopleGrid, ScrollBarVisibility.Disabled);
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

    private void CastGridPanel_Loaded(object sender, RoutedEventArgs args)
    {
        _castGridPanel = sender.As<ItemsWrapGrid>();
        UpdateTextScale();
    }

    private void CastGridPanel_Unloaded(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(_castGridPanel, sender.As<ItemsWrapGrid>())) _castGridPanel = null;
    }

    private void UpdateTextScale()
    {
        if (_updatingCastLayout)
        {
            _castLayoutPending = true;
            return;
        }
        _updatingCastLayout = true;
        try
        {
            var scale = 1d;
            try { scale = Math.Max(1, _displaySettings.TextScaleFactor); }
            catch (System.Runtime.InteropServices.COMException) { }
            var width = Math.Max(0, PeopleGrid.ActualWidth - PeopleGrid.Padding.Left - PeopleGrid.Padding.Right);
            var columns = (int)Math.Floor(width / CastItemSlotWidth);
            var collapsedCount = Math.Min(_people.Count, columns);
            ReconcileVisiblePeople(_expanded ? _people.Count : collapsedCount);
            ExpandButton.Visibility = _people.Count > 0 && (_expanded || _people.Count > collapsedCount)
                ? Visibility.Visible : Visibility.Collapsed;
            ExpandButton.Content = _expanded ? "Show less" : $"View all ({_people.Count})";
            AutomationProperties.SetName(ExpandButton, _expanded ? "Show cast and crew in one row" : $"View all {_people.Count} cast and crew in a grid");
            var height = 176 + 80 * (scale - 1);
            // The collapsed row uses ItemsStackPanel; only resize the loaded expanded grid.
            if (_expanded && _castGridPanel is { IsLoaded: true } panel) panel.ItemHeight = height;
            var rows = Math.Max(1, (int)Math.Ceiling(_people.Count / (double)Math.Max(1, columns)));
            PeopleGrid.Height = _expanded ? Math.Min(rows * height + 16, 448 + 160 * (scale - 1)) : height + 16;
        }
        finally
        {
            _updatingCastLayout = false;
            if (_castLayoutPending)
            {
                _castLayoutPending = false;
                DispatcherQueue.TryEnqueue(() => { if (IsLoaded) UpdateTextScale(); });
            }
        }
    }

    private void ReconcileVisiblePeople(int count)
    {
        while (VisiblePeople.Count > count) VisiblePeople.RemoveAt(VisiblePeople.Count - 1);
        for (var index = 0; index < count; index++)
        {
            if (index == VisiblePeople.Count) VisiblePeople.Add(_people[index]);
            else if (!ReferenceEquals(VisiblePeople[index], _people[index])) VisiblePeople[index] = _people[index];
        }
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
