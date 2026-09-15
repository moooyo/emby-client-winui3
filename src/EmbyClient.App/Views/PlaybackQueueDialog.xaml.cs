using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmbyClient.App.Views;

public sealed partial class PlaybackQueueDialog : ContentDialog
{
    private readonly TransientPlaybackQueue _queue;
    private int _lastAnnouncedCount;
    private XamlRoot? _dialogRoot;

    internal PlaybackQueueDialog(TransientPlaybackQueue queue)
    {
        _queue = queue;
        _lastAnnouncedCount = queue.Count;
        InitializeComponent();
        QueueList.ItemsSource = queue.Items;
        _queue.Changed += QueueChanged;
        Closed += (_, _) => Detach();
        Opened += (_, _) =>
        {
            _dialogRoot = XamlRoot;
            if (_dialogRoot is not null)
            {
                _dialogRoot.Changed += DialogRootChanged;
                UpdateDialogSize();
            }
            if (_queue.Count > 0 && QueueList.SelectedItem is null) QueueList.SelectedIndex = 0;
        };
        UpdateControls();
    }

    internal void Detach()
    {
        _queue.Changed -= QueueChanged;
        if (_dialogRoot is not null) _dialogRoot.Changed -= DialogRootChanged;
        _dialogRoot = null;
    }

    private void DialogRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateDialogSize();

    private void UpdateDialogSize()
    {
        if (_dialogRoot is not { } root) return;
        DialogContent.Width = Math.Clamp(root.Size.Width - 112, 180, 480);
        ContentScroller.MaxHeight = Math.Clamp(root.Size.Height - 240, 96, 480);
    }

    private void QueueChanged(object? sender, EventArgs args)
    {
        UpdateControls();
        // Native collection moves retain selection; refresh position labels after layout catches up.
        DispatcherQueue.TryEnqueue(UpdateRealizedRows);
        if (_lastAnnouncedCount == _queue.Count) return;
        _lastAnnouncedCount = _queue.Count;
        (FrameworkElementAutomationPeer.FromElement(CountText)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(CountText))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void QueueSelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateControls();

    private void UpdateControls()
    {
        CountText.Text = $"{_queue.Count} {(_queue.Count == 1 ? "item" : "items")} queued · Limit {TransientPlaybackQueue.MaximumItems}";
        EmptyState.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = _queue.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        QueueActions.Visibility = _queue.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var index = QueueList.SelectedItem is PlaybackQueueEntry entry ? _queue.IndexOf(entry.EntryId) : -1;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < _queue.Count - 1;
        RemoveButton.IsEnabled = index >= 0;
        ClearButton.IsEnabled = _queue.Count > 0;
    }

    private void MoveUpClicked(object sender, RoutedEventArgs args) => MoveSelected(true);
    private void MoveDownClicked(object sender, RoutedEventArgs args) => MoveSelected(false);

    private void MoveSelected(bool up)
    {
        if (QueueList.SelectedItem is not PlaybackQueueEntry entry) return;
        var button = up ? MoveUpButton : MoveDownButton;
        var buttonHadFocus = button.FocusState != FocusState.Unfocused;
        if (!(up ? _queue.MoveUp(entry.EntryId) : _queue.MoveDown(entry.EntryId))) return;
        QueueList.SelectedItem = entry;
        QueueList.ScrollIntoView(entry);
        UpdateControls();
        if (buttonHadFocus && !button.IsEnabled) FocusSelection();
    }

    private void RemoveClicked(object sender, RoutedEventArgs args) => RemoveSelected();

    private bool RemoveSelected()
    {
        if (QueueList.SelectedItem is not PlaybackQueueEntry entry) return false;
        var index = _queue.IndexOf(entry.EntryId);
        if (!_queue.Remove(entry.EntryId)) return false;
        QueueList.SelectedIndex = Math.Min(index, _queue.Count - 1);
        UpdateControls();
        FocusSelection();
        return true;
    }

    private void ClearClicked(object sender, RoutedEventArgs args)
    {
        _queue.Clear();
        QueueList.SelectedItem = null;
        UpdateControls();
        FocusSelection();
    }

    private void FocusSelection()
    {
        // Restore focus after a selected row or its last available action disappears.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (QueueList.SelectedItem is PlaybackQueueEntry entry)
            {
                QueueList.ScrollIntoView(entry);
                if (QueueList.ContainerFromItem(entry) is Control container)
                    container.Focus(FocusState.Programmatic);
            }
            else if (GetTemplateChild("CloseButton") is Control closeButton)
            {
                closeButton.Focus(FocusState.Programmatic);
            }
        });
    }

    private void QueueKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Delete && RemoveSelected()) args.Handled = true;
    }

    private void QueueContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var entry = args.InRecycleQueue ? null : args.Item as PlaybackQueueEntry;
        UpdateRow(args.ItemContainer, entry);
        if (entry is not null && args.Phase == 0)
            args.RegisterUpdateCallback((_, update) => UpdateRow(update.ItemContainer,
                update.InRecycleQueue ? null : update.Item as PlaybackQueueEntry));
    }

    private void UpdateRealizedRows()
    {
        for (var index = 0; index < _queue.Count; index++)
            if (QueueList.ContainerFromIndex(index) is ListViewItem container)
                UpdateRow(container, _queue.Items[index]);
    }

    private void UpdateRow(ContentControl container, PlaybackQueueEntry? entry)
    {
        var index = entry is null ? -1 : _queue.IndexOf(entry.EntryId);
        var detail = entry?.Detail ?? string.Empty;
        AutomationProperties.SetName(container, entry is null || index < 0 ? string.Empty
            : $"{index + 1} of {_queue.Count}, {entry.Title}, {detail}{(index == 0 ? ", next in queue" : string.Empty)}");
        if (container.ContentTemplateRoot is not FrameworkElement template) return;
        if (template.FindName("OrderText") is TextBlock order)
            order.Text = index < 0 ? string.Empty : (index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        if (template.FindName("NextText") is TextBlock next)
            next.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (template.FindName("DetailText") is TextBlock detailText)
            detailText.Text = detail;
    }

}
