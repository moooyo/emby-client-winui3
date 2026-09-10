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

    internal PlaybackQueueDialog(TransientPlaybackQueue queue)
    {
        _queue = queue;
        _lastAnnouncedCount = queue.Count;
        InitializeComponent();
        QueueList.ItemsSource = queue.Items;
        _queue.Changed += QueueChanged;
        Closed += (_, _) => _queue.Changed -= QueueChanged;
        Opened += (_, _) =>
        {
            if (_queue.Count > 0 && QueueList.SelectedItem is null) QueueList.SelectedIndex = 0;
        };
        UpdateControls();
    }

    internal void Detach() => _queue.Changed -= QueueChanged;

    private void QueueChanged(object? sender, EventArgs args)
    {
        UpdateControls();
        if (_lastAnnouncedCount == _queue.Count) return;
        _lastAnnouncedCount = _queue.Count;
        (FrameworkElementAutomationPeer.FromElement(CountText)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(CountText))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void QueueSelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateControls();

    private void UpdateControls()
    {
        CountText.Text = $"{_queue.Count} of {TransientPlaybackQueue.MaximumItems} items";
        EmptyText.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = _queue.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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

    private void QueueContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        AutomationProperties.SetName(args.ItemContainer,
            !args.InRecycleQueue && args.Item is PlaybackQueueEntry entry ? entry.AutomationName : string.Empty);
}
