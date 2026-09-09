using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmbyClient.App.Views;

public sealed partial class PlaybackQueueDialog : ContentDialog
{
    private readonly TransientPlaybackQueue _queue;

    internal PlaybackQueueDialog(TransientPlaybackQueue queue)
    {
        _queue = queue;
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

    private void QueueChanged(object? sender, EventArgs args) => UpdateControls();
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
        if (!(up ? _queue.MoveUp(entry.EntryId) : _queue.MoveDown(entry.EntryId))) return;
        QueueList.SelectedItem = entry;
        QueueList.ScrollIntoView(entry);
        UpdateControls();
    }

    private void RemoveClicked(object sender, RoutedEventArgs args) => RemoveSelected();

    private bool RemoveSelected()
    {
        if (QueueList.SelectedItem is not PlaybackQueueEntry entry) return false;
        var index = _queue.IndexOf(entry.EntryId);
        if (!_queue.Remove(entry.EntryId)) return false;
        QueueList.SelectedIndex = Math.Min(index, _queue.Count - 1);
        UpdateControls();
        return true;
    }

    private void ClearClicked(object sender, RoutedEventArgs args)
    {
        _queue.Clear();
        QueueList.SelectedItem = null;
        UpdateControls();
    }

    private void QueueKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Delete && RemoveSelected()) args.Handled = true;
    }

    private void QueueContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        AutomationProperties.SetName(args.ItemContainer,
            !args.InRecycleQueue && args.Item is PlaybackQueueEntry entry ? entry.AutomationName : string.Empty);
}
