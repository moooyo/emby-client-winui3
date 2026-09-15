using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;
using WinRT;

namespace EmbyClient.App.Views;

public sealed partial class PersonDetailsDialog : ContentDialog
{
    private CancellationTokenRegistration _sessionRegistration;
    private readonly UISettings _displaySettings = new();
    private bool _textScaleSubscribed;

    internal PersonDetailsDialog(PersonDetailsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        AutomationProperties.SetName(this, $"About {ViewModel.Person.Name}");
        Opened += Dialog_Opened;
        Closed += (_, _) => Detach();
    }

    public PersonDetailsViewModel ViewModel { get; }
    public MediaCardViewModel? SelectedWork { get; private set; }

    private async void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (XamlRoot is { } root)
        {
            UpdateSize(root.Size.Width, root.Size.Height);
            root.Changed += XamlRoot_Changed;
        }
        _sessionRegistration = ViewModel.LifetimeToken.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded) Hide();
        }));
        try
        {
            _displaySettings.TextScaleFactorChanged += TextScaleFactorChanged;
            _textScaleSubscribed = true;
        }
        catch (System.Runtime.InteropServices.COMException) { }
        UpdateTextScale();
        await ViewModel.LoadAsync();
    }

    internal void Detach()
    {
        if (XamlRoot is { } root) root.Changed -= XamlRoot_Changed;
        if (_textScaleSubscribed)
        {
            try { _displaySettings.TextScaleFactorChanged -= TextScaleFactorChanged; }
            catch (System.Runtime.InteropServices.COMException) { }
            _textScaleSubscribed = false;
        }
        _sessionRegistration.Dispose();
        ViewModel.Dispose();
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateSize(sender.Size.Width, sender.Size.Height);

    private void UpdateSize(double width, double height)
    {
        DialogContent.Width = Math.Clamp(width - 112, 220, 680);
        ContentScroller.MaxHeight = Math.Clamp(height - 220, 160, 720);
        UpdateTextScale();
    }

    private void TextScaleFactorChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (_textScaleSubscribed) UpdateTextScale(); });

    private void WorksGrid_Loaded(object sender, RoutedEventArgs args) => UpdateTextScale();

    private void UpdateTextScale()
    {
        if (WorksGrid.ItemsPanelRoot is not { } panelRoot) return;
        var panel = panelRoot.As<ItemsWrapGrid>();
        try { panel.ItemHeight = 272 + 72 * (Math.Max(1, _displaySettings.TextScaleFactor) - 1); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private async void RetryBiography_Click(object sender, RoutedEventArgs args) => await ViewModel.LoadBiographyAsync();
    private async void LoadMore_Click(object sender, RoutedEventArgs args) => await ViewModel.LoadMoreAsync();

    private void Work_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not MediaCardViewModel work) return;
        SelectedWork = work;
        Hide();
    }

    private void Artwork_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is PersonArtwork artwork) artwork.SetImageLoader(ViewModel.LoadArtworkAsync);
    }

    private void Work_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var card = args.InRecycleQueue ? null : args.Item as MediaCardViewModel;
        AutomationProperties.SetName(args.ItemContainer, card is null ? string.Empty : $"{card.Title}, {card.CardSubtitle}. Open details.");
        ToolTipService.SetToolTip(args.ItemContainer, card?.Title);
    }
}
