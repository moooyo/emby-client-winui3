using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private PlaybackContext? _lastPlaybackContext;
    private PlaybackSelection? _retrySettingsDraft;
    private PlaybackSelection? _retrySettingsOriginal;
    private Guid? _retrySettingsRecoveryId;
    private bool _retrySettingsReserved;
    private bool _retrySettingsAutomatic;

    private void PopulateQualityOptions(long selected)
    {
        var updating = _updating;
        _updating = true;
        var limit = _session?.User.Policy?.RemoteClientBitrateLimit;
        if (limit is > 0) selected = Math.Min(selected, limit.Value);
        var values = new List<long> { 1_500_000, 3_000_000, 5_000_000, 10_000_000, 20_000_000, 40_000_000, 80_000_000, 120_000_000, int.MaxValue };
        if (limit is > 0) values.Add(limit.Value);
        values.Add(selected);
        QualitySelector.Items.Clear();
        foreach (var value in values.Where(value => value > 0 && (limit is not > 0 || value <= limit.Value)).Distinct().OrderDescending())
        {
            var label = value == int.MaxValue ? LumenText.Get("Unlimited") : $"{value / 1_000_000d:0.#} Mbps";
            if (value == limit) label += " · " + LumenText.Get("Account maximum");
            var option = new ComboBoxItem { Content = label, Tag = value };
            QualitySelector.Items.Add(option);
            if (value == selected) QualitySelector.SelectedItem = option;
        }
        _updating = updating;
    }

    private void ChangeRetrySettingsClicked(object sender, RoutedEventArgs args)
    {
        if (!BeginRetrySettings()) return;
        OpenSidePanel(PlayerSidePanel.Settings, ChangeRetrySettingsButton);
    }

    private bool BeginRetrySettings()
    {
        var recovery = _coordinator?.Recovery;
        if (_retryInProgress || _coordinator?.Status != PlaybackStatus.Failed || recovery is null
            || _retryIntent != _playIntent || _retryRecoveryId != recovery.RecoveryId) return false;
        if (_retrySettingsRecoveryId == recovery.RecoveryId && _retrySettingsDraft is not null) return true;
        CancelRetrySettings();
        var sourceId = recovery.Selection.MediaSourceId;
        if (sourceId is null && _lastPlaybackContext?.Selection.ItemId == recovery.Selection.ItemId)
            sourceId = _lastPlaybackContext.Source.Id;
        _retrySettingsOriginal = recovery.Selection with { MediaSourceId = sourceId };
        _retrySettingsDraft = _retrySettingsOriginal;
        _retrySettingsRecoveryId = recovery.RecoveryId;
        _retrySettingsReserved = ReserveControlArea.IsOn;
        _retrySettingsAutomatic = AutoPlayNext.IsOn;
        PopulateRetrySelection(_retrySettingsDraft);
        RetrySettingsContext.Text = LumenText.Get("Retry from {0}. Changes apply when you retry; your saved position stays the same.", FormatTime(recovery.Selection.StartPositionTicks));
        RetrySettingsContext.Visibility = Visibility.Visible;
        RetrySettingsFooter.Visibility = Visibility.Visible;
        SidePanelTitle.Text = LumenText.Get("Retry settings");
        SetTransportAvailability(false);
        UpdateRecoveryControls();
        return true;
    }

    private void PopulateRetrySelection(PlaybackSelection selection)
    {
        var updating = _updating;
        _updating = true;
        SourceSelector.SelectedItem = null;
        foreach (var option in SourceSelector.Items.OfType<ComboBoxItem>())
            if (option.Tag is string sourceId && sourceId == selection.MediaSourceId) SourceSelector.SelectedItem = option;
        var source = FindRetrySource(selection.MediaSourceId);
        PopulateTrackSelectors(source ?? new MediaSourceInfo(), selection.AudioStreamIndex, selection.SubtitleStreamIndex);
        PopulateQualityOptions(selection.MaxStreamingBitrate);
        _updating = updating;
        UpdateSelectionPresentation();
    }

    private MediaSourceInfo? FindRetrySource(string? sourceId)
    {
        var source = _item?.MediaSources?.FirstOrDefault(item => item.Id == sourceId);
        if (source?.MediaStreams is { Length: > 0 }) return source;
        if (_lastPlaybackContext is { } context && context.Selection.ItemId == _item?.Id && context.Source.Id == sourceId)
            return context.Source;
        return source;
    }

    private bool TryChangeRetrySource()
    {
        if (_updating || _retrySettingsDraft is not { } draft) return false;
        if (SourceSelector.SelectedItem is ComboBoxItem { Tag: string sourceId } && sourceId != draft.MediaSourceId)
        {
            var originalSource = _retrySettingsOriginal?.MediaSourceId == sourceId ? _retrySettingsOriginal : null;
            _retrySettingsDraft = draft with
            {
                MediaSourceId = sourceId,
                AudioStreamIndex = originalSource?.AudioStreamIndex,
                SubtitleStreamIndex = originalSource?.SubtitleStreamIndex
            };
            var updating = _updating;
            _updating = true;
            PopulateTrackSelectors(FindRetrySource(sourceId) ?? new MediaSourceInfo { Id = sourceId },
                _retrySettingsDraft.AudioStreamIndex, _retrySettingsDraft.SubtitleStreamIndex);
            _updating = updating;
            SetTransportAvailability(false);
        }
        return true;
    }

    private async void ApplyRetryClicked(object sender, RoutedEventArgs args)
    {
        if (_retrySettingsDraft is not { } draft || _retrySettingsRecoveryId != _coordinator?.Recovery?.RecoveryId) return;
        var reserve = ReserveControlArea.IsOn;
        var automatic = AutoPlayNext.IsOn;
        var change = new PlaybackSelectionChange
        {
            MediaSourceId = draft.MediaSourceId,
            AudioStreamIndex = draft.AudioStreamIndex,
            SubtitleStreamIndex = draft.SubtitleStreamIndex,
            MaxStreamingBitrate = draft.MaxStreamingBitrate,
            ForceTranscoding = draft.ForceTranscoding
        };
        CloseSidePanel();
        ReserveControlArea.IsOn = reserve;
        AutoPlayNext.IsOn = automatic;
        _bitrate = draft.MaxStreamingBitrate;
        PopulateQualityOptions(_bitrate);
        await RetryPlaybackAsync(change);
    }

    private void CancelRetrySettingsClicked(object sender, RoutedEventArgs args) => CloseSidePanel();

    private void CancelRetrySettings()
    {
        if (_retrySettingsDraft is null) return;
        var original = _retrySettingsOriginal;
        _retrySettingsDraft = null;
        _retrySettingsOriginal = null;
        _retrySettingsRecoveryId = null;
        ReserveControlArea.IsOn = _retrySettingsReserved;
        AutoPlayNext.IsOn = _retrySettingsAutomatic;
        if (original is not null) PopulateRetrySelection(original);
        RetrySettingsContext.Visibility = RetrySettingsFooter.Visibility = Visibility.Collapsed;
        SidePanelTitle.Text = LumenText.Get("Playback settings");
        SetTransportAvailability(_coordinator?.Status is PlaybackStatus.Playing or PlaybackStatus.Paused
            or PlaybackStatus.Buffering or PlaybackStatus.Seeking);
        UpdateRecoveryControls();
    }

    private void ClearPlaybackNotice()
    {
        PlaybackNotice.IsOpen = false;
        RecoveryPositionText.Visibility = ErrorSecondaryActions.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = ChangeRetrySettingsButton.Visibility = Visibility.Collapsed;
    }

    private void PlaybackNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        UpdateRecoveryControls();
        UpdateStageStatus(_coordinator?.Status ?? PlaybackStatus.Idle);
        ScheduleChromeAutoHide();
    }

    private void UpdateStageStatus(PlaybackStatus status)
    {
        StageStatus.Visibility = PlaybackNotice.IsOpen || status is PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Seeking
            ? Visibility.Collapsed : Visibility.Visible;
        StageStatusText.Text = LumenText.Get(status switch
        {
            PlaybackStatus.Negotiating or PlaybackStatus.Opening => "Starting playback",
            PlaybackStatus.Buffering => "Buffering…",
            PlaybackStatus.Ended => "Playback finished",
            PlaybackStatus.Failed => "Playback interrupted",
            _ => "Ready to play"
        });
        StageMediaText.Text = status == PlaybackStatus.Buffering ? string.Empty : _item is { } item ? PlaybackTitle(item, item.Name) : string.Empty;
    }
}
